using System;
using System.Threading;
using System.Threading.Tasks;
using ClassroomCtrl.Shared.Protocol;
using MessagePack;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

/// <summary>
/// Phase 29-E — bridges native mic capture (<see cref="AudioCaptureService"/>) to the
/// wire as a Student→Teacher talkback stream (path B), triggered by the Teacher's Mic
/// Monitor "Listen". Emits ONLY existing envelopes — no wire change:
///   StudentAudioStreamStart (0x032B) once on start (empty payload),
///   StudentAudioStreamFrame (0x032C) per 100 ms frame (AudioStreamFrameMessage, raw PCM),
///   StudentAudioStreamStop  (0x032D) on stop,
/// plus a MicStateUpdate (0x0642) so the Teacher's per-student mic indicator lights.
/// The shipped Teacher's StudentAudioMixer is hard-fixed at 16 kHz mono 16-bit, so we
/// send exactly that.
/// </summary>
public sealed class AudioStreamer
{
    private const int SampleRate = 16000, Channels = 1, BitsPerSample = 16;

    private readonly AudioCaptureService _audio = new();
    private WireClient? _client;
    private Guid _selfId;
    private int _seq;

    public bool IsStreaming { get; private set; }

    /// <summary>Raised (native thread) after a frame is sent — carries the running seq.</summary>
    public event Action<int>? FrameSent;

    /// <summary>Start mic talkback to the Teacher: send StudentAudioStreamStart, then
    /// stream each PCM frame as StudentAudioStreamFrame. Returns the native start code
    /// (0=ok); Start is sent AFTER a successful capture start (no dangling Start on failure).</summary>
    public async Task<int> StartAsync(WireClient client, Guid selfId)
    {
        if (IsStreaming) return 0;
        _client = client;
        _selfId = selfId;
        _seq = 0;

        _audio.PcmFrameReceived += OnPcm;
        int rc = await _audio.StartAsync(SampleRate, Channels);
        if (rc != 0)
        {
            _audio.PcmFrameReceived -= OnPcm;
            _client = null;
            return rc;
        }

        await SendReliableAsync(MessageType.StudentAudioStreamStart, Array.Empty<byte>());
        await SendMicStateAsync(micLive: true);

        IsStreaming = true;
        return 0;
    }

    public async Task StopAsync()
    {
        if (!IsStreaming) return;
        _audio.PcmFrameReceived -= OnPcm;
        await _audio.StopAsync();

        var client = _client;
        IsStreaming = false;

        if (client != null)
        {
            // Best-effort: on a normal stop the Teacher's StudentAudioMixer removes us;
            // on a disconnect the socket may be dead (send throws) — the Teacher's
            // PeerDisconnected handler covers it.
            try
            {
                await SendMicStateAsync(micLive: false, client);
                await SendReliableAsync(MessageType.StudentAudioStreamStop, Array.Empty<byte>(), client);
            }
            catch { /* link already gone */ }
        }
        _client = null;
    }

    // native real-time thread
    private void OnPcm(byte[] pcm, int sampleRate, int channels)
    {
        var client = _client;
        if (client == null) return;

        var msg = new AudioStreamFrameMessage
        {
            PcmData = pcm,
            SampleRate = SampleRate,
            Channels = Channels,
            BitsPerSample = BitsPerSample,
            TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FrameSeq = Interlocked.Increment(ref _seq),
        };
        var payload = MessagePackSerializer.Serialize(msg);
        _ = SendFrameSafeAsync(client, payload, msg.FrameSeq);
    }

    private async Task SendFrameSafeAsync(WireClient client, byte[] payload, int seq)
    {
        try
        {
            await client.SendAsync(MessageType.StudentAudioStreamFrame, payload, CancellationToken.None);
            FrameSent?.Invoke(seq);
        }
        catch
        {
            // Link dropped mid-stream — the reconnect loop handles it; drop this frame.
        }
    }

    private Task SendMicStateAsync(bool micLive, WireClient? client = null)
    {
        var msg = new MicStateUpdateMessage
        {
            EndpointId = _selfId,
            MicLive = micLive,
            PttMode = false,      // always-on while the Teacher is monitoring
            IsSpeaking = false,
        };
        return SendReliableAsync(MessageType.MicStateUpdate, MessagePackSerializer.Serialize(msg), client);
    }

    private Task SendReliableAsync(MessageType type, byte[] payload, WireClient? client = null)
    {
        client ??= _client;
        if (client == null) return Task.CompletedTask;
        return client.SendAsync(type, payload, CancellationToken.None);
    }
}
