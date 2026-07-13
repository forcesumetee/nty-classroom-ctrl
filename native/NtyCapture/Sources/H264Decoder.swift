// H264Decoder.swift — VideoToolbox H.264 decoder (TT-4-B)
// ---------------------------------------------------------------------------------
// The exact inverse of H264Encoder.swift (M18). Input: Annex-B NAL bytes per frame
// from the wire (keyframe = [SPS][PPS][IDR], delta = [slice]) — the shape BOTH the
// shipped Windows OpenH264 student AND our Mac VideoToolbox student emit. Output: a
// BGRA CVPixelBuffer per frame, delivered synchronously via a C callback so the
// managed ScreenViewModel's codec-dispatch seam (TT-3-C) stays synchronous.
//
// Pipeline (inverse of the encoder's AVCC→Annex-B):
//   Annex-B (3- OR 4-byte start codes)
//     → split NALs, strip start codes
//     → on a keyframe: SPS(7)+PPS(8) → CMVideoFormatDescriptionCreateFromH264ParameterSets
//        → (re)create VTDecompressionSession requesting kCVPixelFormatType_32BGRA
//     → VCL slices (1, 5) → AVCC ([4-byte BE length][NAL]) → CMSampleBuffer
//     → VTDecompressionSessionDecodeFrame (+ WaitForAsynchronousFrames = synchronous)
//     → BGRA CVPixelBuffer → callback (honor CVPixelBufferGetBytesPerRow, stride ≥ w*4)
//
// One instance per open screen-view window (handle-based ABI); the teacher may have
// 1–4 concurrent. Idempotent stop; deinit tears the session down so a dropped handle
// cannot leak a VTDecompressionSession.

import Foundation
import VideoToolbox
import CoreMedia
import CoreVideo

final class H264Decoder {
    private var session: VTDecompressionSession?
    private var formatDesc: CMVideoFormatDescription?
    private var currentSPS: [UInt8] = []
    private var currentPPS: [UInt8] = []

    // ── NAL splitting (handles BOTH 3-byte `00 00 01` and 4-byte `00 00 00 01`
    // start codes — OpenH264 emits both, VideoToolbox emits 4-byte). Returns NAL
    // ranges into `base` with start codes STRIPPED. ──
    private static func splitNALUnits(_ base: UnsafePointer<UInt8>, _ len: Int) -> [(off: Int, len: Int)] {
        func startCode(_ j: Int) -> Int {
            if j + 4 <= len, base[j] == 0, base[j + 1] == 0, base[j + 2] == 0, base[j + 3] == 1 { return 4 }
            if j + 3 <= len, base[j] == 0, base[j + 1] == 0, base[j + 2] == 1 { return 3 }
            return 0
        }
        var result: [(Int, Int)] = []
        var i = 0
        while i < len && startCode(i) == 0 { i += 1 }   // skip to the first start code
        while i < len {
            let sc = startCode(i)
            if sc == 0 { i += 1; continue }
            let nalStart = i + sc
            var j = nalStart
            while j < len && startCode(j) == 0 { j += 1 } // scan to the next start code
            let nalLen = j - nalStart
            if nalLen > 0 { result.append((nalStart, nalLen)) }
            i = j
        }
        return result
    }

    /// Decode one wire frame's Annex-B bytes. Returns 1 if a BGRA frame was delivered
    /// via `cb`, 0 if none yet (waiting for a keyframe / SPS-PPS only / no slice),
    /// negative on error (‑1 bad args, ‑21 session create failed, ‑20 decode error).
    /// The frame is delivered on the CALLING thread before this returns (synchronous).
    func feed(nal base: UnsafePointer<UInt8>, length len: Int, isKeyframe: Bool,
              cb: NtyDecodedCallback, ctx: UnsafeMutableRawPointer?) -> Int32 {
        if len <= 0 { return -1 }

        let nals = H264Decoder.splitNALUnits(base, len)
        if nals.isEmpty { return 0 }

        var sps: [UInt8]?
        var pps: [UInt8]?
        var vcl: [(off: Int, len: Int)] = []
        for nal in nals {
            let type = base[nal.off] & 0x1F        // nal_unit_type = low 5 bits of the header byte
            switch type {
            case 7:  sps = Array(UnsafeBufferPointer(start: base + nal.off, count: nal.len))   // SPS
            case 8:  pps = Array(UnsafeBufferPointer(start: base + nal.off, count: nal.len))   // PPS
            case 1, 5: vcl.append(nal)             // non-IDR slice / IDR slice (VCL)
            default: break                          // SEI(6), AUD(9), etc. — skip
            }
        }

        // (Re)create the session when fresh parameter sets arrive — the shipped
        // stream sends SPS/PPS in-band on every keyframe, so this fires on the first
        // keyframe and again only if they ever change (shipped dims are fixed 1920×1080).
        if let sps = sps, let pps = pps, (session == nil || sps != currentSPS || pps != currentPPS) {
            if !createSession(sps: sps, pps: pps) { return -21 }
        }

        // No session yet → dropped until the first keyframe (bounded ≤ IDR interval).
        guard let session = session, let fmt = formatDesc else { return 0 }
        if vcl.isEmpty { return 0 }               // SPS/PPS-only frame carries no picture

        // Build an AVCC block ([4-byte BE length][NAL]…) from the VCL slices.
        var avcc = [UInt8]()
        avcc.reserveCapacity(len)
        for nal in vcl {
            let n = nal.len
            avcc.append(UInt8((n >> 24) & 0xFF)); avcc.append(UInt8((n >> 16) & 0xFF))
            avcc.append(UInt8((n >> 8) & 0xFF));  avcc.append(UInt8(n & 0xFF))
            avcc.append(contentsOf: UnsafeBufferPointer(start: base + nal.off, count: nal.len))
        }
        guard let sample = makeSampleBuffer(avcc: avcc, fmt: fmt) else { return -20 }

        var produced = false
        let st = VTDecompressionSessionDecodeFrame(
            session, sampleBuffer: sample, flags: [], infoFlagsOut: nil
        ) { status, _, imageBuffer, _, _ in
            guard status == noErr, let image = imageBuffer else { return }
            CVPixelBufferLockBaseAddress(image, .readOnly)
            defer { CVPixelBufferUnlockBaseAddress(image, .readOnly) }
            guard let baseAddr = CVPixelBufferGetBaseAddress(image) else { return }
            let w = Int32(CVPixelBufferGetWidth(image))
            let h = Int32(CVPixelBufferGetHeight(image))
            let bpr = Int32(CVPixelBufferGetBytesPerRow(image))   // stride ≥ w*4 (padding) — honored downstream
            cb(ctx, baseAddr.assumingMemoryBound(to: UInt8.self), w, h, bpr)
            produced = true
        }
        // No frame reordering (Baseline) → the frame is ready immediately; Wait makes
        // the synchronous contract robust regardless of the decoder's threading.
        VTDecompressionSessionWaitForAsynchronousFrames(session)
        if st != noErr { return -20 }
        return produced ? 1 : 0
    }

