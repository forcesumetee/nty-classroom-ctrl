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

    /// <summary>Phase 27-C — true while streaming this screen to the teacher.</summary>
    [ObservableProperty] private bool isStreaming;
    [ObservableProperty] private int streamedFrames;

    public void Reset()
    {
        IsLocked = false;
        PolicyActive = false;
        PolicyChips.Clear();
        IsHandRaised = false;
        LastChat = "";
        LastDeferred = "";
        IsStreaming = false;
        StreamedFrames = 0;
    }

    public void SetPolicy(System.Collections.Generic.IEnumerable<string> chips)
    {
        PolicyChips.Clear();
        foreach (var c in chips) PolicyChips.Add(c);
        PolicyActive = PolicyChips.Count > 0;
    }
}
