namespace RemoteHelper.Listener;

/// <summary>
/// Shows a pairing code to whoever is at the PC. Two implementations: a
/// console printer (macOS dev/testing) and a Windows popup window. Each
/// pairing attempt is a session, so simultaneous pairings don't collide.
/// </summary>
public interface IPairingUI
{
    void Show(Guid session, string deviceName, string pin);
    void Close(Guid session, bool success);
}

/// <summary>Prints the code to the console/log. Used by the macOS build.</summary>
public sealed class ConsolePairingUI : IPairingUI
{
    public void Show(Guid session, string deviceName, string pin)
    {
        Log.Line("");
        Log.Line("       ╔══════════════════════════════════╗");
        Log.Line($"       ║  PAIR WITH: {deviceName,-21}║");
        Log.Line($"       ║        code  >>> {pin} <<<       ║");
        Log.Line("       ╚══════════════════════════════════╝");
        Log.Line("");
    }

    public void Close(Guid session, bool success) =>
        Log.Line(success ? "[auth] pairing complete" : "[auth] pairing dismissed");
}
