using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;

namespace RemoteHelper.Listener;

/// <summary>
/// The macOS face of the listener: no window, no Dock icon, just a keyboard
/// glyph in the menu bar — the Mac counterpart of TrayApp. Runs when the
/// listener is launched from inside "Remote Helper.app"; `dotnet run` keeps
/// the console face for development.
///
/// AppKit is driven straight through the Objective-C runtime (objc_msgSend)
/// rather than a UI framework — a status item and one menu don't justify a
/// dependency. Dialogs and notifications go through osascript, same as
/// MacPairingUI.
/// </summary>
[SupportedOSPlatform("macos")]
internal static class MacTrayApp
{
    private const string LatestReleaseApi =
        "https://api.github.com/repos/Frazilpop/Remote-Helper-Listener/releases/latest";
    private const string ReleasesPage =
        "https://github.com/Frazilpop/Remote-Helper-Listener/releases/latest";

    private static readonly string LaunchAgentPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", "com.frazilpop.remotehelper.listener.plist");

    // Menu-item pointer -> handler. Also pins the handlers against GC.
    private static readonly Dictionary<IntPtr, Action> Actions = new();
    private static IntPtr _target;      // instance of our runtime-built ObjC class
    private static IntPtr _statusLine;  // the disabled "Listening…" menu item
    private static Server? _server;
    private static int _port;
    private static FileStream? _instanceLock;
    private static bool _updateCheckRunning;

    // Keep the IMP delegates alive for the lifetime of the process.
    private static readonly ActionImp ActionThunk = OnMenuAction;
    private static readonly ActionImp MenuUpdateThunk = OnMenuNeedsUpdate;
    private static readonly ActionImp MainDrainThunk = OnMainDrain;
    private static readonly PresentImp WillPresentThunk = OnWillPresentNotification;
    private static readonly AuthImp AuthCompletionThunk = OnNotificationAuth;

    private static bool InAppBundle =>
        AppContext.BaseDirectory.Contains(".app/Contents/", StringComparison.Ordinal);

    // Work handed to the AppKit main thread (server callbacks and async
    // continuations arrive on background threads, and AppKit tolerates only
    // the main one). performSelectorOnMainThread pokes the run loop to drain.
    private static readonly object MainGate = new();
    private static readonly List<Action> MainQueue = new();

    public static void Run(int port, bool noMdns)
    {
        // Autostart + manual launch (or a double-launch from the DMG) must
        // not fight over the port: first one in wins, the rest bow out.
        if (!AcquireSingleInstanceLock()) return;

        Log.Line("[sys]  mac tray app starting");
        // The .NET host doesn't link AppKit, so objc_getClass would quietly
        // return nil for every class (and msgSend-to-nil no-ops) until the
        // framework is actually in the process.
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        _port = port;
        CreateTarget();
        using var cts = new CancellationTokenSource();
        _server = Program.CreateServer(echo: false, new TrayPairingUI());
        var serverTask = Program.RunServerAsync(_server, port, noMdns, cts.Token);

        var app = Send(Cls("NSApplication"), Sel("sharedApplication"));
        SendVoid(app, Sel("setActivationPolicy:"), (IntPtr)1); // accessory: no Dock icon

        BuildStatusItem();
        PromptForAccessibility();

        Send(app, Sel("run"));

        cts.Cancel();
        try { serverTask.Wait(2000); } catch (AggregateException) { }
        Log.Line("[sys]  mac tray app quit");
    }

