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

    /// <summary>Phase 16-C — exposes the shell VM so MainWindow can wire the
    /// own-cam toggle action + read IsBroadcastingCamera back when the
    /// StudentCameraBroadcaster lifecycle changes externally.</summary>
    public StudentConferenceShellViewModel ShellViewModel => _vm;

    public ConferenceGalleryWindow(Guid sessionId, Guid teacherEndpointId, string hostName,
                                   Guid selfEndpointId, string selfDisplayName)
    {
        InitializeComponent();
        SessionId = sessionId;
        _vm = new StudentConferenceShellViewModel(sessionId, teacherEndpointId, hostName)
        {
            SelfEndpointId = selfEndpointId,
            SelfDisplayName = string.IsNullOrWhiteSpace(selfDisplayName)
                ? Environment.MachineName
                : selfDisplayName,
        };
        _vm.RequestClose += (_, _) => Close();
        DataContext = _vm;

        // Phase 22.2-B / Phase 22.3-B — seed the student's own tile at
        // window-open time instead of lazily creating it on the first
        // cam/mic/hand event.  If selfEndpointId is Guid.Empty here
        // (Phase 22.3-B repro: ConferenceStart arrives as a broadcast
        // and lands BEFORE _myEndpointId is captured by MainWindow from
        // any targeted envelope, so MainWindow.OpenConferenceWindow
        // passes Empty), the seed is skipped now and MainWindow calls
        // AdoptSelfEndpoint(...) later when the capture finally happens.
        // EnsurePeerTile is idempotent; the later Set*Live calls just
        // refresh state on the existing tile.
        EnsureSelfTileIfReady();

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

    /// <summary>Phase 22.3-B — idempotent self-tile seed.  Adds the
    /// student's own tile to the gallery iff SelfEndpointId is now
    /// non-empty AND a tile for that id doesn't already exist.  Safe
    /// to call repeatedly: returns true on first seed, false otherwise.
    /// Called from this window's ctor AND from MainWindow's
    /// AdoptSelfEndpoint hook when _myEndpointId is finally captured
    /// later than ConferenceStart.</summary>
    public bool EnsureSelfTileIfReady()
    {
        var id = _vm.SelfEndpointId;
        if (id == Guid.Empty) return false;
        if (_vm.ConferenceGallery.Tiles.Any(t => t.EndpointId == id)) return false;
        EnsurePeerTile(id, _vm.SelfDisplayName, isSelf: true);
        return true;
    }

    /// <summary>Phase 22.3-B — late-binding endpoint adoption.  Called
    /// from MainWindow's _myEndpointId capture when the very first
    /// targeted envelope arrives AFTER the Conference window was
    /// already opened by a ConferenceStart broadcast.  Updates the
    /// shell VM's SelfEndpointId so the self-tile setup pathway has
    /// a non-empty id to work with, then seeds the tile.  No-op if
    /// the VM's SelfEndpointId was already set (only the first call
    /// wins; capturing the same id twice would just be redundant).</summary>
    public void AdoptSelfEndpoint(Guid myEndpointId)
    {
        if (myEndpointId == Guid.Empty) return;
        if (_vm.SelfEndpointId != Guid.Empty) return;
        _vm.SelfEndpointId = myEndpointId;
        IpcClient.LogToFile($"[ConferenceGalleryWindow] AdoptSelfEndpoint {myEndpointId} — seeding self-tile");
        EnsureSelfTileIfReady();
    }

    /// <summary>Phase 16-C — seed / pick up a peer cam tile so the gallery has a
    /// slot to receive frames into.  Called from MainWindow on
    /// ConferenceCameraStart dispatch + from the self-tile setup at Start
    /// time.  Idempotent on re-Start.</summary>
    public ConferenceTileViewModel EnsurePeerTile(Guid sourceId, string displayName, bool isSelf)
    {
        var existing = _vm.ConferenceGallery.Tiles.FirstOrDefault(t => t.EndpointId == sourceId);
        if (existing != null)
        {
            // Refresh display name in case the late-joiner replay carried a
            // newer name; leave isSelf alone (immutable on the tile).
            if (!string.IsNullOrWhiteSpace(displayName)) existing.DisplayName = displayName;
            return existing;
        }
        var tile = new ConferenceTileViewModel(sourceId,
            string.IsNullOrWhiteSpace(displayName) ? "Participant" : displayName, isSelf);
        _vm.ConferenceGallery.Tiles.Add(tile);
        return tile;
    }

    /// <summary>Phase 16-C — peer cam Start hook.  Adds a tile if the sender
    /// is new (e.g. a student we hadn't seen yet) and seeds it with display
    /// name from the envelope so cam-off placeholder shows their initial.</summary>
    public void OnPeerCameraStart(Guid sourceId, string sourceName)
    {
        EnsurePeerTile(sourceId, sourceName, isSelf: false);
    }

    /// <summary>Phase 16-C — peer cam Frame hook.  Decode JPEG, route to the
    /// matching tile by sender id, flip <c>IsCamLive=true</c>.  Synthesizes
    /// the tile if a late frame slips ahead of the Start envelope (transient
    /// race / re-ordered relay).</summary>
    public void OnPeerCameraFrame(Guid sourceId, byte[] jpeg)
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

            var tile = EnsurePeerTile(sourceId, "", isSelf: false);
            tile.JpegFrame = bmp;
            tile.IsCamLive = true;
        }
        catch
        {
            // Decode failure: leave the placeholder up, swallow.
        }
    }

    /// <summary>Phase 16-C — peer cam Stop hook.  Clears IsCamLive so the
    /// cam-off placeholder swaps in; preserves the tile + display name so
    /// the participant stays in the gallery.</summary>
    public void OnPeerCameraStop(Guid sourceId)
    {
        var tile = _vm.ConferenceGallery.Tiles.FirstOrDefault(t => t.EndpointId == sourceId);
        if (tile != null)
        {
            tile.IsCamLive = false;
            tile.JpegFrame = null;
        }
    }

    /// <summary>Phase 16-C — self-tile cam-live state mirror.  Called by
    /// MainWindow when the local StudentCameraBroadcaster start/stop event
    /// fires so the toolbar's 📷 button + self-tile preview stay in sync.</summary>
    public void SetSelfCamLive(bool live)
    {
        _vm.IsBroadcastingCamera = live;
        var selfId = _vm.SelfEndpointId;
        if (selfId == Guid.Empty) return;
        var tile = EnsurePeerTile(selfId, _vm.SelfDisplayName, isSelf: true);
        if (!live)
        {
            tile.IsCamLive = false;
            tile.JpegFrame = null;
        }
    }

    /// <summary>Phase 16-X (Bug F fix, 2026-06-01) — self-tile mic-live mirror.
    /// Subscribed in MainWindow.ConferenceStart via MainWindow.MicStateChanged
    /// so every Phase 4 Part 3b StudentAudioBroadcaster transition lands on
    /// the toolbar 🎙 button + the self-tile mic indicator without a wire
    /// round-trip.</summary>
    public void SetSelfMicLive(bool live)
    {
        _vm.IsMicOn = live;
        var selfId = _vm.SelfEndpointId;
        if (selfId == Guid.Empty) return;
        var tile = EnsurePeerTile(selfId, _vm.SelfDisplayName, isSelf: true);
        tile.IsMicLive = live;
    }

    /// <summary>Phase 16-X (Bug H fix, 2026-06-01) — self-tile hand-raise
    /// mirror.  Called by MainWindow after every _handRaised transition
    /// (toolbar click, classic floating-window click, or inbound
    /// teacher Recognize / HandLower).  Updates the toolbar pill state
    /// (IsHandRaised on VM) AND the self-tile badge (IsHandRaised on
    /// tile) so the user sees their hand state consistently.</summary>
    public void SetSelfHandRaised(bool raised)
    {
        _vm.IsHandRaised = raised;
        var selfId = _vm.SelfEndpointId;
        if (selfId == Guid.Empty) return;
        var tile = EnsurePeerTile(selfId, _vm.SelfDisplayName, isSelf: true);
        tile.IsHandRaised = raised;
        tile.HandRaisedAt = raised ? DateTime.UtcNow : (DateTime?)null;
        _vm.ConferenceGallery.RefreshRaisedHandQueue();
    }

    /// <summary>Phase 16-C — self-tile preview frame.  Called by MainWindow's
    /// StudentCameraBroadcaster OnNewFrame echo so the student sees their
    /// own cam preview without a wire round-trip.</summary>
    public void SetSelfPreviewFrame(byte[] jpeg)
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
            var selfId = _vm.SelfEndpointId;
            if (selfId == Guid.Empty) return;
            var tile = EnsurePeerTile(selfId, _vm.SelfDisplayName, isSelf: true);
            tile.JpegFrame = bmp;
            tile.IsCamLive = true;
        }
        catch { /* transient decode: keep last frame */ }
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
    /// originating participant's tile.
    ///
    /// Phase 16-X (Bug D fix, 2026-05-31) — signature gained
    /// <paramref name="senderId"/> so the reaction lands on the right tile
    /// in the 16-C peer-cam era.  Pre-fix, every reaction (including peer
    /// students') was rendered over the teacher tile because the routing
    /// was hardcoded to <see cref="StudentConferenceShellViewModel.TeacherEndpointId"/>.
    /// Falls back to the teacher tile when the sender's tile isn't in the
    /// gallery yet (defensive — late Reaction envelopes can outrun the
    /// peer-cam Start that would seed the tile).</summary>
    public void ShowReaction(Guid senderId, string emoji)
    {
        var gallery = _vm.ConferenceGallery;
        var tile = gallery.Tiles.FirstOrDefault(t => t.EndpointId == senderId)
                   ?? gallery.Tiles.FirstOrDefault(t => t.EndpointId == _vm.TeacherEndpointId);
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