    private func createSession(sps: [UInt8], pps: [UInt8]) -> Bool {
        // Format description from the parameter sets (raw NAL bytes, start codes stripped).
        var fmt: CMVideoFormatDescription?
        let ok: OSStatus = sps.withUnsafeBufferPointer { spsB in
            pps.withUnsafeBufferPointer { ppsB in
                let ptrs: [UnsafePointer<UInt8>] = [spsB.baseAddress!, ppsB.baseAddress!]
                let sizes: [Int] = [sps.count, pps.count]
                return ptrs.withUnsafeBufferPointer { ptrsB in
                    sizes.withUnsafeBufferPointer { sizesB in
                        CMVideoFormatDescriptionCreateFromH264ParameterSets(
                            allocator: kCFAllocatorDefault,
                            parameterSetCount: 2,
                            parameterSetPointers: ptrsB.baseAddress!,
                            parameterSetSizes: sizesB.baseAddress!,
                            nalUnitHeaderLength: 4,
                            formatDescriptionOut: &fmt)
                    }
                }
            }
        }
        guard ok == noErr, let fmt = fmt else { return false }

        stopSession()   // tear down any prior session before replacing it

        let dims = CMVideoFormatDescriptionGetDimensions(fmt)
        // Request BGRA output so managed code can Marshal.Copy straight into a
        // WriteableBitmap (Bgra8888) with no colour conversion.
        let attrs: [CFString: Any] = [
            kCVPixelBufferPixelFormatTypeKey: Int(kCVPixelFormatType_32BGRA),
            kCVPixelBufferWidthKey: Int(dims.width),
            kCVPixelBufferHeightKey: Int(dims.height),
            kCVPixelBufferIOSurfacePropertiesKey: [CFString: Any]() as CFDictionary,
        ]
        var s: VTDecompressionSession?
        let sc = VTDecompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            formatDescription: fmt,
            decoderSpecification: nil,
            imageBufferAttributes: attrs as CFDictionary,
            outputCallback: nil,               // using the block-handler DecodeFrame variant
            decompressionSessionOut: &s)
        guard sc == noErr, let s = s else { return false }

        session = s
        formatDesc = fmt
        currentSPS = sps
        currentPPS = pps
        return true
    }

    private func makeSampleBuffer(avcc: [UInt8], fmt: CMVideoFormatDescription) -> CMSampleBuffer? {
        var bb: CMBlockBuffer?
        var st = CMBlockBufferCreateWithMemoryBlock(
            allocator: kCFAllocatorDefault, memoryBlock: nil, blockLength: avcc.count,
            blockAllocator: kCFAllocatorDefault, customBlockSource: nil,
            offsetToData: 0, dataLength: avcc.count, flags: 0, blockBufferOut: &bb)
        guard st == kCMBlockBufferNoErr, let block = bb else { return nil }
        st = avcc.withUnsafeBytes { raw in
            CMBlockBufferReplaceDataBytes(with: raw.baseAddress!, blockBuffer: block,
                                          offsetIntoDestination: 0, dataLength: avcc.count)
        }
        guard st == kCMBlockBufferNoErr else { return nil }

        var sample: CMSampleBuffer?
        var sizeArray = [avcc.count]
        st = CMSampleBufferCreateReady(
            allocator: kCFAllocatorDefault, dataBuffer: block, formatDescription: fmt,
            sampleCount: 1, sampleTimingEntryCount: 0, sampleTimingArray: nil,
            sampleSizeEntryCount: 1, sampleSizeArray: &sizeArray, sampleBufferOut: &sample)
        guard st == noErr else { return nil }
        return sample
    }

    private func stopSession() {
        if let s = session {
            VTDecompressionSessionWaitForAsynchronousFrames(s)
            VTDecompressionSessionInvalidate(s)
        }
        session = nil
    }

    /// Idempotent teardown — safe to call more than once (session is nil after the first).
    func stop() {
        stopSession()
        formatDesc = nil
        currentSPS = []
        currentPPS = []
    }

    deinit { stop() }   // a dropped handle can't leak a VTDecompressionSession
}
