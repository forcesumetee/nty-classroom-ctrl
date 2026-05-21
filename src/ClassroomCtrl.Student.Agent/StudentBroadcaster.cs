using ClassroomCtrl.Shared.Codec;
using ClassroomCtrl.Shared.Protocol;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 4 Part 2: Captures this student's screen and sends JPEG frames upstream
/// (Agent → Service → Teacher) on demand. Started when the teacher requests
/// "View Full Screen", stopped when the teacher closes the viewer.
///
/// Pipeline:
///   Capture (GDI BitBlt) → resize 1280×720 → JPEG quality 60 → IPC to Service
///
/// Default rate: 4 FPS (matches Teacher's ScreenBroadcaster).
/// </summary>
// TODO BUG-001: H.264 mode currently hangs ในเส้นทาง Student → Teacher.
// Tracked - workaround ใน Teacher MainViewModel (ใช้ MJPEG เสมอใน ViewStudentScreen).
// ตรวจ encoder lifecycle, ForceKeyframe timing, frame send pipeline.
public class StudentBroadcaster : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private int _frameSeq;

    /// <summary>Native 1080p so the teacher's fullscreen student view doesn't upscale-blur.</summary>
    public int TargetWidth { get; set; } = 1920;
    public int TargetHeight { get; set; } = 1080;
    public long JpegQuality { get; set; } = 60L;
    /// <summary>
    /// Capture rate. Bumped to 4 FPS in Part 4 — OpenH264 doesn't behave reliably below ~3 FPS
    /// (frame-skip + IDR-interval interactions can leave the decoder waiting forever).
    /// Phase 10.14 (Item 3) — bumped to 6 FPS for noticeably smoother live view at Teacher
    /// (perceived ~50% smoother).  Effectively MJPEG-only in practice: Teacher's MainViewModel
    /// forces MJPEG for ViewStudentScreen as the BUG-001 workaround, so the H.264 path is
    /// dormant.  Safe to revert to 4 if/when BUG-001 is fixed and H.264 turns out to be
    /// FPS-sensitive at the new rate.  Bandwidth at 1920×1080 q60 ≈ 30–50 KB/frame × 6 =
    /// ~180–300 KB/s per student; 30 students ≈ 9 MB/s, comfortable on 802.11n+ Wi-Fi.
    /// </summary>
    public int FramesPerSecond { get; set; } = 6;

    /// <summary>Codec used for current/next stream — set by MainWindow before Start.</summary>
    public VideoCodec Codec { get; set; } = VideoCodec.Mjpeg;

    /// <summary>Target bitrate for H.264 mode (CBR). Ignored for MJPEG.</summary>
    public int H264BitrateBps { get; set; } = 500_000;

    private VideoCodec _activeCodec = VideoCodec.Mjpeg;
    private H264EncoderWrapper? _h264;

    public bool IsBroadcasting => _captureTask is { IsCompleted: false };

    public void Start()
    {
        if (IsBroadcasting) return;

        _cts = new CancellationTokenSource();
        _frameSeq = 0;
        _activeCodec = Codec;

        if (_activeCodec == VideoCodec.H264)
        {
            try
            {
                _h264 = new H264EncoderWrapper(TargetWidth, TargetHeight, H264BitrateBps, FramesPerSecond);
                _h264.ForceKeyframe();  // guarantee first emitted frame is IDR
                IpcClient.LogToFile($"[StudentBroadcaster] H.264 encoder initialized (bitrate={H264BitrateBps} bps)");
            }
            catch (Exception ex)
            {
                IpcClient.LogToFile($"[StudentBroadcaster] H.264 init failed, falling back to MJPEG: {ex.Message}");
                _h264 = null;
                _activeCodec = VideoCodec.Mjpeg;
            }
        }

        _captureTask = Task.Run(() => CaptureLoopAsync(_cts.Token));
        IpcClient.LogToFile($"[StudentBroadcaster] Started ({_activeCodec} {FramesPerSecond} FPS, {TargetWidth}x{TargetHeight})");
    }

    public void Stop()
    {
        if (!IsBroadcasting) return;

        _cts?.Cancel();
        try { _captureTask?.Wait(2000); } catch { }
        _captureTask = null;

        _h264?.Dispose();
        _h264 = null;

        IpcClient.LogToFile($"[StudentBroadcaster] Stopped (sent {_frameSeq} frames)");
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        ImageCodecInfo? jpegCodec = null;
        EncoderParameters? encoderParams = null;
        if (_activeCodec == VideoCodec.Mjpeg)
        {
            foreach (var c in ImageCodecInfo.GetImageEncoders())
            {
                if (c.MimeType == "image/jpeg") { jpegCodec = c; break; }
            }
            if (jpegCodec == null)
            {
                IpcClient.LogToFile("[StudentBroadcaster] No JPEG codec available");
                return;
            }
            encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
        }

        var intervalMs = 1000 / Math.Max(1, FramesPerSecond);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var bmp = CaptureFrame();
                if (bmp != null && App.Ipc != null)
                {
                    byte[]? data;
                    bool isKeyframe;

                    if (_activeCodec == VideoCodec.H264 && _h264 != null)
                    {
                        if (!_h264.Encode(bmp, out data, out isKeyframe) || data.Length == 0)
                        {
                            // Encoder produced no output this tick (possible at FPS<3 with frame-skip).
                            // Log first few occurrences to aid diagnosis without flooding.
                            if (_frameSeq < 3)
                                IpcClient.LogToFile($"[StudentBroadcaster] H.264 encode produced 0 bytes (frame {_frameSeq})");
                            data = null;
                            isKeyframe = false;
                        }
                        else if (_frameSeq < 5 || isKeyframe)
                        {
                            IpcClient.LogToFile($"[StudentBroadcaster] H.264 frame seq={_frameSeq + 1} bytes={data.Length} keyframe={isKeyframe}");
                        }
                    }
                    else
                    {
                        data = EncodeJpeg(bmp, jpegCodec!, encoderParams!);
                        isKeyframe = true;
                    }

                    if (data != null)
                    {
                        _frameSeq++;
                        var frame = new ScreenStreamFrameMessage
                        {
                            FrameData = data,
                            Width = TargetWidth,
                            Height = TargetHeight,
                            TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            FrameSeq = _frameSeq,
                            Codec = _activeCodec,
                            IsKeyframe = isKeyframe,
                        };
                        var bytes = MessagePack.MessagePackSerializer.Serialize(frame);
                        var env = Envelope.Create(MessageType.StudentStreamFrame, bytes, Guid.Empty);
                        await App.Ipc.SendAsync(env, ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                IpcClient.LogToFile($"[StudentBroadcaster] frame error: {ex.Message}");
            }

            try { await Task.Delay(intervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>Capture virtual screen and resize to target (32bpp BGRA).</summary>
    private Bitmap? CaptureFrame()
    {
        try
        {
            var bounds = SystemInformation.VirtualScreen;
            if (bounds.Width <= 0 || bounds.Height <= 0) return null;

            using var srcBmp = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(srcBmp))
            {
                g.CopyFromScreen(bounds.Left, bounds.Top, 0, 0,
                    new System.Drawing.Size(bounds.Width, bounds.Height),
                    CopyPixelOperation.SourceCopy);
            }

            var dstBmp = new Bitmap(TargetWidth, TargetHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dstBmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bicubic;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(srcBmp, 0, 0, TargetWidth, TargetHeight);
            }
            return dstBmp;
        }
        catch (Exception ex)
        {
            IpcClient.LogToFile($"[StudentBroadcaster] capture failed: {ex.Message}");
            return null;
        }
    }

    private static byte[]? EncodeJpeg(Bitmap bmp, ImageCodecInfo codec, EncoderParameters encParams)
    {
        try
        {
            using var ms = new MemoryStream();
            bmp.Save(ms, codec, encParams);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            IpcClient.LogToFile($"[StudentBroadcaster] JPEG encode failed: {ex.Message}");
            return null;
        }
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
