using ClassroomCtrl.Shared.Localization;
using System;
using System.Windows;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 15-B (MVP) — student-side conference shell.  Skeleton only:
/// shows the host line and a Leave button.  Spawned by
/// MainWindow on ConferenceStart dispatch; closed by MainWindow on
/// ConferenceEnd dispatch OR by the student clicking Leave (which
/// only closes the window — does NOT broadcast anything; Phase 15-D
/// will wire an opt-out envelope when participants are tracked).
///
/// Phase 15-C replaces the placeholder text with the actual gallery;
/// Phase 15-D adds the Meet-style bottom toolbar.
/// </summary>
public partial class ConferenceGalleryWindow : Window
{
    public Guid SessionId { get; }

    public ConferenceGalleryWindow(Guid sessionId, string hostName)
    {
        InitializeComponent();
        SessionId = sessionId;

        // Localized "Hosted by {0}" if we have a host name, else just session
        // running line.  Loc.Get with a fallback so the window is functional
        // even before step 7 adds the keys.
        if (!string.IsNullOrWhiteSpace(hostName))
        {
            var fmt = Loc.Get("Conf_HostLineFmt", "Hosted by {0}");
            HostLineText.Text = string.Format(fmt, hostName);
        }
        else
        {
            HostLineText.Text = Loc.Get("Conf_HostLineUnknown", "Conference in progress");
        }
    }

    private void Leave_Click(object sender, RoutedEventArgs e)
    {
        // Phase 15-B: Leave is local-only.  Closing the window leaves the
        // student in their normal tray-idle state; the teacher's End broadcast
        // would have closed the window anyway when the session ends.  Phase
        // 15-D wires participant tracking and an opt-out envelope so the
        // teacher knows a student left mid-session.
        Close();
    }
}
