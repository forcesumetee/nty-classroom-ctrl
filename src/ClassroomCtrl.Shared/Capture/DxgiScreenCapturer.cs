using SharpGen.Runtime;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ClassroomCtrl.Shared.Capture;

/// <summary>
/// Phase 11-B inc3 part 2 — DXGI Desktop Duplication capturer.
///
/// Replaces the inc1/inc2 GDI bottleneck (CopyFromScreen + bicubic DrawImage,
/// ~30-80 ms/frame on the dev box, the ~4 FPS ceiling identified in Phase
/// 11-A) with GPU desktop duplication (~1-5 ms/frame).  Capture is still
/// system-memory: we read the desktop texture back to a CPU staging texture
/// and hand a 32bpp BGRA <see cref="Bitmap"/> to the existing
/// <see cref="ClassroomCtrl.Shared.Codec.IVideoEncoder"/> path.  True zero-copy
/// D3D-texture-into-HW-encoder is a later optimization once HW encode is
/// Intel-validated.
///
/// What this capturer handles (the DXGI gotchas — these are what break
/// Desktop Duplication captures in the field):
///   • <b>Cursor</b> — DXGI frames do NOT include the cursor; the GDI path
///     showed it and the teacher needs students to see the pointer.  We
///     composite the system cursor onto the captured frame using
///     <c>OutduplFrameInfo.PointerPosition</c> + the pointer shape from
///     <c>GetFramePointerShape</c> (cached between frames since shape only
///     updates when it changes).  Three pointer-shape types are handled:
///     Color (the common case for arrow, IBeam, etc), MaskedColor, and the
///     legacy 1bpp Monochrome (system text cursor).
///   • <b>Timeout on a static screen</b> — when nothing on screen changed
///     <c>AcquireNextFrame</c> returns DXGI_ERROR_WAIT_TIMEOUT.  We return
///     <c>null</c> so the broadcaster reuses the prior frame instead of
///     blanking the share.  Short timeout (15 ms) keeps the loop responsive.
///   • <b>Access lost</b> — DXGI_ERROR_ACCESS_LOST happens on UAC secure
///     desktop, lock screen, fast-user-switch, resolution change, fullscreen-
///     exclusive games.  We re-create the duplication (and the D3D11 device if
///     needed) and return null for the affected frames.  If recovery fails
///     repeatedly we set <c>_permanentlyFailed</c> and every subsequent
///     <see cref="Capture"/> falls back to a private <see cref="GdiScreenCapturer"/>
///     so the share keeps going.
///   • <b>Unsupported</b> — if <c>DuplicateOutput</c> throws outright (some RDP
///     sessions, old drivers) the ctor lets it propagate and the factory's
///     try/catch falls back to GDI permanently for the session.
///   • <b>Multi-monitor</b> — inc3 captures the primary output only.  Both the
///     Teacher (Primary source) and Student (VirtualScreen source) end up here
///     since DXGI can't duplicate the virtual-screen union; this is the
///     documented inc3 limit.  Multi-monitor / monitor selection is a future
///     follow-up.
/// </summary>
public sealed class DxgiScreenCapturer : IScreenCapturer
{
    public string Description { get; private set; }

    // ─────────── D3D11 / DXGI state ───────────

    private ID3D11Device _device = null!;
    private ID3D11DeviceContext _context = null!;
    private IDXGIOutputDuplication _duplication = null!;
    private ID3D11Texture2D _stagingTexture = null!;
    private int _screenW, _screenH;
    private bool _permanentlyFailed;
    private bool _disposed;

    // GDI fallback used per-frame when DXGI errors transiently (access lost,
    // resolution change in progress), or permanently after repeat failures.
    private readonly GdiScreenCapturer _gdiFallback;
    private int _accessLostRetries;
    private const int AccessLostMaxRetries = 4;

    // ─────────── Cursor state (composited per-frame) ───────────

    private byte[]? _pointerShapeBuffer;
    private OutduplPointerShapeInfo _pointerShapeInfo;
    private bool _cursorVisible;
    private System.Drawing.Point _cursorPos;

    // Cursor BGRA cache — rasterized once per shape change so per-frame
    // compositing is a Marshal.Copy + AlphaBlend instead of bit-twiddling
    // the 1bpp Monochrome shape every frame.
    private byte[]? _cursorBgra;
    private int _cursorBgraW, _cursorBgraH;

