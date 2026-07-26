using Makaretu.Dns;

namespace RemoteHelper.Listener;

public static class Program
{
    private const int DefaultPort = 8737;

    public static void Main(string[] args)
    {
        bool echo = args.Contains("--echo");
        bool noMdns = args.Contains("--no-mdns");
        int port = DefaultPort;
        var portIdx = Array.IndexOf(args, "--port");
        if (portIdx >= 0 && portIdx + 1 < args.Length && int.TryParse(args[portIdx + 1], out var p))
            port = p;

#if WINDOWS
        // --installed is how the installer tells the copy it just launched
        // "announce yourself"; normal starts (autostart, manual) stay silent.
        TrayApp.Run(port, noMdns, justInstalled: args.Contains("--installed"));
#else
        RunConsole(port, noMdns, echo);
#endif
    }

#if !WINDOWS
    private static void RunConsole(int port, bool noMdns, bool echo)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine("""

           .---------.
           | .-----. |
           |+| o o |b|      REMOTE HELPER
           | | \_/ |a|      "Point your phone at me."
           | '-----' |
           '---------'
        """);
        Console.ResetColor();
        Console.WriteLine($"[sys]  {(echo ? "ECHO MODE — nothing will be typed" : "live typing mode")}, Ctrl+C to quit");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        var server = CreateServer(echo, new ConsolePairingUI());
        RunServerAsync(server, port, noMdns, cts.Token).GetAwaiter().GetResult();
    }
#endif

    public static Server CreateServer(bool echo, IPairingUI pairing) =>
        new(KeyInjectorFactory.Create(echo), Environment.MachineName, new TrustStore(), pairing);

    /// <summary>Advertises via Bonjour and serves until cancelled. Shared by the
    /// tray app (Windows) and the console app (macOS development).</summary>
    public static async Task RunServerAsync(Server server, int port, bool noMdns, CancellationToken ct)
    {
        var hostName = Environment.MachineName;
        Log.Line($"[sys]  host: {hostName}");

        ServiceDiscovery? sd = null;
        if (!noMdns)
        {
            try
            {
                sd = new ServiceDiscovery();
                sd.Advertise(new ServiceProfile(hostName, "_remotehelper._tcp", (ushort)port));
                Log.Line($"[net]  advertising \"{hostName}\" via Bonjour (_remotehelper._tcp:{port})");
            }
            catch (Exception ex)
            {
                Log.Line($"[warn] Bonjour advertising failed ({ex.Message}); connect by IP instead");
            }
        }

        try
        {
            await server.RunAsync(port, ct);
        }
        finally
        {
            sd?.Dispose();
        }
    }
}
