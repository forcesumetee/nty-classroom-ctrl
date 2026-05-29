using ClassroomCtrl.Shared.Localization;
using System.Windows;
using System.Windows.Media;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 13-D (Tier 3) — persistent privacy banner shown while the student's
/// mic is live (capturing + emitting voice frames to the group).  Mirrors
/// <see cref="RemoteControlBanner"/> style — pinned top-center, transparent
/// window with a colored capsule — so the visual language for "something on
/// your machine is currently broadcasting" is consistent across features.
///
/// Three visual states driven by <see cref="MicBroadcaster"/> properties:
///   - Hidden when not capturing (IsMuted = true, or PttMode + !IsPttDown).
///   - Green capsule "🎤 Microphone ON · Group N" when capturing but silent.
///   - Red capsule "🔴 Speaking · Group N" when capturing + IsSpeaking.
/// </summary>
public partial class VoiceLiveBanner : Window
{
    private static readonly SolidColorBrush GreenBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81));
    private static readonly SolidColorBrush RedBrush   = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xDC, 0x26, 0x26));

    public VoiceLiveBanner()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionTopCenter();
        GreenBrush.Freeze();
        RedBrush.Freeze();
    }

    /// <summary>Update the banner's color + text based on current state.
    /// Called from MainWindow on every MicBroadcaster.StateChanged.</summary>
    public void UpdateState(bool isSpeaking, string groupName)
    {
        var label = string.IsNullOrEmpty(groupName)
            ? Loc.Get(isSpeaking ? "Voice_BannerSpeaking" : "Voice_BannerLive")
            : string.Format(Loc.Get(isSpeaking ? "Voice_BannerSpeakingFmt" : "Voice_BannerLiveFmt"), groupName);
        BannerText.Text = label;
        BannerBorder.Background = isSpeaking ? RedBrush : GreenBrush;
    }

    private void PositionTopCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top;
    }
}
