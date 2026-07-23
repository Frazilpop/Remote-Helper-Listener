using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
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

    public static void Run(int port, bool noMdns)
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

        using var icon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = $"Remote Helper — listening on port {port}",
        };

        var menu = new ContextMenuStrip();
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
        var quit = new ToolStripMenuItem("Quit");
        quit.Click += (_, _) => Application.Exit();

        menu.Items.Add(status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autostart);
        menu.Items.Add(openLog);
        menu.Items.Add(clearPaired);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(quit);
        icon.ContextMenuStrip = menu;

        // Live status: tooltip + menu line track who's connected (polled on
        // the UI thread — no cross-thread marshalling to get wrong), and
        // double-clicking the icon pops the same summary as a balloon.
        string Summary()
        {
            var clients = server.ConnectedClients;
            return clients.Length == 0
                ? $"No device connected — waiting ({server.PairedCount} paired)"
                : $"Connected: {string.Join(", ", clients)}";
        }
        var poll = new System.Windows.Forms.Timer { Interval = 2000 };
        poll.Tick += (_, _) =>
        {
            var text = $"Remote Helper — {Summary()}";
            icon.Text = text.Length <= 63 ? text : text[..60] + "…"; // NotifyIcon tooltip cap
            status.Text = Summary();
        };
        poll.Start();
        icon.DoubleClick += (_, _) =>
        {
            icon.BalloonTipTitle = "Remote Helper";
            icon.BalloonTipText = $"{Summary()} · port {port}";
            icon.ShowBalloonTip(4000);
        };

        icon.BalloonTipTitle = "Remote Helper is running";
        icon.BalloonTipText = "Waiting for your phone. It starts with Windows automatically.";
        icon.ShowBalloonTip(4000);

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

        SetAutostart(true);
        Process.Start(new ProcessStartInfo(installed) { UseShellExecute = true });
        return true;
    }

    private static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) is not null;
    }

    private static void SetAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
        if (enabled)
            key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
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
