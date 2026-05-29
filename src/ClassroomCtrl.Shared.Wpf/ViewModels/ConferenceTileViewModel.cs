using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Windows.Media.Imaging;

namespace ClassroomCtrl.Shared.Wpf.ViewModels;

/// <summary>
/// Phase 15-C — per-tile state for the Conference gallery.  Each connected
/// participant (student + teacher self) maps to one of these; the parent
/// <see cref="ConferenceGalleryViewModel"/> owns the collection and routes
/// frames + state updates here.
///
/// Properties are kept observable so a XAML DataTemplate can bind directly.
/// Layout decisions (pinned / filmstrip / paged) live on the gallery VM,
/// not on the tile.
///
/// Phase 16-B step 3 — moved from Teacher/ViewModels/ to Shared.Wpf so
/// Student.Agent can host the same gallery surface via its
/// StudentConferenceShellViewModel.
/// </summary>
public partial class ConferenceTileViewModel : ObservableObject
{
    /// <summary>Source endpoint id - immutable for the lifetime of the tile.
    /// Used by gallery sync (Phase 15-C step 1) + active-speaker routing
    /// (step 4) + pin clicks (step 5).</summary>
    public Guid EndpointId { get; }

    /// <summary>True for the teacher's own tile.  Drives "(You)" suffix +
    /// the cam-frame routing path (teacher frames come from
    /// ControlServer.TeacherCameraFrameSent rather than the inbound dispatch).</summary>
    public bool IsSelf { get; }

    [ObservableProperty] private string displayName = "";

    /// <summary>Phase 16-B+ step 3 — first character of <see cref="DisplayName"/>,
    /// uppercased, used as the avatar initial on the cam-off placeholder.
    /// "?" when the display name is empty so the avatar circle never
    /// renders blank.</summary>
    public string Initial
    {
        get
        {
            var s = DisplayName?.Trim();
            if (string.IsNullOrEmpty(s)) return "?";
            return char.ToUpperInvariant(s[0]).ToString();
        }
    }

    partial void OnDisplayNameChanged(string value) => OnPropertyChanged(nameof(Initial));

    /// <summary>Latest JPEG-decoded frame; bound directly to a WPF Image.Source.
    /// Null while the participant's cam is off or before the first frame
    /// arrives.</summary>
    [ObservableProperty] private BitmapSource? jpegFrame;

    [ObservableProperty] private bool isMicLive;
    [ObservableProperty] private bool isSpeaking;
    [ObservableProperty] private bool isCamLive;
    [ObservableProperty] private bool isHandRaised;

    /// <summary>Set by <see cref="ConferenceGalleryViewModel"/> when this tile
    /// is the active pin.  Drives a border accent + governs whether the
    /// layout switches to filmstrip mode.</summary>
    [ObservableProperty] private bool isPinned;

    /// <summary>Phase 15-E — UTC wall-clock at which the participant raised
    /// their hand.  Used by the gallery view-model's RaisedHandQueue to
    /// order entries so the first-raised hand appears at the top of the
    /// teacher's recognize list.  Null while no hand is raised; set
    /// alongside <see cref="IsHandRaised"/> by MainViewModel
    /// .OnHandRaiseReceived.</summary>
    [ObservableProperty] private DateTime? handRaisedAt;

    /// <summary>Phase 15-E step 4 — transient reaction emoji shown floating
    /// over the tile.  Set by MainViewModel.OnReactionReceived for ~3 s,
    /// then cleared by a DispatcherTimer.  Empty string = no reaction.</summary>
    [ObservableProperty] private string currentReactionEmoji = "";

    public ConferenceTileViewModel(Guid endpointId, string displayName, bool isSelf = false)
    {
        EndpointId = endpointId;
        DisplayName = displayName;
        IsSelf = isSelf;
    }
}
