// Audio.swift — AVAudioEngine microphone capture for libNtyCapture.dylib (Phase 29-B)
// ---------------------------------------------------------------------------------
// Mic capture path, parallel to screen (NtyCapture.swift) and camera (Camera.swift),
// and INDEPENDENT (own session state) so audio can run alongside them. Exports
// @_cdecl C symbols the .NET Sandbox P/Invokes (Services/AudioCaptureService.cs).
//
// The shipped wire audio is RAW PCM — 16000 Hz, mono, 16-bit signed little-endian,
// 100 ms frames = 1600 samples = 3200 bytes — with NO codec (matches the Windows
// student's NAudio path byte-for-byte). So there is no encoder: AVAudioEngine taps
// the mic, AVAudioConverter resamples the device's native format (this Mac: 48 kHz)
// down to 16 k mono int16, and we emit fixed 100 ms frames.
//
// New native pattern vs the video subsystems (cheat sheet §20 addendum): AVAudioEngine
// (not AVCaptureSession), an input tap on a real-time audio thread, and AVAudioConverter
// for sample-rate + format + channel conversion.

import Foundation
import AVFoundation

/// PCM frame callback (29-B). `pcm` = `length` bytes of signed 16-bit LE samples
/// (call-scoped — copy before returning). sampleRate/channels describe the frame
/// (always 16000/1 today, but passed through so the .NET side stays self-describing).
///   (ctx, pcm, length, sampleRate, channels)
public typealias NtyPcmCallback = @convention(c)
    (UnsafeMutableRawPointer?, UnsafePointer<UInt8>?, Int32, Int32, Int32) -> Void

// MARK: - Microphone permission (a distinct TCC bucket) ----------------------------

/// 1 = authorized, 0 = not-yet-determined, -1 = denied/restricted. Never prompts.
@_cdecl("nty_audio_check_permission")
public func nty_audio_check_permission() -> Int32 {
    switch AVCaptureDevice.authorizationStatus(for: .audio) {
    case .authorized:    return 1
    case .notDetermined: return 0
    default:             return -1
    }
}

/// Prompts if undetermined and BLOCKS the calling (background) thread until the user
/// decides. Returns 1 if granted, else 0/-1. Grant is effective immediately (no relaunch);
/// the `NSMicrophoneUsageDescription` text is shown to the user.
@_cdecl("nty_audio_request_permission")
public func nty_audio_request_permission() -> Int32 {
    let status = AVCaptureDevice.authorizationStatus(for: .audio)
    if status == .authorized { return 1 }
    if status != .notDetermined { return -1 }
    let sem = DispatchSemaphore(value: 0)
    var granted = false
    AVCaptureDevice.requestAccess(for: .audio) { g in granted = g; sem.signal() }
    sem.wait()
    return granted ? 1 : 0
}

// MARK: - Capture ------------------------------------------------------------------

private final class AudioSession {
    let ctx: UnsafeMutableRawPointer?
    let callback: NtyPcmCallback
    let targetRate: Double
    let targetChannels: AVAudioChannelCount
    let samplesPerFrame: Int          // 1600 = 100 ms @ 16 kHz
    let engine = AVAudioEngine()
    var converter: AVAudioConverter?
    var targetFormat: AVAudioFormat?
    var accum: [Int16] = []           // converted samples awaiting a full frame

    init(ctx: UnsafeMutableRawPointer?, cb: @escaping NtyPcmCallback, rate: Double, channels: AVAudioChannelCount) {
        self.ctx = ctx
        self.callback = cb
        self.targetRate = rate
        self.targetChannels = channels
        self.samplesPerFrame = Int(rate / 10.0)   // 100 ms
        self.accum.reserveCapacity(samplesPerFrame * 3)
    }

    func start() -> Int32 {
        let input = engine.inputNode
        let inputFormat = input.inputFormat(forBus: 0)
        guard inputFormat.sampleRate > 0, inputFormat.channelCount > 0 else { return -2 } // no input / no permission
        guard let target = AVAudioFormat(commonFormat: .pcmFormatInt16, sampleRate: targetRate,
                                         channels: targetChannels, interleaved: true),
              let conv = AVAudioConverter(from: inputFormat, to: target) else { return -6 }
        self.targetFormat = target
        self.converter = conv

        input.installTap(onBus: 0, bufferSize: 4096, format: inputFormat) { [weak self] buffer, _ in
            self?.process(buffer)
        }
        engine.prepare()
        do { try engine.start() } catch { input.removeTap(onBus: 0); return -7 }
        return 0
    }

