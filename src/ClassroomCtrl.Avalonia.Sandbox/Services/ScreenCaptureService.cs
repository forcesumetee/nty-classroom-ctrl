using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

public enum ScreenPermission { Denied, Granted }

/// <summary>
/// Phase 27-A — managed side of the native ScreenCaptureKit helper
/// (native/NtyCapture/libNtyCapture.dylib). UI-agnostic (no Avalonia refs) like
/// <see cref="WireClient"/>; the ViewModel marshals frames to the UI thread.
///
/// Interop pattern (the template for all Phase 27 subsystems): a static
/// <see cref="UnmanagedCallersOnlyAttribute"/> callback (no per-frame delegate
/// marshalling) whose <c>ctx</c> is a <see cref="GCHandle"/> to this instance, so
/// the static callback recovers <c>this</c> and raises an instance event. The
/// GCHandle roots the service for the capture's lifetime.
/// </summary>
public sealed partial class ScreenCaptureService
{
    private const string Lib = "NtyCapture"; // → libNtyCapture.dylib on macOS

    [LibraryImport(Lib)] private static partial int nty_check_permission();
    [LibraryImport(Lib)] private static partial int nty_request_permission();
    [LibraryImport(Lib)] private static partial int nty_capture_start(int fps, nint cb, nint ctx);
    [LibraryImport(Lib)] private static partial int nty_capture_start_jpeg(int fps, int quality, int maxW, int maxH, nint cb, nint ctx);
    [LibraryImport(Lib)] private static partial int nty_capture_start_h264(int fps, int bitrateKbps, nint cb, nint ctx);
    [LibraryImport(Lib)] private static partial void nty_capture_stop();
    [LibraryImport(Lib)] private static partial int nty_last_width();
    [LibraryImport(Lib)] private static partial int nty_last_height();
    [LibraryImport(Lib)] private static partial long nty_frame_count();

    /// <summary>Raised per captured frame on the native delivery thread with a
    /// freshly-copied, tightly-packed (width*4 stride) BGRA8888 buffer. Subscribers
    /// must marshal to their UI thread.</summary>
    public event Action<byte[], int, int>? FrameReceived;

    /// <summary>27-C — raised per captured frame with a freshly-copied complete JPEG
    /// (width/height = encoded/downscaled dims). Fires on the native delivery thread.</summary>
    public event Action<byte[], int, int>? JpegFrameReceived;

    /// <summary>27-B — raised per captured frame with a freshly-copied Annex-B H.264
    /// bundle (keyframe = [SPS][PPS][IDR], delta = [slice]); bool = isKeyframe. Fires on
    /// the native (VideoToolbox) delivery thread.</summary>
    public event Action<byte[], int, int, bool>? H264FrameReceived;

    private GCHandle _self;
    public bool IsCapturing { get; private set; }

    public static bool IsSupported => OperatingSystem.IsMacOS();

    // ── permission ────────────────────────────────────────────────────────────
    public ScreenPermission CheckPermission()
        => IsSupported && nty_check_permission() == 1 ? ScreenPermission.Granted : ScreenPermission.Denied;

    public Task<ScreenPermission> RequestPermissionAsync()
    {
        if (!IsSupported) return Task.FromResult(ScreenPermission.Denied);
        return Task.Run(() => nty_request_permission() == 1 ? ScreenPermission.Granted : ScreenPermission.Denied);
    }

    // ── capture ───────────────────────────────────────────────────────────────
    /// <summary>Start main-display capture at ~<paramref name="fps"/>. Returns 0 on
    /// success or the native negative error code. Runs off the UI thread (the native
    /// call bridges async SC APIs with a bounded wait, so it can take up to a few s).</summary>
    public Task<int> StartAsync(int fps)
    {
        if (!IsSupported) return Task.FromResult(-1000);
        if (IsCapturing) return Task.FromResult(-3);
        return Task.Run(() =>
        {
            _self = GCHandle.Alloc(this);
            int rc;
            unsafe
            {
                delegate* unmanaged[Cdecl]<nint, nint, int, int, int, void> fp = &OnFrameStatic;
                rc = nty_capture_start(fps, (nint)fp, GCHandle.ToIntPtr(_self));
            }
            if (rc == 0) IsCapturing = true;
            else if (_self.IsAllocated) _self.Free();
            return rc;
        });
    }

