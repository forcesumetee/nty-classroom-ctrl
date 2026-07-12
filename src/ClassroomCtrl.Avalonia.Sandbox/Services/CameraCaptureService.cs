using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

/// <summary>Tri-state camera privacy authorization (distinct from Screen Recording).
/// Unlike screen, a grant is effective immediately (no relaunch) and the
/// NSCameraUsageDescription text is shown to the user.</summary>
public enum CameraPermission { NotDetermined, Granted, Denied }

/// <summary>An enumerable video capture device (built-in or external USB cam).</summary>
public readonly record struct CameraDevice(int Index, string Name);

/// <summary>
/// Phase 28-B/C — managed side of the native AVCaptureSession camera helper
/// (native/NtyCapture/Camera.swift). Parallel to <see cref="ScreenCaptureService"/>
/// but INDEPENDENT: its own native session state, so a camera stream can run
/// alongside a screen stream (e.g. a Conference sharing screen + cam).
///
/// Same interop template: a static <see cref="UnmanagedCallersOnlyAttribute"/>
/// JPEG callback whose <c>ctx</c> is a <see cref="GCHandle"/> to this instance;
/// the GCHandle roots the service for the capture's lifetime. Camera frames are
/// JPEG-only on the wire (ConferenceCameraFrame has no codec field), so there is
/// no H.264 camera path.
/// </summary>
public sealed partial class CameraCaptureService
{
    private const string Lib = "NtyCapture"; // → libNtyCapture.dylib on macOS

    [LibraryImport(Lib)] private static partial int nty_camera_check_permission();
    [LibraryImport(Lib)] private static partial int nty_camera_request_permission();
    [LibraryImport(Lib)] private static partial int nty_camera_count();
    [LibraryImport(Lib)] private static partial int nty_camera_name(int index, byte[] buf, int bufLen);
    [LibraryImport(Lib)] private static partial int nty_camera_start_jpeg(int deviceIndex, int width, int height, int fps, int quality, nint cb, nint ctx);
    [LibraryImport(Lib)] private static partial void nty_camera_stop();
    [LibraryImport(Lib)] private static partial int nty_camera_last_width();
    [LibraryImport(Lib)] private static partial int nty_camera_last_height();
    [LibraryImport(Lib)] private static partial long nty_camera_frame_count();

    /// <summary>Raised per captured frame with a freshly-copied complete JPEG
    /// (width/height = encoded dims). Fires on the native (AVFoundation) delivery
    /// thread — subscribers must marshal to their UI thread.</summary>
    public event Action<byte[], int, int>? JpegFrameReceived;

    private GCHandle _self;
    public bool IsCapturing { get; private set; }

    public static bool IsSupported => OperatingSystem.IsMacOS();

    // ── permission ──────────────────────────────────────────────────────────────
    public CameraPermission CheckPermission()
    {
        if (!IsSupported) return CameraPermission.Denied;
        return nty_camera_check_permission() switch
        {
            1 => CameraPermission.Granted,
            0 => CameraPermission.NotDetermined,
            _ => CameraPermission.Denied,
        };
    }

    /// <summary>Prompt if undetermined; BLOCKS the worker thread until the user
    /// decides (native side waits on the dialog). Returns the resulting status.</summary>
    public Task<CameraPermission> RequestPermissionAsync()
    {
        if (!IsSupported) return Task.FromResult(CameraPermission.Denied);
        return Task.Run(() => nty_camera_request_permission() == 1
            ? CameraPermission.Granted : CameraPermission.Denied);
    }

    // ── device enumeration ──────────────────────────────────────────────────────
    /// <summary>Enumerate video devices (names resolvable even before a grant).</summary>
    public Task<IReadOnlyList<CameraDevice>> EnumerateDevicesAsync()
    {
        if (!IsSupported) return Task.FromResult<IReadOnlyList<CameraDevice>>(Array.Empty<CameraDevice>());
        return Task.Run(() =>
        {
            int n = nty_camera_count();
            var list = new List<CameraDevice>(Math.Max(0, n));
            var buf = new byte[256];
            for (int i = 0; i < n; i++)
            {
                int len = nty_camera_name(i, buf, buf.Length);
                string name = len > 0 ? Encoding.UTF8.GetString(buf, 0, len) : $"Camera {i}";
                list.Add(new CameraDevice(i, name));
            }
            return (IReadOnlyList<CameraDevice>)list;
        });
    }

    // ── capture ─────────────────────────────────────────────────────────────────
    /// <summary>Start device[deviceIndex] at a preset chosen from width×height
    /// (320×240 = the shipped peer-cam format), JPEG-encode at quality (0–100),
    /// throttled to ~fps. Returns 0 or a native negative error code.</summary>
    public Task<int> StartAsync(int deviceIndex, int width, int height, int fps, int quality)
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
                rc = nty_camera_start_jpeg(deviceIndex, width, height, fps, quality, (nint)fp, GCHandle.ToIntPtr(_self));
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
            nty_camera_stop();
            IsCapturing = false;
            if (_self.IsAllocated) _self.Free();
        });
    }

    public (int Width, int Height, long Frames) Stats()
        => IsSupported ? (nty_camera_last_width(), nty_camera_last_height(), nty_camera_frame_count()) : (0, 0, 0);

    // ── native callback (delivery-queue thread) ──────────────────────────────────
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnJpegStatic(nint ctx, nint jpeg, int length, int width, int height)
    {
        if (ctx == 0 || jpeg == 0 || length <= 0) return;
        if (GCHandle.FromIntPtr(ctx).Target is CameraCaptureService svc)
        {
            var buf = new byte[length];
            Marshal.Copy(jpeg, buf, 0, length);
            svc.JpegFrameReceived?.Invoke(buf, width, height);
        }
    }
}
