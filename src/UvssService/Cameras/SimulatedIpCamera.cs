using OpenCvSharp;

namespace UvssService.Cameras;

/// <summary>Local dev/testing stand-in for AxisIpCamera -- cycles through
/// every image in TestImageDir instead of hitting a real Axis camera's HTTP
/// snapshot endpoint. Lets a simulated UVSS pass exercise the full
/// plate-detect/OCR/multi-frame-aggregation pipeline with real vehicle
/// images, without needing real camera hardware.
///
/// The "current" image is picked from a SimulatedVehicleSlot (shared per
/// lane, advanced once per pass -- see its own remarks), not a per-instance
/// call counter or a wall-clock timer -- this is what lets the
/// driver/ANPR/overview cameras (three separate SimulatedIpCamera
/// instances) all land on the SAME index for the SAME pass, with no risk
/// of drifting apart mid-pass the way a fixed timer could.</summary>
public class SimulatedIpCamera : IIpCamera
{
    private readonly string[] _imagePaths;
    private readonly SimulatedVehicleSlot _slot;

    public SimulatedIpCamera(string testImageDir, SimulatedVehicleSlot slot)
    {
        _slot = slot;
        _imagePaths = Directory.Exists(testImageDir)
            ? Directory.GetFiles(testImageDir, "*.jpg").OrderBy(p => p).ToArray()
            : Array.Empty<string>();
        if (_imagePaths.Length == 0)
        {
            Console.WriteLine($"SimulatedIpCamera: no .jpg files found in '{testImageDir}' -- snapshots will be unavailable.");
        }
    }

    public Task<Mat?> GetSnapshotAsync(CancellationToken ct = default)
    {
        if (_imagePaths.Length == 0)
        {
            return Task.FromResult<Mat?>(null);
        }
        var index = _slot.Current % _imagePaths.Length;
        var frame = Cv2.ImRead(_imagePaths[index], ImreadModes.Color);
        return Task.FromResult<Mat?>(frame.Empty() ? null : frame);
    }
}
