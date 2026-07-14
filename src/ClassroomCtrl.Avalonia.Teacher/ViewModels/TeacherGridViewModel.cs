using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Teacher.Services;
using ClassroomCtrl.Shared.Protocol;
using ClassroomCtrl.Teacher.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Teacher.ViewModels;

/// <summary>
/// TT-2-C — the student grid VM. Subscribes to the TT-1 <see cref="StudentRoster"/>
/// and projects it into an observable tile collection the grid view binds to.
///
/// THE load-bearing correctness point of TT-2 (§20 pattern, same class as the native
/// callbacks M16–M22): the roster's StudentAdded / StudentRemoved events fire on the
/// transport's background read-loop threads. Avalonia's <see cref="ObservableCollection{T}"/>
/// MUST be mutated on the UI thread (the render loop reads it concurrently). So every
/// mutation is marshaled via <see cref="Dispatcher"/>.UIThread.Post — see
/// <see cref="OnStudentAdded"/> / <see cref="OnStudentRemoved"/>. A direct mutation in
/// the handler would "work" in a naive count test yet crash intermittently at scale.
///
/// TT-6-B — multi-select. The selection LOGIC is the UI-agnostic
/// <see cref="TileSelectionModel"/> (Teacher.Core, committed-gated); this VM delegates to it
/// and syncs each tile's IsSelected on <see cref="TileSelectionModel.SelectionChanged"/>.
/// All selection mutations run on the UI thread (click handlers / ⌘A / Esc, and Prune from
/// the already-marshaled <see cref="OnStudentRemoved"/>), so the tile sync is UI-thread-safe.
/// </summary>
public partial class TeacherGridViewModel : ObservableObject
{
    private readonly StudentRoster _roster;
    private readonly TileSelectionModel _selection = new();

    public ObservableCollection<StudentTileViewModel> Students { get; } = new();

    /// <summary>TT-7-C — the raised-hand queue, in the order hands went up. The teacher
    /// recognizes from here (each row has a Recognize button). Kept in sync with the tiles'
    /// IsHandRaised by the HandRaiseReceived router.</summary>
    public ObservableCollection<StudentTileViewModel> RaisedHands { get; } = new();

    public int ConnectedCount => Students.Count;

    /// <summary>TT-7-C — drives the raised-hand queue's visibility (hidden when no hands up).</summary>
    public bool HasRaisedHands => RaisedHands.Count > 0;

    // TT-6-B — selection aggregates the toolbar binds to.
    public int SelectedCount => _selection.Count;
    public bool HasSelection => _selection.HasSelection;

    // TT-6-C — bulk. CanBulkPower gates the toolbar's power buttons (decision A: enabled while
    // AT LEAST ONE selected student can execute power; the op skips the rest). BulkStatus shows
    // "Sending i of N…" during a run and the honest result summary after (incl. any skip count).
    public bool CanBulkPower => Students.Any(s => _selection.IsSelected(s.EndpointId) && s.CanReceivePower);

    [ObservableProperty] private string bulkStatus = "";

    // Set by App after the command controller is built (it needs the window for the confirm
    // dialog, which is created after this VM). Bulk commands are no-ops until attached.
    private StudentCommandController? _commands;
    public void AttachCommands(StudentCommandController commands) => _commands = commands;

    // TT-7-C — chat/hand/reaction seam + sound + toast. Attached by App after the session is up.
    private ITeacherMessaging? _messaging;
    private ISoundService? _sound;
    private Action<string>? _notify;
    private int _handSeq;

