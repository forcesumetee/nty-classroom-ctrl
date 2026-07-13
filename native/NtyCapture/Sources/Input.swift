// Input.swift — CGEventTap keystroke guard for libNtyCapture.dylib (Phase 31-B)
// ---------------------------------------------------------------------------------
// Closes the two macOS-specific residuals the Phase 30 kiosk shield can't reach on
// its own (Spotlight-launch, Mission Control / Spaces), by intercepting a SMALL,
// explicit set of system keyboard shortcuts while the teacher lock is up.
//
// This is ADDITIVE hardening layered on top of the lock — it is NOT the lock. The
// shield + NSApplicationPresentationOptions (Phase 30) enforce the lock with ZERO
// Accessibility permission; this tap only suppresses a few launch shortcuts and
// requires Accessibility. If Accessibility is denied the lock still fully works —
// the caller (LockService, Phase 31-C) degrades gracefully and simply skips the tap.
//
// SAFETY — a CGEventTap has a bigger blast radius than the shield (a wedged tap could
// freeze the keyboard machine-wide), so every safety property below is deliberate:
//   1. FAILS OPEN. If our callback is slow, the OS auto-disables the tap and posts
//      .tapDisabledByTimeout — at which point keystrokes ALREADY flow through
//      untouched. We re-enable on that event to resume guarding, but the wedged state
//      is not reachable: the OS removes a misbehaving tap before it can strand keys.
//   2. Owned by THIS process. Killing the process tears the tap down (OS-enforced) —
//      the ultimate release, same backstop as the shield.
//   3. Tied to lock state. Installed only while the shield is up; removed on EVERY
//      unlock path (explicit / grace / cap) via LockService (31-C).
//   4. Dedicated run-loop thread. The tap lives on its own CFRunLoop thread, so a busy
//      AppKit main thread can never stall guarding — and stop() is bounded (it joins
//      that thread with a timeout), so uninstall is guaranteed, never hangs.
//   5. Allocation-light, lock-free callback. The hot path takes no locks and allocates
//      nothing, so the OS never has cause to disable us spuriously.
//
// The suppression set is intentionally SMALL (product decision, 31-A): Spotlight +
// Mission Control / Exposé / Spaces + Cmd+Tab/Cmd+` belt-and-suspenders. Deliberately
// LEFT ALONE: Cmd+Q (quit = process death = dead-man auto-unlock, and the tester's
// escape hatch), volume / brightness / media keys, and screenshots.

import Foundation
import CoreGraphics
import ApplicationServices   // AXIsProcessTrusted / AXIsProcessTrustedWithOptions

// MARK: - virtual keycodes (Carbon kVK_*), named for clarity
private let kcTab:   Int64 = 48
private let kcSpace: Int64 = 49
private let kcGrave: Int64 = 50   // `
private let kcF3:    Int64 = 99
private let kcLeft:  Int64 = 123
private let kcRight: Int64 = 124
private let kcDown:  Int64 = 125
private let kcUp:    Int64 = 126
// (kVK_ANSI_Q = 12 is deliberately NOT in any rule — Cmd+Q passes through.)

/// The four "major" modifiers we key suppression rules on (ignore Caps Lock / Fn / numpad).
private let majorMods: CGEventFlags = [.maskCommand, .maskControl, .maskAlternate, .maskShift]

/// One suppression rule: a keycode plus the EXACT set of major modifiers that must be
/// held. Exact-match avoids over-suppression (e.g. Cmd+Ctrl+Space = emoji picker is a
/// different combo than Cmd+Space = Spotlight, and is left alone).
private struct Rule { let key: Int64; let mods: CGEventFlags }

private let suppressRules: [Rule] = [
    Rule(key: kcSpace, mods: [.maskCommand]),                   // Spotlight
    Rule(key: kcSpace, mods: [.maskCommand, .maskAlternate]),   // Finder search / alt Spotlight
    Rule(key: kcF3,    mods: []),                               // Mission Control key (F3, best-effort*)
    Rule(key: kcUp,    mods: [.maskControl]),                   // Mission Control (Ctrl+Up)
    Rule(key: kcDown,  mods: [.maskControl]),                   // App Exposé (Ctrl+Down)
    Rule(key: kcLeft,  mods: [.maskControl]),                   // Spaces ← (Ctrl+Left)
    Rule(key: kcRight, mods: [.maskControl]),                   // Spaces → (Ctrl+Right)
    Rule(key: kcTab,   mods: [.maskCommand]),                   // Cmd+Tab (belt-and-suspenders)
    Rule(key: kcGrave, mods: [.maskCommand]),                   // Cmd+` (belt-and-suspenders)
]
// *The dedicated Mission Control key on some keyboards arrives as an NSSystemDefined
//  event, not a keyDown, so a keyDown tap can't catch that hardware key — the Ctrl+Arrow
//  shortcuts above cover the same actions and ARE keyDown. Documented residual.

