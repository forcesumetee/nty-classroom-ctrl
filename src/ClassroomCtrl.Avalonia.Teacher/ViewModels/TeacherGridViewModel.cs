using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using ClassroomCtrl.Teacher.Core;
using CommunityToolkit.Mvvm.ComponentModel;

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
/// </summary>
public partial class TeacherGridViewModel : ObservableObject
{
    private readonly StudentRoster _roster;

    public ObservableCollection<StudentTileViewModel> Students { get; } = new();

    public int ConnectedCount => Students.Count;

    public TeacherGridViewModel(StudentRoster roster)
    {
        _roster = roster;
        _roster.StudentAdded += OnStudentAdded;
        _roster.StudentRemoved += OnStudentRemoved;
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
        });
}
