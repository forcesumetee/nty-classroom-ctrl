// TT-10 last-ever Mac probe: can macOS capture SYSTEM AUDIO first-party via ScreenCaptureKit?
// Self-exits after 6s (never hangs on a TCC prompt). Reports one verdict line.
import ScreenCaptureKit
import AVFoundation
import CoreMedia
import Foundation

final class Out: NSObject, SCStreamOutput {
    var audioBuffers = 0
    var nonSilent = 0
    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .audio else { return }
        audioBuffers += 1
        guard let bb = CMSampleBufferGetDataBuffer(sampleBuffer) else { return }
        var length = 0
        var ptr: UnsafeMutablePointer<Int8>?
        CMBlockBufferGetDataPointer(bb, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &length, dataPointerOut: &ptr)
        guard let p = ptr, length >= 2 else { return }
        var maxAbs: Int32 = 0
        p.withMemoryRebound(to: Int16.self, capacity: length / 2) { ip in
            for i in 0..<(length / 2) { let a = Int32(ip[i]).magnitude; if Int32(a) > maxAbs { maxAbs = Int32(a) } }
        }
        if maxAbs > 200 { nonSilent += 1 }
    }
}

let out = Out()

Task {
    do {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first else { print("PROBE: no display"); exit(2) }
        let filter = SCContentFilter(display: display, excludingWindows: [])
        let cfg = SCStreamConfiguration()
        cfg.capturesAudio = true
        cfg.sampleRate = 48000
        cfg.channelCount = 2
        cfg.width = 2; cfg.height = 2
        cfg.minimumFrameInterval = CMTime(value: 1, timescale: 2)
        let stream = SCStream(filter: filter, configuration: cfg, delegate: nil)
        let q = DispatchQueue(label: "probe.aud")
        try stream.addStreamOutput(out, type: .audio, sampleHandlerQueue: q)
        try stream.addStreamOutput(out, type: .screen, sampleHandlerQueue: DispatchQueue(label: "probe.scr"))
        try await stream.startCapture()
        print("PROBE: SCK stream started (capturesAudio=true) — sampling 4s")
    } catch {
        print("PROBE: SCK error → \(error)")
        // fall through to the guard which prints the verdict + exits
    }
}

// Hard guard — exit after 6s no matter what (TCC prompt, hang, whatever).
DispatchQueue.global().asyncAfter(deadline: .now() + 6.0) {
    print("PROBE RESULT: audioBuffers=\(out.audioBuffers) nonSilentBuffers=\(out.nonSilent)")
    if out.audioBuffers > 0 && out.nonSilent > 0 {
        print("VERDICT: ✅ SCK captures SYSTEM AUDIO first-party (non-silent buffers received)")
    } else if out.audioBuffers > 0 {
        print("VERDICT: ⚠️ SCK delivered audio buffers but all near-silent (was audio playing? / muted output?)")
    } else {
        print("VERDICT: ❓ no audio buffers — likely TCC (Screen Recording) not granted to THIS binary, or API blocked")
    }
    exit(0)
}
RunLoop.main.run()
