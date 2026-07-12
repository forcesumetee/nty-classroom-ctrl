using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

public enum ScreenPermission { Denied, Granted }

/// <summary>
/// Phase 27-A — managed side of the native ScreenCaptureKit helper
/// (native/NtyCapture/libNtyCapture.dylib). UI-agnostic (no Avalonia refs), like
/// <see cref="WireClient"/> — the ViewModel marshals to the UI thread. This is the
/// interop template for every Phase 27 subsystem: a tiny P/Invoke surface over a
/// Swift dylib, plus (from 27-A-3) a GC-rooted frame callback.
///
/// 27-A-2 scope: Screen Recording permission (TCC). Capture start/stop wiring +
/// the FrameReceived event land in 27-A-3.
/// </summary>
public sealed partial class ScreenCaptureService
{
    private const string Lib = "NtyCapture"; // → libNtyCapture.dylib on macOS

    [LibraryImport(Lib)]
    private static partial int nty_check_permission();

    [LibraryImport(Lib)]
    private static partial int nty_request_permission();

    /// <summary>True only on macOS (the dylib is macOS-only). Guards P/Invoke so the
    /// app still loads on other platforms (permission simply reports Denied).</summary>
    public static bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>Current Screen Recording authorization — never prompts.</summary>
    public ScreenPermission CheckPermission()
    {
        if (!IsSupported) return ScreenPermission.Denied;
        return nty_check_permission() == 1 ? ScreenPermission.Granted : ScreenPermission.Denied;
    }

    /// <summary>
    /// Trigger the system Screen Recording prompt (if undetermined) and return the
    /// authorization at call time. NOTE: for Screen Recording the grant only takes
    /// effect after an app RELAUNCH (TCC caches it at process launch) — callers
    /// should prompt the user to grant + relaunch, then re-<see cref="CheckPermission"/>.
    /// </summary>
    public Task<ScreenPermission> RequestPermissionAsync()
    {
        if (!IsSupported) return Task.FromResult(ScreenPermission.Denied);
        // The native call is quick (shows the prompt, returns current status); run it
        // off the UI thread so a slow first-time TCC init never stalls rendering.
        return Task.Run(() =>
            nty_request_permission() == 1 ? ScreenPermission.Granted : ScreenPermission.Denied);
    }
}
