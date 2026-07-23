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
///
/// Trust is forever; connections die messy deaths. A phone that's killed,
/// sent to sleep or carried out of Wi-Fi range leaves a half-open socket
/// that looks exactly like a quiet one, so three rules keep ghosts harmless:
/// total silence for <see cref="IdleTimeout"/> means dead (live clients ping
/// every 10 s); a device's hello closes that same device's stale previous
/// connection, so a reconnect never competes with its own ghost; and the
/// pairing code is per device, so a mid-pair reconnect shows the same code
/// again instead of stacking a second window with a different one.
/// </summary>
public sealed class Server
{
    /// <summary>Silence for this long means the connection is dead — live
    /// clients ping every 10 s, so this allows six missed pings.</summary>
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    /// <summary>On hello, the device's previous connection is closed if it has
    /// been silent this long (2½ missed pings). One that's still pinging is
    /// genuinely alive — the same device holding two live connections is
    /// legal — so it's left alone.</summary>
    private static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(25);

    /// <summary>An unfinished pairing code stays valid this long across
    /// reconnects, so the number on the PC screen doesn't change mid-pair.</summary>
    private static readonly TimeSpan PairCodeMaxAge = TimeSpan.FromMinutes(10);

    private readonly IKeyInjector _injector;
    private readonly string _hostName;
    private readonly TrustStore _trust;
    private readonly IPairingUI _pairing;
    private readonly ConcurrentDictionary<Guid, string> _clients = new();
    private readonly ConcurrentDictionary<Guid, Conn> _conns = new();
    private readonly ConcurrentDictionary<string, PairSession> _sessions = new();
    /// <summary>Serialises pairing-window show/close decisions, so a dropped
    /// connection's cleanup can't close the window its reconnect just showed.</summary>
    private readonly object _pairGate = new();

    /// <summary>Liveness bookkeeping for one connection.</summary>
    private sealed class Conn
    {
        public required CancellationTokenSource Life;
        public volatile string DeviceId = "";
        public long LastHeard; // Environment.TickCount64 of the last line received
    }

    /// <summary>One device's pairing-in-progress: its code and tries left.
    /// Keyed by device id, shared by all of that device's connections.</summary>
    private sealed class PairSession
    {
        public required string Pin;
        public long Born;
        public int AttemptsLeft = 3;
    }

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

    /// <summary>The tray's "clear paired devices": forget every trust and drop
    /// every live connection, so each device re-pairs on its automatic
    /// reconnect a few seconds later — nothing keeps typing on old credit.</summary>
    public void ForgetAllDevices()
    {
        _trust.Clear();
        foreach (var conn in _conns.Values)
        {
            try { conn.Life.Cancel(); } catch (ObjectDisposedException) { }
        }
        Log.Line("[auth] all paired devices forgotten — each re-pairs on its next connect");
    }

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
        using var life = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var conn = new Conn { Life = life, LastHeard = Environment.TickCount64 };
        _conns[clientId] = conn;
        string deviceId = "";
        string deviceName = "a device";
        var authenticated = false;

