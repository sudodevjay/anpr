using Microsoft.Extensions.Options;
using OpenCvSharp;
using UvssService.Config;
using UvssService.Lanes;
using UvssService.Streaming;

namespace UvssService.Cameras;

/// <summary>Continuously grabs frames from every gate's one camera for the
/// whole lifetime of the service -- NOT gated by whether a vehicle is
/// currently present, so the dashboard's video tiles are genuinely live.
/// GateWorkerHostedService only *subscribes* to a gate's feed (via
/// AnprFrameHub) while a pass is Active, to feed that pass's PlateTrack --
/// the camera itself never starts or stops per vehicle.
///
/// Replaces the old 3-camera-per-lane version (driver/anpr/overview) -- this
/// system uses exactly one camera per gate, configured via the Cameras admin
/// page (CameraConfigStore) instead of appsettings.</summary>
public class CameraStreamingHostedService : BackgroundService
{
    private readonly List<GateOptions> _gates;
    private readonly CameraDefaultsOptions _cameraDefaults;
    private readonly CameraStreamRegistry _registry;
    private readonly GateStateStore _stateStore;
    private readonly AnprFrameHub _anprFrameHub;
    private readonly SimulatedVehicleSlotStore _vehicleSlotStore;
    private readonly CameraConfigStore _cameraConfigStore;

    public CameraStreamingHostedService(
        GateConfigStore gateConfigStore,
        IOptions<CameraDefaultsOptions> cameraDefaults,
        CameraStreamRegistry registry,
        GateStateStore stateStore,
        AnprFrameHub anprFrameHub,
        SimulatedVehicleSlotStore vehicleSlotStore,
        CameraConfigStore cameraConfigStore)
    {
        _gates = gateConfigStore.All.Where(g => g.Enabled).ToList();
        _cameraDefaults = cameraDefaults.Value;
        _registry = registry;
        _stateStore = stateStore;
        _anprFrameHub = anprFrameHub;
        _vehicleSlotStore = vehicleSlotStore;
        _cameraConfigStore = cameraConfigStore;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new List<Task>();
        foreach (var gate in _gates)
        {
            var cameraConfig = _cameraConfigStore.FindByGate(gate.Name);
            if (cameraConfig == null || !cameraConfig.Enabled)
            {
                Console.WriteLine($"[{gate.Name}] no camera configured -- add one on the Cameras page.");
                continue;
            }

            var state = _stateStore.GetOrCreate(gate.Name, gate.DisplayName, gate.Kind);
            var vehicleSlot = _vehicleSlotStore.GetOrCreate(gate.Name);
            var cameraOptions = new IpCameraOptions
            {
                Name = cameraConfig.Name,
                Ip = cameraConfig.Ip,
                Enabled = cameraConfig.Enabled,
                Source = cameraConfig.Source,
                TestImageDir = cameraConfig.TestImageDir,
                Username = cameraConfig.Username,
                Password = cameraConfig.Password,
                RtspPort = cameraConfig.RtspPort,
                RtspStreamPath = cameraConfig.RtspStreamPath,
            };
            var camera = IpCameraFactory.Create(cameraOptions, _cameraDefaults, vehicleSlot);
            tasks.Add(StreamCameraAsync(gate.Name, camera, state.SetVehicleFrame, stoppingToken));
        }
        await Task.WhenAll(tasks);
    }

    private async Task StreamCameraAsync(string gateName, IIpCamera camera, Action<byte[]?> onFrame, CancellationToken ct)
    {
        var broadcaster = _registry.GetOrCreate(gateName, "video");
        var interval = TimeSpan.FromMilliseconds(_cameraDefaults.StreamIntervalMs);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var frame = await camera.GetSnapshotAsync(ct);
                if (frame != null)
                {
                    using (frame)
                    {
                        Cv2.ImEncode(".jpg", frame, out var jpeg);
                        onFrame(jpeg);
                        broadcaster.Publish(jpeg);
                        _anprFrameHub.Publish(gateName, frame);
                    }
                }
            }
            catch (CameraUnavailableException)
            {
                // Logged noisily enough elsewhere in this codebase already;
                // a continuous stream retrying silently on transient
                // failures is the right default here.
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{gateName}] camera stream error: {ex.Message}");
            }

            try
            {
                await Task.Delay(interval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
