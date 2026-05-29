using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Protocol;
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

    /// <summary>Phase 16-B+ step 10 — student-side ConferenceShareStart hook.
    /// Sets the gallery VM's ActiveShareEndpointId + name so the layout
    /// switches from tile-mode to ConferenceShareView.</summary>
    public void OnShareStart(Guid sourceEndpointId, string sourceName)
    {
        _vm.ConferenceGallery.ActiveShareEndpointId = sourceEndpointId;
        _vm.ConferenceGallery.ActiveShareSourceName = string.IsNullOrWhiteSpace(sourceName)
            ? Loc.Get("Conf_TeacherDisplayName", "Teacher")
            : sourceName;
        _vm.ConferenceGallery.ActiveShareFrame = null;
    }

    /// <summary>Phase 16-B+ step 10 — student-side ConferenceShareFrame hook.
    /// Decodes JPEG bytes to a BitmapImage and pushes it into the gallery's
    /// ActiveShareFrame so ConferenceShareView re-renders.  H.264 is logged
    /// + skipped today (the default Teacher codec is MJPEG; H.264 decode
    /// for Conference share falls back to the "Waiting…" placeholder until
    /// a future polish round wires the existing 11-B H264Decoder here).</summary>
    public void OnShareFrame(Guid sourceEndpointId, byte[] frameData, VideoCodec codec)
    {
        if (codec != VideoCodec.Mjpeg)
        {
            // H.264 Conference-share decode deferred; the placeholder stays.
            return;
        }
        try
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(frameData);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();

            if (_vm.ConferenceGallery.ActiveShareEndpointId == null)
            {
                // Late frame after Stop / before Start: synthesize a Start
                // anchor so the layout still swaps.  Banner name falls
                // back to the generic display because we don't know
                // sourceName here (the Start envelope carries it).
                _vm.ConferenceGallery.ActiveShareEndpointId = sourceEndpointId;
                _vm.ConferenceGallery.ActiveShareSourceName =
                    Loc.Get("Conf_TeacherDisplayName", "Teacher");
            }
            _vm.ConferenceGallery.ActiveShareFrame = bmp;
        }
        catch
        {
            // Decode failure: leave whatever frame was last decoded in place
            // so a transient corrupt frame doesn't blank the share.
        }
    }

    /// <summary>Phase 16-B+ step 10 — student-side ConferenceShareStop hook.
    /// Clears ActiveShareEndpointId; ConferenceGalleryView's layout
    /// switcher returns to tile-mode.</summary>
    public void OnShareStop()
    {
        _vm.ConferenceGallery.ActiveShareEndpointId = null;
        _vm.ConferenceGallery.ActiveShareSourceName = "";
        _vm.ConferenceGallery.ActiveShareFrame = null;
    }
}
