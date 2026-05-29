using AForge.Video;
using AForge.Video.DirectShow;
using ClassroomCtrl.Networking;
using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;

namespace ClassroomCtrl.Teacher.Services;

public record CameraDeviceDescriptor(string Moniker, string Name);

/// <summary>Phase 16-C — mode-aware emission selector.  One physical capture,
/// two possible wire paths driven by which mode the teacher is in.</summary>
public enum CamRouting
{
    /// <summary>Classroom 9.5 — emits 0x0460-0x0462 (CameraStart/Frame/Stop);
    /// pops a cam window on each student.</summary>
    Classroom = 0,
    /// <summary>Conference 16-C — emits 0x0680-0x0682 (ConferenceCameraStart/
    /// Frame/Stop); routes to the matching tile in the gallery surface.</summary>
    Conference = 1,
}

/// <summary>
/// Phase 9.5: Webcam capture via DirectShow → JPEG → broadcast to all students.
/// Wraps AForge.Video.DirectShow.VideoCaptureDevice.
///
/// Phase 16-C — gained <see cref="Routing"/> so the same capture pipeline can
/// feed either the Classroom 9.5 wire (0x0460-0x0462) or the Conference
/// 16-C wire (0x0680-0x0682) depending on UI mode.  Mirrors the
/// 16-B+ ScreenBroadcaster three-way switch pattern.
/// </summary>
public class CameraBroadcastService : IDisposable
{
    private readonly ILogger<CameraBroadcastService>? _logger;
    private readonly ControlServer _server;
    private VideoCaptureDevice? _device;
    private long _frameSeq;
    private int _busy;     // 0/1 lock to drop overlapping frames

    public bool IsActive { get; private set; }
    public int JpegQuality { get; set; } = 70;

    /// <summary>Phase 16-C — emission target.  Read on every frame + on
    /// Start/Stop so the broadcaster picks the right wire envelope.  Set by
    /// UI BEFORE Start() when the teacher is in Conference mode; reset on
    /// Stop().  Mutually exclusive at the UI level (Classroom and Conference
    /// modes are exclusive).</summary>
    public CamRouting Routing { get; set; } = CamRouting.Classroom;

    /// <summary>Phase 16-C — session id stamped into the Conference Start
    /// envelope so receivers verify they're in the same session before
    /// allocating a decoder.  Set alongside <see cref="Routing"/> when in
    /// Conference mode; <see cref="Guid.Empty"/> when in Classroom mode.</summary>
    public Guid ConferenceSessionId { get; set; }

    /// <summary>Phase 16-C — display name carried in the Conference Start
    /// envelope so receivers can label the tile without resolving sender→name
    /// independently.  Set alongside <see cref="Routing"/>.</summary>
    public string ConferenceSourceName { get; set; } = "";

    /// <summary>Phase 14-B (Tier 1) — last error from the AForge layer.  Set
    /// by <see cref="Start"/> on failure and by the runtime
    /// <c>VideoSourceError</c> handler when the cam fails mid-stream
    /// (cam unplugged, driver crash, permission revoked).  Surfaced to the
    /// teacher UI verbatim by the toolbar's start-failed message box +
    /// dialog error.</summary>
    public string LastError { get; private set; } = "";

    /// <summary>Phase 14-B (Tier 1) — fired when capture stops due to a
    /// runtime error (NOT a clean Stop() call).  Teacher's MainViewModel
    /// subscribes to refresh the toolbar button text + drop the privacy
    /// banner without the user clicking Stop.</summary>
    public event Action? StoppedDueToError;

    public CameraBroadcastService(ControlServer server, ILogger<CameraBroadcastService>? logger = null)
    {
        _server = server;
        _logger = logger;
    }

    public List<CameraDeviceDescriptor> EnumerateDevices()
    {
        var list = new List<CameraDeviceDescriptor>();
        try
        {
            var infos = new FilterInfoCollection(FilterCategory.VideoInputDevice);
            foreach (FilterInfo info in infos)
                list.Add(new CameraDeviceDescriptor(info.MonikerString, info.Name));
        }
        catch (Exception ex) { _logger?.LogWarning(ex, "EnumerateDevices failed"); }
        return list;
    }

