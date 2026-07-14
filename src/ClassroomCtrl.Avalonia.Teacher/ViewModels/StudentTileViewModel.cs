using System;
using System.Threading.Tasks;
using ClassroomCtrl.Teacher.Core;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    [NotifyPropertyChangedFor(nameof(PowerUnavailableReason))]
    private string osVersion;

    /// <summary>TT-5-B — whether the power actions (logoff/restart/shutdown) should be
    /// OFFERED for this student. True only for Windows students (they execute power);
    /// false for Mac/unknown (no macOS power handler yet). The context menu binds the
    /// power items' IsEnabled to this; lock/unlock are always enabled (both platforms
    /// enforce them).</summary>
    public bool CanReceivePower => StudentPlatform.CanReceivePower(OsVersion);

    /// <summary>TT-5-C — the tooltip shown on the (disabled) power menu items for a student
    /// that can't execute power yet. Null when power IS available, so the tooltip only
    /// appears where it explains a disabled item.</summary>
    public string? PowerUnavailableReason =>
        CanReceivePower ? null : "Power actions aren't available for macOS students yet";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PresenceColorHex))]
    private TilePresence presence = TilePresence.Connected;

    /// <summary>Visual single-selection (from the Sandbox shell's Card_Pressed).</summary>
    [ObservableProperty] private bool isSelected;

    // ─────── TT-7-C: hand-raise + reaction (attributed per tile by EndpointId) ───────

    /// <summary>TT-7-C — this student's hand is up. Set ONLY by the grid's HandRaiseReceived
    /// router, matched by EndpointId == the event's StudentId — so a raise for another student
    /// can never light this tile (the distinguishing property the TT-7-E gate asserts).</summary>
    [ObservableProperty] private bool isHandRaised;

    /// <summary>TT-7-C — the order this hand went up (1-based; 0 = down). Drives the "who was
    /// first" queue so the teacher recognizes students in the order they raised.</summary>
    [ObservableProperty] private int handRaiseOrder;

    /// <summary>TT-7-C — the student's most recent reaction emoji (transient; cleared on expiry).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReaction))]
    private string lastReaction = "";

    public bool HasReaction => !string.IsNullOrEmpty(LastReaction);

    /// <summary>TT-7-C — set by the grid; the tile's Recognize button lowers this hand.</summary>
    public Func<StudentTileViewModel, Task>? RecognizeCallback { get; set; }

    [RelayCommand]
    private Task Recognize() => RecognizeCallback?.Invoke(this) ?? Task.CompletedTask;

    // ─────── TT-9-C: teacher mic-monitor (Listen) ───────

    /// <summary>TT-9-C — the teacher is monitoring this student's mic (its PCM feeds the
    /// mix). Set by the grid after MicMonitorStart/Stop is issued, so the badge + the
    /// checkable menu item reflect the real listen state.</summary>
    [ObservableProperty] private bool isListening;

    /// <summary>TT-9-C — set by the grid; the tile's "Listen to mic" toggle opens/closes it.</summary>
    public Func<StudentTileViewModel, Task>? ListenCallback { get; set; }

    [RelayCommand]
    private Task Listen() => ListenCallback?.Invoke(this) ?? Task.CompletedTask;

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
