using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>
/// TT-4-C — managed wrapper over the native VTDecompressionSession decoder
/// (native/NtyCapture/H264Decoder.swift, handle-based ABI). One instance per open
/// screen-view window (the teacher may have 1–4 concurrent). Mirrors the shipped
/// <c>H264DecoderWrapper</c>'s shape (a TryDecode + IDisposable), and the §20 interop
/// pattern from <c>ScreenCaptureService</c>: a static <see cref="UnmanagedCallersOnlyAttribute"/>
/// callback whose <c>ctx</c> is a <see cref="GCHandle"/> to this instance, so no
/// per-call delegate marshalling and the instance is rooted for the decoder's life.
///
/// The native feed is SYNCHRONOUS (WaitForAsynchronousFrames), so the callback has
/// fired and populated <see cref="_pending"/> by the time <c>feed</c> returns — this
/// wrapper stays a simple synchronous TryDecode, keeping the TT-3-C codec seam
/// synchronous. Each decoded frame is a FRESH WriteableBitmap (the ScreenViewModel
/// disposes the previous one — same contract as the MJPEG path).
/// </summary>
public sealed partial class H264DecoderWrapper : IDisposable
{
    private const string Lib = "NtyCapture"; // → libNtyCapture.dylib on macOS

    [LibraryImport(Lib)] private static partial nint nty_h264_decoder_create();
    [LibraryImport(Lib)] private static partial int nty_h264_decoder_feed(
        nint handle, nint annexb, int length, int isKeyframe, nint cb, nint ctx);
    [LibraryImport(Lib)] private static partial void nty_h264_decoder_destroy(nint handle);

    private nint _handle;
    private GCHandle _self;
    private bool _disposed;

    // Set by the (synchronous) native callback during a feed; read right after.
    private WriteableBitmap? _pending;

    public static bool IsSupported => OperatingSystem.IsMacOS();

    public H264DecoderWrapper()
    {
        if (!IsSupported) throw new PlatformNotSupportedException("H.264 decode requires macOS/VideoToolbox");
        _handle = nty_h264_decoder_create();
        if (_handle == 0) throw new InvalidOperationException("nty_h264_decoder_create failed");
        _self = GCHandle.Alloc(this);
    }

    /// <summary>
    /// Decode one wire frame's Annex-B bytes. Returns a fresh BGRA
    /// <see cref="WriteableBitmap"/> on success, or null when no frame was produced —
    /// which for a DELTA is normal (waiting for the first keyframe) but for a KEYFRAME
    /// signals the decoder can't handle this stream (unsupported SPS/PPS / decode
    /// error) → the caller should fall back to MJPEG.
    /// </summary>
    public WriteableBitmap? TryDecode(byte[] annexB, bool isKeyframe)
    {
        if (_disposed || _handle == 0 || annexB is null || annexB.Length == 0) return null;
        _pending = null;
        int r;
        unsafe
        {
            delegate* unmanaged[Cdecl]<nint, nint, int, int, int, void> fp = &OnDecodedStatic;
            fixed (byte* p = annexB)
            {
                r = nty_h264_decoder_feed(_handle, (nint)p, annexB.Length, isKeyframe ? 1 : 0,
                                          (nint)fp, GCHandle.ToIntPtr(_self));
            }
        }
        return r == 1 ? _pending : null;   // 1 = a frame was delivered via the callback
    }

    // Native (VideoToolbox) callback — fires synchronously within feed. Recovers the
    // instance from the GCHandle ctx (no per-call marshalling) and copies the frame.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnDecodedStatic(nint ctx, nint bgra, int width, int height, int bytesPerRow)
    {
        if (ctx == 0 || bgra == 0 || width <= 0 || height <= 0) return;
        if (GCHandle.FromIntPtr(ctx).Target is H264DecoderWrapper self)
            self.OnDecoded(bgra, width, height, bytesPerRow);
    }

    private void OnDecoded(nint bgra, int width, int height, int bytesPerRow)
    {
        // The native BGRA is valid ONLY during this call (CVPixelBuffer still locked)
        // → copy now. Honor bytesPerRow (stride ≥ width*4 padding — the M16 gotcha):
        // copy row-by-row from the native stride into the WriteableBitmap's stride.
        var wb = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                                     PixelFormat.Bgra8888, AlphaFormat.Premul);
        using (var fb = wb.Lock())
        {
            int dstStride = fb.RowBytes;
            int rowBytes = Math.Min(bytesPerRow, dstStride);
            unsafe
            {
                byte* src = (byte*)bgra;
                byte* dst = (byte*)fb.Address;
                for (int y = 0; y < height; y++)
                    Buffer.MemoryCopy(src + (long)y * bytesPerRow, dst + (long)y * dstStride, dstStride, rowBytes);
            }
        }
        _pending = wb;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != 0) { nty_h264_decoder_destroy(_handle); _handle = 0; }  // destroy exactly once
        if (_self.IsAllocated) _self.Free();
    }
}
