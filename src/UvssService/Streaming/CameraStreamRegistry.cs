using System.Collections.Concurrent;

namespace UvssService.Streaming;

/// <summary>One MjpegBroadcaster per (gate, camera label) pair -- shared
/// between the continuous camera-capture loop that publishes frames and
/// the /stream HTTP endpoint that any number of browser &lt;img&gt; tags
/// read from.</summary>
public class CameraStreamRegistry
{
    private readonly ConcurrentDictionary<(string Gate, string Camera), MjpegBroadcaster> _broadcasters = new();

    public MjpegBroadcaster GetOrCreate(string gateName, string cameraLabel) =>
        _broadcasters.GetOrAdd((gateName, cameraLabel), _ => new MjpegBroadcaster());
}
