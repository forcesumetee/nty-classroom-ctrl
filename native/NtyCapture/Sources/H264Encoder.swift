// H264Encoder.swift — VideoToolbox H.264 encoder (Phase 27-B)
// ---------------------------------------------------------------------------------
// Wraps a VTCompressionSession. Input: BGRA CVPixelBuffers from SCStream. Output:
// concatenated ANNEX-B NAL bytes per frame, with in-band SPS/PPS prepended to every
// keyframe — byte-format-matching the shipped OpenH264 encoder so the Teacher's
// H264Sharp/OpenH264 decoder consumes it unchanged.
//
// Config (locked in the 27-B plan): 1920×1080, Baseline (no B-frames), CBR ~1.5 Mbit/s,
// IDR every fps×2, real-time, hardware-accelerated when available.

import Foundation
import VideoToolbox
import CoreMedia
import CoreVideo

final class H264Encoder {
    private var session: VTCompressionSession?
    private let callback: NtyH264Callback
    private let ctx: UnsafeMutableRawPointer?
    private let width: Int32
    private let height: Int32
    private static let startCode: [UInt8] = [0x00, 0x00, 0x00, 0x01]

    init?(width: Int, height: Int, fps: Int, bitrate: Int,
          callback: @escaping NtyH264Callback, ctx: UnsafeMutableRawPointer?) {
        self.callback = callback
        self.ctx = ctx
        self.width = Int32(width)
        self.height = Int32(height)

        let spec: [CFString: Any] = [
            kVTVideoEncoderSpecification_EnableHardwareAcceleratedVideoEncoder: true
        ]
        var s: VTCompressionSession?
        // outputCallback nil → use the per-frame output-handler API below.
        let st = VTCompressionSessionCreate(
            allocator: kCFAllocatorDefault,
            width: Int32(width), height: Int32(height),
            codecType: kCMVideoCodecType_H264,
            encoderSpecification: spec as CFDictionary,
            imageBufferAttributes: nil,
            compressedDataAllocator: nil,
            outputCallback: nil, refcon: nil,
            compressionSessionOut: &s)
        guard st == noErr, let session = s else { return nil }
        self.session = session

        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_RealTime, value: kCFBooleanTrue)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_ProfileLevel,
                             value: kVTProfileLevel_H264_Baseline_AutoLevel)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AllowFrameReordering, value: kCFBooleanFalse)
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate,
                             value: NSNumber(value: bitrate))
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameInterval,
                             value: NSNumber(value: fps * 2))
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_MaxKeyFrameIntervalDuration,
                             value: NSNumber(value: 2.0))
        VTCompressionSessionPrepareToEncodeFrames(session)
    }

    /// Feed one frame. Encoding is async; the output handler (below) delivers bytes.
    func encode(_ pixelBuffer: CVPixelBuffer, pts: CMTime) {
        guard let session = session else { return }
        let stamp = CMTIME_IS_VALID(pts) ? pts : CMTime(value: 0, timescale: 600)
        VTCompressionSessionEncodeFrame(
            session, imageBuffer: pixelBuffer,
            presentationTimeStamp: stamp, duration: .invalid,
            frameProperties: nil, infoFlagsOut: nil
        ) { [weak self] status, _, sampleBuffer in
            guard let self = self, status == noErr,
                  let sb = sampleBuffer, CMSampleBufferDataIsReady(sb) else { return }
            self.handleOutput(sb)
        }
    }

    private func handleOutput(_ sb: CMSampleBuffer) {
        // Keyframe = a sync sample (NotSync absent or false).
        var isKeyframe = true
        if let arr = CMSampleBufferGetSampleAttachmentsArray(sb, createIfNecessary: false) as? [[CFString: Any]],
           let notSync = arr.first?[kCMSampleAttachmentKey_NotSync] as? Bool {
            isKeyframe = !notSync
        }

        var out = Data()

        // On keyframes, prepend SPS (index 0) + PPS (index 1) as Annex-B (in-band),
        // matching OpenH264's [SPS][PPS][IDR] output.
        if isKeyframe, let fmt = CMSampleBufferGetFormatDescription(sb) {
            for i in 0..<2 {
                var ptr: UnsafePointer<UInt8>?
                var size = 0
                var count = 0
                var headerLen: Int32 = 0
                let st = CMVideoFormatDescriptionGetH264ParameterSetAtIndex(
                    fmt, parameterSetIndex: i,
                    parameterSetPointerOut: &ptr, parameterSetSizeOut: &size,
                    parameterSetCountOut: &count, nalUnitHeaderLengthOut: &headerLen)
                if st == noErr, let ptr = ptr {
                    out.append(contentsOf: H264Encoder.startCode)
                    out.append(ptr, count: size)
                }
            }
        }

        // Slice NALs: the block buffer is AVCC ([4-byte BE length][NAL]…). Convert each
        // to Annex-B ([00 00 00 01][NAL]).
        guard let bb = CMSampleBufferGetDataBuffer(sb) else { return }
        var lengthAtOffset = 0, totalLength = 0
        var dataPointer: UnsafeMutablePointer<Int8>?
        guard CMBlockBufferGetDataPointer(bb, atOffset: 0,
                                          lengthAtOffsetOut: &lengthAtOffset,
                                          totalLengthOut: &totalLength,
                                          dataPointerOut: &dataPointer) == noErr,
              let dp = dataPointer else { return }
        let bytes = UnsafeRawPointer(dp).assumingMemoryBound(to: UInt8.self)
        let headerLen = 4 // AVCC length prefix (default for H.264)
        var offset = 0
        while offset + headerLen <= totalLength {
            var nalLen = 0
            for k in 0..<headerLen { nalLen = (nalLen << 8) | Int(bytes[offset + k]) }
            offset += headerLen
            if nalLen <= 0 || offset + nalLen > totalLength { break }
            out.append(contentsOf: H264Encoder.startCode)
            out.append(bytes + offset, count: nalLen)
            offset += nalLen
        }

        out.withUnsafeBytes { (raw: UnsafeRawBufferPointer) in
            guard let base = raw.bindMemory(to: UInt8.self).baseAddress else { return }
            callback(ctx, base, Int32(out.count), width, height, isKeyframe ? 1 : 0)
        }
    }

    func stop() {
        guard let session = session else { return }
        VTCompressionSessionCompleteFrames(session, untilPresentationTimeStamp: .invalid)
        VTCompressionSessionInvalidate(session)
        self.session = nil
    }
}
