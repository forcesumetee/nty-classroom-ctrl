// Lock.swift — full-screen "screen lock" shield for libNtyCapture.dylib (Phase 30-B)
// ---------------------------------------------------------------------------------
// A HARD, kiosk-lite lock that EXCEEDS the shipped Windows teacher-lock (which is a
// soft topmost overlay on the primary monitor only, no input blocking). Here:
//   * a borderless shield NSWindow at CGShieldingWindowLevel() on EVERY NSScreen
//     (above the Dock, menu bar, and screen saver), rebuilt on display hotplug;
//   * NSApplicationPresentationOptions disabling Cmd+Tab / Force-Quit / logout /
//     Dock / menu bar / Apple menu / hide — a real kiosk, with NO Accessibility
//     permission required;
//   * re-assert on resignActive + wake so it can't be nudged away.
//
// SAFETY: this is enforcement only. The DEAD-MAN switch that guarantees students are
// never stranded lives in the .NET LockService (Phase 30-C): disconnect→45s-grace
// auto-unlock, 30-min max-duration cap, wake re-assert — PLUS the ultimate backstop
// that lives HERE by construction: the shield + options are owned by THIS process, so
// killing the process releases the presentation options (OS-enforced) and drops the
// windows = auto-unlock. There is intentionally NO in-shield escape hotkey.
//
// All AppKit work is main-thread (dispatched internally); NSApp is the host app's
// shared NSApplication (Avalonia on macOS runs the AppKit main loop).

import Foundation
import AppKit

/// Borderless shield window that can still become key (so keystrokes land on it, not
/// on whatever app is behind the shield).
private final class ShieldWindow: NSWindow {
    override var canBecomeKey: Bool { true }
    override var canBecomeMain: Bool { true }
}

private final class LockController {
    var windows: [NSWindow] = []
    var message = "Locked by teacher"
    var active = false
    var observers: [NSObjectProtocol] = []
    // Live clock/date labels across all shield windows (30-D), refreshed each second.
    var clockLabels: [NSTextField] = []
    var dateLabels: [NSTextField] = []
    var clockTimer: Timer?

    // Kiosk levers — all work WITHOUT Accessibility. .disableProcessSwitching blocks
    // Cmd+Tab; .disableForceQuit blocks Cmd+Opt+Esc; .disableSessionTermination blocks
    // logout/shutdown menu; the rest hide/disable Dock, menu bar, Apple menu, Cmd+H.
    let kioskOptions: NSApplication.PresentationOptions = [
        .disableProcessSwitching, .disableForceQuit, .disableSessionTermination,
        .hideDock, .hideMenuBar, .disableAppleMenu, .disableHideApplication,
    ]

    func show(_ msg: String) {
        message = msg.isEmpty ? "Locked by teacher" : msg
        if active { rebuildWindows(); return }   // refresh (e.g. new message)
        active = true
        NSApp.activate(ignoringOtherApps: true)
        NSApp.presentationOptions = kioskOptions
        rebuildWindows()
        startClock()
        registerObservers()
    }

    func hide() {
        guard active else { return }
        active = false
        unregisterObservers()
        stopClock()
        NSApp.presentationOptions = []
        for w in windows { w.orderOut(nil) }
        windows.removeAll()
        clockLabels.removeAll()
        dateLabels.removeAll()
    }

    /// (Re)build one shield window per current display — used on show, hotplug, and wake.
    func rebuildWindows() {
        for w in windows { w.orderOut(nil) }
        windows.removeAll()
        clockLabels.removeAll()
        dateLabels.removeAll()
        var madeKey = false
        for screen in NSScreen.screens {
            let w = makeWindow(for: screen)
            windows.append(w)
            if !madeKey { w.makeKeyAndOrderFront(nil); madeKey = true }
            else { w.orderFrontRegardless() }
        }
        if active { updateClock() }   // populate the fresh labels immediately
    }

    private func styledLabel(_ text: String, size: CGFloat, weight: NSFont.Weight,
                             color: NSColor) -> NSTextField {
        let l = NSTextField(labelWithString: text)
        l.font = NSFont.systemFont(ofSize: size, weight: weight)
        l.textColor = color
        l.alignment = .center
        l.backgroundColor = .clear
        l.isBezeled = false
        l.isEditable = false
        l.translatesAutoresizingMaskIntoConstraints = false
        return l
    }

