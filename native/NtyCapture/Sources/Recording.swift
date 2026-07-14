// Recording.swift — TT-13 / TOR 11.2.9: record the teacher's SCREEN + AUDIO to a file.
//
// Every capture piece was proven on real hardware today: ScreenCaptureKit screen capture (M17),
// SCK system-audio capture (the probe), and screen+audio COEXISTING in ONE SCStream. Recording adds
// only the "write it to a file" step, via AVAssetWriter (native — no ffmpeg bundle / licensing / size
// problem). One SCStream delivers screen CMSampleBuffers (→ H.264 video track) + system-audio
// CMSampleBuffers (→ AAC audio track) into a single .mov the teacher can open in QuickTime.
//
// Audio = SYSTEM audio (what the Mac plays — e.g. a video the teacher shows). This is the lowest-risk
// path (the same single-SCStream capture proven today, no separate mic session, no mixing). Recording
// the mic, or mixing mic + system, is a documented follow-up. SCK audio is macOS 13.0+ → @available.
//
// @_cdecl C symbols the .NET Teacher P/Invokes (Services/TeacherRecorder.cs):
//   nty_record_start(path) → 0 ok / -2 no display / -3 SCK start (TCC? grant Screen Recording +
//                            RELAUNCH) / -4 needs macOS 13 / -5 already recording / -6 writer init
//   nty_record_stop()      → 0 ok / -1 not recording
//   nty_record_is_active() → 1/0
//   nty_record_video_frames() / nty_record_audio_frames() → counters (gate + UI)

import Foundation
import AVFoundation
import ScreenCaptureKit
import CoreMedia
import CoreVideo

@available(macOS 13.0, *)
private final class RecordingSession: NSObject, SCStreamOutput, SCStreamDelegate {
    private let writeQueue = DispatchQueue(label: "nty.record.write")   // serializes ALL writer appends
    private var stream: SCStream?
    private var writer: AVAssetWriter?
    private var videoIn: AVAssetWriterInput?
    private var audioIn: AVAssetWriterInput?
    private var started = false          // session started (first video frame seen)
    private var finished = false
    private let lock = NSLock()
    private(set) var videoFrames: Int64 = 0
    private(set) var audioFrames: Int64 = 0

