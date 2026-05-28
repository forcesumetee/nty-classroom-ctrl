using ClassroomCtrl.Shared.Capture;
using ClassroomCtrl.Shared.Codec;
using ClassroomCtrl.Shared.Protocol;
using System;
using System.Drawing;
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
// Phase 11-B inc1 — BUG-001 status: the decoder side that caused the original
// "View Student under H.264 hangs" symptom was fixed in Phase 10.15.2
// (pre-allocated RgbImage in H264DecoderWrapper).  No code change has ever been
// required on this broadcaster — encoder lifecycle, ForceKeyframe timing, and
// the IPC frame pipeline here are all believed correct.  Pending the 2-PC
// runtime validation in Phase 11-B inc1, this path should now work end-to-end.
// TODO(11-B inc1): once the developer's 2-PC test confirms H.264 frames render
// at the teacher's StudentScreenWindow, delete this comment block.
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
    public int H264BitrateBps { get; set; } = 1_500_000;

    private VideoCodec _activeCodec = VideoCodec.Mjpeg;
    // Phase 11-B inc2 part 1 — was `H264EncoderWrapper? _h264;` with an inline
    // MJPEG fallback path.  Both codecs now go through IVideoEncoder; the
    // factory picks the impl from _activeCodec at Start().
    private IVideoEncoder? _encoder;

    // Phase 11-B inc3 — capture goes through IScreenCapturer (GDI or DXGI) so
    // the broadcaster no longer owns SystemInformation.VirtualScreen +
    // CopyFromScreen + Bicubic resize inline.
    private IScreenCapturer? _capturer;

    public bool IsBroadcasting => _captureTask is { IsCompleted: false };

    public void Start()
    {
        if (IsBroadcasting) return;

        _cts = new CancellationTokenSource();
        _frameSeq = 0;
        _activeCodec = Codec;

        // Phase 11-B inc2 part 1 — go through the factory + IVideoEncoder
        // instead of the prior inline H264/MJPEG branches.  Same fallback
        // story: if H.264 init throws (encoder unavailable on this OS / arch),
        // drop to MJPEG and stamp _activeCodec accordingly so frame messages
        // carry the right Codec on the wire.
        // Phase 11-B inc2 part 2 — factory now also considers App.UseHardwareH264
        // and reports back which encoder it actually selected.
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
                IpcClient.LogToFile($"[StudentBroadcaster] H.264 encoder initialized (bitrate={H264BitrateBps} bps)");
            }
        }
        catch (Exception ex)
        {
            IpcClient.LogToFile($"[StudentBroadcaster] {_activeCodec} init failed, falling back to MJPEG: {ex.Message}");
            _encoder?.Dispose();
            _activeCodec = VideoCodec.Mjpeg;
            _encoder = VideoEncoderFactory.Create(
                VideoCodec.Mjpeg, TargetWidth, TargetHeight,
                FramesPerSecond, H264BitrateBps, JpegQuality,
                useHardwareH264: false,
                out activeEncoderDesc);
        }

        // Phase 11-B inc3 — capture goes through IScreenCapturer.  Student
        // historically captured the virtual screen (union of monitors); under
        // DXGI inc3 falls back to primary-only (documented limit).  Factory
        // try/catch handles DXGI-init failure with permanent GDI fallback.
        _capturer = ScreenCapturerFactory.Create(
            ScreenCaptureSource.VirtualScreen,
            useDxgi: App.UseDxgiCapture,
            out string activeCaptureDesc);

        _captureTask = Task.Run(() => CaptureLoopAsync(_cts.Token));
        IpcClient.LogToFile($"[StudentBroadcaster] Started ({_activeCodec} {FramesPerSecond} FPS, {TargetWidth}x{TargetHeight}, encoder={activeEncoderDesc}, capture={activeCaptureDesc}, UseHardwareH264={App.UseHardwareH264}, UseDxgiCapture={App.UseDxgiCapture})");
    }

    public void Stop()
    {
        if (!IsBroadcasting) return;

        _cts?.Cancel();
        try { _captureTask?.Wait(2000); } catch { }
        _captureTask = null;

        _encoder?.Dispose();
        _encoder = null;
        _capturer?.Dispose();
        _capturer = null;

        IpcClient.LogToFile($"[StudentBroadcaster] Stopped (sent {_frameSeq} frames)");
    }

    private async Task CaptureLoopAsync(CancellationToken ct)
    {
        // Phase 11-B inc2 part 1 — JPEG codec lookup + EncoderParameters setup
        // moved into MJpegEncoder's ctor.  The loop body just calls _encoder.Encode.
        var intervalMs = 1000 / Math.Max(1, FramesPerSecond);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var bmp = _capturer?.Capture(TargetWidth, TargetHeight);
                if (bmp != null && App.Ipc != null && _encoder != null)
                {
                    var encoded = _encoder.Encode(bmp);
                    if (encoded == null)
                    {
                        // Encoder produced no output this tick (e.g. OpenH264 frame-skip
                        // under load).  Pre-refactor this logged only on the H.264
                        // path; preserve that — MJpegEncoder never returns null in
                        // practice so the conditional only fires for H.264.
                        if (_activeCodec == VideoCodec.H264 && _frameSeq < 3)
                            IpcClient.LogToFile($"[StudentBroadcaster] H.264 encode produced 0 bytes (frame {_frameSeq})");
                    }
                    else
                    {
                        var data = encoded.Value.Data;
                        var isKeyframe = encoded.Value.IsKeyframe;

                        if (_activeCodec == VideoCodec.H264 && (_frameSeq < 5 || isKeyframe))
                        {
                            // Phase 10.15 BUG-001 — hex-dump the first 16 bytes so we can verify the
                            // encoder is emitting Annex B start codes (00 00 00 01 or 00 00 01) and
                            // diagnose decoder-side rejection vs encoder-side corruption.  Logged
                            // only for first 5 frames + every keyframe to keep noise low.
                            var preview = data.Length >= 16 ? data.AsSpan(0, 16).ToArray() : data;
                            var hex = BitConverter.ToString(preview).Replace("-", " ");
                            IpcClient.LogToFile($"[StudentBroadcaster] H.264 frame seq={_frameSeq + 1} bytes={data.Length} keyframe={isKeyframe} first16=[{hex}]");
                        }

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

    // Phase 11-B inc3 — CaptureFrame moved into GdiScreenCapturer (VirtualScreen
    // source).  The student broadcaster no longer owns SystemInformation or
    // CopyFromScreen.
    // Phase 11-B inc2 part 1 — EncodeJpeg removed; logic lives in MJpegEncoder.

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
