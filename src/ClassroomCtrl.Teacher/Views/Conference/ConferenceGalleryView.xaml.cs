using System.Windows.Controls;

namespace ClassroomCtrl.Teacher.Views.Conference;

/// <summary>
/// Phase 15-C — shared gallery surface.  Code-light by design: all behaviour
/// is in the gallery view-model (auto-layout, pin, pagination); this control
/// just hosts the bindings.  Both the teacher's embedded ConferenceView and
/// the student's ConferenceGalleryWindow instantiate this control with their
/// own ConferenceGalleryViewModel as DataContext.
/// </summary>
public partial class ConferenceGalleryView : UserControl
{
    public ConferenceGalleryView()
    {
        InitializeComponent();
    }
}