    /// <summary>
    /// One runtime-built ObjC class serves as the menu items' action target,
    /// the menu's delegate (for the live status line), the main-thread
    /// trampoline, and the notification-center delegate (so banners show
    /// even while the app counts as frontmost).
    /// </summary>
    private static void CreateTarget()
    {
        var cls = objc_allocateClassPair(Cls("NSObject"), "RHTrayTarget", 0);
        class_addMethod(cls, Sel("rhAction:"),
            Marshal.GetFunctionPointerForDelegate(ActionThunk), "v@:@");
        class_addMethod(cls, Sel("menuNeedsUpdate:"),
            Marshal.GetFunctionPointerForDelegate(MenuUpdateThunk), "v@:@");
        class_addMethod(cls, Sel("rhMainDrain:"),
            Marshal.GetFunctionPointerForDelegate(MainDrainThunk), "v@:@");
        class_addMethod(cls, Sel("userNotificationCenter:willPresentNotification:withCompletionHandler:"),
            Marshal.GetFunctionPointerForDelegate(WillPresentThunk), "v@:@@@?");
        objc_registerClassPair(cls);
        _target = Send(Send(cls, Sel("alloc")), Sel("init"));

        // Notifications go through the modern UserNotifications framework —
        // the NSUserNotification API this replaced is silently ignored on
        // current macOS. Bundle-only: UNUserNotificationCenter throws in a
        // bare dev process, where Notify falls back to the log anyway.
        if (!InAppBundle) return;
        NativeLibrary.Load("/System/Library/Frameworks/UserNotifications.framework/UserNotifications");
        var center = Send(Cls("UNUserNotificationCenter"), Sel("currentNotificationCenter"));
        SendVoid(center, Sel("setDelegate:"), _target);
        Send(center, Sel("requestAuthorizationWithOptions:completionHandler:"),
            (nuint)6 /* alert|sound */, MakeGlobalBlock(AuthCompletionThunk));
    }

    /// <summary>Banner even while the app is frontmost (banner|list = 24).</summary>
    private static void OnWillPresentNotification(
        IntPtr self, IntPtr sel, IntPtr center, IntPtr notification, IntPtr completion) =>
        CallBlock(completion, 24);

    private static void OnNotificationAuth(IntPtr block, sbyte granted, IntPtr error)
    {
        if (granted == 0) Log.Line("[warn] notifications not allowed — toasts go to this log only");
    }

    /// <summary>Run work on the AppKit main thread (fire and forget).</summary>
    private static void OnMain(Action work)
    {
        lock (MainGate) MainQueue.Add(work);
        SendVoid(_target, Sel("performSelectorOnMainThread:withObject:waitUntilDone:"),
            Sel("rhMainDrain:"), IntPtr.Zero, (sbyte)0);
    }

    private static void OnMainDrain(IntPtr self, IntPtr sel, IntPtr arg)
    {
        Action[] work;
        lock (MainGate)
        {
            work = MainQueue.ToArray();
            MainQueue.Clear();
        }
        foreach (var action in work)
        {
            try { action(); }
            catch (Exception ex) { Log.Line($"[warn] main-thread work failed: {ex.Message}"); }
        }
    }

    private static void BuildStatusItem()
    {
        var version = Program.ListenerVersion();
        var menu = Send(Send(Cls("NSMenu"), Sel("alloc")), Sel("init"));
        AddItem(menu, $"Remote Helper {version}", null);
        _statusLine = AddItem(menu, $"Listening on port {_port}", null);
        AddSeparator(menu);
        var login = AddItem(menu, "Start at login", ToggleStartAtLogin);
        SendVoid(login, Sel("setState:"), (IntPtr)(File.Exists(LaunchAgentPath) ? 1 : 0));
        AddItem(menu, "Open log", () =>
        {
            if (File.Exists(Log.FilePath)) Process.Start("/usr/bin/open", new[] { Log.FilePath });
        });
        AddItem(menu, "Clear paired devices…", ClearPairedDevices);
        AddItem(menu, "Check for updates…", () => _ = CheckForUpdatesAsync());
        AddSeparator(menu);
        AddItem(menu, "Quit", () =>
        {
            SendVoid(Send(Cls("NSApplication"), Sel("sharedApplication")),
                Sel("stop:"), IntPtr.Zero);
        });
        SendVoid(menu, Sel("setDelegate:"), _target);

        var statusBar = Send(Cls("NSStatusBar"), Sel("systemStatusBar"));
        var item = Send(statusBar, Sel("statusItemWithLength:"), -1.0);
        Send(item, Sel("retain")); // the status bar holds only a weak reference
        var button = Send(item, Sel("button"));
        var image = LoadMascotIcon();
        if (image == IntPtr.Zero) // fall back to a generic keyboard glyph
            image = Send(Cls("NSImage"), Sel("imageWithSystemSymbolName:accessibilityDescription:"),
                NSStr("keyboard"), NSStr("Remote Helper"));
        if (image != IntPtr.Zero)
            SendVoid(button, Sel("setImage:"), image);
        else
            SendVoid(button, Sel("setTitle:"), NSStr("⌨"));
        SendVoid(item, Sel("setMenu:"), menu);
    }

