// Camera.swift — AVCaptureSession camera capture for libNtyCapture.dylib (Phase 28-B)
// ---------------------------------------------------------------------------------
// Camera (webcam) capture path, parallel to the ScreenCaptureKit path in
// NtyCapture.swift. Exports @_cdecl C symbols the .NET Sandbox P/Invokes
// (Services/CameraCaptureService.cs). Reuses the shared ImageIO `encodeJpeg`
// (same module) + shared CIContext — camera frames are JPEG-only on the wire
// (ConferenceCameraFrame carries raw JPEG, no codec field), so no H.264 here.
//
// Key differences from the screen path (documented for the cheat sheet §20):
//   * AVFoundation, not ScreenCaptureKit → build.sh links -framework AVFoundation.
//   * Camera privacy permission is a DIFFERENT TCC bucket than Screen Recording,
//     grants EFFECTIVE IMMEDIATELY (no relaunch), and SHOWS the Info.plist
//     NSCameraUsageDescription text to the user.
//   * OWN session state (CamState) — the native single-session NtyState lock is
//     screen-only; camera must coexist with a screen stream (e.g. a Conference
//     sharing screen while the cam is live), so it needs an independent slot.

import Foundation
import AVFoundation
import CoreMedia
import CoreVideo

// MARK: - Device enumeration -------------------------------------------------------

/// Video capture devices: built-in (FaceTime) + external USB cams. `.externalUnknown`
/// is deprecated on macOS 14 but still enumerates external cams on 12.3+.
private func cameraDevices() -> [AVCaptureDevice] {
    let types: [AVCaptureDevice.DeviceType] = [.builtInWideAngleCamera, .externalUnknown]
    let ds = AVCaptureDevice.DiscoverySession(deviceTypes: types,
                                              mediaType: .video, position: .unspecified)
    return ds.devices
}

// MARK: - Camera privacy permission ------------------------------------------------

/// 1 = authorized, 0 = not-yet-determined, -1 = denied/restricted. Never prompts.
@_cdecl("nty_camera_check_permission")
public func nty_camera_check_permission() -> Int32 {
    switch AVCaptureDevice.authorizationStatus(for: .video) {
    case .authorized:    return 1
    case .notDetermined: return 0
    default:             return -1   // .denied / .restricted
    }
}

/// Triggers the system camera prompt if undetermined and BLOCKS the calling
/// (background) thread until the user decides. Returns 1 if granted, else 0/-1.
/// Unlike Screen Recording, the grant is effective immediately — no relaunch.
@_cdecl("nty_camera_request_permission")
public func nty_camera_request_permission() -> Int32 {
    let status = AVCaptureDevice.authorizationStatus(for: .video)
    if status == .authorized { return 1 }
    if status != .notDetermined { return -1 }  // denied/restricted can't be re-prompted
    let sem = DispatchSemaphore(value: 0)
    var granted = false
    AVCaptureDevice.requestAccess(for: .video) { g in granted = g; sem.signal() }
    sem.wait()
    return granted ? 1 : 0
}

/// Number of enumerable video devices.
@_cdecl("nty_camera_count")
public func nty_camera_count() -> Int32 { Int32(cameraDevices().count) }

/// Copy device[index].localizedName as UTF-8 (NUL-terminated) into caller buffer.
/// Returns bytes written (excluding NUL), or -1 on bad index/buffer.
@_cdecl("nty_camera_name")
public func nty_camera_name(_ index: Int32,
                            _ buf: UnsafeMutablePointer<UInt8>?, _ bufLen: Int32) -> Int32 {
    let devices = cameraDevices()
    let idx = Int(index)
    guard idx >= 0, idx < devices.count, let buf = buf, bufLen > 1 else { return -1 }
    let utf8 = Array(devices[idx].localizedName.utf8)
    let n = min(utf8.count, Int(bufLen) - 1)
    for i in 0..<n { buf[i] = utf8[i] }
    buf[n] = 0
    return Int32(n)
}

// MARK: - Capture ------------------------------------------------------------------

private final class CamSession: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate {
    let ctx: UnsafeMutableRawPointer?
    let jpegCallback: NtyJpegCallback
    let quality: Double
    let minInterval: Double      // seconds between delivered frames (fps throttle)
    let session = AVCaptureSession()
    let queue = DispatchQueue(label: "com.nty.classroom.camera", qos: .userInitiated)
    var lastPts: Double = -1