    public DxgiScreenCapturer(ScreenCaptureSource source)
    {
        // Source enum is accepted for API symmetry; inc3 always captures the
        // primary output regardless (see XML doc above).  Description reflects
        // the truth so the broadcaster's startup log doesn't lie.
        Description = source == ScreenCaptureSource.VirtualScreen
            ? "DXGI (primary output — virtual-screen requested, multi-monitor capture deferred)"
            : "DXGI (primary output)";

        _gdiFallback = new GdiScreenCapturer(source);
        Initialize();
    }

    // ─────────── One-time init + recovery ───────────

    /// <summary>Builds the D3D11 device, looks up the primary IDXGIOutput,
    /// activates the duplication, and allocates the staging texture sized to
    /// the output's mode.  Throws on hard failure so the factory falls back
    /// to GDI permanently; transient access-lost recovery uses
    /// <see cref="ReinitDuplication"/> instead.</summary>
    private void Initialize()
    {
        // D3D11 device via Win32 P/Invoke (same shape as
        // MediaFoundationH264AsyncEncoder uses for the HW encoder).  BGRA flag
        // is mandatory for DXGI duplication; VIDEO is not strictly required
        // for read-back but matches what HW encoders expect if a future zero-
        // copy path ever ends up sharing the device.
        int hr = D3D11CreateDevice(
            IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero,
            D3D11_CREATE_DEVICE_BGRA, IntPtr.Zero, 0,
            D3D11_SDK_VERSION,
            out IntPtr devicePtr, out _, out IntPtr ctxPtr);
        if (hr != 0 || devicePtr == IntPtr.Zero)
            throw new InvalidOperationException($"D3D11CreateDevice failed HRESULT 0x{hr:X8}");

        _device = new ID3D11Device(devicePtr);
        _context = new ID3D11DeviceContext(ctxPtr);

        // QI device → IDXGIDevice → GetAdapter → EnumOutputs(0) → QI IDXGIOutput1
        // → DuplicateOutput(device).  Going through the device-derived adapter
        // (instead of CreateDXGIFactory1+EnumAdapters1(0)) guarantees the
        // duplication is on the SAME adapter that created the D3D11 device —
        // DuplicateOutput silently fails with E_INVALIDARG otherwise on
        // multi-GPU laptops.
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        using var adapter = dxgiDevice.GetAdapter();
        adapter.EnumOutputs(0, out IDXGIOutput primaryOutput).CheckError();
        using (primaryOutput)
        {
            using var output1 = primaryOutput.QueryInterface<IDXGIOutput1>();
            _duplication = output1.DuplicateOutput(_device);
            var desc = primaryOutput.Description;
            // Output desktop bounds — convert from RECT-style longs to pixel ints.
            _screenW = desc.DesktopCoordinates.Right  - desc.DesktopCoordinates.Left;
            _screenH = desc.DesktopCoordinates.Bottom - desc.DesktopCoordinates.Top;
        }

        AllocateStagingTexture();
    }

    private void AllocateStagingTexture()
    {
        var stagingDesc = new Texture2DDescription
        {
            Width             = (uint)_screenW,
            Height            = (uint)_screenH,
            MipLevels         = 1,
            ArraySize         = 1,
            Format            = Vortice.DXGI.Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            Usage             = ResourceUsage.Staging,
            BindFlags         = BindFlags.None,
            CPUAccessFlags    = CpuAccessFlags.Read,
            MiscFlags         = ResourceOptionFlags.None,
        };
        _stagingTexture = _device.CreateTexture2D(stagingDesc);
    }

