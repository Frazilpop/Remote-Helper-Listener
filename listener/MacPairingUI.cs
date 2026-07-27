using System.Diagnostics;
using System.Runtime.Versioning;

namespace RemoteHelper.Listener;

/// <summary>
/// The macOS pairing popup: an osascript "display dialog" with the 6-digit
/// code — the Mac counterpart of WindowsPairingUI, with the same rules
/// (one dialog per device id; a mid-pair reconnect replaces the dialog
/// rather than stacking a twin; Close takes it down). The code is also
/// printed through ConsolePairingUI so it still reaches the log/terminal
/// when no dialog can appear (SSH session, headless Mac).
/// </summary>
[SupportedOSPlatform("macos")]
public sealed class MacPairingUI : IPairingUI
{
    private readonly ConsolePairingUI _console = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, Process> _dialogs = new();

    public void Show(string deviceId, string deviceName, string pin)
    {
        _console.Show(deviceId, deviceName, pin);
        lock (_gate)
        {
            KillLocked(deviceId);
            // \n must reach AppleScript as its own escape sequence, hence \\n.
            var text = $"“{Escape(deviceName)}” wants to connect.\\n\\n" +
                       $"Code:  {pin}\\n\\n" +
                       "Type this code on the device.\\nYou'll only be asked once per device.";
            try { _dialogs[deviceId] = Spawn(
                $"display dialog \"{text}\" with title \"Remote Helper — pairing\" " +
                "buttons {\"Dismiss\"} default button 1 with icon note"); }
            catch (Exception ex)
            {
                Log.Line($"[warn] pairing dialog failed ({ex.Message}) — use the code printed above");
            }
        }
    }

    public void Close(string deviceId, bool success)
    {
        _console.Close(deviceId, success);
        lock (_gate) KillLocked(deviceId);
        if (!success) return;
        try { Spawn("display notification \"Paired.\" with title \"Remote Helper\""); }
        catch { } // the log already says it; a missing toast is no loss
    }

    /// <summary>Call while holding _gate.</summary>
    private void KillLocked(string deviceId)
    {
        if (!_dialogs.Remove(deviceId, out var dialog)) return;
        try { if (!dialog.HasExited) dialog.Kill(); }
        catch (InvalidOperationException) { } // exited between check and kill
        dialog.Dispose();
    }

    private static Process Spawn(string appleScript)
    {
        var psi = new ProcessStartInfo("/usr/bin/osascript");
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(appleScript);
        return Process.Start(psi)!;
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
