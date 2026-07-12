// NtyCapture.swift — Swift implementation of the libNtyCapture.dylib C ABI (Phase 27-A/C)
// ---------------------------------------------------------------------------------
// @_cdecl exports each function as a plain C symbol so the .NET side can P/Invoke it.
// Keep signatures in lockstep with Headers/nty_capture.h and the C# LibraryImports.
//
// 27-A-2: permission (TCC). 27-A-3: BGRA capture. 27-C-1: JPEG capture mode (ImageIO
// encode of the downscaled frame) for sending StudentStreamFrame to the Teacher.

import Foundation
import CoreGraphics
import CoreMedia
import CoreVideo
import CoreImage
import ImageIO
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

// MARK: - Callback types -----------------------------------------------------------

/// BGRA frame callback (27-A). bgra valid only during the call; honor bytesPerRow.
public typealias NtyFrameCallback = @convention(c)
    (UnsafeMutableRawPointer?, UnsafePointer<UInt8>?, Int32, Int32, Int32) -> Void

/// JPEG frame callback (27-C). jpeg bytes valid only during the call.
///   (ctx, jpeg, length, width, height)
public typealias NtyJpegCallback = @convention(c)
    (UnsafeMutableRawPointer?, UnsafePointer<UInt8>?, Int32, Int32, Int32) -> Void

/// H.264 frame callback (27-B). Annex-B NAL bytes, call-scoped.
///   (ctx, nal, length, width, height, isKeyframe)
public typealias NtyH264Callback = @convention(c)
    (UnsafeMutableRawPointer?, UnsafePointer<UInt8>?, Int32, Int32, Int32, Int32) -> Void

// MARK: - Capture ------------------------------------------------------------------

private final class CaptureSession: NSObject, SCStreamOutput {
    let ctx: UnsafeMutableRawPointer?
    let bgraCallback: NtyFrameCallback?
    let jpegCallback: NtyJpegCallback?
    let jpegQuality: Double     // 0..1
    let h264: H264Encoder?      // 27-B — set for H.264 mode
    var stream: SCStream?
    let queue = DispatchQueue(label: "com.nty.classroom.capture", qos: .userInitiated)

    init(ctx: UnsafeMutableRawPointer?,
         bgra: NtyFrameCallback?, jpeg: NtyJpegCallback?, quality: Double, h264: H264Encoder? = nil) {
        self.ctx = ctx
        self.bgraCallback = bgra
        self.jpegCallback = jpeg
        self.jpegQuality = quality
        self.h264 = h264
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer,
                of type: SCStreamOutputType) {
        guard type == .screen, sampleBuffer.isValid,
              let pixelBuffer = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        // Only frames with new pixels (skip .idle / .blank).
        if let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
            as? [[SCStreamFrameInfo: Any]],
           let statusRaw = attachments.first?[.status] as? Int,
           let status = SCFrameStatus(rawValue: statusRaw), status != .complete {
            return
        }

        let width = Int32(CVPixelBufferGetWidth(pixelBuffer))
        let height = Int32(CVPixelBufferGetHeight(pixelBuffer))
        NtyState.lastWidth = width
        NtyState.lastHeight = height
        NtyState.frameCount += 1

        // H.264 mode (27-B): feed the pixel buffer to VideoToolbox; the encoder's
        // output handler converts to Annex B and invokes the C callback.
        if let enc = h264 {
            enc.encode(pixelBuffer, pts: CMSampleBufferGetPresentationTimeStamp(sampleBuffer))
            return
        }

        // JPEG mode (27-C): encode the (already-downscaled) frame and deliver bytes.
        if let jcb = jpegCallback {
            guard let data = encodeJpeg(pixelBuffer, quality: jpegQuality) else { return }
            data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
                guard let base = raw.bindMemory(to: UInt8.self).baseAddress else { return }
                jcb(ctx, base, Int32(data.count), width, height)
            }
            return
        }

