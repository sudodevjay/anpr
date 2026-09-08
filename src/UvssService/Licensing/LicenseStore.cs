namespace UvssService.Licensing;

/// <summary>Holds this install's currently-saved license key (persisted as
/// plain text at data/license.lic -- the key itself is safe to store in
/// the clear, it's a signed, non-secret token) and its live validation
/// status. Re-validates on a timer as well as on save, so a license that
/// expires while the app is running (it can stay up for weeks) actually
/// flips to Expired without needing a restart.</summary>
public class LicenseStore : IDisposable
{
    private static readonly TimeSpan RevalidateInterval = TimeSpan.FromMinutes(30);

    private readonly string _path;
    private readonly LicenseValidator _validator;
    private readonly object _lock = new();
    private readonly Timer _timer;

    public event Action? Changed;

    public string DeviceId { get; }
    public string? CurrentKey { get; private set; }
    public LicenseStatus Status { get; private set; }
    public bool IsValid => Status.IsValid;

    public LicenseStore(string path, string contentRootPath, LicenseValidator validator)
    {
        _path = path;
        _validator = validator;
        DeviceId = DeviceIdentity.GetDeviceId(contentRootPath);
        CurrentKey = TryReadKey();
        Status = _validator.Validate(CurrentKey, DeviceId);
        _timer = new Timer(_ => Revalidate(), null, RevalidateInterval, RevalidateInterval);
    }

    public void SetLicenseKey(string licenseKey)
    {
        var trimmed = licenseKey.Trim();
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, trimmed);
            CurrentKey = trimmed;
            Status = _validator.Validate(CurrentKey, DeviceId);
        }
        Changed?.Invoke();
    }

    private void Revalidate()
    {
        LicenseStatus fresh;
        lock (_lock)
        {
            fresh = _validator.Validate(CurrentKey, DeviceId);
            if (fresh == Status)
            {
                return;
            }
            Status = fresh;
        }
        Changed?.Invoke();
    }

    private string? TryReadKey()
    {
        try
        {
            return File.Exists(_path) ? File.ReadAllText(_path).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose() => _timer.Dispose();
}
