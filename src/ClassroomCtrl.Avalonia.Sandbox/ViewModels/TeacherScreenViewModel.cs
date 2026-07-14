using Avalonia.Media.Imaging;
using ClassroomCtrl.Avalonia.Media;
using ClassroomCtrl.Shared.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using MessagePack;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// TT-8-D — displays the teacher's shared screen (the ScreenStreamFrame broadcast). Decodes via the
/// shared <see cref="ScreenFrameDecoder"/> (Media) — MJPEG today, H.264-ready. Dispatch runs on the
/// UI thread (WireClient events are Post-marshaled in ConnectionViewModel), so decode happens on the
/// UI thread — fine at the shipped ~2 fps. Each frame is a fresh Bitmap; the previous is disposed.
/// </summary>
public partial class TeacherScreenViewModel : ObservableObject
{
    private readonly ScreenFrameDecoder _decoder = new();
    private Bitmap? _prev;

    [ObservableProperty] private Bitmap? currentFrame;
    [ObservableProperty] private string statusText = "Waiting for the teacher's screen…";
    [ObservableProperty] private bool isReceiving;

    /// <summary>ScreenStreamStart arrived — the teacher is about to share.</summary>
    public void Begin()
    {
        IsReceiving = true;
        StatusText = "Waiting for the teacher's screen…";
    }

    /// <summary>A ScreenStreamFrame arrived (already on the UI thread). Decode + show.</summary>
    public void OnFrame(byte[] payload)
    {
        try
        {
            var frame = MessagePackSerializer.Deserialize<ScreenStreamFrameMessage>(payload);
            var bmp = _decoder.Decode(frame);
            if (bmp is null) return;   // waiting for a keyframe / undecodable delta — keep the last frame
            _prev?.Dispose();
            _prev = CurrentFrame;
            CurrentFrame = bmp;
            IsReceiving = true;
            StatusText = $"Teacher's screen — {frame.Width}×{frame.Height}";
        }
        catch { /* one bad frame never tears down the view */ }
    }

    /// <summary>ScreenStreamStop (or disconnect) — clear the view and free the decoder promptly.
    /// The decoder is reusable after Dispose (it recreates its native session lazily on the next
    /// share's keyframe), so a later share re-displays cleanly.</summary>
    public void Reset()
    {
        IsReceiving = false;
        StatusText = "Teacher stopped sharing";
        var c = CurrentFrame; CurrentFrame = null; c?.Dispose();
        _prev?.Dispose(); _prev = null;
        _decoder.Dispose();
    }
}
