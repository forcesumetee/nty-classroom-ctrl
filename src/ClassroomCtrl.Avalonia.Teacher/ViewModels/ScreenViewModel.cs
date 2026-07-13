using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Teacher.Services;
using ClassroomCtrl.Shared.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassroomCtrl.Avalonia.Teacher.ViewModels;

/// <summary>
/// TT-3-B — the per-student screen-view VM (one instance per open ScreenViewWindow).
///
/// Lifecycle: <see cref="Start"/> on window-open (subscribe + request stream),
/// <see cref="Stop"/> on window-close (unsubscribe + stop stream). Frames arrive on
/// the transport's background read loop, are FILTERED by studentId (with multiple
/// windows open each renders only its own student), decoded OFF the UI thread, and
/// the resulting Image.Source is set ON the UI thread via Dispatcher.UIThread.Post —
/// the TT-2 marshal pattern; the render loop reads <see cref="CurrentFrame"/>
/// concurrently, so an off-thread mutation would race it (an intermittent 50-seat
/// crash exactly while a frame is drawing).
///
/// TT-3-B decodes MJPEG only (<c>new Bitmap</c> over the JPEG payload, matching the
/// shipped Windows RenderMjpeg). TT-3-C generalizes <see cref="DecodeFrame"/> into a
/// <c>switch (frame.Codec)</c> with a VideoCodec.H264 branch (VTDecompressionSession,
/// TT-4). Requesting MJPEG (not App.SelectedCodec) is deliberate: it is the codec we
/// can already decode with no new native code.
/// </summary>
public sealed partial class ScreenViewModel : ObservableObject, IDisposable
{
    private readonly IStudentStreamSource _source;
    private Bitmap? _pendingDispose;   // the frame CurrentFrame just replaced; freed one cycle later, past the compositor
    private bool _started;
    private bool _disposed;
    private bool _warnedUnsupportedCodec;   // surface a genuinely-unknown codec once, not per frame
    private H264DecoderWrapper? _h264;      // TT-4-C: lazy per-view VTDecompressionSession decoder
    private bool _fellBackToMjpeg;          // set once if H.264 can't decode → switched the stream to MJPEG

    /// <summary>The student whose frames this view renders — the filter key.</summary>
    public Guid StudentId { get; }

    [ObservableProperty] private string title;
    [ObservableProperty] private Bitmap? currentFrame;
    [ObservableProperty] private string statusText = "Connecting…";

    /// <summary>Frames actually rendered (incremented on the UI thread). Lets a headless
    /// test assert the studentId filter — "only the matching student's frames render".</summary>
    public int RenderedFrameCount { get; private set; }

