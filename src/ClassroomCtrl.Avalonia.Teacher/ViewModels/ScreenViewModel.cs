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
    private bool _warnedUnsupportedCodec;   // surface a non-MJPEG stream once, not per frame

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

    /// <summary>Window opened: subscribe to frames and ask the student to start streaming
    /// (MJPEG for TT-3 — the codec we can already decode; H.264 arrives in TT-4). Idempotent.</summary>
    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _source.StudentStreamFrameReceived += OnFrame;
        _ = RequestStreamAsync();
    }

    private async Task RequestStreamAsync()
    {
        try
        {
            await _source.RequestStudentStreamAsync(StudentId, VideoCodec.Mjpeg, CancellationToken.None)
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
        // Fire-and-forget the stop; if the student already left, this targets a gone
        // peer and is a harmless no-op.
        try { _ = _source.StopStudentStreamAsync(StudentId, CancellationToken.None); }
        catch { /* teardown is best-effort */ }
    }

    private void OnFrame(object? sender, (Guid StudentId, ScreenStreamFrameMessage Frame) e)
    {
        if (e.StudentId != StudentId) return;    // FILTER — one window renders one student
        var bmp = RenderFrame(e.Frame);          // codec dispatch + decode, OFF the UI thread
        if (bmp is null)
        {
            NoteIfUnsupportedCodec(e.Frame.Codec);   // H.264 today = clean no-op; surface why, once
            return;
        }
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
    // RenderMjpeg / RenderH264. TT-3 decodes MJPEG; TT-4 fills the H.264 branch. ───────
    //
    // THE SEAM (why TT-4 is additive): TT-4 fills ONLY the VideoCodec.H264 case —
    // VTDecompressionSession → CVPixelBuffer (BGRA) → WriteableBitmap.Lock() +
    // Marshal.Copy → return a FRESH WriteableBitmap per frame (the proven
    // ScreenCaptureViewModel seam). WriteableBitmap derives from Bitmap (verified),
    // so this return type and CurrentFrame / Image.Source already accommodate it. The
    // subscription, studentId filter, request/stop lifecycle, UI-thread marshal, and
    // the dispose-previous-frame logic above are ALL untouched by TT-4.
    private static Bitmap? RenderFrame(ScreenStreamFrameMessage frame) => frame.Codec switch
    {
        VideoCodec.Mjpeg => DecodeMjpeg(frame.FrameData),
        VideoCodec.H264  => null,   // TT-4: VTDecompressionSession → BGRA → WriteableBitmap → fresh WB
        _ => null,
    };

    // MJPEG: the shipped RenderMjpeg equivalent (new Bitmap over the JPEG payload).
    // Returns null on a bad/empty payload so one corrupt frame never tears down the view.
    private static Bitmap? DecodeMjpeg(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;
        try { using var ms = new MemoryStream(data); return new Bitmap(ms); }
        catch { return null; }
    }

    // H.264 (and any not-yet-supported codec) renders nothing today — no crash, no
    // garbage. Surface WHY once (not per frame) on the status strip so a blank view
    // reads as "decoder coming" rather than "broken". A null from a MALFORMED MJPEG
    // frame is a dropped frame, not an unsupported codec — don't warn for that.
    private void NoteIfUnsupportedCodec(VideoCodec codec)
    {
        if (codec == VideoCodec.Mjpeg || _warnedUnsupportedCodec) return;
        _warnedUnsupportedCodec = true;
        Dispatcher.UIThread.Post(() => StatusText = $"{codec} stream — decoder arrives in TT-4");
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