        try
        {
            client.NoDelay = true; // keystrokes must not sit in Nagle's buffer
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            life.CancelAfter(IdleTimeout);

            while (!life.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(life.Token);
                if (line is null) break;
                Volatile.Write(ref conn.LastHeard, Environment.TickCount64);
                life.CancelAfter(IdleTimeout); // any traffic restarts the clock
                if (line.Length == 0) continue;

                JsonNode? msg;
                try { msg = JsonNode.Parse(line); }
                catch (JsonException) { continue; } // garbage in, ignore

                switch ((string?)msg?["t"])
                {
                    case "hello":
                        deviceId = (string?)msg!["deviceId"] ?? "";
                        deviceName = (string?)msg["name"] ?? "a device";
                        conn.DeviceId = deviceId;
                        if (deviceId.Length > 0) CloseStaleTwin(clientId, deviceId, deviceName);

                        if (deviceId.Length > 0 && _trust.IsTrusted(deviceId))
                        {
                            authenticated = true;
                            _clients[clientId] = deviceName;
                            Log.Line($"[net]  {deviceName} connected ({remote})");
                            await SendAsync(stream, new { t = "ok", host = _hostName }, life.Token);
                        }
                        else if (deviceId.Length == 0)
                        {
                            // No device id means it can never pair (trust is
                            // keyed by id) — don't put a code on screen for it.
                            Log.Line($"[auth] {deviceName} ({remote}) sent no device id — can't pair");
                            await SendAsync(stream, new { t = "pair_required", host = _hostName }, life.Token);
                        }
                        else
                        {
                            string pin;
                            lock (_pairGate)
                            {
                                pin = SessionFor(deviceId).Pin;
                                _pairing.Show(deviceId, deviceName, pin);
                            }
                            Log.Line($"[auth] {deviceName} ({remote}) wants to pair — code {pin}, device id {deviceId}");
                            await SendAsync(stream, new { t = "pair_required", host = _hostName }, life.Token);
                        }
                        break;

                    case "pair":
                        if (deviceId.Length == 0) break; // no proper hello first
                        if (_trust.IsTrusted(deviceId))
                        {
                            // Paired a moment ago on another of this device's
                            // connections. Saying so again is harmless and
                            // heals the straggler.
                            authenticated = true;
                            _clients[clientId] = deviceName;
                            await SendAsync(stream, new { t = "paired", host = _hostName }, life.Token);
                        }
                        else if (_sessions.TryGetValue(deviceId, out var session))
                        {
                            if ((string?)msg!["pin"] == session.Pin)
                            {
                                _trust.Trust(deviceId, deviceName);
                                authenticated = true;
                                _clients[clientId] = deviceName;
                                lock (_pairGate)
                                {
                                    _sessions.TryRemove(deviceId, out _);
                                    _pairing.Close(deviceId, success: true);
                                }
                                Log.Line($"[auth] {deviceName}: paired");
                                await SendAsync(stream, new { t = "paired", host = _hostName }, life.Token);
                            }
                            else
                            {
                                var left = Interlocked.Decrement(ref session.AttemptsLeft);
                                if (left <= 0)
                                {
                                    lock (_pairGate)
                                    {
                                        _sessions.TryRemove(deviceId, out _);
                                        _pairing.Close(deviceId, success: false);
                                    }
                                    Log.Line($"[auth] {deviceName}: too many wrong codes, dropping");
                                    await SendAsync(stream, new { t = "pair_failed", attemptsLeft = 0 }, life.Token);
                                    return;
                                }
                                await SendAsync(stream, new { t = "pair_failed", attemptsLeft = left }, life.Token);
                            }
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
                        await SendAsync(stream, new { t = "pong" }, life.Token);
                        break;

                    // Anything else pre-auth is ignored; unknown types are
                    // ignored by design (forward compatibility).
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // Normal disconnects land here — and so do our own deliberate
            // cuts: the idle timeout, and being superseded by a reconnect.
        }
        finally
        {
            _conns.TryRemove(clientId, out _);
            _clients.TryRemove(clientId, out _);
            if (!authenticated && deviceId.Length > 0)
            {
                // Mid-pair and this was the device's last connection: take the
                // code window down. The code itself stays warm (PairCodeMaxAge),
                // so a quick reconnect shows the same number.
                lock (_pairGate)
                {
                    if (_sessions.ContainsKey(deviceId) &&
                        !_conns.Values.Any(c => c.DeviceId == deviceId))
                        _pairing.Close(deviceId, success: false);
                }
            }
            client.Dispose();
            Log.Line($"[net]  {remote} disconnected");
        }
    }

    /// <summary>A device said hello: close any previous connection claiming
    /// the same device id that has gone quiet. A device id names exactly one
    /// physical device, so that old connection is this very device's ghost —
    /// a dead socket left behind when it was killed, slept or changed
    /// networks. Other devices' connections are never touched, and neither is
    /// a same-id connection that's still pinging.</summary>
    private void CloseStaleTwin(Guid selfId, string deviceId, string deviceName)
    {
        var cutoff = Environment.TickCount64 - (long)StaleAfter.TotalMilliseconds;
        foreach (var (id, other) in _conns)
        {
            if (id == selfId || other.DeviceId != deviceId) continue;
            if (Volatile.Read(ref other.LastHeard) > cutoff) continue;
            Log.Line($"[net]  {deviceName} reconnected — dropping its stale old connection");
            try { other.Life.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    /// <summary>The device's live pairing session, minting a fresh code only
    /// when there's none or the one on file has expired. Reuse is what keeps
    /// the number on the PC screen stable while a pairing phone drops and
    /// reconnects. Call while holding _pairGate.</summary>
    private PairSession SessionFor(string deviceId)
    {
        if (_sessions.TryGetValue(deviceId, out var existing) &&
            Environment.TickCount64 - existing.Born < (long)PairCodeMaxAge.TotalMilliseconds &&
            existing.AttemptsLeft > 0)
            return existing;
        var fresh = new PairSession { Pin = GeneratePin(), Born = Environment.TickCount64 };
        _sessions[deviceId] = fresh;
        return fresh;
    }

    private static async Task SendAsync(NetworkStream stream, object payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload) + "\n");
        await stream.WriteAsync(bytes, ct);
    }

    private static string GeneratePin() =>
        RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
}
