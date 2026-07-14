using System;
using System.Collections.Generic;
using System.Linq;

namespace ClassroomCtrl.Teacher.Core;

/// <summary>
/// TT-6-B (macOS port) — the multi-select model for the Teacher grid, UI-agnostic so it
/// lives in Teacher.Core (with StudentRoster / the command seam) and is asserted by the
/// committed <c>--teacherselftest</c>, not a scratchpad gate.
///
/// It tracks selected student ids + a range anchor, and exposes the operations the shipped
/// Windows selection model has (toggle, range, select-all, clear, prune-on-disconnect,
/// snapshot). The grid VM syncs each tile's IsSelected from <see cref="IsSelected"/> on
/// <see cref="SelectionChanged"/>; the ⌘/Shift/plain-click MAPPING is the only piece that
/// stays in the app (it needs Avalonia KeyModifiers) — <see cref="HandleClick"/> takes plain
/// booleans so the mapping's RESULT is still tested here.
///
/// THE DISTINGUISHING PROPERTY (the one a naive model gets wrong): <see cref="Snapshot"/>
/// returns a COPY, so a bulk operation iterating the snapshot is unaffected by a mid-loop
/// disconnect (<see cref="Prune"/>) mutating the live selection. The shipped code snapshots
/// before the bulk loop for exactly this reason; the gate asserts it.
///
/// macOS click idiom (a deliberate divergence from shipped, which toggles on plain click):
/// plain click = select only this; ⌘-click = toggle; Shift-click = range from the anchor.
/// </summary>
public sealed class TileSelectionModel
{
    private readonly HashSet<Guid> _selected = new();
    private Guid? _anchor;

    /// <summary>Raised after any operation that changes the selection set (so the grid can
    /// sync tile visuals + SelectedCount/HasSelection). Not raised when nothing changed.</summary>
    public event Action? SelectionChanged;

    public int Count => _selected.Count;
    public bool HasSelection => _selected.Count > 0;
    public bool IsSelected(Guid id) => _selected.Contains(id);

    /// <summary>Dispatch a click to the right operation from the (already-decoded) modifier
    /// state. <paramref name="orderedIds"/> is the grid's current tile order (for range).</summary>
    public void HandleClick(Guid id, bool cmdKey, bool shiftKey, IReadOnlyList<Guid> orderedIds)
    {
        if (shiftKey && _anchor is not null) SelectRange(orderedIds, id);
        else if (cmdKey) Toggle(id);
        else SelectOnly(id);
    }

    /// <summary>Plain click — select ONLY this tile (deselect the rest); it becomes the anchor.</summary>
    public void SelectOnly(Guid id)
    {
        bool changed = _selected.Count != 1 || !_selected.Contains(id);
        _selected.Clear();
        _selected.Add(id);
        _anchor = id;
        if (changed) Raise();
    }

    /// <summary>⌘-click — toggle this tile's membership; it becomes the anchor.</summary>
    public void Toggle(Guid id)
    {
        if (!_selected.Remove(id)) _selected.Add(id);
        _anchor = id;
        Raise();
    }

    /// <summary>Shift-click — REPLACE the selection with the contiguous range from the anchor
    /// to <paramref name="targetId"/> (inclusive), by position in <paramref name="orderedIds"/>.
    /// The anchor is preserved so successive shift-clicks re-range from the same origin (Finder
    /// behavior). If the anchor is gone (disconnected) this degrades to <see cref="SelectOnly"/>.</summary>
    public void SelectRange(IReadOnlyList<Guid> orderedIds, Guid targetId)
    {
        int anchorIdx = _anchor is Guid a ? IndexOf(orderedIds, a) : -1;
        int targetIdx = IndexOf(orderedIds, targetId);
        if (anchorIdx < 0 || targetIdx < 0) { SelectOnly(targetId); return; }

        int lo = Math.Min(anchorIdx, targetIdx);
        int hi = Math.Max(anchorIdx, targetIdx);
        _selected.Clear();
        for (int i = lo; i <= hi; i++) _selected.Add(orderedIds[i]);
        // anchor unchanged
        Raise();
    }

    public void SelectAll(IReadOnlyList<Guid> orderedIds)
    {
        bool changed = false;
        foreach (var id in orderedIds) changed |= _selected.Add(id);
        if (orderedIds.Count > 0) _anchor = orderedIds[^1];
        if (changed) Raise();
    }

    public void Clear()
    {
        if (_selected.Count == 0) { _anchor = null; return; }
        _selected.Clear();
        _anchor = null;
        Raise();
    }

    /// <summary>Disconnect handling — drop any selected ids no longer present, and clear the
    /// anchor if it left. Called after the grid removes a departed student's tile.</summary>
    public void Prune(IReadOnlyCollection<Guid> presentIds)
    {
        var present = presentIds as HashSet<Guid> ?? new HashSet<Guid>(presentIds);
        int before = _selected.Count;
        _selected.RemoveWhere(id => !present.Contains(id));
        if (_anchor is Guid a && !present.Contains(a)) _anchor = null;
        if (_selected.Count != before) Raise();
    }

    /// <summary>A point-in-time COPY of the selected ids — stable across later mutation
    /// (Prune/Toggle/etc.). Bulk operations iterate this so a mid-loop disconnect can't
    /// corrupt the run. THE distinguishing property (a naive model returns a live view here).</summary>
    public IReadOnlyList<Guid> Snapshot() => _selected.ToList();

    private static int IndexOf(IReadOnlyList<Guid> ordered, Guid id)
    {
        for (int i = 0; i < ordered.Count; i++) if (ordered[i] == id) return i;
        return -1;
    }

    private void Raise() => SelectionChanged?.Invoke();
}