// MARK: - controller state
// The CFMachPort / CFRunLoop live on the dedicated tap thread. Simple globals carry the
// cross-thread flags/counters; the start/stop semaphores fence the transitions so the
// diagnostic reads a consistent value on either side of them.
private final class InputGuardController {
    var tap: CFMachPort?
    var source: CFRunLoopSource?
    var runLoop: CFRunLoop?
    var thread: Thread?
    var stopped: DispatchSemaphore?   // signaled when the tap thread has fully torn down
}
private let guardCtl = InputGuardController()

private var guardActive: Int32 = 0          // 1 while a tap is installed (intent — read by is_active)
private var guardSuppressCount: Int64 = 0   // # events swallowed (diagnostic)
private var guardDisabledCount: Int64 = 0   // # times the OS auto-disabled us then we re-enabled (fail-open proof)
private var guardStallMs: Int32 = 0         // TEST-ONLY: force a slow callback to demo fail-open (default 0)

// MARK: - the tap callback (dedicated tap thread only; lock-free, allocation-light)
private func inputTapCallback(proxy: CGEventTapProxy, type: CGEventType,
                              event: CGEvent, userInfo: UnsafeMutableRawPointer?) -> Unmanaged<CGEvent>? {
    // FAIL-OPEN keystone: the OS disabled us (slow callback, or user-input reset). Keys
    // ALREADY flow untouched at this point; re-enable so we resume guarding. A wedged tap
    // is therefore not a reachable state — recovery is guaranteed by the OS + this line.
    if type == .tapDisabledByTimeout || type == .tapDisabledByUserInput {
        guardDisabledCount &+= 1
        if let t = guardCtl.tap { CGEvent.tapEnable(tap: t, enable: true) }
        return nil
    }

    // TEST-ONLY stall lever (default 0). --inputtest sets this to force the OS watchdog to
    // fire so the fail-open path can be DEMONSTRATED. Production never sets it.
    let stall = guardStallMs
    if stall > 0 { Thread.sleep(forTimeInterval: Double(stall) / 1000.0) }

    if type == .keyDown || type == .flagsChanged {
        let key = event.getIntegerValueField(.keyboardEventKeycode)
        let mods = event.flags.intersection(majorMods)
        for r in suppressRules where r.key == key && mods == r.mods {
            guardSuppressCount &+= 1
            return nil                       // swallow — the shortcut never reaches the system
        }
    }
    return Unmanaged.passUnretained(event)   // everything else passes through untouched
}

// MARK: - lifecycle (C ABI)

/// Start the keystroke guard on a dedicated run-loop thread.
/// Returns 0 on success, or a negative code the caller uses to DEGRADE GRACEFULLY:
///   -1 not Accessibility-trusted (caller keeps the lock, skips the tap)
///   -2 trusted but tap creation failed
///   -3 already running.
@_cdecl("nty_input_guard_start")
public func nty_input_guard_start() -> Int32 {
    if guardActive == 1 { return -3 }
    if !AXIsProcessTrusted() { return -1 }   // graceful: lock still enforced, just no keystroke suppression

    var startResult: Int32 = -2
    let ready = DispatchSemaphore(value: 0)
    let stopped = DispatchSemaphore(value: 0)
    guardCtl.stopped = stopped

    let t = Thread {
        let mask: CGEventMask =
            (CGEventMask(1) << CGEventType.keyDown.rawValue) |
            (CGEventMask(1) << CGEventType.flagsChanged.rawValue)
        guard let tap = CGEvent.tapCreate(tap: .cgSessionEventTap,
                                          place: .headInsertEventTap,
                                          options: .defaultTap,          // .defaultTap = may suppress/modify
                                          eventsOfInterest: mask,
                                          callback: inputTapCallback,
                                          userInfo: nil) else {
            startResult = -2
            ready.signal()
            return
        }
        let src = CFMachPortCreateRunLoopSource(kCFAllocatorDefault, tap, 0)
        let rl = CFRunLoopGetCurrent()
        CFRunLoopAddSource(rl, src, .commonModes)
        CGEvent.tapEnable(tap: tap, enable: true)
        guardCtl.tap = tap
        guardCtl.source = src
        guardCtl.runLoop = rl
        guardActive = 1
        startResult = 0
        ready.signal()                       // start() returns only once the tap is up (or failed)

        CFRunLoopRun()                       // blocks here until stop() calls CFRunLoopStop

        // Teardown (runs after the run loop stops) — the guaranteed uninstall.
        CGEvent.tapEnable(tap: tap, enable: false)
        CFRunLoopRemoveSource(rl, src, .commonModes)
        guardCtl.tap = nil
        guardCtl.source = nil
        guardCtl.runLoop = nil
        stopped.signal()                     // let stop() return only once the tap is truly gone
    }
    t.name = "nty-input-guard"
    guardCtl.thread = t
    t.start()
    ready.wait()
    return startResult
}