    public ScreenViewModel(IStudentStreamSource source, Guid studentId, string displayName, string machineName)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        StudentId = studentId;
        title = string.IsNullOrWhiteSpace(machineName) ? displayName : $"{displayName} — {machineName}";
    }

    /// <summary>Window opened: subscribe to frames and ask the student to start streaming.
    /// TT-4-C requests H.264 (efficient — ~10× the bandwidth of MJPEG); if the decoder
    /// can't handle the stream, <see cref="DecodeH264"/> auto-falls-back to MJPEG. Idempotent.</summary>
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _source.StudentStreamFrameReceived += OnFrame;
        _ = RequestStreamAsync(VideoCodec.H264);
    }

    private async Task RequestStreamAsync(VideoCodec codec)
    {
        try
        {
            await _source.RequestStudentStreamAsync(StudentId, codec, CancellationToken.None)
                         .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => StatusText = $"Could not start stream: {ex.Message}");
        }
    }

    /// <summary>Window closed: unsubscribe and tell the student to stop streaming — never
    /// leave a student encoding/sending to a closed window (the shipped
    /// StudentScreenWindow discipline). Idempotent.</summary>
    public void Stop()
    {
        if (!_started) return;
        _started = false;
        _source.StudentStreamFrameReceived -= OnFrame;
        var dec = _h264; _h264 = null;          // TT-4-C: tear down the decoder (frees the VTDecompressionSession)
        try { dec?.Dispose(); } catch { }
        // Fire-and-forget the stop; if the student already left, this targets a gone
        // peer and is a harmless no-op.
        try { _ = _source.StopStudentStreamAsync(StudentId, CancellationToken.None); }
        catch { /* teardown is best-effort */ }
    }

    private void OnFrame(object? sender, (Guid StudentId, ScreenStreamFrameMessage Frame) e)
    {
        if (e.StudentId != StudentId) return;    // FILTER — one window renders one student
        var bmp = RenderFrame(e.Frame);          // codec dispatch + decode, OFF the UI thread
        if (bmp is null) return;                 // dropped / waiting for keyframe / fell back — RenderFrame owns the "why"
        int w = e.Frame.Width, h = e.Frame.Height;
        Dispatcher.UIThread.Post(() =>           // marshal the Image.Source swap onto the UI thread
        {
            if (_disposed) { bmp.Dispose(); return; }
            _pendingDispose?.Dispose();          // free the frame replaced last cycle (compositor has moved past it)
            _pendingDispose = CurrentFrame;
            CurrentFrame = bmp;                  // binding swaps Image.Source to the new bitmap
            RenderedFrameCount++;
            StatusText = $"Live — {w}×{h}";
        });
    }

    // ─────── The codec fork — mirrors the shipped Windows OnStudentFrame →
    // RenderMjpeg / RenderH264. MJPEG = new Bitmap; H.264 = VTDecompressionSession. ───────
    // Instance (not static) as of TT-4-C: the H.264 branch owns a per-view decoder.
    private Bitmap? RenderFrame(ScreenStreamFrameMessage frame)
    {
        switch (frame.Codec)
        {
            case VideoCodec.Mjpeg: return DecodeMjpeg(frame.FrameData);
            case VideoCodec.H264:  return DecodeH264(frame);
            default:               NoteUnsupportedCodec(frame.Codec); return null;
        }
    }

    // MJPEG: the shipped RenderMjpeg equivalent (new Bitmap over the JPEG payload).
    // Returns null on a bad/empty payload so one corrupt frame never tears down the view.
    private static Bitmap? DecodeMjpeg(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;
        try { using var ms = new MemoryStream(data); return new Bitmap(ms); }
        catch { return null; }
    }

    // H.264 (TT-4-C): a per-view VTDecompressionSession decoder (H264DecoderWrapper),
    // created lazily on the first frame and torn down in Stop(). Returns a FRESH BGRA
    // WriteableBitmap (WriteableBitmap : Bitmap — verified; the dispose-previous logic
    // treats it exactly like MJPEG's Bitmap). A DELTA before the first keyframe returns
    // null (waiting — normal). A KEYFRAME that won't decode means this decoder can't
    // handle the stream → fall back to MJPEG (LIVE-proven) rather than a dead window.
    private Bitmap? DecodeH264(ScreenStreamFrameMessage frame)
    {
        if (_fellBackToMjpeg) return null;   // already switched — ignore any late H.264 frames
        var data = frame.FrameData;
        if (data is null || data.Length == 0) return null;
        try
        {
            _h264 ??= new H264DecoderWrapper();
            var wb = _h264.TryDecode(data, frame.IsKeyframe);
            if (wb is null && frame.IsKeyframe)
                FallBackToMjpeg("H.264 keyframe did not decode");
            return wb;
        }
        catch (Exception ex)   // decoder unavailable / native failure
        {
            FallBackToMjpeg(ex.Message);
            return null;
        }
    }

    // Session-create/decode failure → stop H.264 and re-request MJPEG. The student
    // honors the requested codec (traced TT-3-A), and StudentStreamStop nulls its
    // broadcaster (verified) so the new MJPEG StudentStreamStart takes effect. Surfaced
    // on the status strip — visible, not a silent blank. Fires once.
    private void FallBackToMjpeg(string reason)
    {
        if (_fellBackToMjpeg) return;
        _fellBackToMjpeg = true;
        var dec = _h264; _h264 = null;
        try { dec?.Dispose(); } catch { }
        Dispatcher.UIThread.Post(() => StatusText = "H.264 unavailable — using MJPEG");
        _ = SwitchToMjpegAsync();
    }

    private async Task SwitchToMjpegAsync()
    {
        try
        {
            await _source.StopStudentStreamAsync(StudentId, CancellationToken.None).ConfigureAwait(false);
            await _source.RequestStudentStreamAsync(StudentId, VideoCodec.Mjpeg, CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* best-effort; a dropped student just yields no frames */ }
    }

    // A codec we don't handle at all (neither MJPEG nor H.264 — a future addition).
    // Surface once, not per frame.
    private void NoteUnsupportedCodec(VideoCodec codec)
    {
        if (_warnedUnsupportedCodec) return;
        _warnedUnsupportedCodec = true;
        Dispatcher.UIThread.Post(() => StatusText = $"{codec} stream — unsupported codec");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        _pendingDispose?.Dispose();
        CurrentFrame?.Dispose();
        CurrentFrame = null;
    }
}
