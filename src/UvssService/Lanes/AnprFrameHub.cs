using System.Collections.Concurrent;
using OpenCvSharp;

namespace UvssService.Lanes;

/// <summary>Lets GateWorkerHostedService "tap into" a gate's camera's
/// continuous frame stream (owned by CameraStreamingHostedService, which
/// runs regardless of whether a vehicle is present) only for the duration
/// of an Active pass -- each frame published while subscribed feeds that
/// pass's PlateTrack. The camera itself never starts/stops per pass;
/// only this subscription does.</summary>
public class AnprFrameHub
{
    private readonly ConcurrentDictionary<string, Action<Mat>> _subscribers = new();

    public void Subscribe(string gateName, Action<Mat> handler) => _subscribers[gateName] = handler;

    public void Unsubscribe(string gateName) => _subscribers.TryRemove(gateName, out _);

    public void Publish(string gateName, Mat frame)
    {
        if (_subscribers.TryGetValue(gateName, out var handler))
        {
            handler(frame);
        }
    }
}
