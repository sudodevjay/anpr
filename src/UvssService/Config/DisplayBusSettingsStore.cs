using System.Text.Json;

namespace UvssService.Config;

/// <summary>Every vehicle-number display board at a site is wired onto ONE
/// shared USB-RS485 bus through a single USB-serial adapter -- there's only
/// ever one real COM port for the whole site, not one per display/gate.
/// This is that single site-wide setting (persisted, JSON file) -- changed
/// in exactly one place (the Vehicle Number Displays section's "Display
/// Bus" block), never per-display.</summary>
public class DisplayBusSettingsStore
{
    private readonly object _lock = new();
    private readonly string _filePath;
    private string _comPort = "";

    public event Action? Changed;

    public DisplayBusSettingsStore(string filePath)
    {
        _filePath = filePath;
        Load();
    }

    public string ComPort
    {
        get { lock (_lock) { return _comPort; } }
    }

    public void SetComPort(string comPort)
    {
        lock (_lock)
        {
            _comPort = comPort.Trim();
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
            var loaded = JsonSerializer.Deserialize<DisplayBusSettingsData>(json);
            if (loaded != null)
            {
                _comPort = loaded.ComPort ?? "";
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Display bus settings: failed to load '{_filePath}': {ex.Message}");
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
            var json = JsonSerializer.Serialize(new DisplayBusSettingsData { ComPort = _comPort }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_filePath, json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Display bus settings: failed to save '{_filePath}': {ex.Message}");
        }
    }

    private class DisplayBusSettingsData
    {
        public string? ComPort { get; set; }
    }
}
