using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace ClassroomCtrl.Student.Mac;

/// <summary>
/// Managed wrapper over the native kiosk lock (<c>Lock.swift</c>) and keystroke guard
/// (<c>Input.swift</c>) in <c>libNtyCapture.dylib</c>.
///
/// The native layer provides two orthogonal mechanisms:
///   1. <b>Screen shield</b> — borderless black window at <c>CGShieldingWindowLevel</c> on
///      every display + <c>NSApplicationPresentationOptions</c> kiosk mode that disables
///      Cmd+Tab, Force-Quit, logout, Dock, and menu bar.  Works with ZERO Accessibility
///      permission.  (<c>nty_lock_show/hide</c>)
///   2. <b>Keystroke guard</b> — <c>CGEventTap</c> that suppresses Spotlight, Mission
///      Control, and Spaces shortcuts while the lock is up.  Requires Accessibility;
///      if denied, the lock still works — the guard degrades gracefully.
///      (<c>nty_input_guard_start/stop</c>)
///
/// This class layers three safety mechanisms on top:
///   • <b>Dead-man switch</b> — if the teacher disconnects (TCP drop, crash), the lock
///     auto-releases after <see cref="GraceMs"/> (45 s).  The daemon calls
///     <see cref="OnTeacherDisconnected"/>; if the teacher reconnects before the timer
///     fires, <see cref="OnTeacherReconnected"/> cancels it.
///   • <b>Max-duration cap</b> — an absolute ceiling of <see cref="MaxDurationMs"/>
///     (30 min) prevents a student machine from staying locked indefinitely if the
///     teacher forgets to unlock.
///   • <b>Process-death backstop</b> — if the daemon process is killed, macOS tears down
///     the <c>NSApplicationPresentationOptions</c> and the <c>CGEventTap</c> automatically
///     (both are process-owned resources).  This is an OS-level guarantee, not code.
///
/// Thread safety: all public methods are safe to call from any thread.  The native
/// functions dispatch to the AppKit main thread internally; the Timer callbacks
/// execute on the ThreadPool.  The <see cref="_lock"/> object serializes state
/// transitions so concurrent Lock/Unlock/disconnect calls don't race.
/// </summary>
public sealed partial class LockService : IDisposable
{
    // ──────────── Native P/Invoke (libNtyCapture.dylib) ────────────

    private const string Lib = "NtyCapture";

    // Screen shield
    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void nty_lock_show(string message);

    [LibraryImport(Lib)]
    private static partial void nty_lock_hide();

    [LibraryImport(Lib)]
    private static partial int nty_lock_is_shown();

    // Keystroke guard (CGEventTap)
    [LibraryImport(Lib)]
    private static partial int nty_input_guard_start();

    [LibraryImport(Lib)]
    private static partial void nty_input_guard_stop();

    // Accessibility permission check
    [LibraryImport(Lib)]
    private static partial int nty_accessibility_check();

    [LibraryImport(Lib)]
    private static partial int nty_accessibility_request();

    // ──────────── Configuration ────────────

    /// <summary>Auto-unlock grace period after teacher disconnect (45 seconds).</summary>
    public const int GraceMs = 45_000;

    /// <summary>Absolute maximum lock duration to prevent permanent student lock-out (30 minutes).</summary>
    public const int MaxDurationMs = 30 * 60 * 1000;

    // ──────────── State ────────────

    private readonly ILogger<LockService> _logger;
    private readonly object _lock = new();

    private Timer? _deadManTimer;
    private Timer? _maxDurationTimer;
    private bool _isLocked;
    private bool _inputGuardActive;
    private bool _disposed;
    private DateTimeOffset _lockStartedAt;

    // ──────────── Public API ────────────

    public LockService(ILogger<LockService> logger)
    {
        _logger = logger;
    }

    /// <summary>Whether the lock shield is currently showing.</summary>
    public bool IsLocked
    {
        get { lock (_lock) { return _isLocked; } }
    }