    init(ctx: UnsafeMutableRawPointer?, cb: @escaping NtyJpegCallback, quality: Double, fps: Int32) {
        self.ctx = ctx
        self.jpegCallback = cb
        self.quality = quality
        self.minInterval = fps > 0 ? (1.0 / Double(fps)) * 0.9 : 0  // 0.9 → tolerate jitter
    }

    func captureOutput(_ output: AVCaptureOutput, didOutput sampleBuffer: CMSampleBuffer,
                       from connection: AVCaptureConnection) {
        guard let pb = CMSampleBufferGetImageBuffer(sampleBuffer) else { return }

        // fps throttle: cameras deliver at their native rate (often 30 fps); drop
        // frames to ~fps using monotonic PTS (no wall-clock needed).
        let pts = CMTimeGetSeconds(CMSampleBufferGetPresentationTimeStamp(sampleBuffer))
        if lastPts >= 0, pts - lastPts < minInterval { return }
        lastPts = pts

        let width = Int32(CVPixelBufferGetWidth(pb))
        let height = Int32(CVPixelBufferGetHeight(pb))
        CamState.lastWidth = width
        CamState.lastHeight = height
        CamState.frameCount += 1

        guard let data = encodeJpeg(pb, quality: quality) else { return }
        data.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            guard let base = raw.bindMemory(to: UInt8.self).baseAddress else { return }
            jpegCallback(ctx, base, Int32(data.count), width, height)
        }
    }
}

private enum CamState {
    static let lock = NSLock()
    static var session: CamSession?
    static var lastWidth: Int32 = 0
    static var lastHeight: Int32 = 0
    static var frameCount: Int64 = 0
}

/// Map a requested WxH to the closest standard AVCaptureSession preset. The
/// shipped Windows student uses 320x240 (peer cam), which is an exact preset.
private func cameraPreset(_ w: Int, _ h: Int) -> AVCaptureSession.Preset {
    if w == 320 && h == 240 { return .qvga320x240 }
    if w == 640 && h == 480 { return .vga640x480 }
    if w >= 1280 { return .hd1280x720 }
    return .medium
}

/// 28-B — start the selected camera, deliver JPEG frames (~fps, quality 0-100) via
/// `cb`. width/height select the capture preset (320x240 = shipped peer-cam format).
/// Returns 0 on success; -2 bad device index, -3 already running, -4 null cb,
/// -5 can't add input, -6 can't add output.
@_cdecl("nty_camera_start_jpeg")
public func nty_camera_start_jpeg(_ deviceIndex: Int32, _ width: Int32, _ height: Int32,
                                  _ fps: Int32, _ quality: Int32,
                                  _ cb: NtyJpegCallback?, _ ctx: UnsafeMutableRawPointer?) -> Int32 {
    guard let cb = cb else { return -4 }
    CamState.lock.lock(); defer { CamState.lock.unlock() }
    if CamState.session != nil { return -3 }

    let devices = cameraDevices()
    let idx = Int(deviceIndex)
    guard idx >= 0, idx < devices.count else { return -2 }
    guard let input = try? AVCaptureDeviceInput(device: devices[idx]) else { return -5 }

    let q = min(1.0, max(0.05, Double(quality) / 100.0))
    let cam = CamSession(ctx: ctx, cb: cb, quality: q, fps: fps)
    let s = cam.session
    s.beginConfiguration()
    s.sessionPreset = cameraPreset(Int(width), Int(height))
    guard s.canAddInput(input) else { s.commitConfiguration(); return -5 }
    s.addInput(input)

    let output = AVCaptureVideoDataOutput()
    output.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_32BGRA]
    output.alwaysDiscardsLateVideoFrames = true
    output.setSampleBufferDelegate(cam, queue: cam.queue)
    guard s.canAddOutput(output) else { s.commitConfiguration(); return -6 }
    s.addOutput(output)
    s.commitConfiguration()

    CamState.frameCount = 0
    s.startRunning()
    CamState.session = cam
    return 0
}

@_cdecl("nty_camera_stop")
public func nty_camera_stop() {
    CamState.lock.lock()
    let cam = CamState.session
    CamState.session = nil
    CamState.lock.unlock()
    cam?.session.stopRunning()
}

@_cdecl("nty_camera_last_width")  public func nty_camera_last_width()  -> Int32 { CamState.lastWidth }
@_cdecl("nty_camera_last_height") public func nty_camera_last_height() -> Int32 { CamState.lastHeight }
@_cdecl("nty_camera_frame_count") public func nty_camera_frame_count() -> Int64 { CamState.frameCount }
