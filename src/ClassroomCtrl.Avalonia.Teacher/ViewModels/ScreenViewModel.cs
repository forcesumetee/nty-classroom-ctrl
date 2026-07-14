using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Media;
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
    private readonly ScreenFrameDecoder _decoder = new();   // TT-8-B: shared codec→Bitmap core (owns the VTDecompressionSession)
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
        _decoder.Dispose();                     // TT-8-B: tear down the decoder (frees the VTDecompressionSession); idempotent
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

    // ─────── The codec fork — delegates to the shared ScreenFrameDecoder (TT-8-B). The decoder
    // decodes + signals; THIS VM owns the app-specific H.264→MJPEG fallback policy (re-request
    // MJPEG via the stream source — a teacher-only affordance the broadcast Student can't do). ───────
    private Bitmap? RenderFrame(ScreenStreamFrameMessage frame)
    {
        if (frame.Codec is not (VideoCodec.Mjpeg or VideoCodec.H264))
        {
            NoteUnsupportedCodec(frame.Codec);
            return null;
        }
        if (_fellBackToMjpeg && frame.Codec == VideoCodec.H264)
            return null;   // already switched — ignore any late H.264 frames still in flight

        var bmp = _decoder.Decode(frame);
        if (bmp is null && _decoder.LastKeyframeDecodeFailed)
            FallBackToMjpeg("H.264 keyframe did not decode");   // this decoder can't handle the stream
        return bmp;
    }

    // A KEYFRAME that won't decode → stop H.264 and re-request MJPEG. The student honors the
    // requested codec (traced TT-3-A), and StudentStreamStop nulls its broadcaster (verified) so
    // the new MJPEG StudentStreamStart takes effect. Surfaced on the status strip — visible, not a
    // silent blank. Fires once; the decoder's idle VTDecompressionSession is freed at Stop().
    private void FallBackToMjpeg(string reason)
    {
        if (_fellBackToMjpeg) return;
        _fellBackToMjpeg = true;
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
