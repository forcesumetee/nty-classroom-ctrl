using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
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

    public int ConnectedCount => Students.Count;

    // TT-6-B — selection aggregates the toolbar binds to.
    public int SelectedCount => _selection.Count;
    public bool HasSelection => _selection.HasSelection;

    public TeacherGridViewModel(StudentRoster roster)
    {
        _roster = roster;
        _roster.StudentAdded += OnStudentAdded;
        _roster.StudentRemoved += OnStudentRemoved;
        _selection.SelectionChanged += OnSelectionChanged;
        Students.CollectionChanged += (_, _) => OnPropertyChanged(nameof(ConnectedCount));
    }

    // Fires on a transport background thread → marshal to the UI thread before touching
    // the collection. THIS Post is the load-bearing line of TT-2.
    private void OnStudentAdded(object? sender, StudentRoster.Entry e) =>
        Dispatcher.UIThread.Post(() =>
        {
            var existing = Students.FirstOrDefault(t => t.EndpointId == e.EndpointId);
            if (existing is null)
                Students.Add(new StudentTileViewModel(e.EndpointId, e.DisplayName, e.MachineName, e.OsVersion));
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
            if (tile is not null) Students.Remove(tile);
            // Keep the selection honest after a disconnect (drops the departed id + a stale
            // anchor). Bulk ops already run over a snapshot, so an in-flight bulk is unaffected.
            _selection.Prune(OrderedIds());
        });

    // ─────── TT-6-B: selection — the VM delegates to TileSelectionModel ───────

    /// <summary>The current tile order (for range selection).</summary>
    private List<Guid> OrderedIds() => Students.Select(s => s.EndpointId).ToList();

    /// <summary>A left-click on a tile, with the (decoded) modifier state. macOS idiom:
    /// plain = select only; ⌘ = toggle; Shift = range from the anchor.</summary>
    public void HandleClick(Guid id, bool cmdKey, bool shiftKey) =>
        _selection.HandleClick(id, cmdKey, shiftKey, OrderedIds());

    [RelayCommand]
    private void SelectAll() => _selection.SelectAll(OrderedIds());

    [RelayCommand]
    private void ClearSelection() => _selection.Clear();

    /// <summary>Point-in-time copy of the selected tiles — the bulk target list. Stable
    /// across a mid-loop disconnect (the model's Snapshot + this ToList are both copies).</summary>
    public IReadOnlyList<StudentTileViewModel> GetSelectedSnapshot() =>
        Students.Where(s => _selection.IsSelected(s.EndpointId)).ToList();

    private void OnSelectionChanged()
    {
        foreach (var s in Students) s.IsSelected = _selection.IsSelected(s.EndpointId);
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
    }
}
