using System.Text.Json;
using System.Text.Json.Serialization;

namespace UvssService.Config;

/// <summary>Persisted (JSON file) gate list -- the foundation everything
/// else (cameras, controllers, displays) is assigned to. Fully managed
/// through the Gate Setup admin page: whoever sets a site up creates
/// exactly the gates that site physically has (however many entry/exit
/// lanes), then assigns a camera/controller/display to each -- nothing
/// about the gate list is hand-edited in a config file.
///
/// Same in-memory-list + load/save/Changed-event shape as
/// CameraConfigStore/ControllerConfigStore/DisplayConfigStore -- see their
/// own remarks for the restart-to-apply caveat (a newly-added gate needs a
/// service restart before its camera stream/pass worker actually starts,
/// same limitation as adding a camera or controller today).</summary>
public class GateConfigStore
{
    // GateKind serialised by name ("Entry"/"Exit") rather than the default
    // 0/1 -- readable in the JSON file for anyone who opens it by hand.
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    private readonly List<GateOptions> _entries = new();
    private readonly string _filePath;
    private long _nextId = 1;

    public event Action? Changed;

    public GateConfigStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public IReadOnlyList<GateOptions> All
    {
        get { lock (_lock) { return _entries.OrderBy(g => g.Id).ToList(); } }
    }

    public GateOptions? Find(string name)
    {
        lock (_lock)
        {
            return _entries.FirstOrDefault(g => g.Name == name);
        }
    }

    public GateOptions Add(string displayName, GateKind kind, bool enabled)
    {
        GateOptions entry;
        lock (_lock)
        {
            var id = _nextId++;
            entry = new GateOptions
            {
                Id = id,
                Name = $"gate{id}",
                DisplayName = displayName.Trim(),
                Kind = kind,
                Enabled = enabled,
            };
            _entries.Add(entry);
            Save();
        }
        Changed?.Invoke();
        return entry;
    }

    /// <summary>Only DisplayName/Kind/Enabled can change -- Name is the
    /// stable key every other store/URL/log entry already references, so
    /// it's fixed for the gate's lifetime.</summary>
    public void Update(long id, string displayName, GateKind kind, bool enabled)
    {
        lock (_lock)
        {
            var existing = _entries.FirstOrDefault(g => g.Id == id);
            if (existing == null)
            {
                return;
            }
            existing.DisplayName = displayName.Trim();
            existing.Kind = kind;
            existing.Enabled = enabled;
            Save();
        }
        Changed?.Invoke();
    }

    public void Remove(long id)
    {
        lock (_lock)
        {
            _entries.RemoveAll(g => g.Id == id);
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
            var loaded = JsonSerializer.Deserialize<List<GateOptions>>(json, JsonOptions);
            if (loaded == null)
            {
                return;
            }
            _entries.AddRange(loaded);
            _nextId = _entries.Count > 0 ? _entries.Max(g => g.Id) + 1 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Gate config: failed to load '{_filePath}': {ex.Message}");
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
            var json = JsonSerializer.Serialize(_entries, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Gate config: failed to save '{_filePath}': {ex.Message}");
        }
    }
}
