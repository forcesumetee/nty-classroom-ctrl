using System;
using System.Drawing;

namespace ClassroomCtrl.Shared.Capture;

/// <summary>
/// Phase 11-B inc3 — pluggable screen-capture surface.
///
/// Mirrors the inc2 <see cref="ClassroomCtrl.Shared.Codec.IVideoEncoder"/> pattern:
/// one interface, the broadcasters consume it identically regardless of whether the
/// frames came from the GDI path (inc1/inc2 default — the ~4 FPS bottleneck) or
/// the DXGI Desktop Duplication path (inc3, gated by the <c>UseDxgiCapture</c>
/// HKCU flag — the actual FPS unlock once inc4 raises the cap).
///
/// Contract for every implementation:
///   • <see cref="Capture"/> returns a fresh 32bpp BGRA <see cref="Bitmap"/>
///     sized to (<c>targetWidth</c>, <c>targetHeight</c>) — the same shape both
///     <see cref="ClassroomCtrl.Shared.Codec.IVideoEncoder"/> implementations
///     accept today.  Caller owns it and must <c>using</c> / Dispose.
///   • <see cref="Capture"/> may return <c>null</c> to mean "no new frame this
///     tick — keep the previous one" (DXGI returns this on
///     DXGI_ERROR_WAIT_TIMEOUT for a static screen; GDI returns it only on
///     genuine errors).  The broadcaster's encode loop already handles a null
///     frame by skipping the tick.
///   • <see cref="Description"/> is the single-line label the broadcaster logs
///     once at Start so the dev sees the live capture backend.  Must tell the
///     truth — if DXGI fell back to GDI mid-session, this updates to <c>"GDI
///     (DXGI fell back: &lt;reason&gt;)"</c>.
/// </summary>
public interface IScreenCapturer : IDisposable
{
    /// <summary>Capture the current screen and return a freshly allocated 32bpp
    /// BGRA bitmap at the requested target size, or <c>null</c> if no new frame
    /// is available right now (caller should reuse the prior frame).</summary>
    Bitmap? Capture(int targetWidth, int targetHeight);

    /// <summary>Single-line label naming the live capture backend.  May change
    /// over the encoder's lifetime if DXGI falls back to GDI.</summary>
    string Description { get; }
}

/// <summary>
/// Phase 11-B inc3 — chooses which source region a capturer covers.
///
/// Two broadcasters needed two regions in the GDI era:
///   • Teacher uses the <b>primary</b> output (DPI-aware physical pixels from
///     <c>GetSystemMetrics</c>).
///   • Student uses the <b>virtual screen</b> bounds (<c>VirtualScreen</c> —
///     the union across all monitors).
///
/// DXGI Desktop Duplication captures a single output at a time, so the virtual-
/// screen semantic degrades to "primary output" under DXGI for inc3.  Multi-
/// monitor capture is a documented follow-up (inc4 or later).
/// </summary>
public enum ScreenCaptureSource
{
    /// <summary>Primary monitor only — the source the Teacher broadcaster uses.</summary>
    Primary,
    /// <summary>Union of all monitors — the source the Student broadcaster uses
    /// on GDI.  Under DXGI this falls back to primary-only for inc3.</summary>
    VirtualScreen,
}
