using System.Text.Json;

namespace UvssService.Config;

public record ControllerConfigEntry(
    string GateName,
    string Source,
    string TcpBindAddress,
    int TcpPort,
    int DebounceMs,
    double SimulatedVehicleIntervalSeconds,
    double SimulatedPresentSeconds,
    bool Enabled)
{
    public long Id { get; init; }
}

/// <summary>Persisted (JSON file) barrier-controller config, one entry per
/// gate, managed through the Controllers admin page
/// (Components/Pages/ControllersPage.razor). Same shape as
/// CameraConfigStore -- see its own remarks for the restart-to-apply
/// caveat.</summary>
public class ControllerConfigStore
{
    private readonly object _lock = new();
    private readonly List<ControllerConfigEntry> _entries = new();
    private readonly string _filePath;
    private long _nextId = 1;

    public event Action? Changed;

    public ControllerConfigStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public IReadOnlyList<ControllerConfigEntry> All
    {
        get { lock (_lock) { return _entries.OrderBy(e => e.GateName).ToList(); } }
    }

    public ControllerConfigEntry? FindByGate(string gateName)
    {
        lock (_lock)
        {
            return _entries.FirstOrDefault(e => e.GateName == gateName);
        }
    }

    /// <summary>Replaces any existing entry for this gate -- one barrier
    /// controller per gate.</summary>
    public void UpsertForGate(
        string gateName, string source, string tcpBindAddress, int tcpPort, int debounceMs,
        double simulatedVehicleIntervalSeconds, double simulatedPresentSeconds, bool enabled)
    {
        lock (_lock)
        {
            var existing = _entries.FirstOrDefault(e => e.GateName == gateName);
            var id = existing?.Id ?? _nextId++;
            _entries.RemoveAll(e => e.GateName == gateName);
            _entries.Add(new ControllerConfigEntry(
                gateName, source, tcpBindAddress.Trim(), tcpPort, debounceMs,
                simulatedVehicleIntervalSeconds, simulatedPresentSeconds, enabled)
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
            var loaded = JsonSerializer.Deserialize<List<ControllerConfigEntry>>(json);
            if (loaded == null)
            {
                return;
            }
            _entries.AddRange(loaded);
            _nextId = _entries.Count > 0 ? _entries.Max(e => e.Id) + 1 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Controller config: failed to load '{_filePath}': {ex.Message}");
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
            Console.WriteLine($"Controller config: failed to save '{_filePath}': {ex.Message}");
        }
    }
}
