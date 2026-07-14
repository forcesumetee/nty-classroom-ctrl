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
import ScreenCaptureKit   // TT-10 system-audio probe (nty_sysaudio_probe)

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

// MARK: - Playback (Phase 29-F) -----------------------------------------------------
// Path A: play the Teacher's broadcast/system audio (AudioStreamFrame 0x0329) through
// the Mac speakers. SEPARATE AVAudioEngine + state from capture, so playback and mic
// capture can run concurrently and independently.
//
// Jitter buffer: the wire delivers 100 ms PCM frames but network jitter means they do
// NOT arrive on a clean cadence. Strategy:
//   * PREBUFFER 3 frames (~300 ms) before starting the player — absorbs early jitter so
//     the first frames don't underrun. Cost: ~300 ms added playback latency (fine for
//     a one-way listen; not a conversation).
//   * STEADY STATE: schedule each frame on the player node as it arrives; a completion
//     handler tracks the pending (scheduled-but-unplayed) depth.
//   * OVERRUN guard: if pending ≥ 10 frames (~1 s), DROP the incoming frame — bounds
//     latency creep after a burst/catch-up instead of letting it grow unbounded.
//   * UNDERRUN: AVAudioPlayerNode simply goes silent when it runs dry and resumes when
//     the next buffer is scheduled — a brief glitch, no restart needed.
//
// ⚠ ACOUSTIC FEEDBACK: playing teacher audio (path A) while the mic streams (path B) in
// the same room loops sound. No AEC in M20 — test A and B separately, or with headphones.

private final class PlaybackSession {
    let engine = AVAudioEngine()
    let player = AVAudioPlayerNode()
    let format: AVAudioFormat            // float32 @ srcRate — engine resamples to device rate
    let lock = NSLock()
    var pending = 0
    var started = false
    let prebuffer = 3                    // frames (~300 ms) before play()
    let maxPending = 10                  // cap (~1 s) — drop beyond this to bound latency

    init?(sampleRate: Double, channels: AVAudioChannelCount) {
        guard let fmt = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: channels)
        else { return nil }
        format = fmt
        engine.attach(player)
        engine.connect(player, to: engine.mainMixerNode, format: fmt)
        engine.prepare()
        do { try engine.start() } catch { return nil }
    }

    /// Enqueue one PCM16-LE frame (call-scoped bytes). Converts to float32 and schedules.
    func enqueue(_ bytes: UnsafePointer<UInt8>, _ length: Int) {
        let sampleCount = length / 2
        guard sampleCount > 0 else { return }

        lock.lock(); let p = pending; lock.unlock()
        if p >= maxPending { return }                     // overrun guard → drop

        guard let buf = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(sampleCount)),
              let ch = buf.floatChannelData else { return }
        buf.frameLength = AVAudioFrameCount(sampleCount)
        let dst = ch[0]
        // int16 LE → float32, alignment-safe (read byte pairs explicitly).
        for i in 0..<sampleCount {
            let s = Int16(bitPattern: UInt16(bytes[i * 2]) | (UInt16(bytes[i * 2 + 1]) << 8))
            dst[i] = Float(s) / 32768.0
        }

        lock.lock(); pending += 1; let count = pending; lock.unlock()
        player.scheduleBuffer(buf) { [weak self] in
            guard let self = self else { return }
            self.lock.lock(); self.pending -= 1; self.lock.unlock()
        }
        if !started && count >= prebuffer {               // start after prebuffer fills
            started = true
            player.play()
        }
    }

    func stop() {
        player.stop()
        if engine.isRunning { engine.stop() }
    }
}

private enum PlayState {
    static let lock = NSLock()
    static var session: PlaybackSession?
}

/// 29-F — start the playback engine at sampleRate×channels (0 → 16000/1). Returns 0,
/// -3 already running, -7 engine failed.
@_cdecl("nty_audio_play_start")
public func nty_audio_play_start(_ sampleRate: Int32, _ channels: Int32) -> Int32 {
    PlayState.lock.lock(); defer { PlayState.lock.unlock() }
    if PlayState.session != nil { return -3 }
    let rate = sampleRate > 0 ? Double(sampleRate) : 16000.0
    let chs = AVAudioChannelCount(channels > 0 ? channels : 1)
    guard let s = PlaybackSession(sampleRate: rate, channels: chs) else { return -7 }
    PlayState.session = s
    return 0
}