    /// <summary>Whether the optional keystroke guard (Accessibility-dependent) is active.</summary>
    public bool IsInputGuardActive
    {
        get { lock (_lock) { return _inputGuardActive; } }
    }

    /// <summary>
    /// Check whether the process has Accessibility permission (for keystroke guard).
    /// Does not prompt — use <see cref="RequestAccessibility"/> to trigger the system dialog.
    /// </summary>
    public static bool HasAccessibility => OperatingSystem.IsMacOS() && nty_accessibility_check() == 1;

    /// <summary>
    /// Prompt the user for Accessibility permission.  The grant only takes effect after
    /// a process relaunch on some macOS versions.
    /// Returns the trust state AT CALL TIME (may still be false if the user hasn't toggled it yet).
    /// </summary>
    public static bool RequestAccessibility => OperatingSystem.IsMacOS() && nty_accessibility_request() == 1;

    /// <summary>
    /// Activate the full-screen lock shield on every display.
    /// Optionally installs the keystroke guard if Accessibility is granted.
    /// Starts the max-duration safety timer.
    /// </summary>
    /// <param name="message">Text shown on the lock screen (e.g. teacher name, school).
    /// Empty/null defaults to "Locked by teacher" (native-side default).</param>
    public void Lock(string? message = null)
    {
        if (!OperatingSystem.IsMacOS())
        {
            _logger.LogWarning("[LockService] Lock called on non-macOS — ignored");
            return;
        }

        lock (_lock)
        {
            if (_disposed) return;

            if (_isLocked)
            {
                // Already locked — refresh message only (e.g. teacher changed the text).
                _logger.LogInformation("[LockService] Refreshing lock message");
                nty_lock_show(message ?? "");
                return;
            }

            _isLocked = true;
            _lockStartedAt = DateTimeOffset.UtcNow;

            // 1. Show the kiosk shield (works without Accessibility)
            nty_lock_show(message ?? "");
            _logger.LogInformation("[LockService] Screen lock ACTIVATED");

            // 2. Attempt the keystroke guard (requires Accessibility — degrades gracefully)
            TryStartInputGuard();

            // 3. Start the max-duration safety cap
            _maxDurationTimer?.Dispose();
            _maxDurationTimer = new Timer(
                OnMaxDurationExpired,
                state: null,
                dueTime: MaxDurationMs,
                period: Timeout.Infinite);

            _logger.LogInformation(
                "[LockService] Max-duration timer set ({Min} min)",
                MaxDurationMs / 60_000);
        }
    }

    /// <summary>
    /// Deactivate the lock shield and keystroke guard.  Safe to call when not locked.
    /// Cancels any pending dead-man or max-duration timers.
    /// </summary>
    public void Unlock()
    {
        lock (_lock)
        {
            if (!_isLocked)
            {
                _logger.LogDebug("[LockService] Unlock called but not locked — no-op");
                return;
            }

            PerformUnlock("explicit teacher command");
        }
    }

    /// <summary>
    /// Notify the lock service that the TCP connection to the teacher dropped.
    /// Starts the dead-man countdown — if the teacher doesn't reconnect within
    /// <see cref="GraceMs"/>, the lock auto-releases.
    /// </summary>
    public void OnTeacherDisconnected()
    {
        lock (_lock)
        {
            if (_disposed || !_isLocked) return;

            // Don't restart the timer if one is already ticking
            if (_deadManTimer is not null) return;

            _logger.LogWarning(
                "[LockService] Teacher disconnected — dead-man timer started ({Sec} s grace)",
                GraceMs / 1000);

            _deadManTimer = new Timer(
                OnDeadManExpired,
                state: null,
                dueTime: GraceMs,
                period: Timeout.Infinite);
        }
    }

