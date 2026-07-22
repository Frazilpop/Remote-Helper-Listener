namespace RemoteHelper.Listener;

/// <summary>
/// Turns protocol messages into real keystrokes on the host OS.
/// </summary>
public interface IKeyInjector
{
    /// <summary>Type a string of literal Unicode text into the focused window.</summary>
    void InjectText(string text);

    /// <summary>Press a named non-printing key (see PROTOCOL.md for the list).</summary>
    /// <returns>false if the key name is unknown.</returns>
    bool InjectKey(string keyName);
}

public static class KeyInjectorFactory
{
    public static IKeyInjector Create(bool echoOnly)
    {
        if (echoOnly) return new EchoInjector();
        if (OperatingSystem.IsWindows()) return new WindowsKeyInjector();
        if (OperatingSystem.IsMacOS()) return new MacKeyInjector();
        throw new PlatformNotSupportedException(
            "No key injector for this OS. Run with --echo to test the protocol.");
    }
}

/// <summary>Prints what would be typed instead of typing it. Safe for testing.</summary>
public sealed class EchoInjector : IKeyInjector
{
    public void InjectText(string text) => Console.WriteLine($"[echo] text: \"{text}\"");

    public bool InjectKey(string keyName)
    {
        Console.WriteLine($"[echo] key:  <{keyName}>");
        return true;
    }
}