/// 29-F — enqueue one PCM16-LE frame for playback (call-scoped bytes).
@_cdecl("nty_audio_play_pcm")
public func nty_audio_play_pcm(_ data: UnsafePointer<UInt8>?, _ length: Int32) {
    guard let data = data, length > 0 else { return }
    PlayState.lock.lock(); let s = PlayState.session; PlayState.lock.unlock()
    s?.enqueue(data, Int(length))
}

@_cdecl("nty_audio_play_stop")
public func nty_audio_play_stop() {
    PlayState.lock.lock(); let s = PlayState.session; PlayState.session = nil; PlayState.lock.unlock()
    s?.stop()
}

// MARK: - Multi-source mixer (TT-9-C) -----------------------------------------------
// Teacher-side: mix N students' mic PCM (StudentAudioStreamFrame 0x032C) into one
// output — path A of TT-9. This is the REUSABLE CORE, keyed by an opaque Int32 source
// id: TT-11's student-side peer mixer will consume the same nty_mix_* ABI (mix N-1
// peers, room-bounded). SEPARATE engine + state from capture and single-stream playback.
//
// THE LOAD-BEARING INVARIANT (non-blocking mix): ONE AVAudioPlayerNode per source, all
// summed by the engine's mainMixerNode. A starved node (its sender stalled) plays
// SILENCE and never blocks — so one stalled student never silences the class. This
// reproduces the shipped NAudio ReadFully semantics STRUCTURALLY (independent nodes),
// rather than by a hand-written pre-mix that could reintroduce head-of-line blocking.
//
// Per-source gain = 1/sqrt(activeCount): power-preserving so N summed voices don't clip
// — a DELIBERATE improvement over the shipped StudentAudioMixer (no normalization → it
// clips at high N). The CAP is enforced by the managed layer (TeacherAudioMixer), which
// can surface "N of M open" to the teacher; the native core just mixes what it is given.
//
// Counters (nty_mix_rendered_frames / _source_played / _output_rms) exist so the
// TT-9-D kill-one-sender stall test can ASSERT the invariant headlessly: kill a source
// → rendered_frames keeps advancing (class plays on) + the survivor's played count keeps
// advancing while the killed source's freezes.

private final class MixSource {
    let node = AVAudioPlayerNode()
    let lock = NSLock()
    var pending = 0
    var started = false
    var played: Int64 = 0
    let prebuffer = 2                    // ~200 ms before play() — absorb arrival jitter
    let maxPending = 10                  // cap (~1 s) — drop beyond this to bound latency
}

private final class MixerSession {
    let engine = AVAudioEngine()
    let format: AVAudioFormat            // float32 @ rate, mono — engine resamples to device
    let lock = NSLock()
    var sources: [Int32: MixSource] = [:]
    var rendered: Int64 = 0              // output buffers rendered (mainMixerNode tap)
    var outRms: Int32 = 0               // last mixed-output RMS 0..100 (level meter)

    init?(sampleRate: Double, channels: AVAudioChannelCount) {
        guard let fmt = AVAudioFormat(standardFormatWithSampleRate: sampleRate, channels: channels)
        else { return nil }
        format = fmt
        // Touch mainMixerNode (builds mixer→output), then tap it to count rendered
        // buffers — the "the mix is still advancing" signal for the stall test. The tap
        // fires on the audio thread whenever the engine renders, even silence.
        let mixer = engine.mainMixerNode
        mixer.installTap(onBus: 0, bufferSize: 4096, format: nil) { [weak self] buf, _ in
            guard let self = self else { return }
            self.rendered &+= 1
            if let ch = buf.floatChannelData, buf.frameLength > 0 {
                let n = Int(buf.frameLength); var sum = 0.0
                let p = ch[0]; for i in 0..<n { let v = Double(p[i]); sum += v * v }
                self.outRms = Int32(min(100.0, (sum / Double(n)).squareRoot() * 300.0))
            }
        }
        engine.prepare()
        do { try engine.start() } catch { mixer.removeTap(onBus: 0); return nil }
    }

