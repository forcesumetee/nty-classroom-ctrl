using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

/// <summary>The four TCC permissions the macOS student can use.</summary>
public enum PermId { Screen, Camera, Microphone, Accessibility }

/// <summary>Unified permission state across the four subsystems (screen is binary at the native
/// layer — a preflight bool — so it never reports Denied, only Granted / NotDetermined).</summary>
public enum PermState { NotDetermined, Granted, Denied, Unsupported }

public enum PermTier { Required, Optional }

/// <summary>Static description of one permission row (name / purpose / tier / relaunch caveat).</summary>
public sealed record PermInfo(PermId Id, string Name, string Purpose, PermTier Tier, bool NeedsRelaunch);

/// <summary>
/// Phase 32-D — one place to check/request the four TCC permissions the student uses, reusing the
/// SAME native symbols proven per-subsystem (Screen M16/27-A, Camera M19/28-C, Mic M20/29-D,
/// Accessibility M22/31). Aggregated here so the onboarding view can present all four uniformly.
///
/// Tiers: Screen Recording is the PRIMARY value (the teacher seeing the screen), but the app still
/// connects + locks without it — so nothing here is a HARD block; the rest are feature/enforcement
/// optional. Only Screen Recording needs an app RELAUNCH after granting (TCC caches the capture
/// entitlement at process launch); Camera/Mic/Accessibility take effect immediately.
/// </summary>
public static partial class Permissions
{
    private const string Lib = "NtyCapture";
    // Same symbols the per-subsystem services P/Invoke; the native header is the source of truth.
    [LibraryImport(Lib)] private static partial int nty_check_permission();            // screen (preflight bool)
    [LibraryImport(Lib)] private static partial int nty_request_permission();
    [LibraryImport(Lib)] private static partial int nty_camera_check_permission();     // 1/0/-1
    [LibraryImport(Lib)] private static partial int nty_camera_request_permission();
    [LibraryImport(Lib)] private static partial int nty_audio_check_permission();      // 1/0/-1
    [LibraryImport(Lib)] private static partial int nty_audio_request_permission();
    [LibraryImport(Lib)] private static partial int nty_accessibility_check();         // 1/0
    [LibraryImport(Lib)] private static partial int nty_accessibility_request();

    public static bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>The four rows, in onboarding order (primary first).</summary>
    public static readonly IReadOnlyList<PermInfo> All = new[]
    {
        new PermInfo(PermId.Screen, "Screen Recording",
            "Lets the teacher see your screen — the core classroom feature.", PermTier.Required, NeedsRelaunch: true),
        new PermInfo(PermId.Camera, "Camera",
            "For Conference video (your camera).", PermTier.Optional, NeedsRelaunch: false),
        new PermInfo(PermId.Microphone, "Microphone",
            "For audio talkback and Conference.", PermTier.Optional, NeedsRelaunch: false),
        new PermInfo(PermId.Accessibility, "Accessibility",
            "Blocks Spotlight & Mission Control while locked. The lock still works without it.", PermTier.Optional, NeedsRelaunch: false),
    };

    // ── check (never prompts) ────────────────────────────────────────────────────
    public static PermState Check(PermId id)
    {
        if (!IsSupported) return PermState.Unsupported;
        return id switch
        {
            PermId.Screen        => nty_check_permission() == 1 ? PermState.Granted : PermState.NotDetermined,
            PermId.Camera        => MapTri(nty_camera_check_permission()),
            PermId.Microphone    => MapTri(nty_audio_check_permission()),
            PermId.Accessibility => nty_accessibility_check() == 1 ? PermState.Granted : PermState.NotDetermined,
            _ => PermState.Unsupported,
        };
    }

    // ── request (prompts; the native call may block until the user decides → off the UI thread) ──
    public static Task<PermState> RequestAsync(PermId id)
    {
        if (!IsSupported) return Task.FromResult(PermState.Unsupported);
        return Task.Run(() => id switch
        {
            PermId.Screen        => nty_request_permission() == 1 ? PermState.Granted : PermState.NotDetermined,
            PermId.Camera        => MapTri(nty_camera_request_permission()),
            PermId.Microphone    => MapTri(nty_audio_request_permission()),
            PermId.Accessibility => nty_accessibility_request() == 1 ? PermState.Granted : PermState.NotDetermined,
            _ => PermState.Unsupported,
        });
    }

    private static PermState MapTri(int code) => code switch
    {
        1 => PermState.Granted,
        0 => PermState.NotDetermined,
        _ => PermState.Denied,
    };
}

/// <summary>
/// Pure presentation of a <see cref="PermState"/> (status pill glyph / text / style-class) and the
/// run-readiness rule — Avalonia-free so it's unit-testable headlessly (MockTeacher --permtest). The
/// view rendering + real TCC grant/deny flow are proven LIVE.
/// </summary>
public static class PermissionPresenter
{
    public static (string glyph, string text, string cls) Pill(PermState s) => s switch
    {
        PermState.Granted     => ("🟢", "Granted", "granted"),
        PermState.Denied      => ("🔴", "Denied", "denied"),
        PermState.Unsupported => ("⚪", "Unsupported", "unknown"),
        _                     => ("⚪", "Not requested", "unknown"),   // NotDetermined
    };

    /// <summary>The app is usable once Screen Recording is granted (its primary value). Optional
    /// permissions being absent only disables their features — never blocks running.</summary>
    public static bool IsReadyToRun(PermState screen) => screen == PermState.Granted;
}
