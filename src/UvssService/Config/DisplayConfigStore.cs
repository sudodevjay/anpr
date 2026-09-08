using System.Text.Json;

namespace UvssService.Config;

public record DisplayConfigEntry(
    string GateName,
    int DisplayId,
    bool Enabled)
{
    public long Id { get; init; }
}

/// <summary>Persisted (JSON file) vehicle-number display config, one entry
/// per gate, managed through the Gate Setup admin page. Every display board
/// at a site shares the same COM port (see DisplayBusSettingsStore) -- this
/// store only tracks each gate's own DisplayId (Entry=1, Exit1=2, Exit2=3,
/// etc., matching however the site's displays were physically
/// address-configured). Same shape as CameraConfigStore/ControllerConfigStore
/// -- see their own remarks for the restart-to-apply caveat.</summary>
public class DisplayConfigStore
{
    private readonly object _lock = new();
    private readonly List<DisplayConfigEntry> _entries = new();
    private readonly string _filePath;
    private long _nextId = 1;

    public event Action? Changed;

    public DisplayConfigStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public IReadOnlyList<DisplayConfigEntry> All
    {
        get { lock (_lock) { return _entries.OrderBy(e => e.GateName).ToList(); } }
    }

    public DisplayConfigEntry? FindByGate(string gateName)
    {
        lock (_lock)
        {
            return _entries.FirstOrDefault(e => e.GateName == gateName);
        }
    }

    /// <summary>Replaces any existing entry for this gate -- one display per
    /// gate.</summary>
    public void UpsertForGate(string gateName, int displayId, bool enabled)
    {
        lock (_lock)
        {
            var existing = _entries.FirstOrDefault(e => e.GateName == gateName);
            var id = existing?.Id ?? _nextId++;
            _entries.RemoveAll(e => e.GateName == gateName);
            _entries.Add(new DisplayConfigEntry(gateName, displayId, enabled) { Id = id });
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
            var loaded = JsonSerializer.Deserialize<List<DisplayConfigEntry>>(json);
            if (loaded == null)
            {
                return;
            }
            _entries.AddRange(loaded);
            _nextId = _entries.Count > 0 ? _entries.Max(e => e.Id) + 1 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Display config: failed to load '{_filePath}': {ex.Message}");
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
            Console.WriteLine($"Display config: failed to save '{_filePath}': {ex.Message}");
        }
    }
}
