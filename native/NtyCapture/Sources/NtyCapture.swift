// NtyCapture.swift — Swift implementation of the libNtyCapture.dylib C ABI (Phase 27-A)
// ---------------------------------------------------------------------------------
// @_cdecl exports each function as a plain C symbol so the .NET side can P/Invoke it.
// Keep signatures in lockstep with Headers/nty_capture.h and the C# LibraryImports.
//
// 27-A-2 (this file): Screen Recording permission (TCC) via CoreGraphics, fully
// implemented. Capture start/stop are stubs here — the SCStream pipeline lands in
// 27-A-3.

import Foundation
import CoreGraphics

// MARK: - Permission (TCC) ---------------------------------------------------------

/// Returns 1 if Screen Recording is already authorized for this binary, else 0.
/// Never prompts. Backed by CGPreflightScreenCaptureAccess (macOS 10.15+).
@_cdecl("nty_check_permission")
public func nty_check_permission() -> Int32 {
    return CGPreflightScreenCaptureAccess() ? 1 : 0
}

/// Requests Screen Recording access. If the status is undetermined the system shows
/// the TCC prompt. Returns the authorization AT CALL TIME (1/0).
///
/// NOTE (documented in findings): the user's decision is asynchronous, and for
/// Screen Recording the grant does not take effect until the app is RELAUNCHED —
/// TCC caches the capture entitlement at process launch. Callers should treat a
/// post-grant relaunch as expected and re-check with nty_check_permission.
@_cdecl("nty_request_permission")
public func nty_request_permission() -> Int32 {
    return CGRequestScreenCaptureAccess() ? 1 : 0
}

// MARK: - Capture (stubs — implemented in 27-A-3) ----------------------------------

/// C function-pointer type for per-frame delivery. Matches nty_frame_cb in the header.
public typealias NtyFrameCallback = @convention(c)
    (UnsafeMutableRawPointer?, UnsafePointer<UInt8>?, Int32, Int32, Int32) -> Void

/// Begin capturing the main display. Returns 0 on success, negative on error.
/// 27-A-2 stub: not yet implemented → returns -100 so the .NET side can surface a
/// clear "capture pipeline lands in 27-A-3" message while the permission flow is
/// exercised end-to-end.
@_cdecl("nty_capture_start")
public func nty_capture_start(_ fps: Int32, _ cb: NtyFrameCallback?, _ ctx: UnsafeMutableRawPointer?) -> Int32 {
    return -100
}

/// Stop the capture stream. 27-A-2 stub: no-op.
@_cdecl("nty_capture_stop")
public func nty_capture_stop() {
}
