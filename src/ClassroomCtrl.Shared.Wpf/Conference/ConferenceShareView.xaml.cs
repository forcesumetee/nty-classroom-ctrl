using System.Windows.Controls;

namespace ClassroomCtrl.Shared.Wpf.Conference;

/// <summary>
/// Phase 16-B+ step 8 — Zoom/Meet-style in-frame Conference share view.
/// DataContext = <see cref="ViewModels.ConferenceGalleryViewModel"/>; the
/// view binds to ActiveShareFrame for the primary tile, ActiveShareSourceName
/// for the "X is sharing" pill, and Tiles for the bottom filmstrip.  Layout
/// switch between this view and the tile-mode gallery is handled in
/// ConferenceGalleryView (Step 9) via IsShareActive.
/// </summary>
public partial class ConferenceShareView : UserControl
{
    public ConferenceShareView()
    {
        InitializeComponent();
    }
}
