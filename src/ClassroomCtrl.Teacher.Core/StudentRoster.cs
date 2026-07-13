using ClassroomCtrl.Shared.Protocol;
using ClassroomCtrl.Teacher.Services;

namespace ClassroomCtrl.Teacher.Core;

/// <summary>
/// TT-1-D (macOS port) — the Teacher-side live student list, keyed by the
/// student's app-level <c>EndpointId</c>. Consumes <see cref="ControlServer"/>'s
/// StudentJoined / StudentLeft events (which, post-TT-1-D, both carry the
/// EndpointId) and maintains the authoritative roster.
///
/// This is the correctness centerpiece of TT-1. The shipped Windows Teacher's
/// roster looked a leaving student up by <c>EndpointId == peerId</c> — a compare
/// across two DIFFERENT id namespaces that never matched — then fell back to
/// <c>RemoveAt(Count-1)</c>, dropping the LAST tile instead of the one that left
/// (invisible at 1–2 seats; wrong-student-greys-out at 50). Here EVERY operation
/// is by EndpointId and there is NO positional fallback.
///
/// UI-agnostic on purpose: the future Avalonia.Teacher ViewModel wraps this in an
/// ObservableCollection by subscribing to StudentAdded / StudentRemoved.
/// </summary>
public sealed class StudentRoster
{
    public sealed record Entry(Guid EndpointId, string DisplayName, string MachineName);

    private readonly Dictionary<Guid, Entry> _byId = new();
    private readonly object _gate = new();

    /// <summary>Fired when a student joins or reconnects (the entry may be an update).</summary>
    public event EventHandler<Entry>? StudentAdded;
    /// <summary>Fired with the EndpointId when a student is removed.</summary>
    public event EventHandler<Guid>? StudentRemoved;

    public StudentRoster(ControlServer server)
    {
        server.StudentJoined += OnJoined;
        server.StudentLeft += OnLeft;
    }

    private void OnJoined(object? sender, HelloMessage h)
    {
        var entry = new Entry(h.EndpointId, h.DisplayName, h.MachineName);
        lock (_gate) _byId[h.EndpointId] = entry;   // add, or update on reconnect
        StudentAdded?.Invoke(this, entry);
    }

    private void OnLeft(object? sender, Guid endpointId)
    {
        bool removed;
        lock (_gate) removed = _byId.Remove(endpointId);
        // Only signal a removal if the endpoint was actually present. The
        // transport already suppresses a stale-socket leave for a reconnected
        // student, so a spurious remove of the wrong student cannot occur.
        if (removed) StudentRemoved?.Invoke(this, endpointId);
    }

    /// <summary>Snapshot of the current roster.</summary>
    public IReadOnlyList<Entry> Students
    {
        get { lock (_gate) return _byId.Values.ToList(); }
    }

    public int Count { get { lock (_gate) return _byId.Count; } }

    public bool Contains(Guid endpointId) { lock (_gate) return _byId.ContainsKey(endpointId); }

    public bool TryGet(Guid endpointId, out Entry entry)
    {
        lock (_gate) return _byId.TryGetValue(endpointId, out entry!);
    }
}