    public TeacherGridViewModel(StudentRoster roster)
    {
        _roster = roster;
        _roster.StudentAdded += OnStudentAdded;
        _roster.StudentRemoved += OnStudentRemoved;
        _selection.SelectionChanged += OnSelectionChanged;
        Students.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ConnectedCount));
        RaisedHands.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasRaisedHands));
    }

    // Fires on a transport background thread → marshal to the UI thread before touching
    // the collection. THIS Post is the load-bearing line of TT-2.
    private void OnStudentAdded(object? sender, StudentRoster.Entry e) =>
        Dispatcher.UIThread.Post(() =>
        {
            var existing = Students.FirstOrDefault(t => t.EndpointId == e.EndpointId);
            if (existing is null)
                Students.Add(new StudentTileViewModel(e.EndpointId, e.DisplayName, e.MachineName, e.OsVersion)
                {
                    RecognizeCallback = RecognizeHandAsync,   // TT-7-C: tile's Recognize button lowers its hand
                });
            else
            {
                // Reconnect on the same EndpointId → update in place (no duplicate tile).
                existing.DisplayName = e.DisplayName;
                existing.MachineName = e.MachineName;
                existing.OsVersion = e.OsVersion;   // re-notifies CanReceivePower
                existing.Presence = TilePresence.Connected;
            }
        });

    // Fires on a transport background thread (clean disconnect or stale-sweep) → marshal.
    private void OnStudentRemoved(object? sender, Guid endpointId) =>
        Dispatcher.UIThread.Post(() =>
        {
            var tile = Students.FirstOrDefault(t => t.EndpointId == endpointId);
            if (tile is not null)
            {
                Students.Remove(tile);
                RaisedHands.Remove(tile);   // a departed student can't stay in the recognize queue
            }
            // Keep the selection honest after a disconnect (drops the departed id + a stale
            // anchor). Bulk ops already run over a snapshot, so an in-flight bulk is unaffected.
            _selection.Prune(OrderedIds());
        });

    // ─────── TT-6-B: selection — the VM delegates to TileSelectionModel ───────

    /// <summary>The current tile order (for range selection).</summary>
    private List<Guid> OrderedIds() => Students.Select(s => s.EndpointId).ToList();

    /// <summary>A left-click on a tile, with the (decoded) modifier state. macOS idiom:
    /// plain = select only; ⌘ = toggle; Shift = range from the anchor.</summary>
    public void HandleClick(Guid id, bool cmdKey, bool shiftKey)
    {
        BulkStatus = "";   // a manual selection change clears a stale result summary
        _selection.HandleClick(id, cmdKey, shiftKey, OrderedIds());
    }

    [RelayCommand]
    private void SelectAll()
    {
        BulkStatus = "";
        _selection.SelectAll(OrderedIds());
    }

    [RelayCommand]
    private void ClearSelection()
    {
        BulkStatus = "";
        _selection.Clear();
    }

    // ─────── TT-6-C: bulk actions — fan out through StudentCommandController ───────
    // Routing bulk through the controller means the promoted reliable-channel guard covers it
    // by construction (a bulk command can't go lossy without bypassing the controller).

    [RelayCommand] private Task BulkLock() => RunBulkAsync(StudentCommand.Lock);
    [RelayCommand] private Task BulkUnlock() => RunBulkAsync(StudentCommand.Unlock);
    [RelayCommand] private Task BulkLogoff() => RunBulkAsync(StudentCommand.Logoff);
    [RelayCommand] private Task BulkRestart() => RunBulkAsync(StudentCommand.Restart);
    [RelayCommand] private Task BulkShutdown() => RunBulkAsync(StudentCommand.Shutdown);

    private async Task RunBulkAsync(StudentCommand command)
    {
        if (_commands is null) return;
        var targets = GetSelectedSnapshot()
            .Select(t => new BulkTarget(t.EndpointId, t.CanReceivePower))
            .ToList();
        if (targets.Count == 0) return;

        var progress = new Progress<(int Done, int Total)>(p => BulkStatus = $"Sending {p.Done} of {p.Total}…");
        var result = await _commands.ExecuteBulkAsync(targets, command, progress);
        BulkStatus = FormatBulkResult(result);
    }

    private static string FormatBulkResult(BulkResult r)
    {
        if (r.Cancelled) return "";
        string verb = r.Command switch
        {
            StudentCommand.Lock => "Locked",
            StudentCommand.Unlock => "Unlocked",
            StudentCommand.Logoff => "Logged off",
            StudentCommand.Restart => "Restarted",
            StudentCommand.Shutdown => "Shut down",
            _ => r.Command.ToString(),
        };
        // Decision A — the skip MUST be visible, never a silent partial.
        return r.Skipped > 0
            ? $"{verb} {r.Sent} · {r.Skipped} macOS skipped (not supported)"
            : $"{verb} {r.Sent}";
    }

    /// <summary>Point-in-time copy of the selected tiles — the bulk target list. Stable
    /// across a mid-loop disconnect (the model's Snapshot + this ToList are both copies).</summary>
    public IReadOnlyList<StudentTileViewModel> GetSelectedSnapshot() =>
        Students.Where(s => _selection.IsSelected(s.EndpointId)).ToList();

    private void OnSelectionChanged()
    {
        foreach (var s in Students) s.IsSelected = _selection.IsSelected(s.EndpointId);
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanBulkPower));
    }

    // ─────── TT-7-C: chat/hand/reaction routing — attribution by EndpointId ───────
    // Inbound events fire on a transport background thread → every tile mutation is marshaled
    // to the UI thread, same discipline as OnStudentAdded/Removed.

    /// <summary>Wire the chat/hand/reaction seam in. Split from the ctor because the session
    /// (and sound) are built by App after the roster VM. Idempotent-safe: called once.</summary>
    public void AttachMessaging(ITeacherMessaging messaging, ISoundService sound, Action<string>? notify = null)
    {
        _messaging = messaging;
        _sound = sound;
        _notify = notify;
        messaging.HandRaiseReceived += OnHandRaiseReceived;
        messaging.ReactionReceived += OnReactionReceived;
    }

    // The ATTRIBUTION guard: a hand-raise for StudentId X lights ONLY the tile whose
    // EndpointId == X. If X isn't in the roster, nothing happens — never a fan-out to all
    // tiles. This is the distinguishing property the TT-7-E aggregation gate asserts.
    private void OnHandRaiseReceived(object? sender, HandRaiseMessage msg) =>
        Dispatcher.UIThread.Post(() =>
        {
            var tile = Students.FirstOrDefault(t => t.EndpointId == msg.StudentId);
            if (tile is null) return;

            if (msg.IsRaised)
            {
                if (tile.IsHandRaised) return;   // idempotent (dup raise)
                tile.IsHandRaised = true;
                tile.HandRaiseOrder = ++_handSeq;
                RaisedHands.Add(tile);
                _sound?.Play(NotificationSound.HandRaise);
                _notify?.Invoke($"✋ {tile.DisplayName} raised their hand");
            }
            else LowerLocal(tile);
        });

    private void OnReactionReceived(object? sender, (Guid SenderId, ReactionMessage Msg) e) =>
        Dispatcher.UIThread.Post(() =>
        {
            var tile = Students.FirstOrDefault(t => t.EndpointId == e.SenderId);
            if (tile is null) return;
            var emoji = e.Msg.Emoji;
            tile.LastReaction = emoji;
            // Reactions are ephemeral: clear after a few seconds unless a newer, different one
            // has replaced it. (Clock-skew makes the wire ExpiresAtMs unreliable across machines,
            // so we use a local timer.)
            DispatcherTimer.RunOnce(() =>
            {
                if (tile.LastReaction == emoji) tile.LastReaction = "";
            }, TimeSpan.FromSeconds(4));
        });

    /// <summary>Teacher "Recognize" — lower one student's hand (targeted HandLower) and clear
    /// it locally. Invoked by the tile's own RecognizeCommand (set as its callback). Fire-and-
    /// forget send; a swallowed error can't crash the Teacher.</summary>
    private async Task RecognizeHandAsync(StudentTileViewModel tile)
    {
        LowerLocal(tile);   // optimistic: the queue updates immediately
        if (_messaging is null) return;
        try { await _messaging.SendHandLowerAsync(tile.EndpointId, CancellationToken.None); }
        catch { /* the hand is already lowered locally; a lost lower is re-issuable */ }
    }

    private void LowerLocal(StudentTileViewModel tile)
    {
        tile.IsHandRaised = false;
        tile.HandRaiseOrder = 0;
        RaisedHands.Remove(tile);
    }
}
