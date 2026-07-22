using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace RemoteHelper.Listener;

/// <summary>
/// The TCP server. A device is trusted once it has paired (see
/// docs/PROTOCOL.md): the first time a device connects, the PC shows a
/// 6-digit code the user types back on the device; after that the device's
/// id is remembered forever. Many devices and many PCs freely interconnect —
/// trust is per (device, PC) pair, and pairing is the only gate.
/// </summary>
public sealed class Server
{
    private readonly IKeyInjector _injector;
    private readonly string _hostName;
    private readonly TrustStore _trust;
    private readonly IPairingUI _pairing;
    private readonly ConcurrentDictionary<Guid, string> _clients = new();

    public Server(IKeyInjector injector, string hostName, TrustStore trust, IPairingUI pairing)
    {
        _injector = injector;
        _hostName = hostName;
        _trust = trust;
        _pairing = pairing;
    }

    /// <summary>Names of devices currently connected (for the tray status).</summary>
    public string[] ConnectedClients => _clients.Values.OrderBy(n => n).ToArray();

    /// <summary>How many devices have ever paired with this PC.</summary>
    public int PairedCount => _trust.Count;

    public async Task RunAsync(int port, CancellationToken ct)
    {
        var tcp = new TcpListener(IPAddress.Any, port);
        tcp.Start();
        Log.Line($"[net]  listening on port {port}");

        using var reg = ct.Register(() => tcp.Stop());
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await tcp.AcceptTcpClientAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (SocketException) when (ct.IsCancellationRequested) { break; }
            _ = HandleClientAsync(client, ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        var clientId = Guid.NewGuid();
        var pairSession = Guid.NewGuid();
        string? activePin = null;
        int pinAttemptsLeft = 3;
        string deviceId = "";
        string deviceName = "a device";
        var authenticated = false;

        try
        {
            client.NoDelay = true; // keystrokes must not sit in Nagle's buffer
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null) break;
                if (line.Length == 0) continue;

                JsonNode? msg;
                try { msg = JsonNode.Parse(line); }
                catch (JsonException) { continue; } // garbage in, ignore

                switch ((string?)msg?["t"])
                {
                    case "hello":
                        deviceId = (string?)msg!["deviceId"] ?? "";
                        deviceName = (string?)msg["name"] ?? "a device";

                        if (deviceId.Length > 0 && _trust.IsTrusted(deviceId))
                        {
                            authenticated = true;
                            _clients[clientId] = deviceName;
                            Log.Line($"[net]  {deviceName} connected ({remote})");
                            await SendAsync(stream, new { t = "ok", host = _hostName }, ct);
                        }
                        else
                        {
                            activePin = GeneratePin();
                            pinAttemptsLeft = 3;
                            Log.Line($"[auth] {deviceName} ({remote}) wants to pair — code {activePin}");
                            _pairing.Show(pairSession, deviceName, activePin);
                            await SendAsync(stream, new { t = "pair_required", host = _hostName }, ct);
                        }
                        break;

                    case "pair":
                        if (activePin is null) break;
                        if ((string?)msg!["pin"] == activePin && deviceId.Length > 0)
                        {
                            _trust.Trust(deviceId, deviceName);
                            authenticated = true;
                            activePin = null;
                            _clients[clientId] = deviceName;
                            _pairing.Close(pairSession, success: true);
                            Log.Line($"[auth] {deviceName}: paired");
                            await SendAsync(stream, new { t = "paired", host = _hostName }, ct);
                        }
                        else if (--pinAttemptsLeft <= 0)
                        {
                            _pairing.Close(pairSession, success: false);
                            Log.Line($"[auth] {deviceName}: too many wrong codes, dropping");
                            await SendAsync(stream, new { t = "pair_failed", attemptsLeft = 0 }, ct);
                            return;
                        }
                        else
                        {
                            await SendAsync(stream, new { t = "pair_failed", attemptsLeft = pinAttemptsLeft }, ct);
                        }
                        break;

                    case "text" when authenticated:
                        var s = (string?)msg!["s"];
                        if (!string.IsNullOrEmpty(s)) _injector.InjectText(s);
                        break;

                    case "key" when authenticated:
                        var k = (string?)msg!["k"];
                        if (k is not null && !_injector.InjectKey(k))
                            Log.Line($"[warn] unknown key name: {k}");
                        break;

                    case "ping":
                        await SendAsync(stream, new { t = "pong" }, ct);
                        break;

                    // Anything else pre-auth is ignored; unknown types are
                    // ignored by design (forward compatibility).
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // Normal disconnects land here; nothing to do.
        }
        finally
        {
            if (activePin is not null) _pairing.Close(pairSession, success: false); // abandoned mid-pair
            _clients.TryRemove(clientId, out _);
            client.Dispose();
            Log.Line($"[net]  {remote} disconnected");
        }
    }

    private static async Task SendAsync(NetworkStream stream, object payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload) + "\n");
        await stream.WriteAsync(bytes, ct);
    }

    private static string GeneratePin() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
}
