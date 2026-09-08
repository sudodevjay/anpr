using UvssService.Config;

namespace UvssService.Cameras;

public static class IpCameraFactory
{
    public static IIpCamera Create(IpCameraOptions options, CameraDefaultsOptions defaults, SimulatedVehicleSlot vehicleSlot)
    {
        return options.Source.Trim().ToLowerInvariant() switch
        {
            "simulated" => new SimulatedIpCamera(options.TestImageDir, vehicleSlot),
            "rtsp" => new RtspIpCamera(options, defaults),
            _ => new AxisIpCamera(options, defaults),
        };
    }
}
