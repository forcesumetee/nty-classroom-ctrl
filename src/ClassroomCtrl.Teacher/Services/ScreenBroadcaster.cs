using ClassroomCtrl.Shared.Codec;
using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 4: Captures Teacher's primary screen and broadcasts JPEG frames to all students.
/// 
/// Pipeline:
///   Capture (GDI BitBlt) → resize 1280×720 → JPEG quality 60 → broadcast via TCP
/// 
/// Default rate: 2 FPS (configurable). Pause when no students connected.
/// </summary>
public class ScreenBroadcaster : IDisposable
{
    private readonly ILogger<ScreenBroadcaster> _logger;
    private readonly ControlServer _server;

    private CancellationTokenSource? _cts;
    private Task? _captureTask;
    private int _frameSeq;

    /// <summary>Codec used for current/next broadcast — read on Start.</summary>
    public VideoCodec Codec { get; set; } = VideoCodec.Mjpeg;

    /// <summary>Target bitrate for H.264 mode (CBR). Ignored for MJPEG.</summary>
    public int H264BitrateBps { get; set; } = 500_000;

    private VideoCodec _activeCodec = VideoCodec.Mjpeg;
    private H264EncoderWrapper? _h264;

    /// <summary>Phase 4 Part 5: Reflects the most recently applied bitrate (informational; updated by adaptive controller).</summary>
    public int CurrentBitrateBps => _activeCodec == VideoCodec.H264 ? H264BitrateBps : MapJpegQualityToApproxBitrate(JpegQuality);

    /// <summary>Phase 5a: Fired after each successful encode, before the TCP broadcast — used by RecordingService to tee the stream to disk.</summary>
    public event EventHandler<(byte[] FrameData, bool IsKeyframe, VideoCodec Codec)>? FrameEncoded;

    /// <summary>
    /// Phase 5 Bug Fix: Fired with raw BGRA pixels BEFORE encoding, when <see cref="TeeRawFrames"/> is enabled.
    /// RecordingService consumes this to feed an FFmpeg stdin pipe (Python-style real-time encode).
    /// Allocation cost is non-trivial (~8 MB/frame at 1080p) so the event only fires when explicitly enabled.
    /// </summary>
    public event EventHandler<(byte[] Bgra, int Width, int Height)>? RawFrameCaptured;

    /// <summary>Set by RecordingService while a session is active. Gates the BGRA buffer allocation.</summary>
    public bool TeeRawFrames { get; set; }

    /// <summary>Phase 5a: Force the next H.264 frame to be an IDR keyframe (delegates to encoder). No-op in MJPEG mode.</summary>
    public bool ForceKeyframe()
    {
        if (_activeCodec != VideoCodec.H264 || _h264 == null) return false;
        return _h264.ForceKeyframe();
    }

    // ─────── Tunable encoding settings ───────
    //
    // Bandwidth profiles (per student):
    //   MJPEG 1280×720  @ 2 FPS @ q60         ≈ 250 kbps  (low quality, fallback)
    //   MJPEG 1600×900  @ 4 FPS @ q65         ≈ 1   Mbps  (medium)
    //   MJPEG 1920×1080 @ 4 FPS @ q65         ≈ 1.5–2 Mbps  (default — pixel-perfect, no upscale)
    //   H.264 1920×1080 @ 4 FPS @ 500 kbps    ≈ 500 kbps  ⭐ (default w/ adaptive ABR — Phase 4 Part 5)
    //
    // 30 students × 500 kbps ≈ 15 Mbps egress on Teacher NIC — fits 100 Mbps LAN with headroom.

    /// <summary>Target frame width in pixels. Native 1080p capture so student fullscreen renders 1:1 without upscale blur.</summary>
    public int TargetWidth { get; set; } = 1920;

    /// <summary>Target frame height in pixels.</summary>
    public int TargetHeight { get; set; } = 1080;

    /// <summary>JPEG encoder quality 0–100 (higher = larger frames, sharper text).</summary>
    public int JpegQuality { get; set; } = 65;

    /// <summary>Capture rate. Higher = smoother motion but more bandwidth.</summary>
    public int FramesPerSecond { get; set; } = 4;

    public bool IsBroadcasting => _captureTask is { IsCompleted: false };

    public ScreenBroadcaster(ILogger<ScreenBroadcaster> logger, ControlServer server)
    {
        _logger = logger;
        _server = server;
    }