    func addSource(_ id: Int32) {
        lock.lock(); defer { lock.unlock() }
        if sources[id] != nil { return }
        let s = MixSource()
        engine.attach(s.node)
        engine.connect(s.node, to: engine.mainMixerNode, format: format)
        sources[id] = s
        recomputeGainsLocked()
    }

    func removeSource(_ id: Int32) {
        lock.lock(); defer { lock.unlock() }
        guard let s = sources.removeValue(forKey: id) else { return }
        s.node.stop()
        engine.detach(s.node)              // frees the node — no leaked mixer input eating CPU
        recomputeGainsLocked()
    }

    // Power-preserving normalization: N incoherent voices each scaled by 1/sqrt(N) sum
    // to ~unit RMS instead of N× (which clips). Recomputed on every add/remove.
    private func recomputeGainsLocked() {
        let count = sources.count
        let g = count > 0 ? Float(1.0 / Double(count).squareRoot()) : 1.0
        for s in sources.values { s.node.volume = g }
    }

    func push(_ id: Int32, _ bytes: UnsafePointer<UInt8>, _ length: Int) {
        lock.lock(); let s = sources[id]; lock.unlock()
        guard let s = s else { return }                    // not added (capped/removed) → drop
        let sampleCount = length / 2
        guard sampleCount > 0 else { return }

        s.lock.lock(); let p = s.pending; s.lock.unlock()
        if p >= s.maxPending { return }                    // overrun guard → drop

        guard let buf = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(sampleCount)),
              let ch = buf.floatChannelData else { return }
        buf.frameLength = AVAudioFrameCount(sampleCount)
        let dst = ch[0]
        for i in 0..<sampleCount {
            let v = Int16(bitPattern: UInt16(bytes[i * 2]) | (UInt16(bytes[i * 2 + 1]) << 8))
            dst[i] = Float(v) / 32768.0
        }

        s.lock.lock(); s.pending += 1; let cnt = s.pending; s.lock.unlock()
        s.node.scheduleBuffer(buf) { [weak s] in
            guard let s = s else { return }
            s.lock.lock(); s.pending -= 1; s.played &+= 1; s.lock.unlock()
        }
        if !s.started && cnt >= s.prebuffer {
            s.started = true
            s.node.play()
        }
    }

    func played(_ id: Int32) -> Int64 {
        lock.lock(); let s = sources[id]; lock.unlock()
        guard let s = s else { return -1 }
        s.lock.lock(); defer { s.lock.unlock() }
        return s.played
    }

    func count() -> Int32 { lock.lock(); defer { lock.unlock() }; return Int32(sources.count) }

    func stop() {
        lock.lock(); let ss = sources; sources.removeAll(); lock.unlock()
        for s in ss.values { s.node.stop(); engine.detach(s.node) }
        engine.mainMixerNode.removeTap(onBus: 0)
        if engine.isRunning { engine.stop() }
    }
}

private enum MixState {
    static let lock = NSLock()
    static var session: MixerSession?
}

/// TT-9-C — start the teacher mix engine at sampleRate×channels (0 → 16000/1).
/// Returns 0, -3 already running, -7 engine failed (e.g. no output device).
@_cdecl("nty_mix_start")
public func nty_mix_start(_ sampleRate: Int32, _ channels: Int32) -> Int32 {
    MixState.lock.lock(); defer { MixState.lock.unlock() }
    if MixState.session != nil { return -3 }
    let rate = sampleRate > 0 ? Double(sampleRate) : 16000.0
    let chs = AVAudioChannelCount(channels > 0 ? channels : 1)
    guard let s = MixerSession(sampleRate: rate, channels: chs) else { return -7 }
    MixState.session = s
    return 0
}

/// Register a source (creates its player node + recomputes gains). Idempotent.
@_cdecl("nty_mix_add")
public func nty_mix_add(_ sourceId: Int32) {
    MixState.lock.lock(); let s = MixState.session; MixState.lock.unlock()
    s?.addSource(sourceId)
}

/// Enqueue one PCM16-LE frame for a registered source (call-scoped bytes; dropped if
/// the source isn't registered — i.e. capped or removed).
@_cdecl("nty_mix_push")
public func nty_mix_push(_ sourceId: Int32, _ data: UnsafePointer<UInt8>?, _ length: Int32) {
    guard let data = data, length > 0 else { return }
    MixState.lock.lock(); let s = MixState.session; MixState.lock.unlock()
    s?.push(sourceId, data, Int(length))
}

