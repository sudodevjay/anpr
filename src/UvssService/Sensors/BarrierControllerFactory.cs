using UvssService.Config;

namespace UvssService.Sensors;

public static class BarrierControllerFactory
{
    public static IBarrierController Create(string gateName, ControllerConfigEntry? config)
    {
        var source = config?.Source ?? "simulated";
        return source.Trim().ToLowerInvariant() switch
        {
            "tcp" => new TcpBarrierController(
                gateName,
                config!.TcpBindAddress,
                config.TcpPort),
            _ => new SimulatedBarrierController(
                gateName,
                config?.SimulatedVehicleIntervalSeconds ?? 20,
                config?.SimulatedPresentSeconds ?? 6),
        };
    }
}
