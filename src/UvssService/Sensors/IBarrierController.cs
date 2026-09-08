namespace UvssService.Sensors;

/// <summary>The barrier hardware for one gate: a vehicle-presence sensor
/// (inductive loop or photoelectric beam) plus the boom barrier motor,
/// both on the same controller box/PLC. Replaces the old dual-loop
/// ILaneInterlock (which measured speed across 2 loops) -- this system has
/// no under-vehicle scanning to time, so there's just one presence signal
/// and an open/close command, nothing to measure.</summary>
public interface IBarrierController
{
    /// <summary>Whether this controller is actually reachable right now --
    /// for a real ("tcp") controller, whether the microcontroller currently
    /// has an open connection to this PC; a simulated controller is always
    /// online (there's no real link to lose). Drives the green/red
    /// heartbeat dot on the Gate Setup page.</summary>
    bool IsOnline { get; }

    /// <summary>Blocks until a vehicle arrives at this gate. Returns the
    /// arrival time.</summary>
    Task<DateTime> WaitForVehiclePresentAsync(CancellationToken ct);

    /// <summary>Non-blocking snapshot of whether a vehicle is physically at
    /// the gate right now -- used both for the dashboard's live status badge
    /// and to poll for the vehicle having cleared after a pass decision.</summary>
    Task<bool> IsVehiclePresentAsync(CancellationToken ct = default);

    Task OpenBarrierAsync(CancellationToken ct = default);

    Task CloseBarrierAsync(CancellationToken ct = default);
}
