using System.Windows.Controls;

namespace ClassroomCtrl.Shared.Wpf.Conference;

/// <summary>
/// Phase 15-C — shared gallery surface.  Code-light by design: all behaviour
/// is in the gallery view-model (auto-layout, pin, pagination); this control
/// just hosts the bindings.  Both the teacher's embedded ConferenceView and
/// the student's ConferenceGalleryWindow instantiate this control with their
/// own ConferenceGalleryViewModel as DataContext.
///
/// Phase 16-B step 4 — moved from Teacher/Views/Conference/ to Shared.Wpf.
/// </summary>
public partial class ConferenceGalleryView : UserControl
{
    public ConferenceGalleryView()
    {
        InitializeComponent();
    }
}
