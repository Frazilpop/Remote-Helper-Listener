using System.Text.Json;

namespace RemoteHelper.Listener;

/// <summary>
/// Remembers which devices have paired with this PC, keyed by the device's
/// stable id. Persisted to %LOCALAPPDATA%\RemoteHelper\trusted.json so a
/// device pairs exactly once, ever — the file outlives app restarts and
/// (because the phone keeps its id in the keychain) phone reinstalls too.
/// </summary>
public sealed class TrustStore
{
    public sealed record Device(string DeviceId, string Name, DateTime PairedAtUtc);

    private readonly string _path;
    private readonly List<Device> _devices;
    private readonly object _lock = new();

    public TrustStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteHelper");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "trusted.json");
        _devices = Load();
    }

    public int Count { get { lock (_lock) return _devices.Count; } }

    /// <summary>Distinct names of the paired devices, for display.</summary>
    public string[] Names
    {
        get { lock (_lock) return _devices.Select(d => d.Name).Distinct().OrderBy(n => n).ToArray(); }
    }

    public bool IsTrusted(string deviceId)
    {
        lock (_lock) return _devices.Any(d => d.DeviceId == deviceId);
    }

    public void Trust(string deviceId, string name)
    {
        lock (_lock)
        {
            if (_devices.Any(d => d.DeviceId == deviceId)) return;
            _devices.Add(new Device(deviceId, name, DateTime.UtcNow));
            try
            {
                File.WriteAllText(_path, JsonSerializer.Serialize(_devices,
                    new JsonSerializerOptions { WriteIndented = true }));
            }
            catch (IOException) { }
        }
    }

    /// <summary>Forget every paired device. Each one shows a pairing code
    /// again the next time it connects.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            _devices.Clear();
            try { File.WriteAllText(_path, "[]"); }
            catch (IOException) { }
        }
    }

    private List<Device> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<List<Device>>(File.ReadAllText(_path)) ?? new();
        }
        catch (JsonException)
        {
            Log.Line($"[warn] {_path} is corrupt, starting with no paired devices");
        }
        return new();
    }
}
