using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ClassroomCtrl.Shared.Capture;

/// <summary>
/// Phase 11-B inc3 — the original GDI <c>CopyFromScreen</c> + bicubic
/// <c>DrawImage</c> capture path, extracted verbatim from the two broadcasters'
/// inline implementations so behavior is byte-identical to inc2.
///
/// Cost characteristics (the reason inc3 exists):
///   • CopyFromScreen for the full screen: ~25-50 ms on the dev box.
///   • Bicubic + HighQuality + HighQuality DrawImage to target: ~10-30 ms.
///   • Combined ~30-80 ms/frame — the ~4 FPS bottleneck identified in Phase 11-A.
///
/// One class, two sources:
///   • Teacher used <c>GetSystemMetrics(SM_CXSCREEN/CYSCREEN)</c> for the
///     primary monitor in physical pixels — the DPI-aware fix from earlier
///     phases.  <see cref="ScreenCaptureSource.Primary"/> preserves that.
///   • Student used <see cref="SystemInformation.VirtualScreen"/> bounds — the
///     union across monitors.  <see cref="ScreenCaptureSource.VirtualScreen"/>
///     preserves that.
///
/// Cursor: GDI <c>CopyFromScreen</c> includes the system cursor by default, so
/// no extra compositing is needed here.  (The DXGI sibling has to do it
/// manually — see <see cref="DxgiScreenCapturer"/>.)
/// </summary>
public sealed class GdiScreenCapturer : IScreenCapturer
{
    private readonly ScreenCaptureSource _source;
    private bool _disposed;

    public string Description { get; }

    public GdiScreenCapturer(ScreenCaptureSource source)
    {
        _source = source;
        Description = source == ScreenCaptureSource.Primary
            ? "GDI (primary monitor)"
            : "GDI (virtual screen)";
    }

    public Bitmap? Capture(int targetWidth, int targetHeight)
    {
        if (_disposed) return null;
        try
        {
            // Resolve the source rectangle in physical pixels.  Two sources kept
            // distinct to preserve the inc2 behavior exactly.
            int srcX, srcY, srcW, srcH;
            if (_source == ScreenCaptureSource.Primary)
            {
                srcX = 0;
                srcY = 0;
                srcW = GetSystemMetrics(SM_CXSCREEN);
                srcH = GetSystemMetrics(SM_CYSCREEN);
                if (srcW <= 0 || srcH <= 0)
                {
                    // Fallback to WPF SystemParameters — same fallback the inline
                    // Teacher code used.  Note these may return DIPs on non-DPI-
                    // aware processes; the manifest fix already handles that.
                    srcW = (int)System.Windows.SystemParameters.PrimaryScreenWidth;
                    srcH = (int)System.Windows.SystemParameters.PrimaryScreenHeight;
                }
                if (srcW <= 0 || srcH <= 0) return null;
            }
            else
            {
                // Win32 virtual-screen metrics — same numbers System.Windows.Forms.
                // SystemInformation.VirtualScreen returns, without pulling WinForms
                // into Shared.csproj.
                srcX = GetSystemMetrics(SM_XVIRTUALSCREEN);
                srcY = GetSystemMetrics(SM_YVIRTUALSCREEN);
                srcW = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                srcH = GetSystemMetrics(SM_CYVIRTUALSCREEN);
                if (srcW <= 0 || srcH <= 0) return null;
            }

            using var srcBmp = new Bitmap(srcW, srcH, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(srcBmp))
            {
                g.CopyFromScreen(srcX, srcY, 0, 0, new Size(srcW, srcH), CopyPixelOperation.SourceCopy);
            }

            // 32bpp BGRA — same format both IVideoEncoder implementations consume.
            var dstBmp = new Bitmap(targetWidth, targetHeight, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(dstBmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bicubic;
                g.SmoothingMode    = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.PixelOffsetMode  = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(srcBmp, 0, 0, targetWidth, targetHeight);
            }
            return dstBmp;
        }
        catch
        {
            // Caller treats null as "no frame this tick" — same contract as inc2.
            return null;
        }
    }

    public void Dispose() => _disposed = true;

    // Win32 P/Invoke kept private here so the inline teacher code can be removed
    // entirely — no more DPI-aware metrics scattered across the broadcasters.
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSCREEN        = 0;
    private const int SM_CYSCREEN        = 1;
    private const int SM_XVIRTUALSCREEN  = 76;
    private const int SM_YVIRTUALSCREEN  = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
}
