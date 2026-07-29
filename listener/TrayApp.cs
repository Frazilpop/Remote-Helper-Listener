using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Win32;

namespace RemoteHelper.Listener;

/// <summary>
/// The Windows face of the listener: no window, just a mascot in the tray.
///
/// Running the exe from anywhere except the install location acts as the
/// installer: it stops any running copy, copies itself to
/// %LOCALAPPDATA%\RemoteHelper, registers itself to start with Windows,
/// launches the installed copy and exits. So "double-click the exe in the
/// synced folder" is both first-time setup and how updates roll out.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class TrayApp
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "RemoteHelper";
    private const string LatestReleaseApi =
        "https://api.github.com/repos/Frazilpop/Remote-Helper-Listener/releases/latest";
    private const string ReleaseAssetName = "RemoteHelperListener.exe";

    public static void Run(int port, bool noMdns, bool justInstalled)
    {
        if (RelaunchedInstalledCopy()) return;

        // Second instance of the installed copy (e.g. autostart + manual
        // launch): quietly bow out, one listener is plenty.
        using var mutex = new Mutex(true, @"Local\RemoteHelperListener", out var isFirst);
        if (!isFirst) return;

        Log.Line("[sys]  tray app starting");
        using var cts = new CancellationTokenSource();

        // A never-shown form gives us a handle on the UI thread to marshal
        // pairing popups onto (its HWND is created here, before the message
        // loop, and BeginInvoke queues onto it once Application.Run starts).
        using var marshaller = new Form();
        _ = marshaller.Handle;
        var server = Program.CreateServer(echo: false, new WindowsPairingUI(marshaller));
        var serverTask = Program.RunServerAsync(server, port, noMdns, cts.Token);

        var version = Program.ListenerVersion();
        using var icon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "Remote Helper",
        };

        var menu = new ContextMenuStrip();
        var about = new ToolStripMenuItem($"Remote Helper {version}") { Enabled = false };
        var status = new ToolStripMenuItem($"Listening on port {port}") { Enabled = false };
        var autostart = new ToolStripMenuItem("Start with Windows")
        {
            Checked = IsAutostartEnabled(),
            CheckOnClick = true,
        };
        autostart.CheckedChanged += (_, _) => SetAutostart(autostart.Checked);
        var openLog = new ToolStripMenuItem("Open log");
        openLog.Click += (_, _) =>
        {
            if (File.Exists(Log.FilePath))
                Process.Start(new ProcessStartInfo(Log.FilePath) { UseShellExecute = true });
        };
        var clearPaired = new ToolStripMenuItem("Clear paired devices…");
        clearPaired.Click += (_, _) =>
        {
            var n = server.PairedCount;
            if (n == 0)
            {
                icon.BalloonTipTitle = "Remote Helper";
                icon.BalloonTipText = "No paired devices to clear.";
                icon.ShowBalloonTip(4000);
                return;
            }
            var word = n == 1 ? "device" : "devices";
            var answer = MessageBox.Show(
                $"Forget all {n} paired {word}?\n\nEach one shows its pairing code on this screen again the next time it connects.",
                "Remote Helper — clear paired devices",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
            if (answer != DialogResult.Yes) return;
            server.ForgetAllDevices();
            icon.BalloonTipTitle = "Remote Helper";
            icon.BalloonTipText = $"Forgot {n} {word}. Each will ask to pair again.";
            icon.ShowBalloonTip(4000);
        };
        var checkUpdates = new ToolStripMenuItem("Check for updates…");
        checkUpdates.Click += async (_, _) =>
        {
            checkUpdates.Enabled = false;
            try { await CheckForUpdatesAsync(icon, checkUpdates); }
            finally { checkUpdates.Enabled = true; }
        };
        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) => Application.Exit();

        menu.Items.Add(about);
        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autostart);
        menu.Items.Add(openLog);
        menu.Items.Add(clearPaired);
        menu.Items.Add(checkUpdates);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quit);
        icon.ContextMenuStrip = menu;

        // Live status: the menu line tracks who's connected, or who's paired
        // while nobody is (polled on the UI thread — no cross-thread
        // marshalling to get wrong), and double-clicking the icon pops the
        // same summary as a balloon.
        var poll = new System.Windows.Forms.Timer { Interval = 2000 };
        poll.Tick += (_, _) => status.Text = server.StatusSummary();
        poll.Start();
        icon.DoubleClick += (_, _) =>
        {
            icon.BalloonTipTitle = $"Remote Helper {version}";
            icon.BalloonTipText = $"{server.StatusSummary()} · port {port}";
            icon.ShowBalloonTip(4000);
        };

        // Announce only when the installer just put us here (first install or
        // an update); every ordinary start — autostart included — is silent.
        if (justInstalled)
        {
            icon.BalloonTipTitle = $"Remote Helper {version} installed";
            icon.BalloonTipText = "Waiting for your phone. It starts with Windows automatically.";
            icon.ShowBalloonTip(4000);
        }

        Application.Run();

        cts.Cancel();
        try { serverTask.Wait(2000); } catch (AggregateException) { }
        icon.Visible = false;
        Log.Line("[sys]  tray app quit");
    }

    /// <returns>true if this process handed over to the installed copy and should exit.</returns>
    private static bool RelaunchedInstalledCopy()
    {
        var self = Environment.ProcessPath!;
        var installDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteHelper");
        var installed = Path.Combine(installDir, "RemoteHelperListener.exe");
        if (string.Equals(self, installed, StringComparison.OrdinalIgnoreCase)) return false;

        Log.Line($"[sys]  installing: {self} -> {installed}");
        foreach (var p in Process.GetProcessesByName("RemoteHelperListener"))
        {
            if (p.Id == Environment.ProcessId) continue;
            try { p.Kill(); p.WaitForExit(3000); } catch (Exception) { }
        }

        Directory.CreateDirectory(installDir);
        for (var attempt = 0; ; attempt++)
        {
            try { File.Copy(self, installed, overwrite: true); break; }
            catch (IOException) when (attempt < 10) { Thread.Sleep(300); }
        }

        // Register the INSTALLED copy, not this one: pointing the Run key at
        // the exe we were launched from would re-run this installer (and its
        // "installed" balloon) on every boot.
        SetAutostart(true, installed);
        Process.Start(new ProcessStartInfo(installed)
        {
            Arguments = "--installed",
            UseShellExecute = true,
        });
        return true;
    }

    /// <summary>
    /// "Check for updates" against the public GitHub releases. If a newer
    /// version is out there (and the user says yes), download its exe to a
    /// temp file and run it — the exe is its own installer (see class docs),
    /// so it stops this process, copies itself into place and relaunches.
    /// </summary>
    private static async Task CheckForUpdatesAsync(NotifyIcon icon, ToolStripMenuItem item)
    {
        void Balloon(string title, string text)
        {
            icon.BalloonTipTitle = title;
            icon.BalloonTipText = text;
            icon.ShowBalloonTip(4000);
        }

        var current = Program.ListenerVersion(); // "v1.4.0"
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"RemoteHelperListener/{current.TrimStart('v')}");

            using var doc = JsonDocument.Parse(await http.GetStringAsync(LatestReleaseApi));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest))
            {
                Balloon("Remote Helper", $"Couldn't make sense of the latest release ({tag}).");
                return;
            }
            Version.TryParse(current.TrimStart('v'), out var mine);
            if (latest <= (mine ?? new Version(0, 0, 0)))
            {
                Balloon("Remote Helper", $"You're up to date — {current} is the latest.");
                return;
            }

            string? url = null;
            foreach (var asset in doc.RootElement.GetProperty("assets").EnumerateArray())
                if (asset.GetProperty("name").GetString() == ReleaseAssetName)
                    url = asset.GetProperty("browser_download_url").GetString();
            if (url is null)
            {
                Balloon("Remote Helper", $"{tag} is out but has no Windows exe attached yet.");
                return;
            }

            var answer = MessageBox.Show(
                $"Version {latest} is available (you have {current}).\n\nUpdate now? Remote Helper restarts itself when it's done.",
                "Remote Helper — update available",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information);
            if (answer != DialogResult.Yes) return;

            item.Text = "Downloading update…";
            var tmp = Path.Combine(Path.GetTempPath(), "RemoteHelperListener-update.exe");
            await using (var src = await http.GetStreamAsync(url))
            await using (var dst = File.Create(tmp))
                await src.CopyToAsync(dst);

            Log.Line($"[sys]  update: handing over to v{latest} installer at {tmp}");
            Process.Start(new ProcessStartInfo(tmp) { UseShellExecute = true });
            // The installer kills this process any moment now.
        }
        catch (Exception ex)
        {
            Log.Line($"[warn] update check failed: {ex.Message}");
            Balloon("Remote Helper", $"Update check failed: {ex.Message}");
        }
        finally
        {
            item.Text = "Check for updates…";
        }
    }

    private static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is not null;
    }

    private static void SetAutostart(bool enabled) => SetAutostart(enabled, Environment.ProcessPath!);

    private static void SetAutostart(bool enabled, string exePath)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(RunValueName, $"\"{exePath}\"");
        else
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        Log.Line($"[sys]  start with Windows: {(enabled ? "on" : "off")}");
    }

    private static Icon LoadTrayIcon()
    {
        var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("RemoteHelper.Listener.app.ico");
        return stream is null ? SystemIcons.Application : new Icon(stream);
    }
}
