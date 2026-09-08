using System.Globalization;
using Microsoft.Extensions.Options;
using UvssService.Config;
using UvssService.Detection;
using UvssService.Licensing;
using UvssService.Ocr;
using UvssService.Sensors;
using UvssService.Storage;

namespace UvssService.Lanes;

/// <summary>Per-gate ANPR access-control state machine, one background Task
/// per configured gate:
///
///   Idle --vehicle arrives--> Active --plate read + admission decided-->
///     (barrier opens if Approved) --vehicle clears--> (barrier closes) --> Idle
///
/// Replaces LaneWorkerHostedService's UVSS pass pipeline (line-scan capture,
/// foreign-object detection, dual-loop speed measurement, driver-face
/// tracking) entirely -- this only ever needs one camera's frames (for plate
/// OCR) and one barrier controller (presence sensor + open/close) per gate.
///
/// Unlike the old UVSS pipeline (which ran its OCR aggregation in the
/// background only once the vehicle had fully left, since there was no
/// barrier to time against), the plate read here has to finish and the
/// barrier decision has to land BEFORE the vehicle reaches the physical
/// boom -- so PlateReadSeconds bounds that window, and only the
/// logging/storage/webhook side effects (which don't affect the barrier)
/// run in the background.</summary>
public class GateWorkerHostedService : BackgroundService
{
    private readonly GateStateStore _stateStore;
    private readonly GateDefaultsOptions _gateDefaults;
    private readonly StorageOptions _storage;
    private readonly BackendEventOptions _backendEvent;
    private readonly List<GateOptions> _gates;
    private readonly IPlateDetector _plateDetector;
    private readonly PlateCandidateSearch _plateSearch;
    private readonly PlateOcrOptions _plateOcrOptions;
    private readonly AnprFrameHub _anprFrameHub;
    private readonly EventLogStore _eventLog;
    private readonly VehicleRegistryStore _vehicleRegistry;
    private readonly ControllerConfigStore _controllerConfigStore;
    private readonly BarrierControllerRegistry _barrierControllerRegistry;
    private readonly DisplayConfigStore _displayConfigStore;
    private readonly DisplayBusSettingsStore _displayBusSettings;
    private readonly VehicleDisplayBus _displayBus;
    private readonly LicenseStore _licenseStore;
    private readonly HttpClient _httpClient = new();

    public GateWorkerHostedService(
        GateConfigStore gateConfigStore,
        IOptions<GateDefaultsOptions> gateDefaults,
        IOptions<StorageOptions> storage,
        IOptions<BackendEventOptions> backendEvent,
        IOptions<PlateOcrOptions> plateOcrOptions,
        IPlateDetector plateDetector,
        PlateCandidateSearch plateSearch,
        AnprFrameHub anprFrameHub,
        EventLogStore eventLog,
        VehicleRegistryStore vehicleRegistry,
        ControllerConfigStore controllerConfigStore,
        BarrierControllerRegistry barrierControllerRegistry,
        DisplayConfigStore displayConfigStore,
        DisplayBusSettingsStore displayBusSettings,
        VehicleDisplayBus displayBus,
        LicenseStore licenseStore,
        GateStateStore stateStore)
    {
        _licenseStore = licenseStore;
        _stateStore = stateStore;
        _gateDefaults = gateDefaults.Value;
        _storage = storage.Value;
        _backendEvent = backendEvent.Value;
        _plateOcrOptions = plateOcrOptions.Value;
        _gates = gateConfigStore.All.Where(g => g.Enabled).ToList();
        _plateDetector = plateDetector;
        _plateSearch = plateSearch;
        _anprFrameHub = anprFrameHub;
        _eventLog = eventLog;
        _vehicleRegistry = vehicleRegistry;
        _controllerConfigStore = controllerConfigStore;
        _barrierControllerRegistry = barrierControllerRegistry;
        _displayConfigStore = displayConfigStore;
        _displayBusSettings = displayBusSettings;
        _displayBus = displayBus;
        _httpClient.Timeout = TimeSpan.FromSeconds(_backendEvent.TimeoutSeconds);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_gates.Count == 0)
        {
            Console.WriteLine("No enabled gates found.");
            return;
        }