    public bool Start(string moniker, int width, int height, int fps)
    {
        if (IsActive) return false;
        LastError = "";
        try
        {
            _device = new VideoCaptureDevice(moniker);
            // Pick the closest matching capability if available.
            var cap = _device.VideoCapabilities
                .OrderBy(c => Math.Abs(c.FrameSize.Width - width) + Math.Abs(c.FrameSize.Height - height))
                .FirstOrDefault();
            if (cap != null) _device.VideoResolution = cap;

            _device.NewFrame += OnNewFrame;
            // Phase 14-B (Tier 1) — surface driver-level errors (cam unplugged,
            // permission revoked, another app grabbed the device) so the UI can
            // auto-recover instead of silently freezing on a stale last frame.
            _device.VideoSourceError += OnVideoSourceError;
            _device.Start();
            IsActive = true;

            // Phase 16-C — mode-aware Start envelope.  Classroom emits 0x0460
            // (pops a cam window on every student); Conference emits 0x0680
            // (lands in the gallery tile keyed by sender id).  ConferenceSessionId
            // + ConferenceSourceName are read here so the UI must set them before
            // Start when Routing = Conference.
            if (Routing == CamRouting.Conference)
            {
                var startMsg = new ConferenceCameraStartMessage
                {
                    SessionId = ConferenceSessionId,
                    SourceEndpointId = Guid.Empty,   // server stamps with _teacherId
                    SourceName = ConferenceSourceName,
                    Width = width,
                    Height = height,
                    Fps = fps,
                };
                _ = _server.BroadcastConferenceCameraStartAsync(startMsg, CancellationToken.None);
            }
            else
            {
                var startMsg = new CameraStartMessage { Width = width, Height = height, Fps = fps };
                _ = _server.BroadcastCameraStartAsync(startMsg, CancellationToken.None);
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Camera Start failed");
            LastError = ex.Message;
            return false;
        }
    }

    public void Stop()
    {
        if (!IsActive) return;
        // Phase 16-C — snapshot the mode flag BEFORE we tear down so the Stop
        // envelope picks the right wire path even after the device cleanup.
        var wasConference = Routing == CamRouting.Conference;
        try
        {
            if (_device != null)
            {
                _device.SignalToStop();
                _device.WaitForStop();
                _device.NewFrame -= OnNewFrame;
                _device.VideoSourceError -= OnVideoSourceError;
                _device = null;
            }
        }
        catch { }
        IsActive = false;
        if (wasConference)
        {
            _ = _server.BroadcastConferenceCameraStopAsync(CancellationToken.None);
        }
        else
        {
            _ = _server.BroadcastCameraStopAsync(CancellationToken.None);
        }
        // Reset to Classroom default so a stale Conference flag doesn't carry
        // into the next Start() if the UI flow forgets to set it.
        Routing = CamRouting.Classroom;
        ConferenceSessionId = Guid.Empty;
        ConferenceSourceName = "";
    }

    /// <summary>Phase 14-B (Tier 1) — runtime-error path.  AForge fires this on
    /// driver crashes, sudden cam unplugs, and permission-revoked scenarios.
    /// We auto-stop (so the privacy banner drops + the toolbar returns to
    /// "Start") and fire <see cref="StoppedDueToError"/> so the UI can
    /// surface the cause.</summary>
    private void OnVideoSourceError(object? sender, VideoSourceErrorEventArgs e)
    {
        _logger?.LogWarning("Camera source error: {Desc}", e.Description);
        LastError = e.Description ?? "Camera source error";
        try { Stop(); } catch { }
        try { StoppedDueToError?.Invoke(); } catch { }
    }

    private void OnNewFrame(object? sender, NewFrameEventArgs e)
    {
        // Drop frames that arrive while we're still encoding/sending the previous one.
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        try
        {
            using var ms = new MemoryStream();
            EncodeJpeg(e.Frame, ms, JpegQuality);
            var jpeg = ms.ToArray();
            Interlocked.Increment(ref _frameSeq);
            // Phase 16-C — mode-aware emission.  One encode, two possible wire
            // paths; the encode + JPEG bytes are identical so a future polish
            // round could even fan out to BOTH (mixed-mode UX) without
            // re-encoding.
            if (Routing == CamRouting.Conference)
            {
                var msg = new ConferenceCameraFrameMessage
                {
                    SourceEndpointId = Guid.Empty,   // server stamps with _teacherId
                    JpegData = jpeg,
                    TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                _ = _server.BroadcastConferenceCameraFrameAsync(msg, CancellationToken.None);
            }
            else
            {
                var msg = new CameraFrameMessage
                {
                    JpegData = jpeg,
                    TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                _ = _server.BroadcastCameraFrameAsync(msg, CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Camera frame encode failed");
            LastError = ex.Message;
        }
        finally { Interlocked.Exchange(ref _busy, 0); }
    }

    private static void EncodeJpeg(Bitmap bmp, Stream output, int quality)
    {
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
        using var prm = new EncoderParameters(1);
        prm.Param[0] = new EncoderParameter(Encoder.Quality, (long)quality);
        bmp.Save(output, codec, prm);
    }

    public void Dispose() => Stop();
}
