using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Shared.Codec;

/// <summary>
/// Phase 11-B inc2 part 1 — single switch point that returns the
/// <see cref="IVideoEncoder"/> matching the negotiated codec.
///
/// Today: <see cref="MJpegEncoder"/>, <see cref="OpenH264Encoder"/> (both software).
///
/// Inc2 part 2 will add a Media Foundation hardware H.264 case here, with automatic
/// fallback to <see cref="OpenH264Encoder"/> then <see cref="MJpegEncoder"/>.  The
/// factory is the only place that needs the new case — both broadcasters consume
/// the interface without further change.
/// </summary>
public static class VideoEncoderFactory
{
    /// <param name="codec">Codec the teacher requested (or the student's broadcaster received).</param>
    /// <param name="width">Encoder picture width (pixels).  Used by H.264; MJPEG ignores it (the
    /// JPEG codec reads dimensions off each <c>Bitmap</c>).</param>
    /// <param name="height">Encoder picture height.</param>
    /// <param name="fps">Target frame rate, used by H.264 for rate control and intra period.</param>
    /// <param name="bitrateBps">CBR target for H.264.  Ignored by MJPEG.</param>
    /// <param name="initialMJpegQuality">Initial JPEG quality (0-100) for the MJPEG path.
    /// Pre-inc2: ScreenBroadcaster used 65, StudentBroadcaster used 60 — pass the matching
    /// value from each broadcaster's existing field to preserve behavior exactly.</param>
    public static IVideoEncoder Create(
        VideoCodec codec, int width, int height, int fps, int bitrateBps,
        long initialMJpegQuality)
    {
        return codec switch
        {
            VideoCodec.H264 => new OpenH264Encoder(width, height, bitrateBps, fps),
            _               => new MJpegEncoder(initialMJpegQuality),
        };
    }
}
