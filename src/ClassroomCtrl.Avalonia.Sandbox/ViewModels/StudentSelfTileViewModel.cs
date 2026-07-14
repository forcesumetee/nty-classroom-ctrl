using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// Phase 26.0-C — "how the Teacher sees me." Reflects the state that inbound
/// Teacher commands drive onto this student: lock overlay, applied policy chips,
/// hand-raise, last chat, and a note for capture commands that are deferred until
/// the native macOS APIs land (Phase 27+). No enforcement — reflection only.
/// </summary>
public partial class StudentSelfTileViewModel : ObservableObject
{
    [ObservableProperty] private string displayName = "Mac Sandbox";
    [ObservableProperty] private bool isOnline;

    [ObservableProperty] private bool isLocked;

    [ObservableProperty] private bool policyActive;
    public ObservableCollection<string> PolicyChips { get; } = new();

    [ObservableProperty] private bool isHandRaised;

    [ObservableProperty] private string lastChat = "";

    /// <summary>Most recent command that needs native capture APIs (screenshot /
    /// screen stream / camera) — logged + shown, not enforced.</summary>
    [ObservableProperty] private string lastDeferred = "";

    /// <summary>TT-12 — most recent file the teacher sent to this student (name + outcome).</summary>
    [ObservableProperty] private string lastFile = "";

    /// <summary>Phase 27-C — true while streaming this screen to the teacher.</summary>
    [ObservableProperty] private bool isStreaming;
    [ObservableProperty] private int streamedFrames;
    /// <summary>Phase 27-B — active codec label ("H.264" / "MJPEG").</summary>
    [ObservableProperty] private string streamCodec = "";

    /// <summary>Phase 28-E — true while streaming the camera to the teacher as a
    /// Conference peer-cam (JPEG). Independent of the screen stream above.</summary>
    [ObservableProperty] private bool isCameraLive;
    [ObservableProperty] private int cameraFrames;

    /// <summary>Phase 29-E — true while streaming the mic to the teacher (Mic Monitor
    /// talkback, raw PCM). Independent of screen + camera.</summary>
    [ObservableProperty] private bool isMicLive;
    [ObservableProperty] private int micFrames;

    /// <summary>Phase 29-F — true while playing the teacher's broadcast audio (path A).</summary>
    [ObservableProperty] private bool isPlayingAudio;
    [ObservableProperty] private int playedAudioFrames;

    public void Reset()
    {
        // NOTE: IsLocked is intentionally NOT reset here — the LockService (30-C) is its
        // sole owner, so the lock shield + its self-tile indicator survive the disconnect
        // grace window and flip only on explicit/auto unlock.
        PolicyActive = false;
        PolicyChips.Clear();
        IsHandRaised = false;
        LastChat = "";
        LastDeferred = "";
        IsStreaming = false;
        StreamedFrames = 0;
        IsCameraLive = false;
        CameraFrames = 0;
        IsMicLive = false;
        MicFrames = 0;
        IsPlayingAudio = false;
        PlayedAudioFrames = 0;
    }

    public void SetPolicy(System.Collections.Generic.IEnumerable<string> chips)
    {
        PolicyChips.Clear();
        foreach (var c in chips) PolicyChips.Add(c);
        PolicyActive = PolicyChips.Count > 0;
    }
}