/// Remove a source (stops + detaches its node; recomputes gains). Idempotent — the
/// disconnect-cleanup path so a departed student's node can't leak CPU.
@_cdecl("nty_mix_remove")
public func nty_mix_remove(_ sourceId: Int32) {
    MixState.lock.lock(); let s = MixState.session; MixState.lock.unlock()
    s?.removeSource(sourceId)
}

@_cdecl("nty_mix_stop")
public func nty_mix_stop() {
    MixState.lock.lock(); let s = MixState.session; MixState.session = nil; MixState.lock.unlock()
    s?.stop()
}

@_cdecl("nty_mix_active_count")
public func nty_mix_active_count() -> Int32 {
    MixState.lock.lock(); let s = MixState.session; MixState.lock.unlock(); return s?.count() ?? 0
}

@_cdecl("nty_mix_rendered_frames")
public func nty_mix_rendered_frames() -> Int64 {
    MixState.lock.lock(); let s = MixState.session; MixState.lock.unlock(); return s?.rendered ?? 0
}

@_cdecl("nty_mix_output_rms")
public func nty_mix_output_rms() -> Int32 {
    MixState.lock.lock(); let s = MixState.session; MixState.lock.unlock(); return s?.outRms ?? 0
}

/// Per-source count of frames actually PLAYED (scheduleBuffer completions). Freezes when
/// a source stalls; -1 if the source isn't registered. The survivor-vs-killed signal for
/// the TT-9-D stall test.
@_cdecl("nty_mix_source_played")
public func nty_mix_source_played(_ sourceId: Int32) -> Int64 {
    MixState.lock.lock(); let s = MixState.session; MixState.lock.unlock(); return s?.played(sourceId) ?? -1
}

// MARK: - TT-10 system-audio PROBE (temporary; called from the granted Teacher bundle) --------------
// Answers: with Screen Recording actually granted (bundle identity), do NON-SILENT system-audio
// buffers arrive from SCStreamConfiguration.capturesAudio, AND do audio + screen coexist in ONE
// SCStream (the real use case: teacher plays a video → students see picture AND hear sound)?
// Synchronous (blocks the caller ~durationMs) so the .NET side gets a clean answer. Out-params:
// audioBuffers, nonSilentAudio (Float32 |amp|>0.001), screenBuffers, maxAbs×1000. Returns 0 ok,
// -2 no display, -3 SCK start error (e.g. TCC not granted — grant + RELAUNCH the bundle).

// SCK AUDIO is macOS 13.0+ (the dylib targets 12.3) → gate behind @available; the @_cdecl entry
// does a runtime #available check. All shared state lives in the collector (a class) so the Task
// closure never mutates captured locals (strict-concurrency clean).
@available(macOS 13.0, *)
private final class ProbeCollector: NSObject, SCStreamOutput, SCStreamDelegate {
    var audio = 0, nonSilent = 0, screen = 0
    var maxAbs: Float = 0
    var errText = ""
    var startErr: Int32 = 0
    var stream: SCStream?
    let lock = NSLock()
    func stream(_ s: SCStream, didOutputSampleBuffer sb: CMSampleBuffer, of type: SCStreamOutputType) {
        if type == .screen { lock.lock(); screen += 1; lock.unlock(); return }
        guard type == .audio else { return }
        lock.lock(); audio += 1; lock.unlock()
        guard let bb = CMSampleBufferGetDataBuffer(sb) else { return }
        var len = 0; var ptr: UnsafeMutablePointer<Int8>?
        CMBlockBufferGetDataPointer(bb, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &len, dataPointerOut: &ptr)
        guard let p = ptr, len >= 4 else { return }
        var localMax: Float = 0
        p.withMemoryRebound(to: Float32.self, capacity: len / 4) { fp in
            for i in 0..<(len / 4) { let v = abs(fp[i]); if v > localMax { localMax = v } }
        }
        lock.lock(); if localMax > maxAbs { maxAbs = localMax }; if localMax > 0.001 { nonSilent += 1 }; lock.unlock()
    }
    func stream(_ s: SCStream, didStopWithError e: Error) { lock.lock(); errText = "\(e)"; lock.unlock() }
}