        // BGRA mode (27-A): hand the locked base address to the callback (call-scoped).
        if let bcb = bgraCallback {
            CVPixelBufferLockBaseAddress(pixelBuffer, .readOnly)
            defer { CVPixelBufferUnlockBaseAddress(pixelBuffer, .readOnly) }
            guard let base = CVPixelBufferGetBaseAddress(pixelBuffer) else { return }
            let bpr = Int32(CVPixelBufferGetBytesPerRow(pixelBuffer))
            bcb(ctx, base.assumingMemoryBound(to: UInt8.self), width, height, bpr)
        }
    }
}

/// Encode a BGRA CVPixelBuffer to JPEG via ImageIO. Reuses a shared CIContext.
private func encodeJpeg(_ pixelBuffer: CVPixelBuffer, quality: Double) -> Data? {
    let ci = CIImage(cvPixelBuffer: pixelBuffer)
    guard let cg = NtyState.ciContext.createCGImage(ci, from: ci.extent) else { return nil }
    let out = NSMutableData()
    guard let dest = CGImageDestinationCreateWithData(out as CFMutableData, "public.jpeg" as CFString, 1, nil)
    else { return nil }
    let props: [CFString: Any] = [kCGImageDestinationLossyCompressionQuality: quality]
    CGImageDestinationAddImage(dest, cg, props as CFDictionary)
    guard CGImageDestinationFinalize(dest) else { return nil }
    return out as Data
}

private enum NtyState {
    static let lock = NSLock()
    static var session: CaptureSession?
    static var lastWidth: Int32 = 0
    static var lastHeight: Int32 = 0
    static var frameCount: Int64 = 0
    static let ciContext = CIContext(options: nil)
}

/// Shared stream setup. `targetW/H`=0 → capture at full display size (BGRA mode);
/// otherwise SCStream downscales to targetW×targetH (JPEG mode).
private func startInternal(fps: Int32, targetW: Int, targetH: Int, session: CaptureSession) -> Int32 {
    let sem = DispatchSemaphore(value: 0)
    var content: SCShareableContent?
    var contentError: Error?
    SCShareableContent.getExcludingDesktopWindows(false, onScreenWindowsOnly: true) { c, e in
        content = c; contentError = e; sem.signal()
    }
    if sem.wait(timeout: .now() + 5) == .timedOut { return -5 }
    if contentError != nil { return -1 }
    guard let display = content?.displays.first else { return -2 }

    let filter = SCContentFilter(display: display, excludingApplications: [], exceptingWindows: [])
    let config = SCStreamConfiguration()
    config.pixelFormat = kCVPixelFormatType_32BGRA
    config.width = targetW > 0 ? targetW : display.width
    config.height = targetH > 0 ? targetH : display.height
    config.queueDepth = 3
    config.minimumFrameInterval = CMTime(value: 1, timescale: CMTimeScale(max(1, fps)))
    config.showsCursor = true

    let stream = SCStream(filter: filter, configuration: config, delegate: nil)
    do { try stream.addStreamOutput(session, type: .screen, sampleHandlerQueue: session.queue) }
    catch { return -6 }
    session.stream = stream
    NtyState.frameCount = 0

    let startSem = DispatchSemaphore(value: 0)
    var startError: Error?
    stream.startCapture { e in startError = e; startSem.signal() }
    if startSem.wait(timeout: .now() + 5) == .timedOut { return -7 }
    if startError != nil { return -8 }

    NtyState.session = session
    return 0
}

/// Compute an aspect-preserving downscale of (w,h) to fit within (maxW,maxH); never upscales.
private func fit(_ w: Int, _ h: Int, _ maxW: Int, _ maxH: Int) -> (Int, Int) {
    if maxW <= 0 || maxH <= 0 { return (w, h) }
    let scale = min(Double(maxW) / Double(w), Double(maxH) / Double(h), 1.0)
    return (max(1, Int(Double(w) * scale)), max(1, Int(Double(h) * scale)))
}

