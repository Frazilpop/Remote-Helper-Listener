namespace RemoteHelper.Listener;

/// <summary>
/// Shows a pairing code to whoever is at the PC. Two implementations: a
/// console printer (macOS dev/testing) and a Windows popup window. Keyed by
/// device id, so one device gets at most one code on screen — a device that
/// drops and reconnects mid-pair replaces its window (same code, courtesy of
/// the server reusing it) instead of stacking a twin. Different devices
/// pairing at once don't collide.
/// </summary>
public interface IPairingUI
{
    void Show(string deviceId, string deviceName, string pin);
    void Close(string deviceId, bool success);
}

/// <summary>Prints the code to the console/log. Used by the macOS build.</summary>
public sealed class ConsolePairingUI : IPairingUI
{
    public void Show(string deviceId, string deviceName, string pin)
    {
        Log.Line("");
        Log.Line("       ╔══════════════════════════════════╗");
        Log.Line($"       ║  PAIR WITH: {deviceName,-21}║");
        Log.Line($"       ║        code  >>> {pin} <<<       ║");
        Log.Line("       ╚══════════════════════════════════╝");
        Log.Line("");
    }

    public void Close(string deviceId, bool success) =>
        Log.Line(success ? "[auth] pairing complete" : "[auth] pairing dismissed");
}
