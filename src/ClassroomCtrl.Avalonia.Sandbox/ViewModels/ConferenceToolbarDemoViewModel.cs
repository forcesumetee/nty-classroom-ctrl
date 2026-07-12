using System;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// Sandbox demo VM reproducing the EXACT binding contract that
/// Shared.Wpf/Conference/ConferenceToolbar.xaml expects from its host shell VM
/// (StudentConferenceShellViewModel on Student / MainViewModel on Teacher).
///
/// There is no ConferenceToolbarViewModel in the shipped code — the toolbar binds
/// to whatever parent DataContext it's dropped into. Rather than port a huge
/// Windows-coupled shell VM, this stand-in exposes the same commands + state so
/// the ported toolbar's bindings, active-state classes, and reaction flow can be
/// exercised in isolation. Real integration later binds to the actual shell VMs.
///
/// Port adaptation: the shipped Share button uses 5 enum-based DataTriggers on
/// ShareState. Avalonia conditional classes bind to bools, so this VM exposes
/// computed ShareIsActive (green) / ShareIsPending (amber) derived from ShareState.
/// </summary>
public partial class ConferenceToolbarDemoViewModel : ObservableObject
{
    public enum ShareStateKind { Idle, Requesting, Approved, Sharing }

    /// <summary>Role capabilities gate a couple of buttons (share visibility,
    /// end-vs-leave tooltip). Mirrors host VM's Role sub-object.</summary>
    public sealed class ConferenceRole
    {
        public bool CanShareScreen { get; init; }
        public bool CanEndForAll { get; init; }
    }

    /// <summary>Raised when a reaction is picked, carrying the emoji — the demo
    /// shell subscribes to float it over a tile (fulfils the 24.3 float TODO).</summary>
    public event Action<string>? ReactionPicked;

    public ConferenceRole Role { get; } = new() { CanShareScreen = true, CanEndForAll = true };

    // ── toggle state (each button's active-state driver) ──
    [ObservableProperty] private bool isMicOn = true;
    [ObservableProperty] private bool isBroadcastingCamera;
    [ObservableProperty] private bool isScreenSharing;
    [ObservableProperty] private bool isConferenceSidebarVisible;
    [ObservableProperty] private bool isHandRaised;
    [ObservableProperty] private bool hasNotifications;

    [NotifyPropertyChangedFor(nameof(ShareIsActive))]
    [NotifyPropertyChangedFor(nameof(ShareIsPending))]
    [ObservableProperty] private ShareStateKind shareState = ShareStateKind.Idle;

    /// <summary>Green when the student holds the floor (Approved/Sharing) or the
    /// teacher is actively sharing.</summary>
    public bool ShareIsActive =>
        IsScreenSharing || ShareState is ShareStateKind.Approved or ShareStateKind.Sharing;

    /// <summary>Amber while a share request is pending teacher approval.</summary>
    public bool ShareIsPending => ShareState == ShareStateKind.Requesting;

    partial void OnIsScreenSharingChanged(bool value)
    {
        OnPropertyChanged(nameof(ShareIsActive));
        OnPropertyChanged(nameof(ShareIsPending));
    }

    // ── tooltips (bound directly by the toolbar) ──
    public string MicTooltipText => IsMicOn ? "Mute microphone" : "Unmute microphone";
    public string CameraButtonText => IsBroadcastingCamera ? "Stop camera" : "Start camera";
    public string ShareScreenButtonText => ShareIsActive ? "Stop sharing" : "Share screen";

    partial void OnIsMicOnChanged(bool value) => OnPropertyChanged(nameof(MicTooltipText));
    partial void OnIsBroadcastingCameraChanged(bool value) => OnPropertyChanged(nameof(CameraButtonText));

    // ── commands (toggle local state so the active classes are visibly driven) ──
    [RelayCommand] private void ToggleMic() => IsMicOn = !IsMicOn;
    [RelayCommand] private void ToggleCamera() => IsBroadcastingCamera = !IsBroadcastingCamera;
    [RelayCommand] private void ToggleConferenceSidebar() => IsConferenceSidebarVisible = !IsConferenceSidebarVisible;
    [RelayCommand] private void RaiseHand() => IsHandRaised = !IsHandRaised;

    [RelayCommand]
    private void ShareScreen()
    {
        // Cycle Idle → Requesting → Sharing → Idle to demo the multi-state colors.
        ShareState = ShareState switch
        {
            ShareStateKind.Idle => ShareStateKind.Requesting,
            ShareStateKind.Requesting => ShareStateKind.Sharing,
            _ => ShareStateKind.Idle,
        };
        IsScreenSharing = ShareState == ShareStateKind.Sharing;
    }

    [RelayCommand] private void EndConference() { /* demo no-op */ }

    /// <summary>Picked from the reaction Flyout; raises ReactionPicked so the demo
    /// shell floats it over a tile. Replaces the shipped reflection-based
    /// SendReactionCommand resolution (sandbox binds directly).</summary>
    [RelayCommand]
    private void SendReaction(string? emoji)
    {
        if (!string.IsNullOrEmpty(emoji)) ReactionPicked?.Invoke(emoji);
    }
}