    func stop() {
        engine.inputNode.removeTap(onBus: 0)
        if engine.isRunning { engine.stop() }
    }

    /// Runs on the real-time audio thread. Resample → int16 mono → accumulate → emit
    /// fixed 100 ms frames. Keep it allocation-light and fast.
    private func process(_ inputBuffer: AVAudioPCMBuffer) {
        guard let converter = converter, let target = targetFormat else { return }
        let ratio = targetRate / inputBuffer.format.sampleRate
        let cap = AVAudioFrameCount(Double(inputBuffer.frameLength) * ratio) + 32
        guard let outBuffer = AVAudioPCMBuffer(pcmFormat: target, frameCapacity: cap) else { return }

        var fed = false
        var err: NSError?
        _ = converter.convert(to: outBuffer, error: &err) { _, outStatus in
            if fed { outStatus.pointee = .noDataNow; return nil }
            fed = true
            outStatus.pointee = .haveData
            return inputBuffer
        }
        if err != nil { return }

        let n = Int(outBuffer.frameLength)
        guard n > 0, let ch = outBuffer.int16ChannelData else { return }
        let p = ch[0]
        accum.append(contentsOf: UnsafeBufferPointer(start: p, count: n))

        while accum.count >= samplesPerFrame {
            let frame = Array(accum[0..<samplesPerFrame])
            accum.removeFirst(samplesPerFrame)

            // RMS for the level meter (0..100, scaled ×3 so normal speech is visible).
            var sum = 0.0
            for s in frame { let v = Double(s) / 32768.0; sum += v * v }
            let rms = (sum / Double(frame.count)).squareRoot()
            AudState.lastRms = Int32(min(100.0, max(0.0, rms * 300.0)))
            AudState.frameCount += 1

            frame.withUnsafeBytes { raw in
                callback(ctx, raw.baseAddress?.assumingMemoryBound(to: UInt8.self),
                         Int32(samplesPerFrame * 2), Int32(targetRate), Int32(targetChannels))
            }
        }
    }
}

private enum AudState {
    static let lock = NSLock()
    static var session: AudioSession?
    static var frameCount: Int64 = 0
    static var lastRms: Int32 = 0
}

/// 29-B — start mic capture, delivering 100 ms PCM16 frames at `sampleRate`×`channels`
/// (0 → default 16000/1, the shipped format) via `cb`. Returns 0 on success, negative on
/// error (-2 no input/permission, -3 already running, -4 null cb, -6 converter, -7 engine).
@_cdecl("nty_audio_start_pcm")
public func nty_audio_start_pcm(_ sampleRate: Int32, _ channels: Int32,
                                _ cb: NtyPcmCallback?, _ ctx: UnsafeMutableRawPointer?) -> Int32 {
    guard let cb = cb else { return -4 }
    AudState.lock.lock(); defer { AudState.lock.unlock() }
    if AudState.session != nil { return -3 }

    let rate = sampleRate > 0 ? Double(sampleRate) : 16000.0
    let chs = AVAudioChannelCount(channels > 0 ? channels : 1)
    let session = AudioSession(ctx: ctx, cb: cb, rate: rate, channels: chs)
    AudState.frameCount = 0
    AudState.lastRms = 0
    let rc = session.start()
    if rc == 0 { AudState.session = session }
    return rc
}

@_cdecl("nty_audio_stop")
public func nty_audio_stop() {
    AudState.lock.lock()
    let session = AudState.session
    AudState.session = nil
    AudState.lock.unlock()
    session?.stop()
    AudState.lastRms = 0
}

@_cdecl("nty_audio_frame_count") public func nty_audio_frame_count() -> Int64 { AudState.frameCount }
@_cdecl("nty_audio_last_rms")    public func nty_audio_last_rms()    -> Int32 { AudState.lastRms }
