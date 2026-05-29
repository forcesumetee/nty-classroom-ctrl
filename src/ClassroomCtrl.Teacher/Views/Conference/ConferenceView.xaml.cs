using System.Windows.Controls;

namespace ClassroomCtrl.Teacher.Views.Conference;

/// <summary>
/// Phase 15-B (MVP) — Conference shell skeleton.  Stays a code-light UserControl
/// because all wiring is on <c>MainViewModel</c> (StartConferenceCommand,
/// EndConferenceCommand, IsConferenceSessionActive).  Phase 15-C replaces the
/// "session active" placeholder with the gallery; Phase 15-D adds the Meet-style
/// bottom toolbar in place of the lone End button.
/// </summary>
public partial class ConferenceView : UserControl
{
    public ConferenceView()
    {
        InitializeComponent();
    }
}
