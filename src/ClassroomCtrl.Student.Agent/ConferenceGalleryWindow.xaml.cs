using ClassroomCtrl.Shared.Localization;
using System;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 15-B/C — student-side conference shell.  Hosts a single 16:9 tile
/// for the teacher's webcam frame (Tier 1 only broadcasts teacher cam).
/// Spawned by <c>MainWindow</c> on <c>ConferenceStart</c> dispatch; closed by
/// <c>MainWindow</c> on <c>ConferenceEnd</c> dispatch OR by the student
/// clicking Leave.
///
/// Phase 15-F (or a dedicated Shared.Wpf library) can lift this to the
/// teacher-side multi-tile gallery when student-to-student cam ships.
/// </summary>
public partial class ConferenceGalleryWindow : Window
{
    public Guid SessionId { get; }

    public ConferenceGalleryWindow(Guid sessionId, string hostName)
    {
        InitializeComponent();
        SessionId = sessionId;

        // Header line: "Hosted by {name}" or generic "in progress" line.
        if (!string.IsNullOrWhiteSpace(hostName))
        {
            var fmt = Loc.Get("Conf_HostLineFmt", "Hosted by {0}");
            HostLineText.Text = string.Format(fmt, hostName);
        }
        else
        {
            HostLineText.Text = Loc.Get("Conf_HostLineUnknown", "Conference in progress");
        }

        // Waiting placeholder: "Waiting for {host}'s camera…"
        var waitingFmt = Loc.Get("Conf_WaitingForCamFmt", "Waiting for {0}'s camera…");
        WaitingLineText.Text = string.Format(waitingFmt,
            string.IsNullOrWhiteSpace(hostName) ? Loc.Get("Conf_HostLineUnknown", "the host") : hostName);
    }

    /// <summary>Phase 15-C step 3 — called from <c>MainWindow.xaml.cs</c>
    /// CameraFrame dispatch when this window is open.  Decodes the JPEG into
    /// a frozen BitmapImage and assigns to the teacher-frame Image; first
    /// frame swaps the waiting placeholder for the frame.</summary>
    public void UpdateFrame(byte[] jpeg)
    {
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(jpeg);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            TeacherFrameImage.Source = bmp;
            TeacherFrameImage.Visibility = Visibility.Visible;
            WaitingPanel.Visibility = Visibility.Collapsed;
        }
        catch
        {
            // Decode failure: leave the waiting placeholder up, swallow.
        }
    }

    private void Leave_Click(object sender, RoutedEventArgs e)
    {
        // Phase 15-B/C: Leave is local-only.  Closing the window leaves the
        // student in tray-idle; the teacher's End broadcast would close it
        // anyway when the session ends.  Phase 15-D wires an opt-out envelope.
        Close();
    }

    /// <summary>Phase 15-E step 4 — render the floating emoji over the
    /// teacher tile for ~3 seconds.  A second reaction during the window
    /// replaces the first.  Called from MainWindow.OnIpcMessage on inbound
    /// Reaction envelopes.</summary>
    public void ShowReaction(string emoji)
    {
        ReactionOverlay.Text = emoji ?? "";
        var clearTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        clearTimer.Tick += (s, e) =>
        {
            clearTimer.Stop();
            if (ReactionOverlay.Text == emoji) ReactionOverlay.Text = "";
        };
        clearTimer.Start();
    }
}
