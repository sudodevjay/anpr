using System.Security.Cryptography;
using System.Text;

namespace UvssService.Licensing;

/// <summary>Computes a stable ID for the machine this install is running
/// on, so a license key can be tied to one specific device -- the same idea
/// as any commercial "activate on this PC" scheme. Primarily uses Windows'
/// own per-install MachineGuid (set once by Windows Setup, survives
/// reboots/updates, changes only on a full OS reinstall); falls back to a
/// random ID persisted locally if the registry isn't readable for some
/// reason, so the app still has *a* stable ID rather than a new one every
/// restart.</summary>
public static class DeviceIdentity
{
    public static string GetDeviceId(string contentRootPath)
    {
        var seed = (OperatingSystem.IsWindows() ? TryGetMachineGuid() : null) ?? GetOrCreateFallbackId(contentRootPath);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(seed));
        var hex = Convert.ToHexString(hash)[..20];
        return string.Join("-", Enumerable.Range(0, 4).Select(i => hex.Substring(i * 5, 5)));
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? TryGetMachineGuid()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }
        catch
        {
            return null;
        }
    }

    private static string GetOrCreateFallbackId(string contentRootPath)
    {
        var path = Path.Combine(contentRootPath, "data", "device_id.txt");
        try
        {
            if (File.Exists(path))
            {
                return File.ReadAllText(path).Trim();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var id = Guid.NewGuid().ToString();
            File.WriteAllText(path, id);
            return id;
        }
        catch
        {
            return Environment.MachineName;
        }
    }
}
