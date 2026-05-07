using H264Sharp;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using HImageFormat = H264Sharp.ImageFormat;

namespace ClassroomCtrl.Shared.Codec;

/// <summary>
/// Phase 4 Part 4: Thin wrapper around H264Sharp encoder for screen broadcasting.
/// Accepts a BGRA Bitmap, returns concatenated H.264 NAL units (Annex B start codes included).
///
/// Profile: Baseline + low-latency screen-content config.
/// We initialize from <c>GetDefaultParameters()</c> and then explicitly force:
///   • <c>iUsageType = SCREEN_CONTENT_REAL_TIME</c>
///   • <c>iRCMode  = RC_BITRATE_MODE</c>  (CBR — earlier ConfigType preset defaulted to
///     QUALITY mode which IGNORES the bitrate parameter, producing 5x oversized streams)
///   • <c>iTargetBitrate / iMaxBitrate</c> + matching spatial-layer bitrates
///   • <c>uiIntraPeriod = fps × 2</c> (IDR every 2 seconds — late-join sees first frame &lt;2s)
///   • <c>iComplexityMode = LOW_COMPLEXITY</c>
///   • <c>uiProfileIdc = PRO_BASELINE</c> (no B-frames)
///
/// TODO: Migrate to Firefox-style runtime download from ciscobinary.openh264.org
///       if shipping volume exceeds the MPEG-LA AVC pool 100K units/year free tier.
/// </summary>
public sealed class H264EncoderWrapper : IDisposable
{
    private readonly H264Encoder _encoder = new();
    private bool _disposed;

    public int Width { get; }
    public int Height { get; }
    public int BitrateBps { get; }
    public int FramesPerSecond { get; }

    public H264EncoderWrapper(int width, int height, int bitrateBps, int fps)
    {
        Width = width;
        Height = height;
        BitrateBps = bitrateBps;
        FramesPerSecond = fps;

        var param = _encoder.GetDefaultParameters();
        param.iUsageType        = EUsageType.SCREEN_CONTENT_REAL_TIME;
        param.iPicWidth         = width;
        param.iPicHeight        = height;
        param.fMaxFrameRate     = fps;
        param.iTargetBitrate    = bitrateBps;
        param.iMaxBitrate       = bitrateBps;
        param.iRCMode           = RC_MODES.RC_BITRATE_MODE;
        param.iComplexityMode   = ECOMPLEXITY_MODE.LOW_COMPLEXITY;
        param.uiIntraPeriod     = (uint)Math.Max(1, fps * 2);  // IDR every ~2 seconds
        param.bEnableFrameSkip  = true;                         // drop frames if encoder falls behind

        // Spatial layer 0 must mirror the top-level bitrate — without this OpenH264
        // ignores iTargetBitrate at runtime and falls back to layer defaults.
        if (param.sSpatialLayers != null && param.sSpatialLayers.Length > 0)
        {
            param.iSpatialLayerNum = 1;
            param.sSpatialLayers[0].iVideoWidth        = width;
            param.sSpatialLayers[0].iVideoHeight       = height;
            param.sSpatialLayers[0].fFrameRate         = fps;
            param.sSpatialLayers[0].iSpatialBitrate    = bitrateBps;
            param.sSpatialLayers[0].iMaxSpatialBitrate = bitrateBps;
            param.sSpatialLayers[0].uiProfileIdc       = EProfileIdc.PRO_BASELINE;
        }

        var rc = _encoder.Initialize(param);
        if (rc != 0)
        {
            _encoder.Dispose();
            throw new InvalidOperationException($"OpenH264 encoder Initialize(ext) failed (code {rc})");
        }
    }

    /// <summary>
    /// Encode a BGRA32 bitmap. Returns true if encoding produced output.
    /// On true: <paramref name="nalData"/> contains concatenated NAL units (Annex B), and
    /// <paramref name="isKeyframe"/> is true if any output NAL is IDR.
    /// </summary>
    public bool Encode(Bitmap bgra, out byte[] nalData, out bool isKeyframe)
    {
        nalData = Array.Empty<byte>();
        isKeyframe = false;

        if (_disposed) return false;
        if (bgra.PixelFormat != PixelFormat.Format32bppArgb && bgra.PixelFormat != PixelFormat.Format32bppRgb)
            throw new ArgumentException($"Bitmap must be 32bpp BGRA, got {bgra.PixelFormat}", nameof(bgra));

        var rect = new Rectangle(0, 0, bgra.Width, bgra.Height);
        var data = bgra.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            using var img = new RgbImage(HImageFormat.Bgra, bgra.Width, bgra.Height, data.Stride, data.Scan0);
            if (!_encoder.Encode(img, out var nals) || nals == null || nals.Length == 0)
                return false;

            int total = 0;
            for (int i = 0; i < nals.Length; i++) total += nals[i].Length;
            if (total == 0) return false;

            nalData = new byte[total];
            int offset = 0;
            for (int i = 0; i < nals.Length; i++)
            {
                offset += nals[i].CopyTo(nalData, offset);
                if (nals[i].FrameType == FrameType.IDR) isKeyframe = true;
            }
            return true;
        }
        finally
        {
            bgra.UnlockBits(data);
        }
    }

    /// <summary>Force the next encoded frame to be an IDR keyframe.</summary>
    public bool ForceKeyframe() => !_disposed && _encoder.ForceIntraFrame();

    /// <summary>
    /// Phase 4 Part 5: Adjust the maximum / target bitrate at runtime without restarting
    /// the encoder. CBR mode treats this as the new ceiling; the encoder ramps to the
    /// new target on subsequent frames.
    /// </summary>
    public void SetMaxBitrate(int bitrateBps)
    {
        if (_disposed) return;
        _encoder.SetMaxBitrate(bitrateBps);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _encoder.Dispose(); } catch { }
    }
}
