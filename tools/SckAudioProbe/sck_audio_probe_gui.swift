// TT-10 system-audio probe — FOREGROUND GUI app version (the remaining check).
// Real NSApplication + window (activated) so SCK has a foreground app context, unlike the
// headless CLI run (which started the stream but got 0 buffers). Reads SCK's real Float32
// audio format. Plays sound itself + writes a verdict file. Hard-exits (can't hang).
import Cocoa
import ScreenCaptureKit
import AVFoundation
import CoreMedia
import Foundation

let resultPath = "/private/tmp/claude-501/-Users-fewfee-Dev-nty-classroom-macos/6710efd1-bc44-447a-8799-97a57e1522ff/scratchpad/probe_gui_result.txt"
var log = ""

final class Rec: NSObject, SCStreamOutput, SCStreamDelegate {
    var audio = 0, nonSilent = 0, screen = 0
    var maxAbs: Float = 0
    var fmt = ""
    func stream(_ s: SCStream, didOutputSampleBuffer sb: CMSampleBuffer, of type: SCStreamOutputType) {
        if type == .screen { screen += 1; return }
        guard type == .audio else { return }
        audio += 1
        if fmt.isEmpty, let fd = CMSampleBufferGetFormatDescription(sb),
           let a = CMAudioFormatDescriptionGetStreamBasicDescription(fd)?.pointee {
            fmt = "sr=\(a.mSampleRate) ch=\(a.mChannelsPerFrame) bits=\(a.mBitsPerChannel) flags=\(a.mFormatFlags)"
        }
        guard let bb = CMSampleBufferGetDataBuffer(sb) else { return }
        var len = 0; var ptr: UnsafeMutablePointer<Int8>?
        CMBlockBufferGetDataPointer(bb, atOffset: 0, lengthAtOffsetOut: nil, totalLengthOut: &len, dataPointerOut: &ptr)
        guard let p = ptr, len >= 4 else { return }
        p.withMemoryRebound(to: Float32.self, capacity: len / 4) { fp in
            for i in 0..<(len / 4) { let v = abs(fp[i]); if v > maxAbs { maxAbs = v } }
        }
        if maxAbs > 0.001 { nonSilent += 1 }
    }
    func stream(_ s: SCStream, didStopWithError e: Error) { log += "didStopWithError: \(e)\n" }
}

let rec = Rec()
func writeResult(_ verdict: String) {
    let out = "VERDICT: \(verdict)\naudioBuffers=\(rec.audio) nonSilent=\(rec.nonSilent) maxAbs=\(rec.maxAbs) screenBuffers=\(rec.screen)\naudioFormat=[\(rec.fmt)]\n\(log)"
    try? out.write(toFile: resultPath, atomically: true, encoding: .utf8)
    FileHandle.standardOutput.write(out.data(using: .utf8)!)
}

let app = NSApplication.shared
app.setActivationPolicy(.regular)
let win = NSWindow(contentRect: NSMakeRect(0, 0, 320, 120), styleMask: [.titled], backing: .buffered, defer: false)
win.title = "SCK Audio Probe"; win.center(); win.makeKeyAndOrderFront(nil)
app.activate(ignoringOtherApps: true)

Task {
    do {
        let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
        guard let display = content.displays.first else { writeResult("❓ no display"); exit(2) }
        let cfg = SCStreamConfiguration()
        cfg.capturesAudio = true
        cfg.sampleRate = 48000
        cfg.channelCount = 2
        cfg.width = 640; cfg.height = 480          // realistic dims — rule out the degenerate 2x2 config
        cfg.queueDepth = 6
        cfg.minimumFrameInterval = CMTime(value: 1, timescale: 5)
        let stream = SCStream(filter: SCContentFilter(display: display, excludingWindows: []), configuration: cfg, delegate: rec)
        try stream.addStreamOutput(rec, type: .audio, sampleHandlerQueue: DispatchQueue(label: "a"))
        try stream.addStreamOutput(rec, type: .screen, sampleHandlerQueue: DispatchQueue(label: "s"))
        try await stream.startCapture()
        log += "stream started (foreground GUI, activationPolicy=.regular)\n"
        for _ in 0..<4 {
            let pr = Process(); pr.launchPath = "/usr/bin/afplay"
            pr.arguments = ["/System/Library/Sounds/Sosumi.aiff"]; try? pr.run()
        }
    } catch {
        writeResult("❌/❓ SCK error: \(error)"); exit(3)
    }
}

// Hard guards (main + global) so nothing hangs.
DispatchQueue.main.asyncAfter(deadline: .now() + 7.0) {
    if rec.audio > 0 && rec.nonSilent > 0 { writeResult("✅ SYSTEM AUDIO CAPTURED FIRST-PARTY (non-silent)") }
    else if rec.audio > 0 { writeResult("❌ buffers arrive but SILENT — system audio NOT capturable this way") }
    else { writeResult("❓ still 0 audio buffers even in a foreground GUI app") }
    exit(0)
}
DispatchQueue.global().asyncAfter(deadline: .now() + 10.0) { exit(0) }
app.run()
