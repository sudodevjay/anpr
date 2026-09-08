using System.Collections.Concurrent;
using System.IO.Ports;

namespace UvssService.Sensors;

/// <summary>Every vehicle-number display board at a site shares one
/// USB-RS485 bus (wired A-to-A, B-to-B across every board) through a single
/// USB-serial adapter -- so this keeps one open SerialPort per distinct COM
/// port string (not one per gate/display), reused across every
/// DisplayConfigEntry that names the same port, and addresses an individual
/// board on that shared bus by its own DisplayId.
///
/// Wire protocol (the display boards' real, pipe-delimited command set):
///   "|C|&lt;id&gt;|4|1|0-0-#&lt;text&gt;|"  -- show `text` on display `id`.
///   "|C|&lt;id&gt;|6|"                     -- clear display `id`.
/// Every board on the shared bus sees every command; only the one whose own
/// configured id matches is expected to act on it.</summary>
public class VehicleDisplayBus
{
    private const int BaudRate = 9600;

    private readonly ConcurrentDictionary<string, SerialPort> _ports = new();
    private readonly object _lock = new();

    // Always upper-case on the wire -- these boards show plate numbers,
    // which read as shouting-case on real Indian plates anyway, and a
    // caller passing lowercase (or mixed case, e.g. a plain test message)
    // shouldn't have to remember to convert it itself.
    public void Send(string comPort, int displayId, string text) =>
        WriteCommand(comPort, displayId, $"|C|{displayId}|4|1|0-0-#{text.ToUpperInvariant()}|");

    public void Clear(string comPort, int displayId) =>
        WriteCommand(comPort, displayId, $"|C|{displayId}|6|");

    private void WriteCommand(string comPort, int displayId, string command)
    {
        if (string.IsNullOrWhiteSpace(comPort))
        {
            return;
        }
        try
        {
            var port = GetOrOpenPort(comPort);
            port.Write(command);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Vehicle display bus: failed to send to '{comPort}' (display {displayId}) -- {ex.Message}");
            // The port may have been unplugged/gone bad -- drop it so the
            // next call retries opening it fresh instead of repeatedly
            // failing on a dead handle.
            if (_ports.TryRemove(comPort, out var stale))
            {
                stale.Dispose();
            }
        }
    }

    /// <summary>Tries to open (or confirm already-open) the given COM port
    /// without sending anything -- used by the Gate Setup page's live
    /// online/offline heartbeat, so a display shows a real status even
    /// before any vehicle has triggered a Send yet.</summary>
    public bool EnsureOpen(string comPort)
    {
        if (string.IsNullOrWhiteSpace(comPort))
        {
            return false;
        }
        try
        {
            GetOrOpenPort(comPort);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public bool IsPortOpen(string comPort) =>
        !string.IsNullOrWhiteSpace(comPort) && _ports.TryGetValue(comPort, out var port) && port.IsOpen;

    private SerialPort GetOrOpenPort(string comPort)
    {
        if (_ports.TryGetValue(comPort, out var existing) && existing.IsOpen)
        {
            return existing;
        }

        lock (_lock)
        {
            if (_ports.TryGetValue(comPort, out existing) && existing.IsOpen)
            {
                return existing;
            }
            var port = new SerialPort(comPort, BaudRate);
            port.Open();
            _ports[comPort] = port;
            Console.WriteLine($"Vehicle display bus: opened {comPort}.");
            return port;
        }
    }
}
