using System.Net.Sockets;
using System.Text;

namespace UvssService.Sensors;

/// <summary>Real on-site barrier controller: a small microcontroller board
/// (e.g. ESP32/STM32) with its own static IP on the LAN -- same idea as an
/// IP camera, one device = one IP -- running a small TCP server on a fixed
/// port that reads a vehicle-presence sensor and drives the boom barrier's
/// motor. This PC connects OUT to the controller's IP (same role as this
/// codebase's IP cameras), reconnecting automatically in the background if
/// the link drops or was never up yet when this service started:
///
///   Inbound (controller -> PC), line-based ASCII, \n-terminated:
///     "STATE &lt;0|1&gt;\n"   -- 1 = a vehicle is currently over the presence
///     sensor. Sent on connect, on any change, and periodically as a
///     heartbeat.
///
///   Outbound (PC -> controller):
///     "BARRIER OPEN\n" / "BARRIER CLOSE\n" -- sent by Open/CloseBarrierAsync.</summary>
public class TcpBarrierController : IBarrierController, IDisposable
{
    /// <summary>Every controller board listens on this same fixed port --
    /// devices are told apart by their own IP address, not by port, so
    /// there's nothing per-gate to configure here.</summary>
    public const int DefaultPort = 6000;

    /// <summary>A presence sensor can chatter for a few ms as a vehicle's
    /// front edge arrives -- debounced PC-side so the controller firmware
    /// stays simple. Not user-configurable (no real reason a site would
    /// need to tune this).</summary>
    private const int DebounceMs = 50;

    /// <summary>A bare TCP connect can succeed against anything listening on
    /// that IP:port -- a router, another device on the LAN, even nothing
    /// real at all on some networks -- so it's not proof an actual barrier
    /// controller answered. Online is only declared once the device speaks
    /// its "STATE 0|1" handshake; if nothing valid arrives within this
    /// window, the connection is dropped and retried as if it never
    /// connected.</summary>
    private const int HandshakeTimeoutMs = 3000;

    private readonly string _gateName;
    private readonly string _ip;
    private readonly int _port;
    private readonly object _lock = new();
    private readonly CancellationTokenSource _cts = new();
    private volatile bool _vehiclePresent;
    private volatile bool _isOnline;
    private StreamWriter? _writer;

    public bool IsOnline => _isOnline;

    public TcpBarrierController(string gateName, string ip, int port)
    {
        _gateName = gateName;
        _ip = ip;
        _port = port > 0 ? port : DefaultPort;
        _ = ConnectLoopAsync(_cts.Token);
    }

    private async Task ConnectLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(_ip, _port, ct);
                Console.WriteLine($"[{_gateName}] barrier controller: TCP connected to {_ip}:{_port}, waiting for handshake...");
                await HandleConnectionAsync(client, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{_gateName}] barrier controller: connect to {_ip}:{_port} failed -- {ex.Message}. Retrying...");
            }

            _isOnline = false;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(3), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var writer = new StreamWriter(stream, Encoding.ASCII) { AutoFlush = true, NewLine = "\n" };
        lock (_lock)
        {
            _writer = writer;
        }

        try
        {
            // Prove this is actually a barrier controller, not just something
            // that happened to accept the TCP connection, before showing
            // ONLINE anywhere.
            using var handshakeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            handshakeCts.CancelAfter(HandshakeTimeoutMs);
            string? firstLine;
            try
            {
                firstLine = await reader.ReadLineAsync(handshakeCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"[{_gateName}] barrier controller: connected to {_ip}:{_port} but no STATE handshake within {HandshakeTimeoutMs}ms -- treating as offline.");
                return;
            }
            if (firstLine == null || !HandleLine(firstLine))
            {
                Console.WriteLine($"[{_gateName}] barrier controller: {_ip}:{_port} did not speak the expected protocol -- treating as offline.");
                return;
            }

            _isOnline = true;
            Console.WriteLine($"[{_gateName}] barrier controller online at {_ip}:{_port}.");

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line == null)
                {
                    break;
                }
                HandleLine(line);
            }
        }
        finally
        {
            lock (_lock)
            {
                if (ReferenceEquals(_writer, writer))
                {
                    _writer = null;
                }
            }
            _vehiclePresent = false;
            _isOnline = false;
            Console.WriteLine($"[{_gateName}] barrier controller disconnected from {_ip}:{_port}.");
        }
    }

    /// <returns>true if the line was a valid "STATE 0|1" message.</returns>
    private bool HandleLine(string line)
    {
        var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("STATE", StringComparison.OrdinalIgnoreCase)
            || (parts[1] != "0" && parts[1] != "1"))
        {
            return false;
        }
        _vehiclePresent = parts[1] == "1";
        return true;
    }

    public async Task<DateTime> WaitForVehiclePresentAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (await ConfirmedPresentAsync(ct))
            {
                return DateTime.UtcNow;
            }
        }
        ct.ThrowIfCancellationRequested();
        return DateTime.UtcNow;
    }

    private async Task<bool> ConfirmedPresentAsync(CancellationToken ct)
    {
        if (!_vehiclePresent)
        {
            await Task.Delay(50, ct);
            return false;
        }
        const int pollMs = 10;
        var elapsedMs = 0;
        while (elapsedMs < DebounceMs)
        {
            await Task.Delay(pollMs, ct);
            elapsedMs += pollMs;
            if (!_vehiclePresent)
            {
                return false;
            }
        }
        return _vehiclePresent;
    }

    public Task<bool> IsVehiclePresentAsync(CancellationToken ct = default) => Task.FromResult(_vehiclePresent);

    public Task OpenBarrierAsync(CancellationToken ct = default) => SendCommandAsync("BARRIER OPEN");

    public Task CloseBarrierAsync(CancellationToken ct = default) => SendCommandAsync("BARRIER CLOSE");

    private async Task SendCommandAsync(string command)
    {
        StreamWriter? writer;
        lock (_lock)
        {
            writer = _writer;
        }
        if (writer == null)
        {
            Console.WriteLine($"[{_gateName}] barrier controller: no connection -- can't send '{command}'.");
            return;
        }
        try
        {
            await writer.WriteLineAsync(command);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{_gateName}] barrier controller: failed to send '{command}' -- {ex.Message}");
        }
    }

    public void Dispose() => _cts.Cancel();
}
