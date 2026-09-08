using System.Text.Json;

namespace UvssService.Config;

public record CameraConfigEntry(
    string GateName,
    string Name,
    string Ip,
    string Source,
    string TestImageDir,
    string Username,
    string Password,
    int RtspPort,
    string RtspStreamPath,
    bool Enabled)
{
    public long Id { get; init; }
}

/// <summary>Persisted (JSON file) camera config, one entry per gate, managed
/// through the Cameras admin page (Components/Pages/CamerasPage.razor)
/// instead of hand-edited appsettings -- same in-memory-list +
/// load/save/Changed-event shape as VehicleRegistryStore (Lanes/VehicleRegistry.cs).
/// GateWorkerHostedService/CameraStreamingHostedService read this once at
/// their own startup, so a change here needs an app restart to take effect,
/// same limitation as today's appsettings-bound config.</summary>
public class CameraConfigStore
{
    private readonly object _lock = new();
    private readonly List<CameraConfigEntry> _entries = new();
    private readonly string _filePath;
    private long _nextId = 1;

    public event Action? Changed;

    public CameraConfigStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public IReadOnlyList<CameraConfigEntry> All
    {
        get { lock (_lock) { return _entries.OrderBy(e => e.GateName).ToList(); } }
    }

    public CameraConfigEntry? FindByGate(string gateName)
    {
        lock (_lock)
        {
            return _entries.FirstOrDefault(e => e.GateName == gateName);
        }
    }

    /// <summary>Replaces any existing entry for this gate -- one camera per
    /// gate, so "add" is really "set this gate's camera".</summary>
    public void UpsertForGate(
        string gateName, string name, string ip, string source, string testImageDir,
        string username, string password, int rtspPort, string rtspStreamPath, bool enabled)
    {
        lock (_lock)
        {
            var existing = _entries.FirstOrDefault(e => e.GateName == gateName);
            var id = existing?.Id ?? _nextId++;
            _entries.RemoveAll(e => e.GateName == gateName);
            _entries.Add(new CameraConfigEntry(
                gateName, name.Trim(), ip.Trim(), source, testImageDir.Trim(),
                username.Trim(), password, rtspPort, rtspStreamPath.Trim(), enabled)
            { Id = id });
            Save();
        }
        Changed?.Invoke();
    }

    public void Remove(long id)
    {
        lock (_lock)
        {
            _entries.RemoveAll(e => e.Id == id);
            Save();
        }
        Changed?.Invoke();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                return;
            }
            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<CameraConfigEntry>>(json);
            if (loaded == null)
            {
                return;
            }
            _entries.AddRange(loaded);
            _nextId = _entries.Count > 0 ? _entries.Max(e => e.Id) + 1 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Camera config: failed to load '{_filePath}': {ex.Message}");
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            var json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Camera config: failed to save '{_filePath}': {ex.Message}");
        }
    }
}
