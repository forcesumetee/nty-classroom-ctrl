using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Wpf.ViewModels;
using ClassroomCtrl.Student.Agent.ViewModels;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 15-B/C — student-side conference shell.
///
/// Phase 16-B step 7 — fully rewritten to host the Shared.Wpf gallery +
/// toolbar + sidebar (Conference parity with Teacher).  The single-tile
/// Image is replaced by a full ConferenceGalleryView; cam frames from
/// the teacher route to the matching ConferenceTileViewModel by
/// EndpointId.  Phase 16-C peer cam adds tiles for other students.
/// </summary>
public partial class ConferenceGalleryWindow : Window
{
    public Guid SessionId { get; }

    private readonly StudentConferenceShellViewModel _vm;

    public ConferenceGalleryWindow(Guid sessionId, Guid teacherEndpointId, string hostName)
    {
        InitializeComponent();
        SessionId = sessionId;
        _vm = new StudentConferenceShellViewModel(sessionId, teacherEndpointId, hostName);
        _vm.RequestClose += (_, _) => Close();
        DataContext = _vm;

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
    }

    /// <summary>Phase 16-B step 7 — called from <c>MainWindow.xaml.cs</c>
    /// CameraFrame dispatch when this window is open.  Routes the frame
    /// to the matching tile by sender EndpointId (today only the teacher
    /// tile exists; 16-C peer cam adds more).</summary>
    public void UpdateFrame(Guid senderId, byte[] jpeg)
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

            var tile = _vm.ConferenceGallery.Tiles.FirstOrDefault(t => t.EndpointId == senderId);
            if (tile != null)
            {
                tile.JpegFrame = bmp;
                tile.IsCamLive = true;
            }
        }
        catch
        {
            // Decode failure: leave the placeholder up, swallow.
        }
    }

    /// <summary>Phase 15-E step 4/5 — render the floating emoji over the
    /// teacher tile.  Tier 1: routes to the teacher tile only; 16-C
    /// will surface reactions per-sender to the matching tile.</summary>
    public void ShowReaction(string emoji)
    {
        var tile = _vm.ConferenceGallery.Tiles.FirstOrDefault(t => t.EndpointId == _vm.TeacherEndpointId);
        if (tile != null)
        {
            tile.CurrentReactionEmoji = emoji ?? "";
        }
    }
}
