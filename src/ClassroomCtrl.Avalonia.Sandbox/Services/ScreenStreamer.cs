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
    // Match the shipped student's neighborhood; 6 fps is a smooth-enough demo.
    private const int Fps = 6;
    private const int Quality = 60;
    private const int MaxW = 1280;
    private const int MaxH = 720;

    private readonly ScreenCaptureService _capture = new();
    private WireClient? _client;
    private int _seq;

    public bool IsStreaming { get; private set; }

    /// <summary>Raised (native thread) after a frame is sent — carries the running seq.</summary>
    public event Action<int>? FrameSent;

    /// <summary>Start streaming to <paramref name="client"/>. The Teacher decodes by the
    /// frame's own Codec field, so we always send Mjpeg (JPEG) regardless of the
    /// requested codec — H.264 is Phase 27-B. Returns the native start code (0=ok).</summary>
    public async Task<int> StartAsync(WireClient client)
    {
        if (IsStreaming) return 0;
        _client = client;
        _seq = 0;
        _capture.JpegFrameReceived += OnJpeg;
        int rc = await _capture.StartJpegAsync(Fps, Quality, MaxW, MaxH);
        if (rc != 0) { _capture.JpegFrameReceived -= OnJpeg; _client = null; return rc; }
        IsStreaming = true;
        return 0;
    }

    public async Task StopAsync()
    {
        if (!IsStreaming) return;
        _capture.JpegFrameReceived -= OnJpeg;
        await _capture.StopAsync();
        IsStreaming = false;
        _client = null;
    }

    private void OnJpeg(byte[] jpeg, int width, int height) // native delivery thread
    {
        var client = _client;
        if (client == null) return;

        var msg = new ScreenStreamFrameMessage
        {
            FrameData = jpeg,
            Width = width,
            Height = height,
            TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FrameSeq = Interlocked.Increment(ref _seq),
            Codec = VideoCodec.Mjpeg,
            IsKeyframe = true, // every MJPEG frame is independently decodable
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
