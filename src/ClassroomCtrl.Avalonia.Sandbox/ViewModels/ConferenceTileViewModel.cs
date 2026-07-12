using Avalonia.Media.Imaging;          // PORT CHANGE: was System.Windows.Media.Imaging
using CommunityToolkit.Mvvm.ComponentModel;
using System;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// PORTED from ClassroomCtrl.Shared.Wpf/ViewModels/ConferenceTileViewModel.cs
/// (Phase 24.3 first-view-port experiment).  The ONLY substantive change from
/// the shipped WPF version is the frame image type:
///   WPF:      System.Windows.Media.Imaging.BitmapSource
///   Avalonia: Avalonia.Media.Imaging.Bitmap
/// Everything else — CommunityToolkit.Mvvm ObservableObject, [ObservableProperty]
/// source generators, the computed Initial, partial-method change hook — compiles
/// and behaves identically under Avalonia.  Original doc-comments preserved below.
///
/// ─── original summary ───
/// Phase 15-C — per-tile state for the Conference gallery.  Each connected
/// participant (student + teacher self) maps to one of these; the parent
/// ConferenceGalleryViewModel owns the collection and routes frames + state here.
/// Properties are kept observable so a XAML DataTemplate can bind directly.
/// </summary>
public partial class ConferenceTileViewModel : ObservableObject
{
    /// <summary>Source endpoint id - immutable for the lifetime of the tile.</summary>
    public Guid EndpointId { get; }

    /// <summary>True for the teacher's own tile.  Drives "(You)" suffix.</summary>
    public bool IsSelf { get; }

    [ObservableProperty] private string displayName = "";

    /// <summary>First character of DisplayName, uppercased, used as the avatar
    /// initial on the cam-off placeholder.  "?" when empty so it never blanks.</summary>
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

    /// <summary>Latest JPEG-decoded frame; bound directly to an Image.Source.
    /// Null while the participant's cam is off or before the first frame.
    /// PORT: Avalonia Bitmap (was WPF BitmapSource).</summary>
    [ObservableProperty] private Bitmap? jpegFrame;

    [ObservableProperty] private bool isMicLive;
    [ObservableProperty] private bool isSpeaking;
    [ObservableProperty] private bool isCamLive;
    [ObservableProperty] private bool isHandRaised;

    /// <summary>Phase 22 — dark-theme "HOST" badge, top-right on the teacher tile.</summary>
    [ObservableProperty] private bool isHost;

    /// <summary>Set when this tile is the active pin.  Drives a border accent.</summary>
    [ObservableProperty] private bool isPinned;

    /// <summary>Phase 15-E — UTC wall-clock at which the participant raised a hand.</summary>
    [ObservableProperty] private DateTime? handRaisedAt;

    /// <summary>Phase 15-E step 4 — transient reaction emoji shown floating over
    /// the tile.  Empty string = no reaction.  (Float-up animation deferred in
    /// the port — see ConferenceTile.axaml.cs TODO(Phase 25 or later).)</summary>
    [ObservableProperty] private string currentReactionEmoji = "";

    public ConferenceTileViewModel(Guid endpointId, string displayName, bool isSelf = false)
    {
        EndpointId = endpointId;
        DisplayName = displayName;
        IsSelf = isSelf;
    }
}
