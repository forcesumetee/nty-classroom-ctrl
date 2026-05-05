using ClassroomCtrl.Shared.Protocol;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 4 Part 3b: Captures the student's microphone and sends PCM frames upstream
/// (Agent → Service → Teacher). Started when student clicks "Unmute Mic".
///
/// Pipeline:
///   Mic (16kHz/16-bit/mono if supported, else native + resample)
///   → 100ms chunks (1600 samples = 3200 bytes) → IPC → Service → TCP → Teacher
///
/// On the wire: MessageType.StudentAudioStreamFrame, payload = AudioStreamFrameMessage.
/// </summary>
public class StudentAudioBroadcaster : IDisposable
{
    private WaveInEvent? _micCapture;
    private BufferedWaveProvider? _micBuffer;
    private ISampleProvider? _outputSampleProvider;

    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private int _frameSeq;

    public const int TargetSampleRate = 16000;
    public const int TargetChannels = 1;
    public const int FrameDurationMs = 100;
    public const int SamplesPerFrame = TargetSampleRate * FrameDurationMs / 1000; // 1600
    public const int BytesPerFrame = SamplesPerFrame * 2;                          // 3200

    /// <summary>True if mic device exists and capture has started.</summary>
    public bool IsRunning => _pumpTask is { IsCompleted: false };

    /// <summary>Returns false if no mic device is available — caller should not toggle on.</summary>
    public static bool HasMicrophone() => WaveInEvent.DeviceCount > 0;

    /// <summary>
    /// Send Start signal first, begin capture. Caller is expected to also send Stop on toggle off.
    /// </summary>
    public void Start()
    {
        if (IsRunning) return;

        _cts = new CancellationTokenSource();
        _frameSeq = 0;

        if (!TryStartMic())
        {
            IpcClient.LogToFile("[StudentAudioBroadcaster] No mic — aborting Start");
            return;
        }

        // Notify teacher to register a buffer for me
        _ = SendControlAsync(MessageType.StudentAudioStreamStart, _cts.Token);

        _pumpTask = Task.Run(() => PumpLoopAsync(_cts.Token));
        IpcClient.LogToFile($"[StudentAudioBroadcaster] Started ({TargetSampleRate}Hz mono PCM, {FrameDurationMs}ms frames)");
    }

    public void Stop()
    {
        if (!IsRunning) return;

        _cts?.Cancel();
        try { _pumpTask?.Wait(1500); } catch { }
        _pumpTask = null;

        try { _micCapture?.StopRecording(); } catch { }
        _micCapture?.Dispose();
        _micCapture = null;
        _micBuffer = null;
        _outputSampleProvider = null;

        // Tell teacher to unregister my buffer (best effort)
        try { _ = SendControlAsync(MessageType.StudentAudioStreamStop, CancellationToken.None); } catch { }

        IpcClient.LogToFile($"[StudentAudioBroadcaster] Stopped (sent {_frameSeq} frames)");
    }

    private bool TryStartMic()
    {
        if (!HasMicrophone()) return false;

        // Try native 16kHz mono 16-bit first; fall back to 44.1kHz mono and resample.
        WaveFormat? format = null;
        Exception? lastErr = null;

        foreach (var candidate in new[]
        {
            new WaveFormat(TargetSampleRate, 16, 1),
            new WaveFormat(44100, 16, 1),
            new WaveFormat(48000, 16, 1),
        })
        {
            try
            {
                _micCapture = new WaveInEvent
                {
                    WaveFormat = candidate,
                    BufferMilliseconds = 50,
                };
                _micCapture.StartRecording();
                _micCapture.StopRecording();
                _micCapture.Dispose();
                format = candidate;
                break;
            }
            catch (Exception ex)
            {
                lastErr = ex;
                _micCapture?.Dispose();
                _micCapture = null;
            }
        }

        if (format == null)
        {
            IpcClient.LogToFile($"[StudentAudioBroadcaster] No supported mic format: {lastErr?.Message}");
            return false;
        }

        try
        {
            _micCapture = new WaveInEvent
            {
                WaveFormat = format,
                BufferMilliseconds = 50,
            };
            _micBuffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
            };
            _micCapture.DataAvailable += (_, e) =>
            {
                _micBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
            };

            ISampleProvider sp = _micBuffer.ToSampleProvider();
            if (sp.WaveFormat.SampleRate != TargetSampleRate)
                sp = new WdlResamplingSampleProvider(sp, TargetSampleRate);
            _outputSampleProvider = sp;

            _micCapture.StartRecording();
            IpcClient.LogToFile($"[StudentAudioBroadcaster] Mic started: {format.SampleRate}Hz/{format.BitsPerSample}-bit/{format.Channels}ch");
            return true;
        }
        catch (Exception ex)
        {
            IpcClient.LogToFile($"[StudentAudioBroadcaster] Mic init failed: {ex.Message}");
            _micCapture?.Dispose();
            _micCapture = null;
            _micBuffer = null;
            _outputSampleProvider = null;
            return false;
        }
    }

    private async Task PumpLoopAsync(CancellationToken ct)
    {
        var sampleBuf = new float[SamplesPerFrame];
        var pcmBuf = new byte[BytesPerFrame];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var sp = _outputSampleProvider;
                if (sp == null) break;

                int read = sp.Read(sampleBuf, 0, SamplesPerFrame);
                if (read > 0 && App.Ipc != null)
                {
                    int byteCount = read * 2;
                    for (int i = 0; i < read; i++)
                    {
                        var s = sampleBuf[i];
                        if (s > 1.0f) s = 1.0f;
                        else if (s < -1.0f) s = -1.0f;
                        short pcm = (short)(s * 32767f);
                        pcmBuf[i * 2] = (byte)(pcm & 0xFF);
                        pcmBuf[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
                    }

                    var data = new byte[byteCount];
                    Buffer.BlockCopy(pcmBuf, 0, data, 0, byteCount);

                    _frameSeq++;
                    var frame = new AudioStreamFrameMessage
                    {
                        PcmData = data,
                        SampleRate = TargetSampleRate,
                        Channels = TargetChannels,
                        BitsPerSample = 16,
                        TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        FrameSeq = _frameSeq,
                    };
                    var bytes = MessagePack.MessagePackSerializer.Serialize(frame);
                    var env = Envelope.Create(MessageType.StudentAudioStreamFrame, bytes, Guid.Empty);
                    await App.Ipc.SendAsync(env, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                IpcClient.LogToFile($"[StudentAudioBroadcaster] frame error: {ex.Message}");
            }

            try { await Task.Delay(FrameDurationMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task SendControlAsync(MessageType type, CancellationToken ct)
    {
        if (App.Ipc == null) return;
        var env = Envelope.Create(type, Array.Empty<byte>(), Guid.Empty);
        await App.Ipc.SendAsync(env, ct);
    }

    public void Dispose()
    {
        Stop();
        _cts?.Dispose();
    }
}