        var runtimes = _gates.Select(BuildRuntime).ToList();
        Console.WriteLine($"Running {runtimes.Count} gate worker(s).");
        var passTasks = runtimes.Select(gate => RunGateAsync(gate, stoppingToken));
        var controllerStatusTasks = runtimes.Select(gate => PollControllerStatusAsync(gate, stoppingToken));
        var displayStatusTask = PollDisplayBusStatusAsync(runtimes, _displayBus, _displayBusSettings, stoppingToken);
        await Task.WhenAll(passTasks.Concat(controllerStatusTasks).Append(displayStatusTask));
    }

    /// <summary>How often the Gate Setup page's online/offline heartbeats
    /// refresh -- run independently of the Idle/Active pass state machine,
    /// so a controller/display that's actually disconnected shows red even
    /// while idle.</summary>
    private static readonly TimeSpan ControllerStatusPollInterval = TimeSpan.FromMilliseconds(500);

    private static async Task PollControllerStatusAsync(GateRuntime gate, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            gate.State.SetControllerOnline(gate.Controller.IsOnline);
            try
            {
                await Task.Delay(ControllerStatusPollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Unlike the barrier controller (which keeps a persistent
    /// connection open on its own), the display bus only opens the shared COM
    /// port lazily on the first Send -- so this proactively tries to
    /// open/confirm it too, otherwise a display with no vehicle activity yet
    /// would show "offline" even when it's actually fine. Every display
    /// shares the same physical bus/COM port, so this checks it once per
    /// cycle and applies the same result to every enabled display -- polling
    /// per-gate would let concurrent checks race and show different displays
    /// on the same wire with different statuses at the same instant.</summary>
    private static async Task PollDisplayBusStatusAsync(
        List<GateRuntime> gates, VehicleDisplayBus displayBus, DisplayBusSettingsStore displayBusSettings, CancellationToken ct)
    {
        var displayGates = gates.Where(g => g.Display is { Enabled: true }).ToList();
        if (displayGates.Count == 0)
        {
            return;
        }
        while (!ct.IsCancellationRequested)
        {
            var comPort = displayBusSettings.ComPort;
            var online = displayBus.EnsureOpen(comPort) && displayBus.IsPortOpen(comPort);
            foreach (var gate in displayGates)
            {
                gate.State.SetDisplayOnline(online);
            }
            try
            {
                await Task.Delay(ControllerStatusPollInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private GateRuntime BuildRuntime(GateOptions gate)
    {
        var controllerConfig = _controllerConfigStore.FindByGate(gate.Name);
        var controller = BarrierControllerFactory.Create(gate.Name, controllerConfig);
        _barrierControllerRegistry.Register(gate.Name, controller);
        return new GateRuntime
        {
            Name = gate.Name,
            DisplayName = gate.DisplayName,
            Kind = gate.Kind,
            State = _stateStore.GetOrCreate(gate.Name, gate.DisplayName, gate.Kind),
            Controller = controller,
            Display = _displayConfigStore.FindByGate(gate.Name),
            MaxPassSeconds = _gateDefaults.MaxPassSeconds,
            PlateReadSeconds = _gateDefaults.PlateReadSeconds,
            StorageEnabled = _storage.Enabled,
            VehicleImageDir = ResolvePath(_storage.VehicleImageDir),
            PlateDetector = _plateDetector,
            PlateSearch = _plateSearch,
            TrackMaxFrames = _plateOcrOptions.TrackMaxFrames,
            AggregationEnabled = _plateOcrOptions.AggregationEnabled,
            AggregationWindowSize = _plateOcrOptions.AggregationWindowSize,
            AggregationMinCandidates = _plateOcrOptions.AggregationMinCandidates,
        };
    }

    private async Task RunGateAsync(GateRuntime gate, CancellationToken ct)
    {
        Console.WriteLine($"[{gate.Name}] gate worker started.");
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await RunOnePassAsync(gate, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{gate.Name}] pass failed: {ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(1), ct).ContinueWith(_ => { });
            }
        }
    }

    private async Task RunOnePassAsync(GateRuntime gate, CancellationToken ct)
    {
        var entryTime = await gate.Controller.WaitForVehiclePresentAsync(ct);
        if (ct.IsCancellationRequested)
        {
            return;
        }

        Console.WriteLine($"[{gate.Name}] vehicle present.");
        gate.State.SetStatus("Active");
        gate.State.SetVehiclePresent(true);

        // Freeze the camera's current frame right at arrival -- the camera
        // streams continuously and independently of this pass (see
        // CameraStreamingHostedService), so by the time the plate read/log
        // finishes, it may already show a different vehicle otherwise.
        var passVehicleJpeg = gate.State.VehicleFrameJpeg;

        var plateTrack = new PlateTrack(gate.PlateDetector, gate.TrackMaxFrames);
        _anprFrameHub.Subscribe(gate.Name, frame => plateTrack.AddFrame(frame));

        await WaitForPlateReadWindowAsync(gate, entryTime, ct);
        _anprFrameHub.Unsubscribe(gate.Name);

        var plateResult = RecognizePlateAcrossTrack(gate, plateTrack);
        plateTrack.Reset();

        if (gate.Display is { Enabled: true } display && !string.IsNullOrEmpty(plateResult.PlateText))
        {
            _displayBus.Send(_displayBusSettings.ComPort, display.DisplayId, plateResult.PlateText);
        }

        var (driverName, admission, dossierNotes) = DecideAdmission(plateResult.PlateText);
        if (admission == AdmissionStatus.Approved && !_licenseStore.IsValid)
        {
            Console.WriteLine($"[{gate.Name}] {_licenseStore.Status.Message} -- barrier kept closed regardless of admission.");
            admission = AdmissionStatus.Denied;
            dossierNotes = string.IsNullOrEmpty(dossierNotes)
                ? $"License not valid -- {_licenseStore.Status.Message}"
                : $"{dossierNotes} | License not valid -- {_licenseStore.Status.Message}";
        }
        if (admission == AdmissionStatus.Approved)
        {
            Console.WriteLine($"[{gate.Name}] plate '{plateResult.PlateText}' approved -- opening barrier.");
            await gate.Controller.OpenBarrierAsync(ct);
            gate.State.SetBarrierOpen(true);
        }
        else
        {
            var shown = string.IsNullOrEmpty(plateResult.PlateText) ? "(unread)" : plateResult.PlateText;
            Console.WriteLine($"[{gate.Name}] plate '{shown}' {admission.ToString().ToLowerInvariant()} -- barrier stays closed.");
        }

        // Logging/storage/webhook side effects don't affect the barrier --
        // run them in the background so this gate is free to notice the
        // NEXT vehicle (once this one clears) without waiting on them.
        _ = LogAndNotifyInBackgroundAsync(gate, plateResult, driverName, admission, dossierNotes, passVehicleJpeg);

        var cleared = await WaitForVehicleClearAsync(gate, entryTime, ct);
        if (admission == AdmissionStatus.Approved)
        {
            await gate.Controller.CloseBarrierAsync(ct);
            gate.State.SetBarrierOpen(false);
        }
        if (!cleared)
        {
            Console.WriteLine($"[{gate.Name}] vehicle never cleared within {gate.MaxPassSeconds}s -- stuck sensor, or vehicle stopped on/over it.");
        }

        gate.State.SetVehiclePresent(false);
        gate.State.SetStatus("Idle");
    }

    private static async Task WaitForPlateReadWindowAsync(GateRuntime gate, DateTime entryTime, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var elapsed = (DateTime.UtcNow - entryTime).TotalSeconds;
            if (elapsed >= gate.PlateReadSeconds)
            {
                return;
            }
            if (!await gate.Controller.IsVehiclePresentAsync(ct))
            {
                // Unusually fast vehicle -- already gone, decide with
                // whatever frames were captured.
                return;
            }
            await Task.Delay(150, ct).ContinueWith(_ => { });
        }
    }

    private static async Task<bool> WaitForVehicleClearAsync(GateRuntime gate, DateTime entryTime, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var elapsed = (DateTime.UtcNow - entryTime).TotalSeconds;
            if (elapsed >= gate.MaxPassSeconds)
            {
                return false;
            }
            if (!await gate.Controller.IsVehiclePresentAsync(ct))
            {
                return true;
            }
            await Task.Delay(150, ct).ContinueWith(_ => { });
        }
        return false;
    }

    private async Task LogAndNotifyInBackgroundAsync(
        GateRuntime gate, AggregatedPlateResult plateResult, string driverName, AdmissionStatus admission,
        string dossierNotes, byte[]? passVehicleJpeg)
    {
        try
        {
            gate.State.SetLastPassVehicleFrame(passVehicleJpeg);
            gate.State.SetPlateResult(
                plateResult.PlateText, (float)plateResult.PlateConfidence, gate.PlateDetector.Available,
                plateResult.PlateStateCode, plateResult.PlateStateName, plateResult.PlateTextValid,
                plateResult.AggregationTextVotes, plateResult.AggregationWindowSize, plateResult.AggregationMinCandidatesMet,
                plateResult.PlateCropJpeg, plateResult.PlateColorCategory);

            if (!string.IsNullOrEmpty(plateResult.PlateText))
            {
                Console.WriteLine(
                    $"[{gate.Name}] plate read: {plateResult.PlateText} (confidence={plateResult.PlateConfidence:F2}, "
                    + $"votes={plateResult.AggregationTextVotes}/{plateResult.AggregationWindowSize}, valid={plateResult.PlateTextValid}, "
                    + $"plate color={plateResult.PlateColorCategory} [{UvssService.Ocr.PlateColorClassifier.CategoryLabel(plateResult.PlateColorCategory)}])");
            }

            if (gate.StorageEnabled)
            {
                ImageStorage.SaveBytes(gate.VehicleImageDir, gate.Name, passVehicleJpeg);
            }

            _eventLog.Add(new EventLogEntry(
                DateTime.Now, gate.Name, gate.DisplayName, gate.Kind,
                plateResult.PlateText, plateResult.PlateStateName, plateResult.PlateTextValid,
                driverName, admission, dossierNotes, plateResult.PlateColorCategory, passVehicleJpeg, plateResult.PlateCropJpeg));

            await PostEventAsync(gate, admission, plateResult, passVehicleJpeg);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{gate.Name}] background finalize failed: {ex.Message}");
        }
    }

    /// <summary>Exact same C# port of anpr-ai-service's multi-frame
    /// plate-OCR aggregation used by the old UVSS pipeline -- takes the best
    /// AggregationWindowSize frames PlateTrack collected during the read
    /// window, runs the full single-frame pipeline on each, then combines
    /// the per-frame reads via per-character consensus voting.</summary>
    private static AggregatedPlateResult RecognizePlateAcrossTrack(GateRuntime gate, PlateTrack plateTrack)
    {
        if (!gate.AggregationEnabled || plateTrack.FrameCount == 0)
        {
            return new AggregatedPlateResult();
        }

        var selectedFrames = plateTrack.SelectedFrames(gate.AggregationWindowSize);
        var results = PlateFrameProcessor.ProcessFrameBatch(selectedFrames, gate.PlateDetector, gate.PlateSearch);
        return PlateAggregation.SelectAggregatedResult(results, gate.AggregationMinCandidates);
    }

    private async Task PostEventAsync(
        GateRuntime gate, AdmissionStatus admission, AggregatedPlateResult plateResult, byte[]? vehicleJpeg)
    {
        try
        {
            using var content = new MultipartFormDataContent
            {
                { new StringContent(gate.Name), "gate_name" },
                { new StringContent(gate.Kind.ToString()), "gate_kind" },
                { new StringContent(plateResult.PlateText), "plate_text" },
                { new StringContent(plateResult.PlateConfidence.ToString(CultureInfo.InvariantCulture)), "plate_confidence" },
                { new StringContent(plateResult.PlateStateCode), "plate_state_code" },
                { new StringContent(plateResult.PlateStateName), "plate_state_name" },
                { new StringContent(plateResult.PlateTextValid.ToString().ToLowerInvariant()), "plate_text_valid" },
                { new StringContent(admission.ToString()), "admission" },
                { new StringContent(plateResult.AggregationTextVotes.ToString(CultureInfo.InvariantCulture)), "aggregation_text_votes" },
                { new StringContent(plateResult.AggregationWindowSize.ToString(CultureInfo.InvariantCulture)), "aggregation_window_size" },
            };
            if (vehicleJpeg is { } jpeg)
            {
                content.Add(new ByteArrayContent(jpeg), "vehicle", $"{gate.Name}_vehicle.jpg");
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, _backendEvent.EventUrl) { Content = content };
            if (!string.IsNullOrEmpty(_backendEvent.EventToken))
            {
                request.Headers.Add("X-ANPR-Event-Token", _backendEvent.EventToken);
            }
            using var response = await _httpClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{gate.Name}] failed to post ANPR event: {ex.Message}");
        }
    }

    /// <summary>Cross-references the read plate against VehicleRegistryStore
    /// to decide the barrier/audit-log verdict. Unlike the old UVSS pipeline
    /// (which left an unregistered plate "Pending" for a human, since a
    /// closed barrier wasn't at stake), this system's whole point is that
    /// only registered vehicles may pass -- so anything that isn't an
    /// explicit Whitelist entry is Denied outright (a Blacklist entry is
    /// Denied with a stronger note); there is no manual/pending
    /// operator-review flow.</summary>
    private (string DriverName, AdmissionStatus Admission, string DossierNotes) DecideAdmission(string plateText)
    {
        var match = _vehicleRegistry.FindByPlate(plateText);
        var driverName = match?.OwnerName is { Length: > 0 } name ? name : "Unknown";
        var registryNote = match?.Notes ?? "";
        var classification = match?.Classification ?? VehicleClassification.Normal;

        if (classification == VehicleClassification.Blacklist)
        {
            return (driverName, AdmissionStatus.Denied, $"CRITICAL ALERT: Blacklisted vehicle! {registryNote}".Trim());
        }
        if (classification == VehicleClassification.Whitelist)
        {
            return (driverName, AdmissionStatus.Approved, registryNote);
        }
        // No canned "vehicle not in registry" filler here -- the plate's
        // colour category (see PlateColorClassifier) is the useful
        // per-vehicle signal shown instead; DossierNotes only carries a
        // genuine operator-written registry note, if there is one.
        return (driverName, AdmissionStatus.Denied, registryNote);
    }

    private static string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);

    private class GateRuntime
    {
        public required string Name { get; init; }
        public required string DisplayName { get; init; }
        public required GateKind Kind { get; init; }
        public required GateState State { get; init; }
        public required IBarrierController Controller { get; init; }
        public DisplayConfigEntry? Display { get; init; }
        public required double MaxPassSeconds { get; init; }
        public required double PlateReadSeconds { get; init; }
        public required bool StorageEnabled { get; init; }
        public required string VehicleImageDir { get; init; }
        public required IPlateDetector PlateDetector { get; init; }
        public required PlateCandidateSearch PlateSearch { get; init; }
        public required int TrackMaxFrames { get; init; }
        public required bool AggregationEnabled { get; init; }
        public required int AggregationWindowSize { get; init; }
        public required int AggregationMinCandidates { get; init; }
    }
}
