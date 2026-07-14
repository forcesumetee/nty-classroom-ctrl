using Avalonia.Media.Imaging;
using ClassroomCtrl.Avalonia.Media;
using ClassroomCtrl.Shared.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using MessagePack;

namespace ClassroomCtrl.Student.Mac.Agent.ViewModels;

/// <summary>
/// Backing state for the fullscreen broadcast viewer. Decodes the teacher's screen stream via the shared
/// <see cref="H264DecoderWrapper"/> / <see cref="ScreenFrameDecoder"/> and exposes the current frame for an
/// Image. All methods run on the UI thread (the App marshals incoming IPC frames via Dispatcher).
///
/// Buffer reuse (the anti-GC-spike requirement): H.264 frames decode into a two-slot double buffer — each
/// frame reuses a <see cref="WriteableBitmap"/> instead of allocating one (only re-allocating on the first
/// frame or a resolution change). We always decode into the slot NOT currently on screen, then swap, so the
/// render thread never reads a bitmap mid-write. MJPEG (fallback) has no reusable buffer, so it disposes the
/// previous frame as it goes.
/// </summary>
public sealed partial class BroadcastViewModel : ObservableObject
{
    private H264DecoderWrapper? _h264;
    private readonly WriteableBitmap?[] _buffers = new WriteableBitmap?[2];   // double buffer (H.264)
    private int _idx;

    [ObservableProperty] private Bitmap? currentFrame;
    [ObservableProperty] private string statusText = "Waiting for the teacher's screen…";
    [ObservableProperty] private bool isWaiting = true;

    /// <summary>ScreenStreamStart — the teacher is about to share; show the waiting state.</summary>
    public void Begin()
    {
        IsWaiting = true;
        StatusText = "Waiting for the teacher's screen…";
    }

    /// <summary>A ScreenStreamFrame arrived (already on the UI thread). Decode + show.</summary>
    public void OnFrame(byte[] payload)
    {
        ScreenStreamFrameMessage frame;
        try { frame = MessagePackSerializer.Deserialize<ScreenStreamFrameMessage>(payload); }
        catch { return; }

        var shown = frame.Codec switch
        {
            VideoCodec.H264 => DecodeH264(frame),
            VideoCodec.Mjpeg => DecodeMjpeg(frame.FrameData),
            _ => null,
        };
        if (shown is null) return;   // waiting for a keyframe / undecodable delta — keep the last frame

        CurrentFrame = shown;
        IsWaiting = false;
        StatusText = $"Teacher's screen — {frame.Width}×{frame.Height}";
    }

    private Bitmap? DecodeH264(ScreenStreamFrameMessage frame)
    {
        if (!H264DecoderWrapper.IsSupported || frame.FrameData.Length == 0) return null;
        try
        {
            _h264 ??= new H264DecoderWrapper();
            var wb = _h264.TryDecodeInto(frame.FrameData, frame.IsKeyframe, _buffers[_idx]);
            if (wb is null) return null;
            _buffers[_idx] = wb;   // may be the reused slot or a freshly-allocated one (first/resolution change)
            _idx ^= 1;             // next frame targets the OTHER slot → never the one currently displayed
            return wb;
        }
        catch { return null; }
    }

    private Bitmap? DecodeMjpeg(byte[] data)
    {
        var bmp = ScreenFrameDecoder.DecodeMjpeg(data);
        if (bmp is null) return null;
        var old = CurrentFrame;
        if (!IsReuseBuffer(old)) old?.Dispose();   // dispose the previous MJPEG bitmap, never a reused H.264 slot
        return bmp;
    }

    /// <summary>ScreenStreamStop / disconnect — clear the view and free the decoder + buffers.</summary>
    public void Reset()
    {
        IsWaiting = true;
        StatusText = "Teacher stopped sharing";

        var c = CurrentFrame;
        CurrentFrame = null;
        if (!IsReuseBuffer(c)) c?.Dispose();
        _buffers[0]?.Dispose(); _buffers[1]?.Dispose();
        _buffers[0] = null; _buffers[1] = null; _idx = 0;

        _h264?.Dispose(); _h264 = null;
    }

    private bool IsReuseBuffer(Bitmap? b) => ReferenceEquals(b, _buffers[0]) || ReferenceEquals(b, _buffers[1]);
}