    // ---- menu handlers ----------------------------------------------------

    private static void OnMenuAction(IntPtr self, IntPtr sel, IntPtr sender)
    {
        if (Actions.TryGetValue(sender, out var handler))
        {
            try { handler(); }
            catch (Exception ex) { Log.Line($"[warn] menu action failed: {ex.Message}"); }
        }
    }

    private static void OnMenuNeedsUpdate(IntPtr self, IntPtr sel, IntPtr menu)
    {
        var server = _server;
        if (server is null || _statusLine == IntPtr.Zero) return;
        var clients = server.ConnectedClients;
        var summary = clients.Length == 0
            ? $"Waiting for a device ({server.PairedCount} paired) · port {_port}"
            : $"Connected: {string.Join(", ", clients)}";
        SendVoid(_statusLine, Sel("setTitle:"), NSStr(summary));
    }

    private static void ToggleStartAtLogin()
    {
        // A LaunchAgent rather than a login item: no automation permission
        // prompt, and it shows up in System Settings → Login Items all the
        // same. Takes effect at next login; the listener is running already.
        if (File.Exists(LaunchAgentPath))
        {
            File.Delete(LaunchAgentPath);
            Log.Line("[sys]  start at login: off");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LaunchAgentPath)!);
            File.WriteAllText(LaunchAgentPath, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key><string>com.frazilpop.remotehelper.listener</string>
                    <key>ProgramArguments</key>
                    <array><string>{Environment.ProcessPath}</string></array>
                    <key>RunAtLoad</key><true/>
                </dict>
                </plist>
                """);
            Log.Line("[sys]  start at login: on");
        }
        // Reflect the new state on the checkmark (sender lookup would do,
        // but the item is easy to find by title-independent identity).
        foreach (var (item, handler) in Actions)
            if (handler == (Action)ToggleStartAtLogin)
                SendVoid(item, Sel("setState:"), (IntPtr)(File.Exists(LaunchAgentPath) ? 1 : 0));
    }

    private static void ClearPairedDevices()
    {
        var server = _server!;
        var n = server.PairedCount;
        if (n == 0)
        {
            InfoAlertOnMain("No paired devices to clear.",
                "Each device pairs the first time it connects.");
            return;
        }
        var word = n == 1 ? "device" : "devices";
        if (!ConfirmAlert($"Forget all {n} paired {word}?",
                "Each one shows its pairing code on this screen again the next time it connects.",
                action: "Forget", dismiss: "Cancel", actionDefault: false))
            return;
        server.ForgetAllDevices();
        Notify($"Forgot {n} {word}. Each will ask to pair again.");
    }

    /// <summary>
    /// "Check for updates" against the public GitHub releases. Unlike the
    /// Windows exe the Mac app isn't its own installer, so a newer version
    /// just opens the releases page — download, drag to Applications, done.
    /// </summary>
    private static async Task CheckForUpdatesAsync()
    {
        if (_updateCheckRunning) return;
        _updateCheckRunning = true;
        var current = Program.ListenerVersion(); // "v1.6.0"
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"RemoteHelperListener/{current.TrimStart('v')}");

            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseApi));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            {
                InfoAlertOnMain("Couldn't check for updates.",
                    $"The latest release tag ({tag}) doesn't look like a version.");
                return;
            }
            Version.TryParse(current.TrimStart('v'), out var mine);
            if (latest <= (mine ?? new Version(0, 0, 0)))
            {
                InfoAlertOnMain("You're up to date.", $"{current} is the latest version.");
                return;
            }
            var download = await ConfirmOnMainAsync(
                $"Version {latest} is available (you have {current}).",
                "Open the download page? Drag the new app into Applications to update.",
                action: "Download", dismiss: "Not now", actionDefault: true);
            if (download)
                Process.Start("/usr/bin/open", new[] { ReleasesPage });
        }
        catch (Exception ex)
        {
            Log.Line($"[warn] update check failed: {ex.Message}");
            InfoAlertOnMain("Update check failed.", ex.Message);
        }
        finally
        {
            _updateCheckRunning = false;
        }
    }

    // ---- plumbing ---------------------------------------------------------

    private static bool AcquireSingleInstanceLock()
    {
        var path = Path.Combine(Path.GetDirectoryName(Log.FilePath)!, "listener.lock");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _instanceLock = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return true;
        }
        catch (IOException)
        {
            return false; // another instance holds it
        }
    }

    /// <summary>
    /// Typing needs Accessibility permission for THIS app (the dev console
    /// build borrows the terminal's). Asking the system to prompt puts
    /// "Remote Helper" straight into the Privacy &amp; Security list with a
    /// switch to flip — far better than sending the user spelunking.
    /// </summary>
    private static void PromptForAccessibility()
    {
        try
        {
            var lib = NativeLibrary.Load(AppServices);
            var key = Marshal.ReadIntPtr(NativeLibrary.GetExport(lib, "kAXTrustedCheckOptionPrompt"));
            var yes = Send(Cls("NSNumber"), Sel("numberWithBool:"), (sbyte)1);
            var options = Send(Cls("NSDictionary"), Sel("dictionaryWithObject:forKey:"), yes, key);
            if (!AXIsProcessTrustedWithOptions(options))
                Log.Line("[warn] no Accessibility permission yet — typing is blocked until it's granted");
        }
        catch (Exception ex)
        {
            Log.Line($"[warn] accessibility check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// The mascot, embedded as 1x/2x PNGs and combined into one 18x18pt
    /// template image (his monitor still runs wider than tall within the
    /// square) — macOS then renders it black or white to match the menu
    /// bar, like every native status icon.
    /// </summary>
    private static IntPtr LoadMascotIcon()
    {
        try
        {
            var image = Send(Send(Cls("NSImage"), Sel("alloc")), Sel("initWithSize:"), 18.0, 18.0);
            var loaded = false;
            foreach (var name in new[] { "MenuBarIcon18.png", "MenuBarIcon36.png" })
            {
                using var stream = Assembly.GetExecutingAssembly()
                    .GetManifestResourceStream("RemoteHelper.Listener.mac." + name);
                if (stream is null) continue;
                var bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
                var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
                try
                {
                    var data = Send(Cls("NSData"), Sel("dataWithBytes:length:"),
                        pin.AddrOfPinnedObject(), (IntPtr)bytes.Length);
                    var rep = Send(Cls("NSBitmapImageRep"), Sel("imageRepWithData:"), data);
                    if (rep == IntPtr.Zero) continue;
                    SendVoid(rep, Sel("setSize:"), 18.0, 18.0);
                    SendVoid(image, Sel("addRepresentation:"), rep);
                    loaded = true;
                }
                finally { pin.Free(); }
            }
            if (!loaded) return IntPtr.Zero;
            SendVoid(image, Sel("setTemplate:"), (sbyte)1);
            return image;
        }
        catch (Exception ex)
        {
            Log.Line($"[warn] mascot icon failed to load: {ex.Message}");
            return IntPtr.Zero;
        }
    }

    /// <summary>
    /// A real app notification (title, mascot icon and all), via the modern
    /// UserNotifications framework. For passive events only — anything the
    /// user is actively waiting on should be an alert instead, since
    /// notification permission can be declined.
    /// </summary>
    private static void Notify(string text)
    {
        Log.Line($"[sys]  {text}");
        if (!InAppBundle) return;
        OnMain(() =>
        {
            var content = Send(Send(Cls("UNMutableNotificationContent"), Sel("alloc")), Sel("init"));
            SendVoid(content, Sel("setTitle:"), NSStr("Remote Helper"));
            SendVoid(content, Sel("setBody:"), NSStr(text));
            var request = Send(Cls("UNNotificationRequest"),
                Sel("requestWithIdentifier:content:trigger:"),
                NSStr(Guid.NewGuid().ToString()), content, IntPtr.Zero);
            Send(Send(Cls("UNUserNotificationCenter"), Sel("currentNotificationCenter")),
                Sel("addNotificationRequest:withCompletionHandler:"), request, IntPtr.Zero);
            Send(content, Sel("release"));
        });
    }

    /// <summary>One-button "OK" alert — the reply to a direct user action.</summary>
    private static void InfoAlertOnMain(string message, string info) => OnMain(() =>
    {
        SendVoid(Send(Cls("NSApplication"), Sel("sharedApplication")),
            Sel("activateIgnoringOtherApps:"), (sbyte)1);
        var alert = Send(Send(Cls("NSAlert"), Sel("alloc")), Sel("init"));
        SendVoid(alert, Sel("setMessageText:"), NSStr(message));
        SendVoid(alert, Sel("setInformativeText:"), NSStr(info));
        Send(alert, Sel("runModal"));
        Send(alert, Sel("release"));
    });

    /// <summary>
    /// A native two-button NSAlert (which wears the app icon, unlike the
    /// osascript dialogs it replaced). Main thread only — see
    /// ConfirmOnMainAsync for the anywhere version.
    /// </summary>
    /// <returns>true if the action button was pressed.</returns>
    private static bool ConfirmAlert(string message, string info,
        string action, string dismiss, bool actionDefault)
    {
        SendVoid(Send(Cls("NSApplication"), Sel("sharedApplication")),
            Sel("activateIgnoringOtherApps:"), (sbyte)1);
        var alert = Send(Send(Cls("NSAlert"), Sel("alloc")), Sel("init"));
        SendVoid(alert, Sel("setMessageText:"), NSStr(message));
        SendVoid(alert, Sel("setInformativeText:"), NSStr(info));
        // The first button added is the default (Return key).
        Send(alert, Sel("addButtonWithTitle:"), NSStr(actionDefault ? action : dismiss));
        Send(alert, Sel("addButtonWithTitle:"), NSStr(actionDefault ? dismiss : action));
        var response = Send(alert, Sel("runModal")); // first button = 1000, second = 1001
        Send(alert, Sel("release"));
        return response == (IntPtr)(actionDefault ? 1000 : 1001);
    }

    private static Task<bool> ConfirmOnMainAsync(string message, string info,
        string action, string dismiss, bool actionDefault)
    {
        var tcs = new TaskCompletionSource<bool>();
        OnMain(() => tcs.SetResult(ConfirmAlert(message, info, action, dismiss, actionDefault)));
        return tcs.Task;
    }

    /// <summary>
    /// The pairing window, tray-mode edition: a native always-on-top panel
    /// in the same outfit as the Windows one — near-black, cyan headline,
    /// the code big and magenta. One window per device id; a mid-pair
    /// reconnect replaces the window rather than stacking a twin. Server
    /// callbacks arrive on background threads and AppKit only tolerates the
    /// main one, so operations queue up here and a performSelectorOnMainThread
    /// pokes the run loop to drain them. The code is also echoed through
    /// ConsolePairingUI so it reaches the log when no window can appear.
    /// </summary>
    private sealed class TrayPairingUI : IPairingUI
    {
        private static readonly ConsolePairingUI Echo = new();
        private static readonly Dictionary<string, IntPtr> Open = new(); // main thread only

        public void Show(string deviceId, string deviceName, string pin)
        {
            Echo.Show(deviceId, deviceName, pin);
            OnMain(() =>
            {
                Destroy(deviceId);
                Open[deviceId] = CreateWindow(deviceName, pin);
            });
        }

        public void Close(string deviceId, bool success)
        {
            Echo.Close(deviceId, success);
            OnMain(() => Destroy(deviceId));
            if (success) Notify("Paired.");
        }

        private static void Destroy(string deviceId)
        {
            if (!Open.Remove(deviceId, out var win)) return;
            SendVoid(win, Sel("orderOut:"), IntPtr.Zero);
            Send(win, Sel("release"));
        }

        private static IntPtr CreateWindow(string deviceName, string pin)
        {
            var win = Send(Send(Cls("NSWindow"), Sel("alloc")),
                Sel("initWithContentRect:styleMask:backing:defer:"),
                new NSRect(0, 0, 380, 230), 3 /* titled|closable */, 2 /* buffered */, (sbyte)1);
            SendVoid(win, Sel("setReleasedWhenClosed:"), (sbyte)0);
            SendVoid(win, Sel("setTitle:"), NSStr("Remote Helper — pairing"));
            SendVoid(win, Sel("setLevel:"), (IntPtr)3); // floating: above normal windows
            SendVoid(win, Sel("setBackgroundColor:"), Rgb(10, 10, 18));
            // Dark title bar to match the panel, whatever the system theme.
            var dark = Send(Cls("NSAppearance"), Sel("appearanceNamed:"), NSStr("NSAppearanceNameDarkAqua"));
            if (dark != IntPtr.Zero) SendVoid(win, Sel("setAppearance:"), dark);

            var content = Send(win, Sel("contentView"));
            AddLabel(content, $"“{deviceName}” wants to connect",
                new NSRect(10, 180, 360, 30), Mono(13, bold: true), Rgb(64, 230, 255));
            AddLabel(content, pin,
                new NSRect(10, 84, 360, 64), Mono(44, bold: true), Rgb(255, 90, 230));
            AddLabel(content, "Type this code on the device.",
                new NSRect(10, 46, 360, 16), Mono(10, bold: false), Rgb(140, 140, 150));
            AddLabel(content, "You'll only be asked once per device.",
                new NSRect(10, 26, 360, 16), Mono(10, bold: false), Rgb(140, 140, 150));

            Send(win, Sel("center"));
            SendVoid(win, Sel("makeKeyAndOrderFront:"), IntPtr.Zero);
            SendVoid(Send(Cls("NSApplication"), Sel("sharedApplication")),
                Sel("activateIgnoringOtherApps:"), (sbyte)1);
            return win;
        }

        private static void AddLabel(IntPtr parent, string text, NSRect frame, IntPtr font, IntPtr color)
        {
            var label = Send(Cls("NSTextField"), Sel("labelWithString:"), NSStr(text));
            SendVoid(label, Sel("setFrame:"), frame);
            SendVoid(label, Sel("setAlignment:"), (IntPtr)1); // NSTextAlignmentCenter (1 since the 10.12 SDK unified with iOS; 2 is right)
            SendVoid(label, Sel("setFont:"), font);
            SendVoid(label, Sel("setTextColor:"), color);
            SendVoid(parent, Sel("addSubview:"), label);
        }

        private static IntPtr Mono(double size, bool bold) =>
            Send(Cls("NSFont"), Sel("monospacedSystemFontOfSize:weight:"), size, bold ? 0.4 : 0.0);

        private static IntPtr Rgb(int r, int g, int b) =>
            Send(Cls("NSColor"), Sel("colorWithSRGBRed:green:blue:alpha:"),
                r / 255.0, g / 255.0, b / 255.0, 1.0);
    }

    // ---- Objective-C runtime ----------------------------------------------

    private static IntPtr AddItem(IntPtr menu, string title, Action? onClick)
    {
        // NSMenu auto-enables: items with a target+action are live, items
        // without (the version and status lines) render disabled — exactly
        // the Windows menu's look, for free.
        var item = Send(Send(Cls("NSMenuItem"), Sel("alloc")),
            Sel("initWithTitle:action:keyEquivalent:"),
            NSStr(title), onClick is null ? IntPtr.Zero : Sel("rhAction:"), NSStr(""));
        if (onClick is not null)
        {
            SendVoid(item, Sel("setTarget:"), _target);
            Actions[item] = onClick;
        }
        SendVoid(menu, Sel("addItem:"), item);
        return item;
    }

    private static void AddSeparator(IntPtr menu) =>
        SendVoid(menu, Sel("addItem:"), Send(Cls("NSMenuItem"), Sel("separatorItem")));

    private static IntPtr NSStr(string s) =>
        Send(Cls("NSString"), Sel("stringWithUTF8String:"), s);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ActionImp(IntPtr self, IntPtr sel, IntPtr sender);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void PresentImp(IntPtr self, IntPtr sel,
        IntPtr center, IntPtr notification, IntPtr completion);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AuthImp(IntPtr block, sbyte granted, IntPtr error);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void BlockInvoke1(IntPtr block, nuint arg);

    /// <summary>
    /// Hand-rolled Objective-C block (global flavour: no captures, never
    /// freed) wrapping a static delegate — the UserNotifications API speaks
    /// only in blocks. Layout: isa, flags, reserved, invoke, descriptor.
    /// </summary>
    private static IntPtr MakeGlobalBlock(Delegate invoke)
    {
        var descriptor = Marshal.AllocHGlobal(16);
        Marshal.WriteIntPtr(descriptor, 0, IntPtr.Zero); // reserved
        Marshal.WriteIntPtr(descriptor, 8, (IntPtr)32);  // block size
        var block = Marshal.AllocHGlobal(32);
        var libSystem = NativeLibrary.Load("/usr/lib/libSystem.B.dylib");
        Marshal.WriteIntPtr(block, 0, NativeLibrary.GetExport(libSystem, "_NSConcreteGlobalBlock"));
        Marshal.WriteInt32(block, 8, 0x10000000); // BLOCK_IS_GLOBAL
        Marshal.WriteInt32(block, 12, 0);
        Marshal.WriteIntPtr(block, 16, Marshal.GetFunctionPointerForDelegate(invoke));
        Marshal.WriteIntPtr(block, 24, descriptor);
        return block;
    }

    /// <summary>Call a block handed to us by the system (invoke ptr at offset 16).</summary>
    private static void CallBlock(IntPtr block, nuint arg)
    {
        if (block == IntPtr.Zero) return;
        Marshal.GetDelegateForFunctionPointer<BlockInvoke1>(Marshal.ReadIntPtr(block, 16))(block, arg);
    }

    private const string LibObjC = "/usr/lib/libobjc.A.dylib";
    private const string AppServices =
        "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";

    [DllImport(LibObjC, EntryPoint = "objc_getClass")]
    private static extern IntPtr Cls(string name);

    [DllImport(LibObjC, EntryPoint = "sel_registerName")]
    private static extern IntPtr Sel(string name);

    [DllImport(LibObjC)]
    private static extern IntPtr objc_allocateClassPair(IntPtr superclass, string name, nint extraBytes);

    [DllImport(LibObjC)]
    private static extern void objc_registerClassPair(IntPtr cls);

    [DllImport(LibObjC)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool class_addMethod(IntPtr cls, IntPtr sel, IntPtr imp, string types);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, IntPtr arg1, IntPtr arg2);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector,
        IntPtr arg1, IntPtr arg2, IntPtr arg3);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, double arg);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, sbyte arg);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, nuint arg1, IntPtr arg2);

    // NSSize is two doubles passed in registers on both arm64 and x64 —
    // identical to two plain double arguments. NSRect (4 doubles) does NOT
    // get that treatment on x64, so it's declared as a real struct and the
    // runtime marshals it per the native ABI.
    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NSRect
    {
        public readonly double X, Y, W, H;
        public NSRect(double x, double y, double w, double h) { X = x; Y = y; W = w; H = h; }
    }

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector, double w, double h);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector,
        double r, double g, double b, double a);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector,
        NSRect rect, nuint styleMask, nuint backing, sbyte defer);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector, NSRect rect);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector,
        IntPtr arg1, IntPtr arg2, sbyte arg3);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector, double w, double h);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector, sbyte arg);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend", CharSet = CharSet.Ansi)]
    private static extern IntPtr Send(IntPtr receiver, IntPtr selector,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string arg);

    [DllImport(LibObjC, EntryPoint = "objc_msgSend")]
    private static extern void SendVoid(IntPtr receiver, IntPtr selector, IntPtr arg);

    [DllImport(AppServices)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool AXIsProcessTrustedWithOptions(IntPtr options);
}
