using System;
using System.Threading;
using System.Threading.Tasks;
using ClassroomCtrl.Shared.Protocol;
using MessagePack;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

/// <summary>
/// Phase 27-C — bridges native JPEG capture (<see cref="ScreenCaptureService"/>) to the
/// wire (<see cref="WireClient"/>): on the Teacher's StudentStreamStart, capture the
/// main display as JPEG and send each frame as a StudentStreamFrame carrying a
/// <see cref="ScreenStreamFrameMessage"/> (Codec=Mjpeg). Uses ONLY existing envelopes —
/// no wire change. Mirrors the shipped StudentBroadcaster (1280×720, Q60, low fps).
/// </summary>
public sealed class ScreenStreamer
{
    // MJPEG params (27-C) — match the shipped student's neighborhood.
    private const int Fps = 6;
    private const int Quality = 60;
    private const int MaxW = 1280;
    private const int MaxH = 720;
    // H.264 params (27-B) — 1080p is fixed in the native encoder; match shipped bitrate.
    private const int H264Fps = 10;
    private const int H264BitrateKbps = 1500;

    private readonly ScreenCaptureService _capture = new();
    private WireClient? _client;
    private int _seq;

    public bool IsStreaming { get; private set; }
    public VideoCodec Codec { get; private set; } = VideoCodec.Mjpeg;

    /// <summary>Raised (native thread) after a frame is sent — carries the running seq.</summary>
    public event Action<int>? FrameSent;

    /// <summary>Start streaming to <paramref name="client"/> in the requested codec.
    /// H.264 (27-B) or MJPEG (27-C, fallback). Returns the native start code (0=ok).</summary>
    public async Task<int> StartAsync(WireClient client, VideoCodec codec)
    {
        if (IsStreaming) return 0;
        _client = client;
        _seq = 0;
        Codec = codec;

        int rc;
        if (codec == VideoCodec.H264)
        {
            _capture.H264FrameReceived += OnH264;
            rc = await _capture.StartH264Async(H264Fps, H264BitrateKbps);
            if (rc != 0) { _capture.H264FrameReceived -= OnH264; _client = null; return rc; }
        }
        else
        {
            _capture.JpegFrameReceived += OnJpeg;
            rc = await _capture.StartJpegAsync(Fps, Quality, MaxW, MaxH);
            if (rc != 0) { _capture.JpegFrameReceived -= OnJpeg; _client = null; return rc; }
        }
        IsStreaming = true;
        return 0;
    }

    public async Task StopAsync()
    {
        if (!IsStreaming) return;
        _capture.JpegFrameReceived -= OnJpeg;
        _capture.H264FrameReceived -= OnH264;
        await _capture.StopAsync();
        IsStreaming = false;
        _client = null;
    }

    // native delivery threads
    private void OnJpeg(byte[] jpeg, int width, int height)
        => SendFrame(jpeg, width, height, VideoCodec.Mjpeg, keyframe: true);
    private void OnH264(byte[] nal, int width, int height, bool keyframe)
        => SendFrame(nal, width, height, VideoCodec.H264, keyframe);

    private void SendFrame(byte[] data, int width, int height, VideoCodec codec, bool keyframe)
    {
        var client = _client;
        if (client == null) return;

        var msg = new ScreenStreamFrameMessage
        {
            FrameData = data,
            Width = width,
            Height = height,
            TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FrameSeq = Interlocked.Increment(ref _seq),
            Codec = codec,
            IsKeyframe = keyframe,
        };
        var payload = MessagePackSerializer.Serialize(msg);
        _ = SendSafeAsync(client, payload, msg.FrameSeq);
    }

    private async Task SendSafeAsync(WireClient client, byte[] payload, int seq)
    {
        try
        {
            await client.SendAsync(MessageType.StudentStreamFrame, payload, CancellationToken.None);
            FrameSent?.Invoke(seq);
        }
        catch
        {
            // Link dropped mid-stream — the reconnect loop handles it; drop this frame.
        }
    }
}