    /// <summary>27-C — start JPEG capture: downscale to fit maxW×maxH, encode at
    /// `quality` (0–100), raise <see cref="JpegFrameReceived"/> per frame. Matches the
    /// shipped StudentBroadcaster (1280×720, Q60, ~4 fps). Returns 0 or a native error.</summary>
    public Task<int> StartJpegAsync(int fps, int quality, int maxW, int maxH)
    {
        if (!IsSupported) return Task.FromResult(-1000);
        if (IsCapturing) return Task.FromResult(-3);
        return Task.Run(() =>
        {
            _self = GCHandle.Alloc(this);
            int rc;
            unsafe
            {
                delegate* unmanaged[Cdecl]<nint, nint, int, int, int, void> fp = &OnJpegStatic;
                rc = nty_capture_start_jpeg(fps, quality, maxW, maxH, (nint)fp, GCHandle.ToIntPtr(_self));
            }
            if (rc == 0) IsCapturing = true;
            else if (_self.IsAllocated) _self.Free();
            return rc;
        });
    }

    /// <summary>27-B — start H.264 capture (1920×1080, Baseline, CBR ~bitrateKbps,
    /// IDR every fps×2) via VideoToolbox; raise <see cref="H264FrameReceived"/> per frame.
    /// Returns 0 or a native error code.</summary>
    public Task<int> StartH264Async(int fps, int bitrateKbps)
    {
        if (!IsSupported) return Task.FromResult(-1000);
        if (IsCapturing) return Task.FromResult(-3);
        return Task.Run(() =>
        {
            _self = GCHandle.Alloc(this);
            int rc;
            unsafe
            {
                delegate* unmanaged[Cdecl]<nint, nint, int, int, int, int, void> fp = &OnH264Static;
                rc = nty_capture_start_h264(fps, bitrateKbps, (nint)fp, GCHandle.ToIntPtr(_self));
            }
            if (rc == 0) IsCapturing = true;
            else if (_self.IsAllocated) _self.Free();
            return rc;
        });
    }

    public Task StopAsync()
    {
        if (!IsSupported || !IsCapturing) return Task.CompletedTask;
        return Task.Run(() =>
        {
            nty_capture_stop();
            IsCapturing = false;
            if (_self.IsAllocated) _self.Free();
        });
    }

    public (int Width, int Height, long Frames) Stats()
        => IsSupported ? (nty_last_width(), nty_last_height(), nty_frame_count()) : (0, 0, 0);

    // ── native callback (delivery-queue thread) ─────────────────────────────────
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnFrameStatic(nint ctx, nint bgra, int width, int height, int bytesPerRow)
    {
        if (ctx == 0 || bgra == 0) return;
        if (GCHandle.FromIntPtr(ctx).Target is ScreenCaptureService svc)
            svc.OnFrame(bgra, width, height, bytesPerRow);
    }

    // JPEG delivery (27-C): copy the call-scoped JPEG bytes and raise the event.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnJpegStatic(nint ctx, nint jpeg, int length, int width, int height)
    {
        if (ctx == 0 || jpeg == 0 || length <= 0) return;
        if (GCHandle.FromIntPtr(ctx).Target is ScreenCaptureService svc)
        {
            var buf = new byte[length];
            Marshal.Copy(jpeg, buf, 0, length);
            svc.JpegFrameReceived?.Invoke(buf, width, height);
        }
    }

    // H.264 delivery (27-B): copy the call-scoped Annex-B bytes and raise the event.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnH264Static(nint ctx, nint nal, int length, int width, int height, int isKeyframe)
    {
        if (ctx == 0 || nal == 0 || length <= 0) return;
        if (GCHandle.FromIntPtr(ctx).Target is ScreenCaptureService svc)
        {
            var buf = new byte[length];
            Marshal.Copy(nal, buf, 0, length);
            svc.H264FrameReceived?.Invoke(buf, width, height, isKeyframe != 0);
        }
    }

    private void OnFrame(nint bgra, int width, int height, int bytesPerRow)
    {
        // The native buffer is valid ONLY during this call (CVPixelBuffer still
        // locked), so copy now. Honor bytesPerRow (row padding: measured 5888 vs
        // width*4=5880 on a 1470-wide display) → copy row-by-row into a
        // tightly-packed width*4 buffer that WriteableBitmap can ingest directly.
        int dstStride = width * 4;
        var buf = new byte[dstStride * height];
        unsafe
        {
            byte* src = (byte*)bgra;
            fixed (byte* dst = buf)
            {
                for (int y = 0; y < height; y++)
                    Buffer.MemoryCopy(src + (long)y * bytesPerRow, dst + (long)y * dstStride, dstStride, dstStride);
            }
        }
        FrameReceived?.Invoke(buf, width, height);
    }
}
