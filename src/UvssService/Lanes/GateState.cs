using UvssService.Config;
using UvssService.Ocr;

namespace UvssService.Lanes;

/// <summary>Live, observable state for one gate -- GateWorkerHostedService
/// publishes to this, Home.razor subscribes to `Changed` and re-renders.
/// Replaces LaneState: drops everything that only made sense for
/// under-vehicle scanning (the growing scan image, foreign-object
/// detections, loop-pair speed/line-count, the driver camera frame) in
/// favour of a single vehicle-facing camera frame and barrier state.</summary>
public class GateState
{
    public string GateName { get; }
    public string GateDisplayName { get; }
    public GateKind GateKind { get; }

    public string Status { get; private set; } = "Idle";
    public bool VehiclePresent { get; private set; }
    public bool BarrierOpen { get; private set; }

    /// <summary>Live green/red heartbeat for the Gate Setup page -- whether
    /// this gate's barrier controller is actually reachable right now
    /// (refreshed continuously by GateWorkerHostedService's controller
    /// status poller, independent of Idle/Active pass state).</summary>
    public bool ControllerOnline { get; private set; }

    /// <summary>Live green/red heartbeat for the Gate Setup page's Vehicle
    /// Number Displays table -- whether this gate's display's COM port is
    /// currently open (refreshed continuously, independent of whether a
    /// vehicle has actually triggered a Send yet).</summary>
    public bool DisplayOnline { get; private set; }

    /// <summary>The gate camera's current live frame.</summary>
    public byte[]? VehicleFrameJpeg { get; private set; }

    /// <summary>The vehicle frame frozen at the moment the LAST completed
    /// pass started -- shown while idle instead of the raw live feed, which
    /// (in simulation) keeps cycling to a different stock vehicle the
    /// instant a pass ends and would otherwise show a mismatched vehicle
    /// next to PlateText, which is also from that same last pass.</summary>
    public byte[]? LastPassVehicleFrameJpeg { get; private set; }

    public string PlateText { get; private set; } = "";
    public float PlateConfidence { get; private set; }
    public bool PlateOcrAvailable { get; private set; }
    public byte[]? PlateCropJpeg { get; private set; }
    public string PlateStateCode { get; private set; } = "";
    public string PlateStateName { get; private set; } = "";
    public bool PlateTextValid { get; private set; }
    public int AggregationTextVotes { get; private set; }
    public int AggregationWindowSize { get; private set; }
    public bool AggregationMinCandidatesMet { get; private set; }
    public PlateColorCategory PlateColorCategory { get; private set; } = PlateColorCategory.Unknown;

    public event Action? Changed;

    public GateState(string gateName, string gateDisplayName, GateKind gateKind)
    {
        GateName = gateName;
        GateDisplayName = gateDisplayName;
        GateKind = gateKind;
    }

    public void SetStatus(string status)
    {
        Status = status;
        Notify();
    }

    public void SetVehiclePresent(bool present)
    {
        if (VehiclePresent == present)
        {
            return;
        }
        VehiclePresent = present;
        Notify();
    }

    public void SetBarrierOpen(bool open)
    {
        BarrierOpen = open;
        Notify();
    }

    public void SetControllerOnline(bool online)
    {
        if (ControllerOnline == online)
        {
            return;
        }
        ControllerOnline = online;
        Notify();
    }

    public void SetDisplayOnline(bool online)
    {
        if (DisplayOnline == online)
        {
            return;
        }
        DisplayOnline = online;
        Notify();
    }

    public void SetVehicleFrame(byte[]? jpeg)
    {
        VehicleFrameJpeg = jpeg;
        Notify();
    }

    public void SetLastPassVehicleFrame(byte[]? jpeg)
    {
        LastPassVehicleFrameJpeg = jpeg;
        Notify();
    }

    public void SetPlateResult(
        string text, float confidence, bool ocrAvailable, string stateCode = "", string stateName = "",
        bool textValid = false, int textVotes = 0, int windowSize = 0, bool minCandidatesMet = false,
        byte[]? plateCropJpeg = null, PlateColorCategory colorCategory = PlateColorCategory.Unknown)
    {
        PlateText = text;
        PlateConfidence = confidence;
        PlateOcrAvailable = ocrAvailable;
        PlateCropJpeg = plateCropJpeg;
        PlateStateCode = stateCode;
        PlateStateName = stateName;
        PlateTextValid = textValid;
        AggregationTextVotes = textVotes;
        AggregationWindowSize = windowSize;
        AggregationMinCandidatesMet = minCandidatesMet;
        PlateColorCategory = colorCategory;
        Notify();
    }

    private void Notify() => Changed?.Invoke();
}

/// <summary>Registry so Blazor pages can look up a gate's state by name.</summary>
public class GateStateStore
{
    private readonly Dictionary<string, GateState> _states = new();

    public GateState GetOrCreate(string gateName, string gateDisplayName, GateKind gateKind)
    {
        if (!_states.TryGetValue(gateName, out var state))
        {
            state = new GateState(gateName, gateDisplayName, gateKind);
            _states[gateName] = state;
        }
        return state;
    }

    public GateState? Find(string gateName) => _states.TryGetValue(gateName, out var state) ? state : null;

    public IReadOnlyCollection<GateState> All => _states.Values;
}
