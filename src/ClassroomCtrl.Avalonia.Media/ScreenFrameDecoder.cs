using System;
using System.IO;
using Avalonia.Media.Imaging;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Avalonia.Media;

/// <summary>
/// TT-8-B — the pure codec→Bitmap core, extracted from the Teacher's ScreenViewModel so BOTH
/// the Teacher (student screens) and the Student (the teacher's shared screen) decode identically.
/// Mirrors the shipped Windows OnStudentFrame → RenderMjpeg / RenderH264 fork. Owns a lazy
/// per-instance <see cref="H264DecoderWrapper"/> (VTDecompressionSession).
///
/// It decodes and SIGNALS; it does NOT decide policy. When an H.264 KEYFRAME fails to decode,
/// <see cref="LastKeyframeDecodeFailed"/> is set — the CALLER chooses what to do (the Teacher
/// re-requests MJPEG via its stream source; the Student, which receives a broadcast it can't
/// re-request, just surfaces a status). That split keeps the app-specific fallback out of here.
/// </summary>
public sealed class ScreenFrameDecoder : IDisposable
{
    private H264DecoderWrapper? _h264;

    /// <summary>True after the most recent <see cref="Decode"/> when an H.264 KEYFRAME could not
    /// decode (unsupported stream / decoder error / no VideoToolbox). A delta returning null
    /// (waiting for the first keyframe) does NOT set this — that's normal.</summary>
    public bool LastKeyframeDecodeFailed { get; private set; }

    /// <summary>Decode one wire frame to a fresh <see cref="Bitmap"/> (caller disposes the
    /// previous one). Returns null on drop / waiting-for-keyframe / failure — inspect
    /// <see cref="LastKeyframeDecodeFailed"/> for the H.264 "can't decode this stream" signal.</summary>
    public Bitmap? Decode(ScreenStreamFrameMessage frame)
    {
        LastKeyframeDecodeFailed = false;
        return frame.Codec switch
        {
            VideoCodec.Mjpeg => DecodeMjpeg(frame.FrameData),
            VideoCodec.H264 => DecodeH264(frame),
            _ => null,   // unknown codec — caller surfaces "unsupported"
        };
    }

    /// <summary>MJPEG: <c>new Bitmap</c> over the JPEG payload (the shipped RenderMjpeg equivalent).
    /// Null on a bad/empty payload so one corrupt frame never tears down the view.</summary>
    public static Bitmap? DecodeMjpeg(byte[]? data)
    {
        if (data is null || data.Length == 0) return null;
        try { using var ms = new MemoryStream(data); return new Bitmap(ms); }
        catch { return null; }
    }

    private Bitmap? DecodeH264(ScreenStreamFrameMessage frame)
    {
        var data = frame.FrameData;
        if (data is null || data.Length == 0) return null;
        if (!H264DecoderWrapper.IsSupported)
        {
            LastKeyframeDecodeFailed = frame.IsKeyframe;   // non-mac → can't decode; signal fallback on a keyframe
            return null;
        }
        try
        {
            _h264 ??= new H264DecoderWrapper();
            var wb = _h264.TryDecode(data, frame.IsKeyframe);
            if (wb is null && frame.IsKeyframe) LastKeyframeDecodeFailed = true;
            return wb;
        }
        catch   // native failure / session-create error
        {
            LastKeyframeDecodeFailed = true;
            return null;
        }
    }

    public void Dispose()
    {
        var d = _h264;
        _h264 = null;
        try { d?.Dispose(); } catch { /* idempotent teardown */ }
    }
}