    private func makeWindow(for screen: NSScreen) -> NSWindow {
        let w = ShieldWindow(contentRect: screen.frame, styleMask: .borderless,
                             backing: .buffered, defer: false)
        w.level = NSWindow.Level(rawValue: Int(CGShieldingWindowLevel()))
        w.collectionBehavior = [.canJoinAllSpaces, .stationary, .fullScreenAuxiliary]
        w.backgroundColor = .black
        w.isOpaque = true
        w.hasShadow = false
        w.setFrame(screen.frame, display: true)

        let content = NSView(frame: NSRect(origin: .zero, size: screen.frame.size))
        content.wantsLayer = true
        content.layer?.backgroundColor = NSColor.black.cgColor

        // Vertical stack: lock glyph · clock · date · headline · brand. NO escape hotkey
        // printed (unlike Windows) — we have no hatch, so printing one would be worse.
        let glyph = styledLabel("🔒", size: 72, weight: .regular, color: .white)
        let clock = styledLabel("--:--", size: 64, weight: .thin, color: .white)
        let date = styledLabel("", size: 17, weight: .regular, color: NSColor(white: 0.72, alpha: 1))
        let headline = styledLabel(message, size: 26, weight: .semibold, color: .white)
        let brand = styledLabel("NTY ClassroomCtrl", size: 12, weight: .medium,
                                color: NSColor(white: 0.45, alpha: 1))
        clockLabels.append(clock)
        dateLabels.append(date)

        let stack = NSStackView(views: [glyph, clock, date, headline, brand])
        stack.orientation = .vertical
        stack.alignment = .centerX
        stack.spacing = 14
        stack.setCustomSpacing(28, after: date)     // gap before the headline
        stack.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(stack)
        NSLayoutConstraint.activate([
            stack.centerXAnchor.constraint(equalTo: content.centerXAnchor),
            stack.centerYAnchor.constraint(equalTo: content.centerYAnchor),
        ])
        w.contentView = content
        return w
    }

    // MARK: clock (30-D)
    private func startClock() {
        stopClock()
        updateClock()
        let t = Timer(timeInterval: 1.0, repeats: true) { [weak self] _ in self?.updateClock() }
        RunLoop.main.add(t, forMode: .common)
        clockTimer = t
    }

    private func stopClock() { clockTimer?.invalidate(); clockTimer = nil }

    private func updateClock() {
        let now = Date()
        let tf = DateFormatter(); tf.dateFormat = "HH:mm"
        let df = DateFormatter(); df.dateFormat = "EEEE, d MMMM yyyy"
        let t = tf.string(from: now), d = df.string(from: now)
        for l in clockLabels { l.stringValue = t }
        for l in dateLabels { l.stringValue = d }
    }

    private func reassert() {
        guard active else { return }
        NSApp.activate(ignoringOtherApps: true)
        NSApp.presentationOptions = kioskOptions
        for w in windows { w.orderFrontRegardless() }
    }

    private func registerObservers() {
        let nc = NotificationCenter.default
        // Display hotplug while locked → cover the new display set.
        observers.append(nc.addObserver(forName: NSApplication.didChangeScreenParametersNotification,
                                        object: nil, queue: .main) { [weak self] _ in
            guard let self = self, self.active else { return }
            self.rebuildWindows()
        })
        // Lost frontmost (presentation options only hold while active) → re-assert.
        observers.append(nc.addObserver(forName: NSApplication.didResignActiveNotification,
                                        object: nil, queue: .main) { [weak self] _ in
            self?.reassert()
        })
        // Wake — options/windows can drop during sleep → re-assert + rebuild.
        observers.append(NSWorkspace.shared.notificationCenter.addObserver(
            forName: NSWorkspace.didWakeNotification, object: nil, queue: .main) { [weak self] _ in
            guard let self = self, self.active else { return }
            self.reassert()
            self.rebuildWindows()
        })
    }

    private func unregisterObservers() {
        for o in observers {
            NotificationCenter.default.removeObserver(o)
            NSWorkspace.shared.notificationCenter.removeObserver(o)
        }
        observers.removeAll()
    }
}

private let lockController = LockController()   // touched on main thread only
private var lockShown = false                   // intent flag, readable from any thread

@_cdecl("nty_lock_show")
public func nty_lock_show(_ message: UnsafePointer<CChar>?) {
    let s = message != nil ? String(cString: message!) : ""
    lockShown = true
    if Thread.isMainThread { lockController.show(s) }
    else { DispatchQueue.main.async { lockController.show(s) } }
}

@_cdecl("nty_lock_hide")
public func nty_lock_hide() {
    lockShown = false
    if Thread.isMainThread { lockController.hide() }
    else { DispatchQueue.main.async { lockController.hide() } }
}

@_cdecl("nty_lock_is_shown")
public func nty_lock_is_shown() -> Int32 { lockShown ? 1 : 0 }
