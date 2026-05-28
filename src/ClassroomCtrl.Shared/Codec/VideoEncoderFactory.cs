using ClassroomCtrl.Shared.Protocol;
using System;

namespace ClassroomCtrl.Shared.Codec;

/// <summary>
/// Phase 11-B inc2 part 1 — single switch point that returns the
/// <see cref="IVideoEncoder"/> matching the negotiated codec.
///
/// Phase 11-B inc2 part 2 — adds the Media Foundation HW dispatch behind
/// <paramref name="useHardwareH264"/>.  Fallback chain when the flag is on and
/// codec is H264:
///     MediaFoundationH264Encoder  →  OpenH264Encoder  →  (broadcaster catches further → MJpegEncoder)
/// Each catch is silent except for the description the caller logs once at Start
/// (so the dev's startup-log line shows exactly which encoder is actually live).
///
/// When the flag is off, behavior is byte-identical to part 1.
/// </summary>
public static class VideoEncoderFactory
{
    /// <param name="codec">Codec the teacher requested (or the student's broadcaster received).</param>
    /// <param name="width">Encoder picture width (pixels).  Used by H.264; MJPEG ignores it.</param>
    /// <param name="height">Encoder picture height.</param>
    /// <param name="fps">Target frame rate, used by H.264 for rate control and intra period.</param>
    /// <param name="bitrateBps">CBR target for H.264.  Ignored by MJPEG.</param>
    /// <param name="initialMJpegQuality">Initial JPEG quality (0-100) for the MJPEG path.</param>
    /// <param name="useHardwareH264">Phase 11-B inc2 part 2 — when true AND codec is H264,
    /// try the Media Foundation HW encoder first; on any failure cleanly fall back to
    /// OpenH264 (software).  When false, behavior is byte-identical to part 1.</param>
    /// <param name="activeDescription">Out — single-line label naming the live encoder, intended
    /// for the broadcaster's startup-log line.  E.g. "MediaFoundation SW", "OpenH264 SW",
    /// "MJPEG q65".  Round 2 of inc2 part 2 selects only the MS software H.264 MFT via direct
    /// CoCreateInstance, so the prefix is always "SW" here today; round 3 (async HW MFT
    /// enumeration) will flip to "HW" when MFTEnumEx picks a Quick Sync / NVENC / AMF MFT.</param>
    public static IVideoEncoder Create(
        VideoCodec codec, int width, int height, int fps, int bitrateBps,
        long initialMJpegQuality, bool useHardwareH264, out string activeDescription)
    {
        if (codec == VideoCodec.H264)
        {
            if (useHardwareH264)
            {
                try
                {
                    var mf = new MediaFoundationH264Encoder(width, height, bitrateBps, fps);
                    // Round 2 always selects the MS SW H.264 MFT (CLSID_MSH264EncoderMFT)
                    // via direct CoCreateInstance — there is no HW path here yet.  The
                    // prefix tells the truth so the broadcaster's startup log doesn't
                    // mislead the dev into thinking Quick Sync is engaged.  Round 3 will
                    // either swap this prefix dynamically based on the MFT chosen by
                    // MFTEnumEx, or move the HW/SW determination into the encoder so the
                    // factory just reads ActiveEncoderDescription.
                    activeDescription = $"MediaFoundation SW ({mf.ActiveEncoderDescription})";
                    return mf;
                }
                catch (Exception)
                {
                    // Spec says: silent fallback chain, but the caller's startup log
                    // (which reads activeDescription) makes the selection visible to
                    // the dev without flooding per-frame logs.  No rethrow.
                }
            }
            activeDescription = "OpenH264 SW";
            return new OpenH264Encoder(width, height, bitrateBps, fps);
        }

        activeDescription = $"MJPEG q{initialMJpegQuality}";
        return new MJpegEncoder(initialMJpegQuality);
    }
}
