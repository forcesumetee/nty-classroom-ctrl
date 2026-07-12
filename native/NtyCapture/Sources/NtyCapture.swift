// NtyCapture.swift — Swift implementation of the libNtyCapture.dylib C ABI (Phase 27-A)
// ---------------------------------------------------------------------------------
// @_cdecl exports each function as a plain C symbol so the .NET side can P/Invoke it.
// Keep signatures in lockstep with Headers/nty_capture.h and the C# LibraryImports.
//
// 27-A-2: Screen Recording permission (TCC).
// 27-A-3: main-display capture via SCStream → BGRA frames delivered to the C callback.

import Foundation
import CoreGraphics
import CoreMedia
import CoreVideo
import ScreenCaptureKit

// MARK: - Permission (TCC) ---------------------------------------------------------

@_cdecl("nty_check_permission")
public func nty_check_permission() -> Int32 {
    return CGPreflightScreenCaptureAccess() ? 1 : 0
}

@_cdecl("nty_request_permission")
public func nty_request_permission() -> Int32 {
    return CGRequestScreenCaptureAccess() ? 1 : 0
}

// MARK: - Capture ------------------------------------------------------------------

/// C function-pointer type for per-frame delivery. Matches nty_frame_cb in the header.
public typealias NtyFrameCallback = @convention(c)
    (UnsafeMutableRawPointer?, UnsafePointer<UInt8>?, Int32, Int32, Int32) -> Void

/// Single active capture session. Guarded so start/stop are idempotent and safe.
private final class CaptureSession: NSObject, SCStreamOutput {
    let callback: NtyFrameCallback
    let ctx: UnsafeMutableRawPointer?
    var stream: SCStream?
    // Dedicated serial queue for frame delivery (never the main thread).
    let queue = DispatchQueue(label: "com.nty.classroom.capture", qos: .userInitiated)

    init(callback: @escaping NtyFrameCallback, ctx: UnsafeMutableRawPointer?) {
        self.callback = callback
        self.ctx = ctx
    }

    // SCStreamOutput — called on `queue` per delivered frame.
    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
                of type: SCStreamOutputType) {
        guard type == .screen else { return }
        guard sampleBuffer.isValid,
              let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        // Frames whose status attachment isn't `.complete` carry no new pixels
        // (idle screen) — skip them.
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
            as? [[SCStreamFrameInfo: Any]],
           let statusRaw = attachments.first?[.status] as? Int,
           let status = SCFrameStatus(rawValue: statusRaw),
           status != .complete {
            return
        }

        CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly)
        defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly) }

        guard let base = CVPixelBufferGetBaseAddress(pixelBuffer) else { return }
        let width = Int32(CVPixelBufferGetWidth(pixelBuffer))
        let height = Int32(CVPixelBufferGetHeight(pixelBuffer))
        let bytesPerRow = Int32(CVPixelBufferGetBytesPerRow(pixelBuffer))

        NtyState.lastWidth = width
        NtyState.lastHeight = height
        NtyState.frameCount += 1

        // Synchronous call: `bgra` is valid only until this returns (buffer still locked).
        callback(ctx, base.assumingMemoryBound(to: UInt8.self), width, height, bytesPerRow)
    }
}

/// Process-wide capture state (single session; matches the C ABI's singleton model).
private enum NtyState {
    static let lock = NSLock()
    static var session: CaptureSession?
    static var lastWidth: Int32 = 0
    static var lastHeight: Int32 = 0
    static var frameCount: Int64 = 0
}

@_cdecl("nty_capture_start")
public func nty_capture_start(_ fps: Int32, _ cb: NtyFrameCallback?, _ ctx: UnsafeMutableRawPointer?) -> Int32 {
    guard let cb = cb else { return -4 } // no callback
    NtyState.lock.lock()
    defer { NtyState.lock.unlock() }
    if NtyState.session != nil { return -3 } // already running

    // SCShareableContent is async; bridge to sync for the simple C ABI with a semaphore.
    let sem = DispatchSemaphore(value: 0)
    var content: SCShareableContent?
    var contentError: Error?
    SCShareableContent.getExcludingDesktopWindows(false, onScreenWindowsOnly: true) { c, e in
        content = c; contentError = e; sem.signal()
    }
    // Bounded wait so a hung TCC/content query can't deadlock the caller.
    if sem.wait(timeout: .now() + 5) == .timedOut { return -5 }
    if contentError != nil { return -1 } // typically "not permitted"
    guard let display = content?.displays.first else { return -2 } // no display

    let session = CaptureSession(callback: cb, ctx: ctx)
    let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])

    let config = SCStreamConfiguration()
    config.pixelFormat = kCVPixelFormatType_32BGRA
    config.width = display.width
    config.height = display.height
    config.queueDepth = 3
    config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(max(1, fps)))
    config.showsCursor = true

    let stream = SCStream(filter: filter, configuration: config, delegate: nil)
    do {
        try stream.addStreamOutput(session, type: .screen, sampleHandlerQueue: session.queue)
    } catch {
        return -6 // failed to attach output
    }
    session.stream = stream
    NtyState.frameCount = 0

    // startCapture is async; block briefly for its completion so we return a real code.
    let startSem = DispatchSemaphore(value: 0)
    var startError: Error?
    stream.startCapture { e in startError = e; startSem.signal() }
    if startSem.wait(timeout: .now() + 5) == .timedOut { return -7 }
    if startError != nil { return -8 }

    NtyState.session = session
    return 0
}

@_cdecl("nty_capture_stop")
public func nty_capture_stop() {
    NtyState.lock.lock()
    let session = NtyState.session
    NtyState.session = nil
    NtyState.lock.unlock()
    guard let session = session, let stream = session.stream else { return }
    let sem = DispatchSemaphore(value: 0)
    stream.stopCapture { _ in sem.signal() }
    _ = sem.wait(timeout: .now() + 3)
    session.stream = nil
}

/// Optional stats accessors (used by the .NET stats UI).
@_cdecl("nty_last_width")  public func nty_last_width()  -> Int32 { NtyState.lastWidth }
@_cdecl("nty_last_height") public func nty_last_height() -> Int32 { NtyState.lastHeight }
@_cdecl("nty_frame_count") public func nty_frame_count() -> Int64 { NtyState.frameCount }
