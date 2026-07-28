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
    private static readonly ActionImp PairDrainThunk = OnPairDrain;

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
    /// the menu's delegate (for the live status line), and the main-thread
    /// trampoline the pairing windows bounce through.
    /// </summary>
    private static void CreateTarget()
    {
        var cls = objc_allocateClassPair(Cls("NSObject"), "RHTrayTarget", 0);
        class_addMethod(cls, Sel("rhAction:"),
            Marshal.GetFunctionPointerForDelegate(ActionThunk), "v@:@");
        class_addMethod(cls, Sel("menuNeedsUpdate:"),
            Marshal.GetFunctionPointerForDelegate(MenuUpdateThunk), "v@:@");
        class_addMethod(cls, Sel("rhPairDrain:"),
            Marshal.GetFunctionPointerForDelegate(PairDrainThunk), "v@:@");
        objc_registerClassPair(cls);
        _target = Send(Send(cls, Sel("alloc")), Sel("init"));
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

    private static void OnPairDrain(IntPtr self, IntPtr sel, IntPtr arg) =>
        TrayPairingUI.DrainOnMainThread();

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
            Notify("No paired devices to clear.");
            return;
        }
        var word = n == 1 ? "device" : "devices";
        var answer = RunAppleScript(
            $"display dialog \"Forget all {n} paired {word}?\\n\\nEach one shows its pairing code on this screen again the next time it connects.\" " +
            "with title \"Remote Helper — clear paired devices\" " +
            "buttons {\"Cancel\", \"Forget\"} default button 1 cancel button 1 with icon caution");
        if (answer is null || !answer.Contains("Forget")) return;
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
                Notify($"Couldn't make sense of the latest release ({tag}).");
                return;
            }
            Version.TryParse(current.TrimStart('v'), out var mine);
            if (latest <= (mine ?? new Version(0, 0, 0)))
            {
                Notify($"You're up to date — {current} is the latest.");
                return;
            }
            var answer = RunAppleScript(
                $"display dialog \"Version {latest} is available (you have {current}).\\n\\nOpen the download page? Drag the new app into Applications to update.\" " +
                "with title \"Remote Helper — update available\" " +
                "buttons {\"Not now\", \"Download\"} default button 2 cancel button 1 with icon note");
            if (answer is not null && answer.Contains("Download"))
                Process.Start("/usr/bin/open", new[] { ReleasesPage });
        }
        catch (Exception ex)
        {
            Log.Line($"[warn] update check failed: {ex.Message}");
            Notify($"Update check failed: {ex.Message}");
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
    /// The mascot, embedded as 1x/2x PNGs and combined into one 22x18pt
    /// template image (his monitor is wider than tall, and the status item
    /// stretches to fit) — macOS then renders it black or white to match
    /// the menu bar, like every native status icon.
    /// </summary>
    private static IntPtr LoadMascotIcon()
    {
        try
        {
            var image = Send(Send(Cls("NSImage"), Sel("alloc")), Sel("initWithSize:"), 22.0, 20.0);
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
                    SendVoid(rep, Sel("setSize:"), 22.0, 20.0);
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

    private static void Notify(string text) =>
        RunAppleScript($"display notification \"{EscapeAs(text)}\" with title \"Remote Helper\"");

    /// <returns>osascript's stdout, or null if it failed or was cancelled.</returns>
    private static string? RunAppleScript(string script)
    {
        try
        {
            var psi = new ProcessStartInfo("/usr/bin/osascript") { RedirectStandardOutput = true };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            using var p = Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0 ? output : null;
        }
        catch (Exception ex)
        {
            Log.Line($"[warn] osascript failed: {ex.Message}");
            return null;
        }
    }

    private static string EscapeAs(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");

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
        private static readonly object Gate = new();
        private static readonly List<(bool Show, string Id, string Name, string Pin)> Queue = new();
        private static readonly Dictionary<string, IntPtr> Open = new();

        public void Show(string deviceId, string deviceName, string pin)
        {
            Echo.Show(deviceId, deviceName, pin);
            lock (Gate) Queue.Add((true, deviceId, deviceName, pin));
            Poke();
        }

        public void Close(string deviceId, bool success)
        {
            Echo.Close(deviceId, success);
            lock (Gate) Queue.Add((false, deviceId, "", ""));
            Poke();
            if (success) Notify("Paired.");
        }

        private static void Poke() =>
            SendVoid(_target, Sel("performSelectorOnMainThread:withObject:waitUntilDone:"),
                Sel("rhPairDrain:"), IntPtr.Zero, (sbyte)0);

        internal static void DrainOnMainThread()
        {
            (bool Show, string Id, string Name, string Pin)[] ops;
            lock (Gate)
            {
                ops = Queue.ToArray();
                Queue.Clear();
            }
            foreach (var op in ops)
            {
                if (Open.Remove(op.Id, out var old))
                {
                    SendVoid(old, Sel("orderOut:"), IntPtr.Zero);
                    Send(old, Sel("release"));
                }
                if (op.Show) Open[op.Id] = CreateWindow(op.Name, op.Pin);
            }
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
