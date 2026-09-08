namespace UvssService.Sensors;

/// <summary>Local dev/testing barrier controller with no real hardware --
/// Task.Delay-driven, same idea as the old SimulatedLaneInterlock (no
/// timers/background threads, just delays inline in the methods callers
/// already call). A vehicle "arrives" after SimulatedVehicleIntervalSeconds,
/// stays "present" for SimulatedPresentSeconds, then clears -- repeating
/// every cycle for as long as the gate worker keeps calling
/// WaitForVehiclePresentAsync. Open/Close just log; there's no physical
/// barrier to drive.</summary>
public class SimulatedBarrierController : IBarrierController
{
    private readonly string _gateName;
    private readonly double _vehicleIntervalSeconds;
    private readonly double _presentSeconds;
    private bool _present;
    private DateTime _presentSinceUtc;

    public SimulatedBarrierController(string gateName, double vehicleIntervalSeconds, double presentSeconds)
    {
        _gateName = gateName;
        _vehicleIntervalSeconds = Math.Max(1, vehicleIntervalSeconds);
        _presentSeconds = Math.Max(0.5, presentSeconds);
    }

    // A simulated controller has no real link to lose -- always "online".
    public bool IsOnline => true;

    public async Task<DateTime> WaitForVehiclePresentAsync(CancellationToken ct)
    {
        await Task.Delay(TimeSpan.FromSeconds(_vehicleIntervalSeconds), ct);
        _present = true;
        _presentSinceUtc = DateTime.UtcNow;
        return _presentSinceUtc;
    }

    public Task<bool> IsVehiclePresentAsync(CancellationToken ct = default)
    {
        if (_present && (DateTime.UtcNow - _presentSinceUtc).TotalSeconds >= _presentSeconds)
        {
            _present = false;
        }
        return Task.FromResult(_present);
    }

    public Task OpenBarrierAsync(CancellationToken ct = default)
    {
        Console.WriteLine($"[{_gateName}] (simulated) barrier OPEN.");
        return Task.CompletedTask;
    }

    public Task CloseBarrierAsync(CancellationToken ct = default)
    {
        Console.WriteLine($"[{_gateName}] (simulated) barrier CLOSE.");
        return Task.CompletedTask;
    }
}
