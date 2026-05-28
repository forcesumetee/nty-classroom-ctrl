using ClassroomCtrl.Shared.Protocol;
using System;

namespace ClassroomCtrl.Shared.Codec;

/// <summary>
/// Phase 11-B inc2 part 1 — single switch point that returns the
/// <see cref="IVideoEncoder"/> matching the negotiated codec.
///
/// Phase 11-B inc2 part 2 round 2 — adds the Media Foundation SW dispatch
/// behind <paramref name="useHardwareH264"/>.  CoCreateInstance of the MS SW
/// H.264 MFT — no HW path yet.
///
/// Phase 11-B inc2 part 2 round 3 — adds the Media Foundation HW (async)
/// dispatch in front of the SW path.  Full fallback chain when the flag is on
/// and codec is H264:
///     MediaFoundationH264AsyncEncoder (HW: Quick Sync / NVENC / AMF)
///       → MediaFoundationH264Encoder  (SW: MS H264 Encoder MFT, sync, round 2)
///       → OpenH264Encoder             (CPU OpenH264, always available)
///       → MJpegEncoder                (broadcaster catches further)
/// Each catch is silent except for <paramref name="activeDescription"/>, which
/// the broadcaster logs once at Start so the dev sees which encoder is actually
/// live.
///
/// When the flag is off, codec H264 still works — it just skips both MF tiers
/// and lands on OpenH264, same as round 1 / round 2 with the flag off.
/// </summary>
public static class VideoEncoderFactory
{
    /// <param name="codec">Codec the teacher requested (or the student's broadcaster received).</param>
    /// <param name="width">Encoder picture width (pixels).  Used by H.264; MJPEG ignores it.</param>
    /// <param name="height">Encoder picture height.</param>
    /// <param name="fps">Target frame rate, used by H.264 for rate control and intra period.</param>
    /// <param name="bitrateBps">CBR target for H.264.  Ignored by MJPEG.</param>
    /// <param name="initialMJpegQuality">Initial JPEG quality (0-100) for the MJPEG path.</param>
    /// <param name="useHardwareH264">When true AND codec is H264, try the MF HW
    /// async encoder first, then the MF SW encoder, before falling back to
    /// OpenH264.  When false, behavior is byte-identical to part 1.</param>
    /// <param name="activeDescription">Out — single-line label naming the live
    /// encoder.  Round 3 reads <see cref="MediaFoundationH264AsyncEncoder.ActiveEncoderDescription"/>
    /// directly so the HW vs SW prefix tells the truth (HW only when MFTEnumEx
    /// returned a hardware MFT and it activated cleanly).</param>
    public static IVideoEncoder Create(
        VideoCodec codec, int width, int height, int fps, int bitrateBps,
        long initialMJpegQuality, bool useHardwareH264, out string activeDescription)
    {
        if (codec == VideoCodec.H264)
        {
            if (useHardwareH264)
            {
                // Tier 1 — HW async (Quick Sync / NVENC / AMF).
                try
                {
                    var hw = new MediaFoundationH264AsyncEncoder(width, height, bitrateBps, fps);
                    activeDescription = hw.ActiveEncoderDescription;
                    return hw;
                }
                catch (Exception)
                {
                    // Silent fall-through.  The startup log line (which reads
                    // activeDescription) reveals the selection without flooding
                    // per-frame logs.  Round 4 may want a one-shot Serilog line
                    // for the HW→SW degradation so the dev can investigate.
                }

                // Tier 2 — SW MFT (round 2 path).  Stays intact.
                try
                {
                    var sw = new MediaFoundationH264Encoder(width, height, bitrateBps, fps);
                    activeDescription = $"MediaFoundation SW ({sw.ActiveEncoderDescription})";
                    return sw;
                }
                catch (Exception)
                {
                    // Fall through to OpenH264.
                }
            }
            activeDescription = "OpenH264 SW";
            return new OpenH264Encoder(width, height, bitrateBps, fps);
        }

        activeDescription = $"MJPEG q{initialMJpegQuality}";
        return new MJpegEncoder(initialMJpegQuality);
    }
}
