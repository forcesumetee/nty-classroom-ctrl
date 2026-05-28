using ClassroomCtrl.Shared.Protocol;
using System;
using System.Drawing;

namespace ClassroomCtrl.Shared.Codec;

/// <summary>
/// Phase 11-B inc2 part 1 — <see cref="IVideoEncoder"/> adapter over the existing
/// <see cref="H264EncoderWrapper"/> (Cisco OpenH264 via H264Sharp).  Pure pass-through;
/// the wrapper does the work.  Pre-inc2 both broadcasters inlined the same
/// <c>_h264.Encode(bmp, out data, out isKeyframe) || data.Length == 0</c> dance.
///
/// This adapter sits as the software fallback below the Media Foundation HW encoder
/// added in inc2 part 2.  Behavior here matches the pre-inc2 inline path exactly —
/// same wrapper ctor params, same SetMaxBitrate / ForceKeyframe semantics.
/// </summary>
public sealed class OpenH264Encoder : IVideoEncoder
{
    public VideoCodec Codec => VideoCodec.H264;

    private readonly H264EncoderWrapper _wrapper;
    private bool _disposed;

    public OpenH264Encoder(int width, int height, int bitrateBps, int fps)
    {
        _wrapper = new H264EncoderWrapper(width, height, bitrateBps, fps);
        // Note: the broadcaster's Start() explicitly calls ForceKeyframe() after
        // construction (matching the pre-inc2 two-statement pattern).  Not done
        // in the ctor so the factory + any future test code can construct cheaply
        // without committing to an initial IDR.
    }

    public EncodedFrame? Encode(Bitmap frame)
    {
        if (_disposed) return null;
        // Belt-and-suspenders: wrapper.Encode already returns false on zero NALs
        // (H264EncoderWrapper.cs:95-100), but pre-inc2 the broadcasters guarded
        // both ` !ok ` and ` data.Length == 0 `, so we mirror exactly.
        if (!_wrapper.Encode(frame, out var data, out var isKeyframe) || data.Length == 0)
            return null;
        return new EncodedFrame { Data = data, IsKeyframe = isKeyframe };
    }

    public bool ForceKeyframe() => !_disposed && _wrapper.ForceKeyframe();

    public void SetMaxBitrate(int bitsPerSecond)
    {
        if (_disposed) return;
        _wrapper.SetMaxBitrate(bitsPerSecond);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _wrapper.Dispose(); } catch { }
    }
}