    public void Start()
    {
        if (IsBroadcasting) return;

        _cts = new CancellationTokenSource();
        _frameSeq = 0;
        _activeCodec = Codec;

        // Phase 4 Part 5: pull initial bitrate from adaptive controller (if available)
        // and subscribe to live bitrate changes.
        var adaptive = App.AdaptiveBitrate;
        if (adaptive != null)
        {
            H264BitrateBps = adaptive.CurrentBitrateBps;
            JpegQuality = MapBitrateToJpegQuality(adaptive.CurrentBitrateBps);
            adaptive.BitrateChanged += OnAdaptiveBitrateChanged;
        }

        if (_activeCodec == VideoCodec.H264)
        {
            try
            {
                _h264 = new H264EncoderWrapper(TargetWidth, TargetHeight, H264BitrateBps, FramesPerSecond);
                _h264.ForceKeyframe();  // guarantee first emitted frame is IDR
                _logger.LogInformation("H.264 encoder initialized (bitrate={Bitrate} bps)", H264BitrateBps);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "H.264 encoder init failed — falling back to MJPEG");
                _h264 = null;
                _activeCodec = VideoCodec.Mjpeg;
            }
        }

        // Notify students to open viewer
        _ = _server.BroadcastScreenStreamControlAsync(start: true, _cts.Token);