    func start(path: String) -> Int32 {
        let setup = DispatchSemaphore(value: 0)
        let errBox = _RecBox()
        Task {
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                guard let display = content.displays.first else { errBox.err = -2; setup.signal(); return }

                // Even dimensions (H.264 requires them); point-sized output keeps the file light + crisp.
                let w = Int(display.width)  & ~1
                let h = Int(display.height) & ~1

                let url = URL(fileURLWithPath: path)
                try? FileManager.default.removeItem(at: url)
                let aw = try AVAssetWriter(outputURL: url, fileType: .mov)

                let vSettings: [String: Any] = [
                    AVVideoCodecKey: AVVideoCodecType.h264,
                    AVVideoWidthKey: w,
                    AVVideoHeightKey: h,
                ]
                let vIn = AVAssetWriterInput(mediaType: .video, outputSettings: vSettings)
                vIn.expectsMediaDataInRealTime = true
                if aw.canAdd(vIn) { aw.add(vIn) }

                let aSettings: [String: Any] = [
                    AVFormatIDKey: kAudioFormatMPEG4AAC,
                    AVSampleRateKey: 48000,
                    AVNumberOfChannelsKey: 2,
                    AVEncoderBitRateKey: 128000,
                ]
                let aIn = AVAssetWriterInput(mediaType: .audio, outputSettings: aSettings)
                aIn.expectsMediaDataInRealTime = true
                if aw.canAdd(aIn) { aw.add(aIn) }

                self.writer = aw; self.videoIn = vIn; self.audioIn = aIn

                let cfg = SCStreamConfiguration()
                cfg.width = w; cfg.height = h
                cfg.minimumFrameInterval = CMTime(value: 1, timescale: 30)   // up to 30 fps
                cfg.pixelFormat = kCVPixelFormatType_32BGRA
                cfg.showsCursor = true
                cfg.capturesAudio = true
                cfg.sampleRate = 48000
                cfg.channelCount = 2

                let s = SCStream(filter: SCContentFilter(display: display, excludingWindows: []),
                                 configuration: cfg, delegate: self)
                // ONE serial queue for both outputs → appends never race across tracks.
                try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: self.writeQueue)
                try s.addStreamOutput(self, type: .audio, sampleHandlerQueue: self.writeQueue)
                try await s.startCapture()
                self.stream = s
                setup.signal()
            } catch {
                NSLog("[nty_record_start] \(error)"); errBox.err = -3; setup.signal()
            }
        }
        _ = setup.wait(timeout: .now() + 8)
        if errBox.err != 0 { return errBox.err }
        if writer == nil { return -6 }
        return 0
    }

    func stop() -> Int32 {
        let s = self.stream; self.stream = nil
        if s == nil { return -1 }
        let capStop = DispatchSemaphore(value: 0)
        Task { try? await s?.stopCapture(); capStop.signal() }
        _ = capStop.wait(timeout: .now() + 4)

        // Finalize on the write queue so no append races the finish.
        let done = DispatchSemaphore(value: 0)
        writeQueue.async {
            self.lock.lock(); let already = self.finished; self.finished = true; let didStart = self.started; self.lock.unlock()
            guard let aw = self.writer, !already, didStart, aw.status == .writing else { done.signal(); return }
            self.videoIn?.markAsFinished()
            self.audioIn?.markAsFinished()
            aw.finishWriting { done.signal() }
        }
        _ = done.wait(timeout: .now() + 8)
        return 0
    }

    func stream(_ st: SCStream, didOutputSampleBuffer sb: CMSampleBuffer, of type: SCStreamOutputType) {
        guard CMSampleBufferDataIsReady(sb), let aw = writer else { return }

        if type == .screen {
            // SCK sends a frame even when nothing changed; only append "complete" frames.
            if let attach = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[SCStreamFrameInfo: Any]],
               let statusRaw = attach.first?[.status] as? Int,
               let status = SCFrameStatus(rawValue: statusRaw), status != .complete {
                return
            }
            lock.lock()
            if !started {
                if aw.status == .unknown {
                    aw.startWriting()
                    aw.startSession(atSourceTime: CMSampleBufferGetPresentationTimeStamp(sb))
                    started = true
                }
            }
            let ready = started && (videoIn?.isReadyForMoreMediaData ?? false) && !finished
            lock.unlock()
            if ready, aw.status == .writing, videoIn?.append(sb) == true {
                lock.lock(); videoFrames += 1; lock.unlock()
            }
        } else if type == .audio {
            lock.lock()
            let ready = started && (audioIn?.isReadyForMoreMediaData ?? false) && !finished
            lock.unlock()
            if ready, aw.status == .writing, audioIn?.append(sb) == true {
                lock.lock(); audioFrames += 1; lock.unlock()
            }
        }
    }

    func stream(_ s: SCStream, didStopWithError e: Error) { NSLog("[nty_record] stream stopped: \(e)") }
}

// Tiny thread-safe error box for the async setup (mirrors ProbeCollector's use in Audio.swift).
@available(macOS 13.0, *)
private final class _RecBox { var err: Int32 = 0 }

@available(macOS 13.0, *)
private var _recSession: RecordingSession?
private let _recLock = NSLock()

@_cdecl("nty_record_start")
public func nty_record_start(_ path: UnsafePointer<CChar>?) -> Int32 {
    guard #available(macOS 13.0, *) else { return -4 }
    guard let path = path else { return -6 }
    let p = String(cString: path)
    _recLock.lock(); defer { _recLock.unlock() }
    if _recSession != nil { return -5 }
    let s = RecordingSession()
    let rc = s.start(path: p)
    if rc == 0 { _recSession = s }
    return rc
}

@_cdecl("nty_record_stop")
public func nty_record_stop() -> Int32 {
    guard #available(macOS 13.0, *) else { return -4 }
    _recLock.lock(); let s = _recSession; _recSession = nil; _recLock.unlock()
    guard let s = s else { return -1 }
    return s.stop()
}

@_cdecl("nty_record_is_active")
public func nty_record_is_active() -> Int32 {
    guard #available(macOS 13.0, *) else { return 0 }
    _recLock.lock(); let active = _recSession != nil; _recLock.unlock()
    return active ? 1 : 0
}

@_cdecl("nty_record_video_frames")
public func nty_record_video_frames() -> Int64 {
    guard #available(macOS 13.0, *) else { return 0 }
    _recLock.lock(); let n = _recSession?.videoFrames ?? 0; _recLock.unlock()
    return n
}

@_cdecl("nty_record_audio_frames")
public func nty_record_audio_frames() -> Int64 {
    guard #available(macOS 13.0, *) else { return 0 }
    _recLock.lock(); let n = _recSession?.audioFrames ?? 0; _recLock.unlock()
    return n
}
