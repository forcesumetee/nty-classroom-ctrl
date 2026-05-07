using H264Sharp;
using System;

namespace ClassroomCtrl.Shared.Codec;

/// <summary>
/// Phase 4 Part 4: Thin wrapper around H264Sharp decoder for screen viewing.
/// Accepts H.264 NAL bytes, outputs RGB pixel data (caller decides pixel format mapping for WPF).
///
/// TODO: Migrate to Firefox-style runtime download from ciscobinary.openh264.org
///       if shipping volume exceeds the MPEG-LA AVC pool 100K units/year free tier.
/// </summary>
public sealed class H264DecoderWrapper : IDisposable
{
    private readonly H264Decoder _decoder = new();
    private bool _disposed;

    public H264DecoderWrapper()
    {
        var rc = _decoder.Initialize();
        if (rc != 0)
        {
            _decoder.Dispose();
            throw new InvalidOperationException($"OpenH264 decoder Initialize failed (code {rc})");
        }
    }

    /// <summary>
    /// Decode one H.264 NAL bundle. Returns true on success with rgb data + dimensions + format.
    /// May return false legitimately when the decoder needs more data (e.g. waiting for keyframe);
    /// callers should drop the frame and continue.
    /// </summary>
    public bool TryDecode(byte[] nal, out byte[] rgb, out int width, out int height, out ImageFormat format)
    {
        rgb = Array.Empty<byte>();
        width = 0;
        height = 0;
        format = ImageFormat.Bgr;

        if (_disposed || nal == null || nal.Length == 0) return false;

        try
        {
            // RgbImage overload of Decode uses `ref` (not `out`) — must pre-declare.
            // The decoder reassigns/allocates the image; we just hand it a starting reference.
            RgbImage? img = null;
            DecodingState state;
            bool ok = _decoder.Decode(nal, 0, nal.Length, false, out state, ref img!);
            if (!ok || img == null) return false;

            width = img.Width;
            height = img.Height;
            format = img.Format;
            rgb = img.GetBytes();
            img.Dispose();
            return width > 0 && height > 0 && rgb.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _decoder.Dispose(); } catch { }
    }
}
