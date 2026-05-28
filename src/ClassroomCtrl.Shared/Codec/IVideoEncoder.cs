using ClassroomCtrl.Shared.Protocol;
using System;
using System.Drawing;

namespace ClassroomCtrl.Shared.Codec;

/// <summary>
/// Phase 11-B inc2 part 1 — common surface for screen-frame encoders.
///
/// Extracted from the inline <c>if (codec == H264) … else …</c> blocks in
/// <c>ScreenBroadcaster</c> and <c>StudentBroadcaster</c>.  The two existing
/// software encoders (<see cref="MJpegEncoder"/>, <see cref="OpenH264Encoder"/>)
/// implement this interface; Increment 2 part 2 will add a third implementation
/// for Media Foundation hardware H.264 (Quick Sync / NVENC / AMF) and slot it
/// into the same fallback chain via <see cref="VideoEncoderFactory"/>.
///
/// This part 1 is a pure refactor — no behavior change.  Same bytes on the
/// wire, same FPS, same bitrate adaptation.  The abstraction exists so that
/// adding HW encode in part 2 is a single new case in the factory rather than
/// touching the broadcasters' capture loops a third time.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>Codec the broadcaster should stamp onto every <c>ScreenStreamFrameMessage</c>
    /// emitted from this encoder.  Read once per frame.</summary>
    VideoCodec Codec { get; }

    /// <summary>
    /// Encode one captured frame.  Returns <c>null</c> when the encoder produced no
    /// output this tick — e.g. OpenH264 frame-skip under encoder pressure, or a
    /// transient encode error.  Pre-refactor both broadcasters had a guard of the
    /// shape <c>if (data != null) send</c>; the null-return preserves that contract.
    /// Throws only on hard configuration / disposed-instance errors.
    /// </summary>
    EncodedFrame? Encode(Bitmap frame);

    /// <summary>Request that the next emitted frame is a keyframe (IDR for H.264).
    /// Returns <c>false</c> if the encoder cannot honour the request — notably
    /// MJPEG, where every frame is already self-contained and "keyframe" is
    /// meaningless.  Used at broadcaster <c>Start()</c> to guarantee the first
    /// frame is decodable, and may be used by the adaptive-bitrate controller
    /// to recover quality-drop students in a future increment.</summary>
    bool ForceKeyframe();

    /// <summary>
    /// Hand the adaptive bitrate controller's new target down to the encoder.
    /// H.264 implementations forward to their CBR setter; the MJPEG implementation
    /// maps bps → JPEG quality internally (so the AdaptiveBitrateController emits
    /// a single number regardless of which codec is active).
    /// </summary>
    void SetMaxBitrate(int bitsPerSecond);
}

/// <summary>
/// Result of one <see cref="IVideoEncoder.Encode"/> call.  Mirrors the
/// (byte[], bool) pair the broadcasters previously assembled inline.
/// </summary>
public readonly struct EncodedFrame
{
    public byte[] Data { get; init; }
    public bool IsKeyframe { get; init; }
}
