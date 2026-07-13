using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

/// <summary>
/// Phase 30-C — enforces the teacher screen-lock via the native shield (Lock.swift)
/// and owns the FOUR-LAYER DEAD-MAN SWITCH so a lock can never permanently strand a
/// student (the shipped Windows lock has no such safety — it stays up forever).
///
/// Layers:
///  1. Process-kill → the OS releases the presentation options + drops the shield
///     windows (owned by this process) = auto-unlock. By construction; no code here.
///  2. Disconnect grace → on leaving Connected, a SINGLE continuous 45 s timer; if the
///     wire returns to Connected it's cancelled (lock holds — the Wi-Fi-blip case); if
///     45 s elapse still not Connected → auto-unlock (the teacher-death case). SILENT —
///     the shield never reveals the countdown, so it isn't a teachable exploit.
///  3. Max-duration cap → an independent 30 min timer from when the lock SHOWS → unlock
///     regardless of connection (backstop against a wedged-but-alive teacher).
///  4. Sleep/wake re-assert → handled natively (Lock.swift didWake) while native's
///     `active` state tracks this service's <see cref="_locked"/> (single source of truth).
///
/// UI-agnostic (no Avalonia refs); the ViewModel reflects <see cref="LockStateChanged"/>.
/// </summary>
public sealed partial class LockService
{
    private const string Lib = "NtyCapture";

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial void nty_lock_show(string message);
    [LibraryImport(Lib)] private static partial void nty_lock_hide();
    [LibraryImport(Lib)] private static partial int nty_lock_is_shown();

    private readonly int _graceMs;         // disconnect grace (Wi-Fi blip vs teacher-death)
    private readonly int _maxDurationMs;   // max-duration cap
    private readonly Action<string> _shieldShow;   // shield backend (native by default)
    private readonly Action _shieldHide;
    private readonly Func<bool> _shieldIsShown;

    public static bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>Production: 45 s grace, 30 min cap, NATIVE shield.</summary>
    public LockService(int graceMs = 45_000, int maxDurationMs = 30 * 60_000)
        : this(graceMs, maxDurationMs,
               static m => { if (IsSupported) nty_lock_show(m); },
               static () => { if (IsSupported) nty_lock_hide(); },
               static () => IsSupported && nty_lock_is_shown() == 1)
    { }

    /// <summary>Test seam: inject a shield backend so the FOUR-LAYER DEAD-MAN LOGIC can
    /// be proven (shrunk timers, flag-tracking backend) with ZERO AppKit involvement —
    /// no window, no main-loop dependency, no screen-takeover risk. The native shield
    /// RENDERING is proven separately (30-B auto-hide harness + 30-F LIVE).</summary>
    public LockService(int graceMs, int maxDurationMs,
                       Action<string> shieldShow, Action shieldHide, Func<bool> shieldIsShown)
    {
        _graceMs = graceMs;
        _maxDurationMs = maxDurationMs;
        _shieldShow = shieldShow;
        _shieldHide = shieldHide;
        _shieldIsShown = shieldIsShown;
    }

    private readonly object _gate = new();
    private bool _locked;      // teacher intends locked (LockScreen received, no Unlock/auto-unlock yet)
    private bool _shieldUp;    // native shield currently shown
    private string _message = "Locked by teacher";
    private Timer? _graceTimer;
    private Timer? _capTimer;

    /// <summary>Raised when the shield goes up (true) / comes down (false), incl. auto-unlock.
    /// Fires on the caller's or a timer thread — marshal to the UI thread.</summary>
    public event Action<bool>? LockStateChanged;

    /// <summary>Debug-only trace (grace start/cancel/fire, cap fire). Never shown to the student.</summary>
    public event Action<string>? Log;

    public bool IsLocked { get { lock (_gate) return _locked; } }

    // ── teacher commands (from wire dispatch) ────────────────────────────────────
    /// <summary>LockScreen received. The connection is up (we just received on it), so
    /// show now, start the 30 min cap, and clear any stale grace.</summary>
    public void Lock(string? message)
    {
        lock (_gate)
        {
            _message = string.IsNullOrWhiteSpace(message) ? "Locked by teacher" : message!;
            _locked = true;
            CancelGrace();
            StartCap();
            ShowShield();
        }
    }

    /// <summary>Explicit UnlockScreen from the teacher.</summary>
    public void Unlock()
    {
        lock (_gate)
        {
            if (!_locked && !_shieldUp) return;
            _locked = false;
            CancelGrace();
            CancelCap();
            HideShield();
        }
    }

    // ── dead-man layer 2: disconnect grace ───────────────────────────────────────
    /// <summary>Feed EVERY WireClient status transition here. Only matters while locked.
    /// Connected → cancel grace (lock holds). Non-Connected → start ONE continuous grace
    /// window (do not restart on Reconnecting→Disconnected).</summary>
    public void OnConnectionStatus(WireStatus status)
    {
        lock (_gate)
        {
            if (!_locked) return;
            if (status == WireStatus.Connected)
            {
                CancelGrace();                 // reconnected → cancel pending unlock
                if (!_shieldUp) ShowShield();  // re-assert if it had dropped
            }
            else
            {
                StartGraceIfIdle();            // continuous 45 s from first leaving Connected
            }
        }
    }

    private void StartGraceIfIdle()
    {
        if (_graceTimer != null) return;       // already counting — do NOT restart
        Log?.Invoke("grace start (45s, silent)");
        _graceTimer = new Timer(_ => OnGraceFired(), null, _graceMs, Timeout.Infinite);
    }

    private void CancelGrace()
    {
        if (_graceTimer == null) return;
        _graceTimer.Dispose();
        _graceTimer = null;
        Log?.Invoke("grace cancel (reconnected — lock holds)");
    }

    private void OnGraceFired()
    {
        lock (_gate)
        {
            if (_graceTimer == null) return;   // cancelled in the race
            _graceTimer.Dispose();
            _graceTimer = null;
            Log?.Invoke("grace FIRED (45s no reconnect) → auto-unlock");
            _locked = false;
            CancelCap();
            HideShield();
        }
    }

    // ── dead-man layer 3: max-duration cap ───────────────────────────────────────
    private void StartCap()
    {
        CancelCap();
        _capTimer = new Timer(_ => OnCapFired(), null, _maxDurationMs, Timeout.Infinite);
    }

    private void CancelCap()
    {
        _capTimer?.Dispose();
        _capTimer = null;
    }

    private void OnCapFired()
    {
        lock (_gate)
        {
            if (_capTimer == null) return;
            _capTimer.Dispose();
            _capTimer = null;
            Log?.Invoke("max-duration cap FIRED (30min) → auto-unlock");
            _locked = false;
            CancelGrace();
            HideShield();
        }
    }

    // ── shield backend (called under _gate) ──────────────────────────────────────
    private void ShowShield()
    {
        _shieldShow(_message);
        if (!_shieldUp) { _shieldUp = true; LockStateChanged?.Invoke(true); }
    }

    private void HideShield()
    {
        _shieldHide();
        if (_shieldUp) { _shieldUp = false; LockStateChanged?.Invoke(false); }
    }

    /// <summary>Shield-backend truth (1 if the shield is up) — for tests/verification.</summary>
    public bool ShieldShown() => _shieldIsShown();
}
