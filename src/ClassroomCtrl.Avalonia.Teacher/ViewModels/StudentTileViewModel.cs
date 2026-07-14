using System;
using ClassroomCtrl.Avalonia.Teacher.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassroomCtrl.Avalonia.Teacher.ViewModels;

/// <summary>
/// TT-2-C — presence state of a tile. The TT-1 roster is binary (present = in the
/// collection; absent = removed), so a live tile is always <see cref="Connected"/>.
/// Reconnecting / Stale are placeholders for when the transport surfaces an
/// intermediate state (a grace-period is a later phase) — the enum leaves room.
/// </summary>
public enum TilePresence { Connected, Reconnecting, Stale }

/// <summary>
/// TT-2-C — the REAL per-student tile VM (the observable version of the Session-1
/// demo shape). Fed by <c>TeacherGridViewModel</c> from the TT-1 <c>StudentRoster</c>.
/// Only the fields TT-2 shows are here; badges (room/policy/hand/host/mic/webcam)
/// and the screen thumbnail are deferred to later phases.
/// </summary>
public partial class StudentTileViewModel : ObservableObject
{
    /// <summary>The student's app-level identity (stable; the roster key).</summary>
    public Guid EndpointId { get; }

    [ObservableProperty] private string displayName;
    [ObservableProperty] private string machineName;

    /// <summary>The student's reported OS (from Hello). Drives <see cref="CanReceivePower"/>;
    /// may change on reconnect, so it's observable and re-notifies the derived flag.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanReceivePower))]
    private string osVersion;

    /// <summary>TT-5-B — whether the power actions (logoff/restart/shutdown) should be
    /// OFFERED for this student. True only for Windows students (they execute power);
    /// false for Mac/unknown (no macOS power handler yet). The context menu binds the
    /// power items' IsEnabled to this; lock/unlock are always enabled (both platforms
    /// enforce them).</summary>
    public bool CanReceivePower => StudentPlatform.CanReceivePower(OsVersion);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresenceColorHex))]
    private TilePresence presence = TilePresence.Connected;

    /// <summary>Visual single-selection (from the Sandbox shell's Card_Pressed).</summary>
    [ObservableProperty] private bool isSelected;

    /// <summary>The presence affordance color (green = connected). The card binds this
    /// through HexToBrushConverter — a legible "this tile is a connected student".</summary>
    public string PresenceColorHex => Presence switch
    {
        TilePresence.Connected => "#34A853",     // green
        TilePresence.Reconnecting => "#FBBC04",  // amber
        _ => "#9AA0A6",                           // grey (stale)
    };

    public StudentTileViewModel(Guid endpointId, string displayName, string machineName, string osVersion = "")
    {
        EndpointId = endpointId;
        this.displayName = displayName;
        this.machineName = machineName;
        this.osVersion = osVersion;
    }
}
