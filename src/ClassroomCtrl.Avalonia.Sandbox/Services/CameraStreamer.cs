using System;
using System.Threading;
using System.Threading.Tasks;
using ClassroomCtrl.Shared.Protocol;
using MessagePack;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

/// <summary>
/// Phase 28-D — bridges native camera JPEG capture (<see cref="CameraCaptureService"/>)
/// to the wire (<see cref="WireClient"/>) as a Conference Mode peer-cam stream.
///
/// Emits the existing Conference-camera envelope family — NO wire change:
///   ConferenceCameraStart (0x0680) once on start (SessionId + SourceEndpointId + name),
///   ConferenceCameraFrame (0x0681) per frame (raw JPEG, no codec field),
///   ConferenceCameraStop  (0x0682) on stop.
/// The shipped Teacher routes frames to the matching gallery tile by
/// SourceEndpointId (== this client's EndpointId / Envelope.SenderId) and flips
/// IsCamLive. 320×240 @ 10 fps Q70 matches the shipped Windows student exactly.
/// </summary>
public sealed class CameraStreamer
{
    // Shipped peer-cam format (Windows student: AForge 320×240, ~10 fps, Q70).
    private const int CamWidth = 320, CamHeight = 240, CamFps = 10, CamQuality = 70;

    private readonly CameraCaptureService _camera = new();
    private WireClient? _client;
    private Guid _selfId;
    private int _deviceIndex;
    private int _seq;

    public bool IsStreaming { get; private set; }

    /// <summary>Raised (native thread) after a frame is sent — carries the running seq.</summary>
    public event Action<int>? FrameSent;

    /// <summary>Start the peer-cam stream: send ConferenceCameraStart, then capture the
    /// selected camera as JPEG and send each frame as a ConferenceCameraFrame. Returns
    /// the native start code (0=ok); on native failure, no Start signal lingers because
    /// it is sent AFTER a successful capture start.</summary>
    public async Task<int> StartAsync(WireClient client, Guid selfId, Guid sessionId,
                                      string sourceName, int deviceIndex = 0)
    {
        if (IsStreaming) return 0;
        _client = client;
        _selfId = selfId;
        _deviceIndex = deviceIndex;
        _seq = 0;

        _camera.JpegFrameReceived += OnJpeg;
        int rc = await _camera.StartAsync(deviceIndex, CamWidth, CamHeight, CamFps, CamQuality);
        if (rc != 0)
        {
            _camera.JpegFrameReceived -= OnJpeg;
            _client = null;
            return rc;
        }

        // Announce the stream so the Teacher labels the tile (frames alone would also
        // light it up, but Start carries the name + session for correctness).
        var start = new ConferenceCameraStartMessage
        {
            SessionId = sessionId,
            SourceEndpointId = selfId,
            SourceName = sourceName,
            Width = CamWidth,
            Height = CamHeight,
            Fps = CamFps,
        };
        await SendReliableAsync(MessageType.ConferenceCameraStart, MessagePackSerializer.Serialize(start));

        IsStreaming = true;
        return 0;
    }

    public async Task StopAsync()
    {
        if (!IsStreaming) return;
        _camera.JpegFrameReceived -= OnJpeg;
        await _camera.StopAsync();

        var client = _client;
        var selfId = _selfId;
        IsStreaming = false;

        if (client != null)
        {
            // Best-effort: on a normal stop the Teacher clears the tile from this;
            // on a disconnect the socket may already be dead (send throws) — the
            // Teacher's PeerDisconnected handler fires an implicit stop anyway.
            try
            {
                var stop = new ConferenceCameraStopMessage { SourceEndpointId = selfId };
                await SendReliableAsync(MessageType.ConferenceCameraStop,
                                        MessagePackSerializer.Serialize(stop), client);
            }
            catch { /* link already gone — implicit stop covers it */ }
        }
        _client = null;
    }

    // native delivery thread
    private void OnJpeg(byte[] jpeg, int width, int height)
    {
        var client = _client;
        if (client == null) return;

        var msg = new ConferenceCameraFrameMessage
        {
            SourceEndpointId = _selfId,
            JpegData = jpeg,
            TimestampMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var payload = MessagePackSerializer.Serialize(msg);
        int seq = Interlocked.Increment(ref _seq);
        _ = SendFrameSafeAsync(client, payload, seq);
    }

    private async Task SendFrameSafeAsync(WireClient client, byte[] payload, int seq)
    {
        try
        {
            await client.SendAsync(MessageType.ConferenceCameraFrame, payload, CancellationToken.None);
            FrameSent?.Invoke(seq);
        }
        catch
        {
            // Link dropped mid-stream — the reconnect loop handles it; drop this frame.
        }
    }

    private Task SendReliableAsync(MessageType type, byte[] payload, WireClient? client = null)
    {
        client ??= _client;
        if (client == null) return Task.CompletedTask;
        return client.SendAsync(type, payload, CancellationToken.None);
    }
}