        _captureTask = Task.Run(() => CaptureLoopAsync(_cts.Token));
        _logger.LogInformation("Screen broadcasting started ({Codec} {Fps} FPS, {W}x{H}, {Bps} bps)",
            _activeCodec, FramesPerSecond, TargetWidth, TargetHeight, CurrentBitrateBps);
    }

    public void Stop()
    {
        if (!IsBroadcasting) return;

        var adaptive = App.AdaptiveBitrate;
        if (adaptive != null) adaptive.BitrateChanged -= OnAdaptiveBitrateChanged;

        _cts?.Cancel();
        try { _captureTask?.Wait(2000); } catch { }
        _captureTask = null;

        _h264?.Dispose();
        _h264 = null;

        // Notify students to close viewer
        _ = _server.BroadcastScreenStreamControlAsync(start: false, CancellationToken.None);
        _logger.LogInformation("Screen broadcasting stopped (sent {Frames} frames)", _frameSeq);
    }

    private void OnAdaptiveBitrateChanged(object? sender, BitrateChangedEventArgs e)
    {
        if (_activeCodec == VideoCodec.H264 && _h264 != null)
        {
            try
            {
                _h264.SetMaxBitrate(e.NewBitrateBps);
                H264BitrateBps = e.NewBitrateBps;
                _logger.LogInformation("H.264 max bitrate updated to {Bps} ({Reason})", e.NewBitrateBps, e.Reason);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "SetMaxBitrate failed");
            }
        }
        else if (_activeCodec == VideoCodec.Mjpeg)
        {
            JpegQuality = MapBitrateToJpegQuality(e.NewBitrateBps);
            _logger.LogInformation("JPEG quality updated to {Q} for {Bps} bps target ({Reason})",
                JpegQuality, e.NewBitrateBps, e.Reason);
        }
    }

    /// <summary>Map a target bitrate to a reasonable JPEG quality value (used in MJPEG mode).</summary>
    private static int MapBitrateToJpegQuality(int bps)
    {
        // Linear breakpoints: 200k→q40, 500k→q60, 1M→q75, 2M→q85
        if (bps <= 200_000) return 40;
        if (bps <= 500_000) return 40 + (bps - 200_000) * 20 / 300_000;
        if (bps <= 1_000_000) return 60 + (bps - 500_000) * 15 / 500_000;
        if (bps <= 2_000_000) return 75 + (bps - 1_000_000) * 10 / 1_000_000;
        return 85;
    }

    private static int MapJpegQualityToApproxBitrate(int q)
    {
        // Inverse of MapBitrateToJpegQuality (for diagnostic display)
        if (q <= 40) return 200_000;
        if (q <= 60) return 200_000 + (q - 40) * 300_000 / 20;
        if (q <= 75) return 500_000 + (q - 60) * 500_000 / 15;
        if (q <= 85) return 1_000_000 + (q - 75) * 1_000_000 / 10;
        return 2_000_000;
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        var intervalMs = 1000 / Math.Max(1, FramesPerSecond);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var bmp = CaptureFrame();
                if (bmp != null)
                {
                    // Phase 5 bug-fix: tee raw BGRA pixels to recorder BEFORE encode.
                    // Only allocate the buffer when recording is actually active (~8 MB/frame at 1080p
                    // would otherwise be 32 MB/s of GC pressure for nothing).
                    if (TeeRawFrames && RawFrameCaptured != null)
                    {
                        try
                        {
                            var bgra = ExtractBgraPixels(bmp);
                            RawFrameCaptured.Invoke(this, (bgra, bmp.Width, bmp.Height));
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "RawFrameCaptured tee failed");
                        }
                    }

                    byte[]? data;
                    bool isKeyframe;

                    if (_activeCodec == VideoCodec.H264 && _h264 != null)
                    {
                        if (!_h264.Encode(bmp, out data, out isKeyframe) || data.Length == 0)
                        {
                            data = null;
                            isKeyframe = false;
                        }
                    }
                    else
                    {
                        data = EncodeJpeg(bmp);
                        isKeyframe = true;
                    }

                    if (data != null)
                    {
                        _frameSeq++;

                        // Phase 5a: tee to recording before sending over the wire.
                        // Diagnostic log for first ~5 frames + every keyframe to catch stream drops without spamming.
                        if (_frameSeq <= 5 || isKeyframe)
                        {
                            App.LogDebug($"[Broadcast] FrameEncoded seq={_frameSeq} codec={_activeCodec} size={data.Length} keyframe={isKeyframe}");
                        }
                        try { FrameEncoded?.Invoke(this, (data, isKeyframe, _activeCodec)); }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "FrameEncoded subscriber threw");
                            App.LogDebug($"[Broadcast] FrameEncoded subscriber threw: {ex.Message}");
                        }

                        var msg = new ScreenStreamFrameMessage
                        {
                            FrameData = data,
                            Width = TargetWidth,
                            Height = TargetHeight,
                            TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                            FrameSeq = _frameSeq,
                            Codec = _activeCodec,
                            IsKeyframe = isKeyframe,
                        };
                        await _server.BroadcastScreenFrameAsync(msg, ct);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Capture frame failed");
            }

            try { await Task.Delay(intervalMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private bool _firstCaptureLogged;

    /// <summary>Capture primary screen via GDI and resize to TargetWidth × TargetHeight (32bpp BGRA).</summary>
    private Bitmap? CaptureFrame()
    {
        try
        {
            // Prefer GetSystemMetrics — for a DPI-aware process this returns PHYSICAL pixels.
            // SystemParameters.PrimaryScreenWidth was unreliable in some test runs (returned
            // DIPs, e.g. 1536 instead of 1920 at 125% scaling); GetSystemMetrics + DPI-aware
            // manifest is the well-trodden combo.
            int srcW = GetSystemMetrics(SM_CXSCREEN);
            int srcH = GetSystemMetrics(SM_CYSCREEN);
            if (srcW <= 0 || srcH <= 0)
            {
                srcW = (int)SystemParameters.PrimaryScreenWidth;
                srcH = (int)SystemParameters.PrimaryScreenHeight;
            }
            if (srcW <= 0 || srcH <= 0) return null;

            if (!_firstCaptureLogged)
            {
                _firstCaptureLogged = true;
                int dipW = (int)SystemParameters.PrimaryScreenWidth;
                int dipH = (int)SystemParameters.PrimaryScreenHeight;
                _logger.LogInformation("Screen capture source: GetSystemMetrics={W}x{H}, SystemParameters={DipW}x{DipH}, target={TgtW}x{TgtH}",
                    srcW, srcH, dipW, dipH, TargetWidth, TargetHeight);
                App.LogDebug($"[Capture] First frame: GetSystemMetrics={srcW}x{srcH}, SystemParameters={dipW}x{dipH}, target={TargetWidth}x{TargetHeight}");
                if (dipW != srcW || dipH != srcH)
                {
                    App.LogDebug($"[Capture] WARNING: DIP/physical mismatch — DPI awareness may not be active (WPF caching). Capture is using physical {srcW}x{srcH}.");
                }
            }

            using var srcBmp = new Bitmap(srcW, srcH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(srcBmp))
            {
                g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(srcW, srcH), CopyPixelOperation.SourceCopy);
            }

            // 32bpp BGRA — works for both JPEG encoder and H264Sharp's RgbImage(Bgra)
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
            _logger.LogWarning(ex, "Screen capture failed");
            return null;
        }
    }

    /// <summary>
    /// Lock the bitmap and copy its 32bpp BGRA pixels into a managed byte[]. Caller owns the buffer.
    /// </summary>
    private static byte[] ExtractBgraPixels(Bitmap bgra)
    {
        var rect = new Rectangle(0, 0, bgra.Width, bgra.Height);
        var data = bgra.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            // Stride may include row padding; we copy `Stride * Height` bytes verbatim. FFmpeg's
            // -video_size matches Width × Height, so as long as Stride == Width*4 (which it is for
            // 32bppArgb at multiples of 4), the layout is correct.
            int byteCount = data.Stride * bgra.Height;
            var buf = new byte[byteCount];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, byteCount);
            return buf;
        }
        finally
        {
            bgra.UnlockBits(data);
        }
    }

    private byte[]? EncodeJpeg(Bitmap bmp)
    {
        try
        {
            var jpegEncoder = GetJpegEncoder();
            var encParams = new EncoderParameters(1);
            encParams.Param[0] = new EncoderParameter(Encoder.Quality, (long)JpegQuality);

            using var ms = new MemoryStream();
            bmp.Save(ms, jpegEncoder, encParams);
            return ms.ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "JPEG encode failed");
            return null;
        }
    }

    private static ImageCodecInfo GetJpegEncoder()
    {
        var codecs = ImageCodecInfo.GetImageEncoders();
        foreach (var c in codecs)
        {
            if (c.FormatID == ImageFormat.Jpeg.Guid) return c;
        }
        throw new InvalidOperationException("JPEG encoder not found");
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}