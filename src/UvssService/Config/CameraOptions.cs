namespace UvssService.Config;

/// <summary>One gate's camera config. Built on the fly from a
/// CameraConfigStore entry (see CameraConfigStore.cs) -- not bound directly
/// from appsettings anymore, since cameras are managed through the Cameras
/// admin page instead.</summary>
public class IpCameraOptions
{
    public string Name { get; set; } = "";
    public string Ip { get; set; } = "";
    public bool Enabled { get; set; } = false;

    /// <summary>"axis" for a real on-site Axis snapshot camera (HTTP
    /// snapshot CGI, polled once per frame); "rtsp" for any camera exposing
    /// a plain RTSP video stream (opened once, read continuously -- the more
    /// common case for non-Axis IP/CCTV cameras); "simulated" for local
    /// dev/testing without hardware (cycles through TestImageDir).</summary>
    public string Source { get; set; } = "axis";
    public string TestImageDir { get; set; } = "";

    /// <summary>Per-camera credentials -- falls back to CameraDefaultsOptions'
    /// Username/Password when left blank, so a site with one shared
    /// camera login doesn't have to repeat it per camera, but a camera with
    /// its own separate login still can.</summary>
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";

    /// <summary>"rtsp" source only -- the stream's TCP port (554 is the RTSP
    /// default) and path, e.g. "/cam/realmonitor?channel=1&amp;subtype=0"
    /// for a typical Dahua/Hikvision-style NVR, or "/stream1" for many
    /// generic ONVIF cameras -- check the camera/NVR's own documentation for
    /// its exact path.</summary>
    public int RtspPort { get; set; } = 554;
    public string RtspStreamPath { get; set; } = "/";
}