    /// <summary>Recreates just the duplication object after an access-lost
    /// event.  The device usually survives — if it doesn't, the next D3D11
    /// call throws DEVICE_REMOVED, which trips <see cref="_permanentlyFailed"/>
    /// and falls everything back to GDI.</summary>
    private bool TryReinitDuplication()
    {
        try
        {
            // Wait briefly — UAC / lock / resolution change windows usually
            // resolve within 500 ms.  Anything quicker can re-fail immediately.
            Thread.Sleep(50);
            try { _duplication?.Dispose(); } catch { }
            _duplication = null!;

            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            adapter.EnumOutputs(0, out IDXGIOutput primaryOutput).CheckError();
            using (primaryOutput)
            {
                using var output1 = primaryOutput.QueryInterface<IDXGIOutput1>();
                _duplication = output1.DuplicateOutput(_device);
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ─────────── IScreenCapturer ───────────

    public Bitmap? Capture(int targetWidth, int targetHeight)
    {
        if (_disposed) return null;
        if (_permanentlyFailed) return _gdiFallback.Capture(targetWidth, targetHeight);

        try
        {
            return CaptureDxgi(targetWidth, targetHeight);
        }
        catch (SharpGenException sge) when (sge.HResult == DXGI_ERROR_ACCESS_LOST
                                          || sge.HResult == DXGI_ERROR_ACCESS_DENIED)
        {
            // Try to recover from a single access-lost event; while recovering,
            // fall back to GDI so the share doesn't blank.
            _accessLostRetries++;
            if (_accessLostRetries > AccessLostMaxRetries)
            {
                _permanentlyFailed = true;
                Description = "GDI (DXGI fell back: access-lost retry exhausted)";
            }
            else
            {
                bool recovered = TryReinitDuplication();
                if (!recovered)
                {
                    _permanentlyFailed = true;
                    Description = "GDI (DXGI fell back: reinit failed after access lost)";
                }
                else
                {
                    _accessLostRetries = 0;
                }
            }
            return _gdiFallback.Capture(targetWidth, targetHeight);
        }
        catch (Exception ex)
        {
            // Any other DXGI failure (DEVICE_REMOVED, etc.) — give up on DXGI
            // and use GDI for the rest of the session.  The startup log
            // already named the original backend; per-frame errors don't
            // need to flood logs.
            _permanentlyFailed = true;
            Description = $"GDI (DXGI fell back: {ex.GetType().Name})";
            return _gdiFallback.Capture(targetWidth, targetHeight);
        }
    }

    private Bitmap? CaptureDxgi(int targetWidth, int targetHeight)
    {
        // Short timeout — static screen returns DXGI_ERROR_WAIT_TIMEOUT here
        // and we just return null so the broadcaster reuses the prior frame.
        var hr = _duplication.AcquireNextFrame(15u, out OutduplFrameInfo frameInfo,
                                               out IDXGIResource desktopResource);
        if (hr.Failure)
        {
            if (hr.Code == DXGI_ERROR_WAIT_TIMEOUT) return null;
            // Re-throw access-lost so the outer Capture catches it.  Anything
            // else also bubbles up to the outer permanent-fallback handler.
            throw new SharpGenException(hr);
        }

        try
        {
            using (desktopResource)
            using (var desktopTexture = desktopResource.QueryInterface<ID3D11Texture2D>())
            {
                // GPU copy — the desktop texture lives in VRAM; this enqueues a
                // GPU→GPU copy into our CPU-readable staging texture which Map
                // then reads back over PCIe.  Typical cost: 1-3 ms.
                _context.CopyResource(_stagingTexture, desktopTexture);
            }

            // Refresh cached pointer shape if this frame includes a new one.
            if (frameInfo.PointerShapeBufferSize > 0)
            {
                RefreshPointerShape((int)frameInfo.PointerShapeBufferSize);
            }
            // Cursor visibility + position from the frame info — DXGI updates
            // these even when the shape didn't change.
            _cursorVisible = frameInfo.PointerPosition.Visible;
            _cursorPos     = new System.Drawing.Point(frameInfo.PointerPosition.Position.X,
                                                     frameInfo.PointerPosition.Position.Y);
        }
        finally
        {
            // Release the desktop frame promptly — DXGI will refuse the next
            // AcquireNextFrame until we do.
            _duplication.ReleaseFrame();
        }

        // Read the staging texture back into a managed Bitmap.  This is the
        // CPU↔GPU bandwidth cost (typically 1-3 ms at 1080p).
        return MapAndBuildBitmap(targetWidth, targetHeight);
    }

    private Bitmap MapAndBuildBitmap(int targetWidth, int targetHeight)
    {
        // Build a Bitmap at the captured native resolution first; cursor
        // compositing happens on this surface (in screen coords); then resize
        // to target if needed.  The common case is 1920x1080 captured + 1920x1080
        // target so the resize is a no-op.
        var srcBmp = new Bitmap(_screenW, _screenH, PixelFormat.Format32bppArgb);
        var lockData = srcBmp.LockBits(
            new Rectangle(0, 0, _screenW, _screenH),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

        var mapResult = _context.Map(_stagingTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None,
                                     out MappedSubresource mapped);
        try
        {
            mapResult.CheckError();
            unsafe
            {
                byte* src = (byte*)mapped.DataPointer;
                byte* dst = (byte*)lockData.Scan0;
                int rowBytes = _screenW * 4;
                int srcPitch = (int)mapped.RowPitch;
                int dstPitch = lockData.Stride;
                if (srcPitch == dstPitch && srcPitch == rowBytes)
                {
                    // Fast path — no row padding either side.
                    Buffer.MemoryCopy(src, dst, (long)rowBytes * _screenH, (long)rowBytes * _screenH);
                }
                else
                {
                    // Row-by-row to respect the larger of the two strides.
                    for (int y = 0; y < _screenH; y++)
                        Buffer.MemoryCopy(src + y * srcPitch, dst + y * dstPitch, rowBytes, rowBytes);
                }
            }
        }
        finally
        {
            srcBmp.UnlockBits(lockData);
            _context.Unmap(_stagingTexture, 0);
        }

        // Composite the cursor onto the captured frame.  GDI does the heavy
        // lifting via DrawImage on the cached cursor BGRA.
        if (_cursorVisible && _cursorBgra != null && _cursorBgraW > 0 && _cursorBgraH > 0)
        {
            CompositeCursor(srcBmp);
        }

        // Resize if the broadcaster wants a different target size.  Bilinear
        // is plenty for screen-share — bicubic was part of the inc2 cost.
        if (srcBmp.Width == targetWidth && srcBmp.Height == targetHeight)
            return srcBmp;

        var dst2 = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(dst2))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.PixelOffsetMode   = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(srcBmp, 0, 0, targetWidth, targetHeight);
        }
        srcBmp.Dispose();
        return dst2;
    }

    // ─────────── Cursor compositing ───────────

    /// <summary>Fetch the new pointer shape into our managed buffer and pre-
    /// rasterize it to a BGRA bitmap so per-frame compositing is a single
    /// <see cref="Graphics.DrawImage(Image, Rectangle)"/> call.  Handles the
    /// three pointer-shape types Windows produces: Color (the modern arrow,
    /// IBeam, hand etc.), MaskedColor (legacy themed XOR cursors), and the
    /// 1bpp Monochrome system cursor (text caret).</summary>
    private void RefreshPointerShape(int bufferSize)
    {
        if (_pointerShapeBuffer == null || _pointerShapeBuffer.Length < bufferSize)
            _pointerShapeBuffer = new byte[bufferSize];

        unsafe
        {
            fixed (byte* p = _pointerShapeBuffer)
            {
                var hr = _duplication.GetFramePointerShape(
                    (uint)bufferSize, (IntPtr)p,
                    out uint required, out _pointerShapeInfo);
                if (hr.Failure) return;
            }
        }

        var type   = (PointerShapeType)_pointerShapeInfo.Type;
        int width  = (int)_pointerShapeInfo.Width;
        int height = (int)_pointerShapeInfo.Height;
        int pitch  = (int)_pointerShapeInfo.Pitch;
        if (width <= 0 || height <= 0) { _cursorBgra = null; return; }

        if (type == PointerShapeType.Color)
        {
            // Direct BGRA copy — the buffer already has alpha for compositing.
            _cursorBgra = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
                Buffer.BlockCopy(_pointerShapeBuffer, y * pitch,
                                 _cursorBgra, y * width * 4, width * 4);
            _cursorBgraW = width;
            _cursorBgraH = height;
        }
        else if (type == PointerShapeType.MaskedColor)
        {
            // MaskedColor: BGRA where alpha=0 → opaque copy, alpha=0xFF → XOR
            // with destination.  For inc3 we treat the XOR pixels as opaque
            // black on white, which gives a visible-but-imperfect cursor —
            // good enough since modern Windows uses Color almost everywhere.
            _cursorBgra = new byte[width * height * 4];
            for (int y = 0; y < height; y++)
            {
                int srcRow = y * pitch;
                int dstRow = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    byte b = _pointerShapeBuffer[srcRow + x * 4 + 0];
                    byte g = _pointerShapeBuffer[srcRow + x * 4 + 1];
                    byte r = _pointerShapeBuffer[srcRow + x * 4 + 2];
                    byte a = _pointerShapeBuffer[srcRow + x * 4 + 3];
                    if (a == 0)
                    {
                        // Opaque pixel — keep colour, alpha=255.
                        _cursorBgra[dstRow + x * 4 + 0] = b;
                        _cursorBgra[dstRow + x * 4 + 1] = g;
                        _cursorBgra[dstRow + x * 4 + 2] = r;
                        _cursorBgra[dstRow + x * 4 + 3] = 255;
                    }
                    else
                    {
                        // XOR pixel — approximation: render as opaque pixel
                        // with inverted RGB (good enough for visibility).
                        _cursorBgra[dstRow + x * 4 + 0] = (byte)(255 - b);
                        _cursorBgra[dstRow + x * 4 + 1] = (byte)(255 - g);
                        _cursorBgra[dstRow + x * 4 + 2] = (byte)(255 - r);
                        _cursorBgra[dstRow + x * 4 + 3] = 255;
                    }
                }
            }
            _cursorBgraW = width;
            _cursorBgraH = height;
        }
        else if (type == PointerShapeType.Monochrome)
        {
            // Monochrome: 1bpp packed (8 px / byte).  Height field is 2x
            // displayed height: first half AND mask, second half XOR mask.
            //   AND=0, XOR=0 → black
            //   AND=0, XOR=1 → white
            //   AND=1, XOR=0 → transparent (screen pass-through)
            //   AND=1, XOR=1 → inverse of screen (approx: opaque dark grey)
            int realH = height / 2;
            _cursorBgra = new byte[width * realH * 4];
            for (int y = 0; y < realH; y++)
            {
                int andRow = y * pitch;
                int xorRow = (realH + y) * pitch;
                int dstRow = y * width * 4;
                for (int x = 0; x < width; x++)
                {
                    int bytePos = x >> 3;
                    int bitPos  = 7 - (x & 7);
                    bool andBit = (_pointerShapeBuffer[andRow + bytePos] & (1 << bitPos)) != 0;
                    bool xorBit = (_pointerShapeBuffer[xorRow + bytePos] & (1 << bitPos)) != 0;
                    byte b, g, r, a;
                    if (!andBit && !xorBit)      { b = 0;   g = 0;   r = 0;   a = 255; } // black
                    else if (!andBit && xorBit)  { b = 255; g = 255; r = 255; a = 255; } // white
                    else if (andBit && xorBit)   { b = 64;  g = 64;  r = 64;  a = 255; } // approx inverse
                    else                         { b = 0;   g = 0;   r = 0;   a = 0;   } // transparent
                    _cursorBgra[dstRow + x * 4 + 0] = b;
                    _cursorBgra[dstRow + x * 4 + 1] = g;
                    _cursorBgra[dstRow + x * 4 + 2] = r;
                    _cursorBgra[dstRow + x * 4 + 3] = a;
                }
            }
            _cursorBgraW = width;
            _cursorBgraH = realH;
        }
        else
        {
            // Unknown / future shape type — skip.  Cursor stays at the last
            // shape we successfully decoded.
        }
    }

    private void CompositeCursor(Bitmap dst)
    {
        // Build a transient cursor bitmap from the cached BGRA buffer and draw
        // it at the recorded position.  Graphics.DrawImage handles per-pixel
        // alpha for the Color/MaskedColor paths and the synthetic alpha for
        // the Monochrome path.
        var cursorBmp = new Bitmap(_cursorBgraW, _cursorBgraH, PixelFormat.Format32bppArgb);
        var data = cursorBmp.LockBits(new Rectangle(0, 0, _cursorBgraW, _cursorBgraH),
            ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            // Stride may have padding; copy row-by-row.
            for (int y = 0; y < _cursorBgraH; y++)
            {
                Marshal.Copy(_cursorBgra!, y * _cursorBgraW * 4,
                             data.Scan0 + y * data.Stride, _cursorBgraW * 4);
            }
        }
        finally { cursorBmp.UnlockBits(data); }

        try
        {
            using var g = Graphics.FromImage(dst);
            g.CompositingMode = System.Drawing.Drawing2D.CompositingMode.SourceOver;
            g.DrawImage(cursorBmp, _cursorPos.X, _cursorPos.Y, _cursorBgraW, _cursorBgraH);
        }
        finally { cursorBmp.Dispose(); }
    }

    // ─────────── Dispose ───────────

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _duplication?.Dispose(); } catch { }
        try { _stagingTexture?.Dispose(); } catch { }
        try { _context?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }
        try { _gdiFallback?.Dispose(); } catch { }
    }

    // ─────────── P/Invoke + constants ───────────

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr pAdapter, int driverType, IntPtr software, uint flags,
        IntPtr pFeatureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

    private const int  D3D_DRIVER_TYPE_HARDWARE = 1;
    private const uint D3D11_CREATE_DEVICE_BGRA = 0x20;
    private const uint D3D11_SDK_VERSION        = 7;

    // DXGI error codes — Result struct's HResult comparison uses raw int.
    private const int DXGI_ERROR_WAIT_TIMEOUT  = unchecked((int)0x887A0027);
    private const int DXGI_ERROR_ACCESS_LOST   = unchecked((int)0x887A0026);
    private const int DXGI_ERROR_ACCESS_DENIED = unchecked((int)0x887A002B);
}
