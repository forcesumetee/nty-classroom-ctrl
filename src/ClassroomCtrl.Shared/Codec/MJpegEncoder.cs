using ClassroomCtrl.Shared.Protocol;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace ClassroomCtrl.Shared.Codec;

/// <summary>
/// Phase 11-B inc2 part 1 — wraps the GDI+ JPEG encoder both broadcasters used
/// inline pre-refactor (<c>Bitmap.Save(ms, jpegCodec, EncoderParameter Quality)</c>).
/// Behavior is byte-identical to that inline path; this class only relocates it.
///
/// Bitrate adaptation: <see cref="SetMaxBitrate"/> maps bps → JPEG quality via the
/// same linear breakpoints <c>ScreenBroadcaster</c> used (200k→q40, 500k→q60,
/// 1M→q75, 2M→q85).  Pre-refactor <c>OnAdaptiveBitrateChanged</c> owned that
/// mapping; preserving it inside the encoder keeps the <see cref="IVideoEncoder"/>
/// API uniform without changing what <c>AdaptiveBitrateController</c> feeds.
///
/// Every JPEG frame is self-contained, so <see cref="ForceKeyframe"/> is a
/// no-op (returns false so a caller cycling codecs can distinguish "honoured"
/// from "n/a").
/// </summary>
public sealed class MJpegEncoder : IVideoEncoder
{
    public VideoCodec Codec => VideoCodec.Mjpeg;

    private readonly ImageCodecInfo _jpegCodec;
    private EncoderParameters _encParams;
    private long _quality;
    private bool _disposed;

    public MJpegEncoder(long initialQuality)
    {
        _jpegCodec = FindJpegCodec()
            ?? throw new InvalidOperationException("No JPEG codec available");
        _quality = initialQuality;
        _encParams = BuildParams(_quality);
    }

    public EncodedFrame? Encode(Bitmap frame)
    {
        if (_disposed) return null;
        using var ms = new MemoryStream();
        frame.Save(ms, _jpegCodec, _encParams);
        return new EncodedFrame { Data = ms.ToArray(), IsKeyframe = true };
    }

    public bool ForceKeyframe() => false;

    public void SetMaxBitrate(int bitsPerSecond)
    {
        if (_disposed) return;
        var newQuality = MapBitrateToJpegQuality(bitsPerSecond);
        if (newQuality == _quality) return;
        _quality = newQuality;
        var old = _encParams;
        _encParams = BuildParams(_quality);
        try { old?.Dispose(); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _encParams?.Dispose(); } catch { }
    }

    private static ImageCodecInfo? FindJpegCodec()
    {
        foreach (var c in ImageCodecInfo.GetImageEncoders())
            if (c.MimeType == "image/jpeg") return c;
        return null;
    }

    private static EncoderParameters BuildParams(long quality)
    {
        var p = new EncoderParameters(1);
        p.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        return p;
    }

    /// <summary>Verbatim copy of <c>ScreenBroadcaster.MapBitrateToJpegQuality</c> (pre-inc2).
    /// Lives here now so MJPEG owns its own bitrate→quality calculus.  Public + static so
    /// <c>ScreenBroadcaster.OnAdaptiveBitrateChanged</c> can still mirror onto its display-only
    /// <c>JpegQuality</c> field (used by the startup log).</summary>
    public static int MapBitrateToJpegQuality(int bps)
    {
        if (bps <= 200_000) return 40;
        if (bps <= 500_000) return 40 + (bps - 200_000) * 20 / 300_000;
        if (bps <= 1_000_000) return 60 + (bps - 500_000) * 15 / 500_000;
        if (bps <= 2_000_000) return 75 + (bps - 1_000_000) * 10 / 1_000_000;
        return 85;
    }
}