@_cdecl("nty_capture_start")
public func nty_capture_start(_ fps: Int32, _ cb: NtyFrameCallback?, _ ctx: UnsafeMutableRawPointer?) -> Int32 {
    guard let cb = cb else { return -4 }
    NtyState.lock.lock(); defer { NtyState.lock.unlock() }
    if NtyState.session != nil { return -3 }
    let session = CaptureSession(ctx: ctx, bgra: cb, jpeg: nil, quality: 0.6)
    return startInternal(fps: fps, targetW: 0, targetH: 0, session: session)
}

/// 27-C — capture the main display, downscale to fit maxW×maxH, JPEG-encode at
/// `quality` (0..100), deliver bytes via `cb`. Matches the shipped StudentBroadcaster
/// (1280×720, quality 60, ~4 fps).
@_cdecl("nty_capture_start_jpeg")
public func nty_capture_start_jpeg(_ fps: Int32, _ quality: Int32, _ maxW: Int32, _ maxH: Int32,
                                   _ cb: NtyJpegCallback?, _ ctx: UnsafeMutableRawPointer?) -> Int32 {
    guard let cb = cb else { return -4 }
    NtyState.lock.lock(); defer { NtyState.lock.unlock() }
    if NtyState.session != nil { return -3 }

    // Need the display size up front to compute the target. Query once here.
    let sem = DispatchSemaphore(value: 0)
    var content: SCShareableContent?
    SCShareableContent.getExcludingDesktopWindows(false, onScreenWindowsOnly: true) { c, _ in
        content = c; sem.signal()
    }
    if sem.wait(timeout: .now() + 5) == .timedOut { return -5 }
    guard let display = content?.displays.first else { return -2 }
    let (tw, th) = fit(display.width, display.height, Int(maxW), Int(maxH))

    let q = min(1.0, max(0.05, Double(quality) / 100.0))
    let session = CaptureSession(ctx: ctx, bgra: nil, jpeg: cb, quality: q)
    return startInternal(fps: fps, targetW: tw, targetH: th, session: session)
}

/// 27-B — capture the main display at 1920×1080 and H.264-encode via VideoToolbox
/// (Baseline, CBR, IDR every fps×2), delivering Annex-B NAL bytes per frame.
/// `bitrateKbps` in kbit/s (e.g. 1500). Matches the shipped OpenH264 wire format.
@_cdecl("nty_capture_start_h264")
public func nty_capture_start_h264(_ fps: Int32, _ bitrateKbps: Int32,
                                   _ cb: NtyH264Callback?, _ ctx: UnsafeMutableRawPointer?) -> Int32 {
    guard let cb = cb else { return -4 }
    NtyState.lock.lock(); defer { NtyState.lock.unlock() }
    if NtyState.session != nil { return -3 }

    let w = 1920, h = 1080
    guard let enc = H264Encoder(width: w, height: h, fps: Int(max(1, fps)),
                                bitrate: Int(max(100, bitrateKbps)) * 1000, callback: cb, ctx: ctx)
    else { return -9 } // VTCompressionSession create failed

    let session = CaptureSession(ctx: ctx, bgra: nil, jpeg: nil, quality: 0, h264: enc)
    return startInternal(fps: fps, targetW: w, targetH: h, session: session)
}

@_cdecl("nty_capture_stop")
public func nty_capture_stop() {
    NtyState.lock.lock()
    let session = NtyState.session
    NtyState.session = nil
    NtyState.lock.unlock()
    guard let session = session else { return }
    if let stream = session.stream {
        let sem = DispatchSemaphore(value: 0)
        stream.stopCapture { _ in sem.signal() }
        _ = sem.wait(timeout: .now() + 3)
        session.stream = nil
    }
    session.h264?.stop()
}

@_cdecl("nty_last_width")  public func nty_last_width()  -> Int32 { NtyState.lastWidth }
@_cdecl("nty_last_height") public func nty_last_height() -> Int32 { NtyState.lastHeight }
@_cdecl("nty_frame_count") public func nty_frame_count() -> Int64 { NtyState.frameCount }