    /// <summary>
    /// Notify the lock service that the teacher reconnected (TCP re-established).
    /// Cancels the dead-man countdown if one is ticking.
    /// </summary>
    public void OnTeacherReconnected()
    {
        lock (_lock)
        {
            if (_deadManTimer is null) return;

            _deadManTimer.Dispose();
            _deadManTimer = null;

            _logger.LogInformation("[LockService] Teacher reconnected — dead-man timer cancelled");
        }
    }

    // ──────────── Timer callbacks ────────────

    private void OnDeadManExpired(object? state)
    {
        lock (_lock)
        {
            if (!_isLocked) return;
            _logger.LogWarning("[LockService] Dead-man timer fired — auto-unlocking (teacher gone for {Sec} s)", GraceMs / 1000);
            PerformUnlock("dead-man switch (teacher disconnect timeout)");
        }
    }

    private void OnMaxDurationExpired(object? state)
    {
        lock (_lock)
        {
            if (!_isLocked) return;
            var elapsed = DateTimeOffset.UtcNow - _lockStartedAt;
            _logger.LogWarning(
                "[LockService] Max-duration cap reached ({Elapsed:F0} min) — auto-unlocking",
                elapsed.TotalMinutes);
            PerformUnlock("max-duration cap");
        }
    }

    // ──────────── Internal ────────────

    /// <summary>Core unlock — must be called under <see cref="_lock"/>.</summary>
    private void PerformUnlock(string reason)
    {
        // 1. Stop the keystroke guard first (so keys flow while the shield is tearing down)
        if (_inputGuardActive)
        {
            try
            {
                nty_input_guard_stop();
                _inputGuardActive = false;
                _logger.LogInformation("[LockService] Input guard stopped");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[LockService] nty_input_guard_stop failed");
            }
        }

        // 2. Hide the shield
        try
        {
            nty_lock_hide();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[LockService] nty_lock_hide failed");
        }

        // 3. Cancel timers
        _deadManTimer?.Dispose();
        _deadManTimer = null;
        _maxDurationTimer?.Dispose();
        _maxDurationTimer = null;

        _isLocked = false;

        var elapsed = DateTimeOffset.UtcNow - _lockStartedAt;
        _logger.LogInformation(
            "[LockService] Screen lock DEACTIVATED (reason: {Reason}, was locked for {Elapsed:F1} s)",
            reason, elapsed.TotalSeconds);
    }

    /// <summary>
    /// Attempt to install the CGEventTap keystroke guard.  Degrades gracefully:
    /// if Accessibility is not granted the lock still works (the shield + presentation
    /// options enforce the kiosk), just without suppressing Spotlight/Mission Control keys.
    /// </summary>
    private void TryStartInputGuard()
    {
        try
        {
            int rc = nty_input_guard_start();
            switch (rc)
            {
                case 0:
                    _inputGuardActive = true;
                    _logger.LogInformation("[LockService] Input guard installed (Accessibility granted)");
                    break;
                case -1:
                    _logger.LogInformation(
                        "[LockService] Accessibility not granted — lock enforced via shield only " +
                        "(Spotlight/Mission Control keys NOT suppressed). Grant Accessibility in " +
                        "System Settings > Privacy > Accessibility for full kiosk lock.");
                    break;
                case -2:
                    _logger.LogWarning("[LockService] CGEventTap creation failed (code -2)");
                    break;
                case -3:
                    _inputGuardActive = true;  // already running = effectively active
                    _logger.LogDebug("[LockService] Input guard already running");
                    break;
                default:
                    _logger.LogWarning("[LockService] nty_input_guard_start returned unexpected code: {Code}", rc);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Native interop failure — the lock shield still works, just no tap.
            _logger.LogError(ex, "[LockService] Failed to start input guard");
        }
    }

    // ──────────── IDisposable ────────────

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;

            if (_isLocked)
            {
                _logger.LogWarning("[LockService] Disposing while locked — force-unlocking");
                PerformUnlock("service dispose");
            }
            else
            {
                // Defensive cleanup even when not locked
                _deadManTimer?.Dispose();
                _maxDurationTimer?.Dispose();
            }
        }
    }
}