@available(macOS 13.0, *)
private func runSysAudioProbe(_ durationMs: Int32,
                             _ outAudio: UnsafeMutablePointer<Int32>?,
                             _ outNonSilent: UnsafeMutablePointer<Int32>?,
                             _ outScreen: UnsafeMutablePointer<Int32>?,
                             _ outMaxAbsMilli: UnsafeMutablePointer<Int32>?) -> Int32 {
    let collector = ProbeCollector()
    let setup = DispatchSemaphore(value: 0)
    Task {
        do {
            let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
            guard let display = content.displays.first else { collector.startErr = -2; setup.signal(); return }
            let cfg = SCStreamConfiguration()
            cfg.capturesAudio = true            // ← the system-audio tap
            cfg.sampleRate = 48000
            cfg.channelCount = 2
            cfg.width = 640; cfg.height = 480    // real screen output IN THE SAME STREAM (coexistence test)
            cfg.minimumFrameInterval = CMTime(value: 1, timescale: 5)
            let stream = SCStream(filter: SCContentFilter(display: display, excludingWindows: []),
                                  configuration: cfg, delegate: collector)
            try stream.addStreamOutput(collector, type: .audio, sampleHandlerQueue: DispatchQueue(label: "probe.a"))
            try stream.addStreamOutput(collector, type: .screen, sampleHandlerQueue: DispatchQueue(label: "probe.s"))
            try await stream.startCapture()
            collector.stream = stream
            setup.signal()
        } catch {
            collector.lock.lock(); collector.errText = "\(error)"; collector.lock.unlock()
            collector.startErr = -3; setup.signal()
        }
    }

    _ = setup.wait(timeout: .now() + 8)
    if collector.startErr != 0 {
        NSLog("[nty_sysaudio_probe] start error: \(collector.errText)")
        return collector.startErr
    }
    Thread.sleep(forTimeInterval: Double(max(1000, durationMs)) / 1000.0)   // collect buffers
    let stop = DispatchSemaphore(value: 0)
    let s = collector.stream
    Task { try? await s?.stopCapture(); stop.signal() }
    _ = stop.wait(timeout: .now() + 3)

    collector.lock.lock()
    outAudio?.pointee = Int32(collector.audio)
    outNonSilent?.pointee = Int32(collector.nonSilent)
    outScreen?.pointee = Int32(collector.screen)
    outMaxAbsMilli?.pointee = Int32(min(1000.0, collector.maxAbs * 1000.0))
    collector.lock.unlock()
    return 0
}

@_cdecl("nty_sysaudio_probe")
public func nty_sysaudio_probe(_ durationMs: Int32,
                               _ outAudio: UnsafeMutablePointer<Int32>?,
                               _ outNonSilent: UnsafeMutablePointer<Int32>?,
                               _ outScreen: UnsafeMutablePointer<Int32>?,
                               _ outMaxAbsMilli: UnsafeMutablePointer<Int32>?) -> Int32 {
    if #available(macOS 13.0, *) {
        return runSysAudioProbe(durationMs, outAudio, outNonSilent, outScreen, outMaxAbsMilli)
    }
    return -4   // SCK audio needs macOS 13.0+
}

// MARK: - TT-10-C "Share Computer Audio" — capture SYSTEM audio → wire PCM (proven by the probe) ----
// SCK capturesAudio, asking SCK for the WIRE rate directly (16 kHz mono) so there's no resampler:
// each SCK Float32 buffer → clamp → Int16 → accumulate → emit 100 ms / 3200-byte frames via the same
// NtyPcmCallback the mic path uses. The teacher then broadcasts these as AudioStreamFrame 0x0329
// (TT-10-B path, no wire change) → students play them with their existing playback. TCC = Screen
// Recording (a signed bundle — the probe proved a bare binary has no TCC identity). macOS 13.0+.

@available(macOS 13.0, *)
private final class SysAudioSession: NSObject, SCStreamOutput, SCStreamDelegate {
    let ctx: UnsafeMutableRawPointer?
    let callback: NtyPcmCallback
    let samplesPerFrame = 1600            // 100 ms @ 16 kHz
    var accum: [Int16] = []
    var stream: SCStream?
    let lock = NSLock()

    init(_ ctx: UnsafeMutableRawPointer?, _ cb: @escaping NtyPcmCallback) { self.ctx = ctx; self.callback = cb }