/// Stop the guard. UNCONDITIONAL and BOUNDED — safe when idle, never hangs (joins the tap
/// thread with a 2 s ceiling). This is the guaranteed-uninstall counterpart to start().
@_cdecl("nty_input_guard_stop")
public func nty_input_guard_stop() {
    guardActive = 0
    guardStallMs = 0
    guardSuppressCount = 0
    guardDisabledCount = 0
    if let rl = guardCtl.runLoop {
        CFRunLoopStop(rl)
        CFRunLoopWakeUp(rl)                  // ensure the stop is observed promptly
        _ = guardCtl.stopped?.wait(timeout: .now() + 3.0)   // join, but never block forever (covers a mid-stall teardown)
    }
    guardCtl.thread = nil
    guardCtl.stopped = nil
}

/// 1 if a tap is currently installed, else 0.
@_cdecl("nty_input_guard_is_active")
public func nty_input_guard_is_active() -> Int32 { guardActive }

// MARK: - Accessibility permission (C ABI)

/// 1 if THIS process is Accessibility-trusted (may install a suppressing tap), else 0. Never prompts.
@_cdecl("nty_accessibility_check")
public func nty_accessibility_check() -> Int32 { AXIsProcessTrusted() ? 1 : 0 }

/// Prompt for Accessibility if undetermined (shows the system dialog once), and return trust
/// AT CALL TIME (1/0). The grant is asynchronous — guide the user to System Settings ▸ Privacy
/// ▸ Accessibility, then poll nty_accessibility_check. Ad-hoc-signed builds may need re-granting
/// after a rebuild (the binary's signature changes).
@_cdecl("nty_accessibility_request")
public func nty_accessibility_request() -> Int32 {
    let key = kAXTrustedCheckOptionPrompt.takeUnretainedValue()
    let opts = [key: true] as CFDictionary
    return AXIsProcessTrustedWithOptions(opts) ? 1 : 0
}

// MARK: - diagnostics / test hooks (C ABI)
// Used by MockTeacher --inputtest to prove guaranteed-uninstall and DEMONSTRATE fail-open.
// Harmless in production (counters are read-only; the stall/synthesize levers are never
// invoked outside the test harness).

/// Cumulative count of suppressed shortcuts since start (proves suppression fired).
@_cdecl("nty_input_guard_suppress_count")
public func nty_input_guard_suppress_count() -> Int64 { guardSuppressCount }

/// Cumulative count of OS auto-disable events we caught + re-enabled (proves fail-open recovery).
@_cdecl("nty_input_guard_disabled_count")
public func nty_input_guard_disabled_count() -> Int64 { guardDisabledCount }

/// TEST-ONLY: make the callback deliberately sleep `ms` to force the OS watchdog to disable a
/// slow tap (the live fail-open demonstration). 0 disables the stall. Production never calls this.
@_cdecl("nty_input_guard_set_stall_ms")
public func nty_input_guard_set_stall_ms(_ ms: Int32) { guardStallMs = max(0, ms) }

/// 1 if the tap is currently ENABLED (guarding), 0 if disabled (keys flowing) or absent.
/// After a force-disable this reads 0 = the fail-open state.
@_cdecl("nty_input_guard_is_enabled")
public func nty_input_guard_is_enabled() -> Int32 {
    if let t = guardCtl.tap { return CGEvent.tapIsEnabled(tap: t) ? 1 : 0 }
    return 0
}

/// TEST-ONLY: synthesize `count` key presses (down+up) at HID level so they propagate up to our
/// session tap — lets the harness prove suppression + drive the fail-open demo with no human.
@_cdecl("nty_input_test_synthesize")
public func nty_input_test_synthesize(_ keycode: Int32, _ flags: UInt64, _ count: Int32) {
    let src = CGEventSource(stateID: .hidSystemState)
    for _ in 0..<max(0, count) {
        if let down = CGEvent(keyboardEventSource: src, virtualKey: CGKeyCode(keycode), keyDown: true) {
            down.flags = CGEventFlags(rawValue: flags)
            down.post(tap: .cghidEventTap)
        }
        if let up = CGEvent(keyboardEventSource: src, virtualKey: CGKeyCode(keycode), keyDown: false) {
            up.flags = CGEventFlags(rawValue: flags)
            up.post(tap: .cghidEventTap)
        }
    }
}
