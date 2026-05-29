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

/// <summary>
/// Phase 9.5: Webcam capture via DirectShow → JPEG → broadcast to all students.
/// Wraps AForge.Video.DirectShow.VideoCaptureDevice.
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

            var startMsg = new CameraStartMessage { Width = width, Height = height, Fps = fps };
            _ = _server.BroadcastCameraStartAsync(startMsg, CancellationToken.None);
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
        _ = _server.BroadcastCameraStopAsync(CancellationToken.None);
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
            var msg = new CameraFrameMessage
            {
                JpegData = ms.ToArray(),
                TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            Interlocked.Increment(ref _frameSeq);
            _ = _server.BroadcastCameraFrameAsync(msg, CancellationToken.None);
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
