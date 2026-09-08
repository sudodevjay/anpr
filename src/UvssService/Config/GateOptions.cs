namespace UvssService.Config;

public enum GateKind { Entry, Exit }

/// <summary>One physical checkpoint gate (a boom barrier + one camera).
/// Managed entirely through the Gate Setup admin page (see
/// GateConfigStore) -- a site can have as many gates as it actually has,
/// created/removed on the spot by whoever sets the site up, same as
/// cameras/controllers/displays.</summary>
public class GateOptions
{
    public long Id { get; set; }

    /// <summary>Stable, auto-generated key (e.g. "gate3") used to look up
    /// this gate's camera/controller/display config and to key its live
    /// state, event log rows, and /stream/{gate}/video URL -- assigned once
    /// at creation and never changed afterwards (editing a gate only
    /// changes DisplayName/Kind/Enabled).</summary>
    public string Name { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public GateKind Kind { get; set; }
    public bool Enabled { get; set; } = true;
}

public class CameraDefaultsOptions
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string SnapshotPath { get; set; } = "/axis-cgi/jpg/image.cgi";
    public string SnapshotResolution { get; set; } = "1920x1080";
    public int SnapshotCompression { get; set; } = 10;
    public int SnapshotTimeoutSeconds { get; set; } = 6;

    /// <summary>How often (ms) the continuous camera-streaming loop grabs a
    /// fresh snapshot for a gate's /stream/{gate}/video MJPEG endpoint --
    /// runs for the service's whole lifetime, independent of whether a
    /// vehicle is present. Lower = smoother video, more camera/CPU load.</summary>
    public int StreamIntervalMs { get; set; } = 150;
}

public class PlateDetectorOptions
{
    public bool Enabled { get; set; } = true;
    public string ModelPath { get; set; } = "weights/plate_detector.onnx";
    public float Confidence { get; set; } = 0.4f;
}

public class PlateOcrOptions
{
    public bool Enabled { get; set; } = true;
    public string BackboneModelPath { get; set; } = "weights/ctc_backbone_and_head.onnx";
    public string DecoderModelPath { get; set; } = "weights/sar_decoder_step.onnx";
    public string EmbeddingPath { get; set; } = "weights/sar_embedding_99x512.bin";
    public string CharDictPath { get; set; } = "weights/en_dict.txt";

    public bool AggregationEnabled { get; set; } = true;
    public int TrackMaxFrames { get; set; } = 40;
    public int AggregationWindowSize { get; set; } = 10;
    public int AggregationMinCandidates { get; set; } = 7;
}

public class StorageOptions
{
    public bool Enabled { get; set; } = true;
    public string VehicleImageDir { get; set; } = "captured_vehicle";
}

public class BackendEventOptions
{
    public string EventUrl { get; set; } = "http://localhost:5003/api/anpr/events";
    public int TimeoutSeconds { get; set; } = 8;
    public string EventToken { get; set; } = "";
}

/// <summary>Per-gate runtime tuning that isn't hardware config (that lives in
/// CameraConfigStore/ControllerConfigStore instead).</summary>
public class GateDefaultsOptions
{
    /// <summary>Longest a vehicle is allowed to sit "present" before this
    /// gate's worker gives up on the pass as a sensor fault and resets to
    /// Idle -- mirrors the old MaxPassSeconds idea, just without any
    /// loop-speed measurement attached to it.</summary>
    public double MaxPassSeconds { get; set; } = 30;

    /// <summary>How long (seconds) to collect camera frames for the
    /// multi-frame plate-OCR aggregation before deciding admission and
    /// commanding the barrier -- short enough that the decision lands well
    /// before an approaching vehicle reaches the physical boom, unlike the
    /// old UVSS pipeline (which could afford to wait for the whole vehicle
    /// to fully clear the loops before finishing OCR, since there was no
    /// barrier to time against).</summary>
    public double PlateReadSeconds { get; set; } = 4;
}
