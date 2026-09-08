using UvssService.Config;
using UvssService.Ocr;

namespace UvssService.Lanes;

public enum AdmissionStatus { Approved, Pending, Denied }

/// <summary>Id is stamped by EventLogStore.Add (not the caller) so every
/// entry gets a stable identity the dashboard can select-by, independent of
/// its ever-shifting position in the newest-first list. The per-pass
/// snapshot images are carried here (not just referenced from GateState,
/// which keeps overwriting itself as the next pass runs) so clicking an
/// older row in the sidebar can show exactly what that pass looked like.
/// DriverName/Admission/DossierNotes are decided once at finalize time by
/// cross-referencing VehicleRegistryStore (see GateWorkerHostedService) --
/// changing a vehicle's registry classification later does not retroactively
/// change past log entries, same as a real access-control audit log.</summary>
public record EventLogEntry(
    DateTime Timestamp,
    string GateName,
    string GateDisplayName,
    GateKind GateKind,
    string PlateText,
    string PlateStateName,
    bool PlateTextValid,
    string DriverName,
    AdmissionStatus Admission,
    string DossierNotes,
    PlateColorCategory PlateColorCategory,
    byte[]? VehicleFrameJpeg,
    byte[]? PlateCropJpeg)
{
    public long Id { get; init; }
}

/// <summary>Today's completed-pass event log, newest first, for the
/// dashboard's log panel -- a plain in-memory ring buffer (bounded so a
/// long-running day doesn't grow unbounded); not persisted, matches the
/// "today's logs" framing (a restart naturally starts a fresh log).</summary>
public class EventLogStore
{
    private const int MaxEntries = 500;
    private readonly object _lock = new();
    private readonly LinkedList<EventLogEntry> _entries = new();
    private long _nextId = 1;

    public event Action? Changed;

    public void Add(EventLogEntry entry)
    {
        lock (_lock)
        {
            entry = entry with { Id = _nextId++ };
            _entries.AddFirst(entry);
            while (_entries.Count > MaxEntries)
            {
                _entries.RemoveLast();
            }
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<EventLogEntry> Recent(int count = 100)
    {
        lock (_lock)
        {
            return _entries.Take(count).ToList();
        }
    }

    public void Remove(long id)
    {
        lock (_lock)
        {
            var node = _entries.First;
            while (node != null)
            {
                if (node.Value.Id == id)
                {
                    _entries.Remove(node);
                    break;
                }
                node = node.Next;
            }
        }
        Changed?.Invoke();
    }
}