    func start() -> Int32 {
        let setup = DispatchSemaphore(value: 0)
        let errBox = ProbeCollector()   // reuse as a tiny thread-safe int box (startErr/stream)
        Task {
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                guard let display = content.displays.first else { errBox.startErr = -2; setup.signal(); return }
                let cfg = SCStreamConfiguration()
                cfg.capturesAudio = true
                cfg.sampleRate = 16000        // ask SCK for the wire rate directly → no resampler
                cfg.channelCount = 1
                cfg.width = 2; cfg.height = 2  // audio-only intent; a throwaway video output keeps the stream running
                cfg.minimumFrameInterval = CMTime(value: 1, timescale: 1)
                let s = SCStream(filter: SCContentFilter(display: display, excludingWindows: []),
                                 configuration: cfg, delegate: self)
                try s.addStreamOutput(self, type: .audio, sampleHandlerQueue: DispatchQueue(label: "sysaud.a"))
                try s.addStreamOutput(self, type: .screen, sampleHandlerQueue: DispatchQueue(label: "sysaud.s"))
                try await s.startCapture()
                errBox.stream = s
                setup.signal()
            } catch {
                NSLog("[nty_sysaudio_start] \(error)"); errBox.startErr = -3; setup.signal()
            }
        }
        _ = setup.wait(timeout: .now() + 8)
        if errBox.startErr != 0 { return errBox.startErr }
        self.stream = errBox.stream
        return 0
    }

    func stop() {
        let s = self.stream; self.stream = nil
        let sem = DispatchSemaphore(value: 0)
        Task { try? await s?.stopCapture(); sem.signal() }
        _ = sem.wait(timeout: .now() + 3)
    }

    func stream(_ st: SCStream, didOutputSampleBuffer sb: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio else { return }   // ignore the throwaway screen output
        guard let bb = CMSampleBufferGetDataBuffer(sb) else { return }
        var len = 0; var ptr: UnsafeMutablePointer<Int8>?
        CMBlockBufferGetDataPointer(bb, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &len, dataPointerOut: &ptr)
        guard let p = ptr, len >= 4 else { return }
        let n = len / 4

        var toEmit: [[Int16]] = []
        lock.lock()
        p.withMemoryRebound(to: Float32.self, capacity: n) { fp in
            for i in 0..<n {
                var v = fp[i]; if v > 1 { v = 1 } else if v < -1 { v = -1 }
                accum.append(Int16(v * 32767))
            }
        }
        while accum.count >= samplesPerFrame {
            toEmit.append(Array(accum[0..<samplesPerFrame]))
            accum.removeFirst(samplesPerFrame)
        }
        lock.unlock()

        for frame in toEmit {
            frame.withUnsafeBytes { raw in
                callback(ctx, raw.baseAddress?.assumingMemoryBound(to: UInt8.self),
                         Int32(samplesPerFrame * 2), 16000, 1)
            }
        }
    }
    func stream(_ st: SCStream, didStopWithError e: Error) { NSLog("[nty_sysaudio] stopped: \(e)") }
}

private enum SysAudState {
    static let lock = NSLock()
    static var session: AnyObject?
}

/// TT-10-C — start SYSTEM-audio capture (SCK), delivering 100 ms PCM16 16 kHz mono frames via `cb`.
/// Returns 0, -2 no display, -3 SCK/TCC error (Screen Recording), -4 null cb, -5 pre-macOS-13.
@_cdecl("nty_sysaudio_start")
public func nty_sysaudio_start(_ cb: NtyPcmCallback?, _ ctx: UnsafeMutableRawPointer?) -> Int32 {
    guard let cb = cb else { return -4 }
    if #available(macOS 13.0, *) {
        SysAudState.lock.lock(); defer { SysAudState.lock.unlock() }
        if SysAudState.session != nil { return -3 }
        let s = SysAudioSession(ctx, cb)
        let rc = s.start()
        if rc == 0 { SysAudState.session = s }
        return rc
    }
    return -5
}

@_cdecl("nty_sysaudio_stop")
public func nty_sysaudio_stop() {
    if #available(macOS 13.0, *) {
        SysAudState.lock.lock(); let s = SysAudState.session as? SysAudioSession; SysAudState.session = nil; SysAudState.lock.unlock()
        s?.stop()
    }
}
