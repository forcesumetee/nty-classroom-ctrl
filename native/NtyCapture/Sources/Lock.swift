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
        registerObservers()
    }

    func hide() {
        guard active else { return }
        active = false
        unregisterObservers()
        NSApp.presentationOptions = []
        for w in windows { w.orderOut(nil) }
        windows.removeAll()
    }

    /// (Re)build one shield window per current display — used on show, hotplug, and wake.
    func rebuildWindows() {
        for w in windows { w.orderOut(nil) }
        windows.removeAll()
        var madeKey = false
        for screen in NSScreen.screens {
            let w = makeWindow(for: screen)
            windows.append(w)
            if !madeKey { w.makeKeyAndOrderFront(nil); madeKey = true }
            else { w.orderFrontRegardless() }
        }
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

        let label = NSTextField(labelWithString: message)
        label.font = NSFont.systemFont(ofSize: 30, weight: .semibold)
        label.textColor = .white
        label.alignment = .center
        label.backgroundColor = .clear
        label.isBezeled = false
        label.isEditable = false
        label.translatesAutoresizingMaskIntoConstraints = false
        content.addSubview(label)
        NSLayoutConstraint.activate([
            label.centerXAnchor.constraint(equalTo: content.centerXAnchor),
            label.centerYAnchor.constraint(equalTo: content.centerYAnchor),
        ])
        w.contentView = content
        return w
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
