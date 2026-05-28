using System;

namespace ClassroomCtrl.Shared.Capture;

/// <summary>
/// Phase 11-B inc3 — single switch point that returns the
/// <see cref="IScreenCapturer"/> matching the requested source + flag combo.
///
/// inc3 part 1 (this checkpoint): always returns GDI — zero behavior change vs
/// inc2.  The <c>useDxgi</c> parameter is wired through the call sites so part
/// 2 can flip the branch in without touching the broadcasters again.
///
/// inc3 part 2: when <c>useDxgi</c> is true, tries
/// <see cref="DxgiScreenCapturer"/> first; on init failure, falls back to GDI
/// permanently for the session.  In-session failures (access lost, etc.) are
/// handled inside the DXGI capturer itself with per-frame GDI fallback so the
/// share keeps going through the UAC prompt, lock screen, fullscreen-exclusive
/// app, etc.
/// </summary>
public static class ScreenCapturerFactory
{
    /// <param name="source">Which screen region to capture.  Teacher uses
    /// <see cref="ScreenCaptureSource.Primary"/>; Student uses
    /// <see cref="ScreenCaptureSource.VirtualScreen"/>.</param>
    /// <param name="useDxgi">inc3 part 2 — when true, return DXGI; false (the
    /// inc3 default) always returns GDI.</param>
    /// <param name="activeDescription">Out — single-line label the broadcaster
    /// logs at Start.  Reflects the actual backend in use.</param>
    public static IScreenCapturer Create(ScreenCaptureSource source, bool useDxgi, out string activeDescription)
    {
        if (useDxgi)
        {
            try
            {
                var dxgi = new DxgiScreenCapturer(source);
                activeDescription = dxgi.Description;
                return dxgi;
            }
            catch (Exception ex)
            {
                // Silent fall-through to GDI.  activeDescription tells the truth
                // so the broadcaster's startup log shows the degradation without
                // flooding per-frame logs.
                activeDescription = $"GDI (DXGI init failed: {ex.Message.Split('\n')[0]})";
                return new GdiScreenCapturer(source);
            }
        }
        var gdi = new GdiScreenCapturer(source);
        activeDescription = gdi.Description;
        return gdi;
    }
}
