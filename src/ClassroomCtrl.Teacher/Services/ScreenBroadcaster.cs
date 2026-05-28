using ClassroomCtrl.Shared.Capture;
using ClassroomCtrl.Shared.Codec;
using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
    // Phase 11-B inc2 part 1 — was `H264EncoderWrapper? _h264;` with an inline
    // MJPEG fallback path.  Now both codecs go through IVideoEncoder; the
    // factory picks the impl from _activeCodec at Start().
    private IVideoEncoder? _encoder;

    // Phase 11-B inc3 — capture goes through IScreenCapturer (GDI or DXGI) so
    // the broadcaster no longer owns GetSystemMetrics + CopyFromScreen.
    private IScreenCapturer? _capturer;

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
    public bool ForceKeyframe() => _encoder?.ForceKeyframe() ?? false;

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
            JpegQuality = MJpegEncoder.MapBitrateToJpegQuality(adaptive.CurrentBitrateBps);
            adaptive.BitrateChanged += OnAdaptiveBitrateChanged;
        }

        // Phase 11-B inc2 part 1 — go through the factory + IVideoEncoder
        // instead of the prior inline H264/MJPEG branches.  Same fallback story:
        // if H.264 init throws, drop to MJPEG and stamp _activeCodec accordingly
        // so frame messages carry the right Codec on the wire.
        // Phase 11-B inc2 part 2 — factory now also considers App.UseHardwareH264
        // and reports back which encoder it actually selected so this Start log
        // accurately reflects HW vs SW vs MJPEG.
        string activeEncoderDesc;
        try
        {
            _encoder = VideoEncoderFactory.Create(
                _activeCodec, TargetWidth, TargetHeight,
                FramesPerSecond, H264BitrateBps, JpegQuality,
                useHardwareH264: App.UseHardwareH264,
                out activeEncoderDesc);
            if (_activeCodec == VideoCodec.H264)
            {
                _encoder.ForceKeyframe();  // guarantee first emitted frame is IDR
                _logger.LogInformation("H.264 encoder initialized (bitrate={Bitrate} bps)", H264BitrateBps);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Codec} encoder init failed — falling back to MJPEG", _activeCodec);
            _encoder?.Dispose();
            _activeCodec = VideoCodec.Mjpeg;
            // Force useHardwareH264:false on the fallback so we can't loop back into MF.
            _encoder = VideoEncoderFactory.Create(
                VideoCodec.Mjpeg, TargetWidth, TargetHeight,
                FramesPerSecond, H264BitrateBps, JpegQuality,
                useHardwareH264: false,
                out activeEncoderDesc);
        }

        // Phase 11-B inc3 — capture goes through IScreenCapturer.  Teacher
        // captures the primary monitor; UseDxgiCapture flag (HKCU) picks DXGI
        // when on, GDI otherwise.  Factory's try/catch handles DXGI-init
        // failure by falling back to GDI permanently for this session.
        _capturer = ScreenCapturerFactory.Create(
            ScreenCaptureSource.Primary,
            useDxgi: App.UseDxgiCapture,
            out string activeCaptureDesc);

        // Notify students to open viewer
        _ = _server.BroadcastScreenStreamControlAsync(start: true, _cts.Token);

        _captureTask = Task.Run(() => CaptureLoopAsync(_cts.Token));
        _logger.LogInformation("Screen broadcasting started ({Codec} {Fps} FPS, {W}x{H}, {Bps} bps, encoder={Encoder}, capture={Capture})",
            _activeCodec, FramesPerSecond, TargetWidth, TargetHeight, CurrentBitrateBps, activeEncoderDesc, activeCaptureDesc);
        App.LogDebug($"[Broadcast] Active encoder: {activeEncoderDesc} (UseHardwareH264 flag={App.UseHardwareH264})");
        App.LogDebug($"[Broadcast] Active capture: {activeCaptureDesc} (UseDxgiCapture flag={App.UseDxgiCapture})");
    }

    public void Stop()
    {
        if (!IsBroadcasting) return;

        var adaptive = App.AdaptiveBitrate;
        if (adaptive != null) adaptive.BitrateChanged -= OnAdaptiveBitrateChanged;

        _cts?.Cancel();
        try { _captureTask?.Wait(2000); } catch { }
        _captureTask = null;

        _encoder?.Dispose();
        _encoder = null;
        _capturer?.Dispose();
        _capturer = null;

        // Notify students to close viewer
        _ = _server.BroadcastScreenStreamControlAsync(start: false, CancellationToken.None);
        _logger.LogInformation("Screen broadcasting stopped (sent {Frames} frames)", _frameSeq);
    }

    private void OnAdaptiveBitrateChanged(object? sender, BitrateChangedEventArgs e)
    {
        if (_encoder == null) return;
        try
        {
            // Phase 11-B inc2 part 1 — single SetMaxBitrate call regardless of codec.
            // The encoder impl handles the mapping (OpenH264Encoder forwards CBR,
            // MJpegEncoder re-derives JPEG quality from bps via the shared formula).
            // Broadcaster's H264BitrateBps / JpegQuality fields are display-only
            // mirrors for the startup log + CurrentBitrateBps; preserved exactly so
            // the existing log lines stay byte-identical.
            _encoder.SetMaxBitrate(e.NewBitrateBps);
            if (_activeCodec == VideoCodec.H264)
            {
                H264BitrateBps = e.NewBitrateBps;
                _logger.LogInformation("H.264 max bitrate updated to {Bps} ({Reason})", e.NewBitrateBps, e.Reason);
            }
            else if (_activeCodec == VideoCodec.Mjpeg)
            {
                JpegQuality = MJpegEncoder.MapBitrateToJpegQuality(e.NewBitrateBps);
                _logger.LogInformation("JPEG quality updated to {Q} for {Bps} bps target ({Reason})",
                    JpegQuality, e.NewBitrateBps, e.Reason);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SetMaxBitrate failed");
        }
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
                using var bmp = _capturer?.Capture(TargetWidth, TargetHeight);
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

                    // Phase 11-B inc2 part 1 — single Encode call through IVideoEncoder.
                    // Returning null preserves the pre-refactor "encoder produced no
                    // output this tick" silent-skip semantics (OpenH264 frame-skip /
                    // transient encode error) — the previous `if (data != null)` gate
                    // was equivalent.
                    var encoded = _encoder?.Encode(bmp);
                    if (encoded.HasValue)
                    {
                        var data = encoded.Value.Data;
                        var isKeyframe = encoded.Value.IsKeyframe;
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

    // Phase 11-B inc3 — CaptureFrame + GetSystemMetrics + _firstCaptureLogged
    // moved into GdiScreenCapturer (Primary source) so the broadcaster no longer
    // owns DPI / metrics resolution.  The first-frame DPI-mismatch warning is
    // not re-emitted by the capturer (the GetSystemMetrics call is the same;
    // if it returned DIPs in inc2, it still does so now) — kept silent.

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

    // Phase 11-B inc2 part 1 — EncodeJpeg + GetJpegEncoder removed; logic now
    // lives in MJpegEncoder.  ExtractBgraPixels is still used by the recording
    // tee (RawFrameCaptured event) and stays here.

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}