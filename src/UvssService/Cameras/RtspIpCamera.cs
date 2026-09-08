using OpenCvSharp;
using UvssService.Config;

namespace UvssService.Cameras;

/// <summary>A plain RTSP video stream camera -- most non-Axis IP/CCTV
/// cameras and NVRs speak RTSP rather than exposing an HTTP snapshot CGI
/// endpoint, so this is the more commonly-needed real-camera integration.
/// Builds "rtsp://user:pass@ip:port/path" (username/password omitted
/// entirely from the URL when both are blank -- many cameras allow
/// unauthenticated RTSP) and opens it once via OpenCvSharp's VideoCapture
/// (FFmpeg backend), then just reads the next buffered frame on each
/// GetSnapshotAsync call rather than reconnecting per frame.
///
/// Needs the OpenCvSharp4.runtime.win native binaries (already a project
/// dependency) to have been built with FFmpeg/RTSP support, which the
/// standard NuGet package is -- if a real camera still fails to open here,
/// the likely next step is confirming the exact RTSP path in the camera's
/// own documentation/ONVIF discovery tool, not this code.</summary>
public class RtspIpCamera : IIpCamera, IDisposable
{
    private readonly IpCameraOptions _camera;
    private readonly string _url;
    private readonly object _lock = new();
    private VideoCapture? _capture;

    public RtspIpCamera(IpCameraOptions camera, CameraDefaultsOptions defaults)
    {
        _camera = camera;
        var username = string.IsNullOrEmpty(camera.Username) ? defaults.Username : camera.Username;
        var password = string.IsNullOrEmpty(camera.Password) ? defaults.Password : camera.Password;
        _url = BuildRtspUrl(camera.Ip, camera.RtspPort, camera.RtspStreamPath, username, password);
    }

    private static string BuildRtspUrl(string ip, int port, string streamPath, string username, string password)
    {
        var host = port > 0 ? $"{ip}:{port}" : ip;
        var path = string.IsNullOrEmpty(streamPath) ? "" : (streamPath.StartsWith('/') ? streamPath : $"/{streamPath}");
        if (string.IsNullOrEmpty(username) && string.IsNullOrEmpty(password))
        {
            return $"rtsp://{host}{path}";
        }
        var userInfo = $"{Uri.EscapeDataString(username)}:{Uri.EscapeDataString(password)}";
        return $"rtsp://{userInfo}@{host}{path}";
    }

    public Task<Mat?> GetSnapshotAsync(CancellationToken ct = default)
    {
        if (!_camera.Enabled)
        {
            return Task.FromResult<Mat?>(null);
        }

        // Opening (or re-opening after a drop) blocks synchronously inside
        // OpenCvSharp/FFmpeg for up to its own internal connect timeout --
        // tens of seconds when the camera is unreachable. Run it on a
        // dedicated thread pool thread rather than inline, otherwise every
        // gate's camera loop would block the caller (including, at startup,
        // CameraStreamingHostedService's own ExecuteAsync, which would in
        // turn stall the whole host from ever getting to Kestrel) one gate
        // at a time instead of all gates failing/retrying in parallel.
        return Task.Run(() =>
        {
            lock (_lock)
            {
                if (_capture == null || !_capture.IsOpened())
                {
                    _capture?.Dispose();
                    _capture = new VideoCapture(_url, VideoCaptureAPIs.FFMPEG);
                    if (!_capture.IsOpened())
                    {
                        _capture.Dispose();
                        _capture = null;
                        throw new CameraUnavailableException($"Could not open RTSP stream for '{_camera.Name}'.");
                    }
                }

                var frame = new Mat();
                if (!_capture.Read(frame) || frame.Empty())
                {
                    frame.Dispose();
                    // Force a fresh connection attempt next call -- a dropped
                    // RTSP session doesn't reliably recover just by calling
                    // Read() again.
                    _capture.Dispose();
                    _capture = null;
                    throw new CameraUnavailableException($"RTSP stream for '{_camera.Name}' returned no frame -- will reconnect.");
                }
                return (Mat?)frame;
            }
        }, ct);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _capture?.Dispose();
            _capture = null;
        }
    }
}
