using ClassroomCtrl.Shared.Localization;
using System.Windows;

namespace ClassroomCtrl.Teacher;

/// <summary>
/// Phase 14-B (Tier 1) — persistent privacy banner shown on the teacher's
/// own screen while their webcam is broadcasting to the classroom.  Mirrors
/// the <see cref="ClassroomCtrl.Student.Agent.VoiceLiveBanner"/> visual
/// language (pinned top-center, transparent window, single colored capsule)
/// so the "something on your machine is currently broadcasting" idiom is
/// consistent across mic + cam + remote-control features.
///
/// Tier 1 has only one state (Live).  Tier 2 may swap to a two-state
/// capsule (idle/active) when student-side cam lands; for now the red
/// capsule + static text is the right signal.
/// </summary>
public partial class CamLiveBanner : Window
{
    public CamLiveBanner()
    {
        InitializeComponent();
        Loaded += (_, _) => PositionTopCenter();
        // Pull localized banner copy once at load; Loc isn't dispatcher-affined
        // and the banner doesn't outlive a language switch in Tier 1 (closed
        // on Stop).  Falls back to the XAML literal if the key is missing.
        BannerText.Text = Loc.Get("Conf_BannerLive", BannerText.Text);
    }

    private void PositionTopCenter()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top;
    }
}
