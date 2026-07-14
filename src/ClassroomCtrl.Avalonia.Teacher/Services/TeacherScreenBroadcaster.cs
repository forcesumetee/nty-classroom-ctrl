using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>
/// TT-8-C — the Teacher's "Share My Screen": capture the main display (native ScreenCaptureKit, the
/// M17 path) and broadcast each frame to all students via <see cref="ITeacherScreenSink"/>. Mirrors
/// the shipped ScreenBroadcaster (1280×720, JPEG Q60, ~2 fps, primary display). Frames route LOSSY;
/// the STOP routes reliable (bug #5) — both decided inside ControlServer.
///
/// Reuses the SAME native capture ABI as the Student's ScreenCaptureService (nty_capture_start_jpeg
/// /_stop). The native capture is a process SINGLETON, but that's fine: the Teacher captures ONE
/// screen, and DECODING student screens is a separate handle-based subsystem — no conflict. (Kept a
/// small Teacher-side driver for batch 1 rather than sharing ScreenCaptureService — the capture ABI
/// is a trivial 3-function surface; fold into Media later if it grows.)
///
/// TCC: capturing needs Screen Recording (nty_check/request_permission) — NEW for the Teacher app,
/// which until TT-8 only decoded (buffers, no permission). The grant binds at process launch → run
/// the Teacher as the BUNDLE, not `dotnet run` (the TT-4-D friction; P35 signing removes it).
/// </summary>
public sealed partial class TeacherScreenBroadcaster
{
    private const string Lib = "NtyCapture"; // → libNtyCapture.dylib on macOS
    [LibraryImport(Lib)] private static partial int nty_check_permission();
    [LibraryImport(Lib)] private static partial int nty_request_permission();
    [LibraryImport(Lib)] private static partial int nty_capture_start_jpeg(int fps, int quality, int maxW, int maxH, nint cb, nint ctx);
    [LibraryImport(Lib)] private static partial void nty_capture_stop();

    // Match the shipped ScreenBroadcaster (primary display, 1280×720, Q60, ~2 fps).
    private const int Fps = 2, Quality = 60, MaxW = 1280, MaxH = 720;

    private readonly ITeacherScreenSink _sink;
    private GCHandle _self;
    private int _seq;

    public bool IsSharing { get; private set; }
    public static bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>Raised (native delivery thread) after a frame is broadcast — the running frame seq.</summary>
    public event Action<int>? FrameSent;

    public TeacherScreenBroadcaster(ITeacherScreenSink sink) => _sink = sink;

    public bool HasPermission => IsSupported && nty_check_permission() == 1;

    public Task<bool> RequestPermissionAsync()
        => IsSupported ? Task.Run(() => nty_request_permission() == 1) : Task.FromResult(false);

    /// <summary>Start capturing + broadcasting the teacher's screen (MJPEG). Returns 0 on success or a
    /// native negative code (-1 = Screen Recording not permitted, -3 = already running, -1000 = not macOS).</summary>
    public async Task<int> StartAsync()
    {
        if (!IsSupported) return -1000;
        if (IsSharing) return 0;
        _seq = 0;
        int rc = await Task.Run(() =>
        {
            _self = GCHandle.Alloc(this);
            int r;
            unsafe
            {
                delegate* unmanaged[Cdecl]<nint, nint, int, int, int, void> fp = &OnJpegStatic;
                r = nty_capture_start_jpeg(Fps, Quality, MaxW, MaxH, (nint)fp, GCHandle.ToIntPtr(_self));
            }
            if (r != 0 && _self.IsAllocated) _self.Free();
            return r;
        });
        if (rc != 0) return rc;
        IsSharing = true;
        await _sink.BroadcastScreenStreamControlAsync(true, CancellationToken.None);   // lossy START
        return 0;
    }

    public async Task StopAsync()
    {
        if (!IsSharing) return;
        IsSharing = false;
        await Task.Run(() => nty_capture_stop());
        if (_self.IsAllocated) _self.Free();
        await _sink.BroadcastScreenStreamControlAsync(false, CancellationToken.None);   // reliable STOP (bug #5)
    }

    // Native (delivery-queue thread): copy the call-scoped JPEG bytes, then broadcast.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnJpegStatic(nint ctx, nint jpeg, int length, int width, int height)
    {
        if (ctx == 0 || jpeg == 0 || length <= 0) return;
        if (GCHandle.FromIntPtr(ctx).Target is TeacherScreenBroadcaster self)
        {
            var buf = new byte[length];
            Marshal.Copy(jpeg, buf, 0, length);
            self.OnJpeg(buf, width, height);
        }
    }

    private void OnJpeg(byte[] jpeg, int width, int height)
    {
        var msg = new ScreenStreamFrameMessage
        {
            FrameData = jpeg,
            Width = width,
            Height = height,
            TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FrameSeq = Interlocked.Increment(ref _seq),
            Codec = VideoCodec.Mjpeg,
            IsKeyframe = true,
        };
        _ = SendSafeAsync(msg);
    }

    private async Task SendSafeAsync(ScreenStreamFrameMessage msg)
    {
        try
        {
            await _sink.BroadcastScreenFrameAsync(msg, CancellationToken.None);
            FrameSent?.Invoke(msg.FrameSeq);
        }
        catch { /* a dropped frame is fine — lossy by design (DropOldest) */ }
    }
}
