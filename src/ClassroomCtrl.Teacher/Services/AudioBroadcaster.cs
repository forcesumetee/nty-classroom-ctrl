using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 4 Part 3a + Phase 5 Task 3 (split sources): Two independent toggles —
/// <see cref="MicEnabled"/> for the teacher's microphone and
/// <see cref="SystemAudioEnabled"/> for WASAPI loopback ("Share Computer Audio").
/// Either source can be enabled or disabled live; the pump and Stream-Start /
/// Stream-Stop control messages are managed automatically based on whether any
/// source is active.
///
/// Pipeline (active sources only):
///   Mic (16 kHz / 16-bit / mono)             ┐
///                                              ├→ MixingSampleProvider (16 kHz mono float)
///   Loopback (native rate → 16 kHz mono)     ┘
///   → 100 ms read (1600 samples) → PCM 16-bit → TCP broadcast.
/// </summary>
public class AudioBroadcaster : IDisposable
{
    private readonly ILogger<AudioBroadcaster> _logger;
    private readonly ControlServer _server;
    private readonly object _stateLock = new();

    // Pump state
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private MixingSampleProvider? _mixer;
    private int _frameSeq;

    // Mic source
    private WaveInEvent? _micCapture;
    private BufferedWaveProvider? _micBuffer;
    private ISampleProvider? _micMixerInput;
    private bool _micEnabled;

    // Loopback (system audio) source
    private WasapiLoopbackCapture? _loopbackCapture;
    private BufferedWaveProvider? _loopbackBuffer;
    private ISampleProvider? _loopbackMixerInput;
    private bool _systemAudioEnabled;

    public const int TargetSampleRate = 16000;
    public const int TargetChannels = 1;
    public const int FrameDurationMs = 100;
    public const int SamplesPerFrame = TargetSampleRate * FrameDurationMs / 1000; // 1600
    public const int BytesPerFrame = SamplesPerFrame * 2;                          // 3200

    public bool IsRunning => _pumpTask is { IsCompleted: false };

    /// <summary>Phase 5a: Fired after each PCM frame is built — used by RecordingService to capture audio to disk.</summary>
    public event EventHandler<(byte[] Pcm, int SampleRate, int Channels, int BitsPerSample)>? AudioFrameProduced;

    public AudioBroadcaster(ILogger<AudioBroadcaster> logger, ControlServer server)
    {
        _logger = logger;
        _server = server;
    }

    /// <summary>Toggle the microphone source. Live — adds/removes from mixer without restarting the broadcast.</summary>
    public bool MicEnabled
    {
        get { lock (_stateLock) return _micEnabled; }
        set
        {
            lock (_stateLock)
            {
                if (_micEnabled == value) return;
                _micEnabled = value;
                ApplyStateLocked();
            }
        }
    }

    /// <summary>Toggle WASAPI loopback ("Share Computer Audio"). Live — adds/removes from mixer.</summary>
    public bool SystemAudioEnabled
    {
        get { lock (_stateLock) return _systemAudioEnabled; }
        set
        {
            lock (_stateLock)
            {
                if (_systemAudioEnabled == value) return;
                _systemAudioEnabled = value;
                ApplyStateLocked();
            }
        }
    }

    private void ApplyStateLocked()
    {
        bool wantPump = _micEnabled || _systemAudioEnabled;
        bool isRunning = IsRunning;

        if (wantPump && !isRunning)
        {
            StartPumpLocked();
        }

        if (IsRunning)
        {
            if (_micEnabled && _micMixerInput == null) AddMicLocked();
            else if (!_micEnabled && _micMixerInput != null) RemoveMicLocked();

            if (_systemAudioEnabled && _loopbackMixerInput == null) AddLoopbackLocked();
            else if (!_systemAudioEnabled && _loopbackMixerInput != null) RemoveLoopbackLocked();
        }

        if (!wantPump && isRunning)
        {
            StopPumpLocked();
        }
    }

    private void StartPumpLocked()
    {
        _cts = new CancellationTokenSource();
        _frameSeq = 0;
        var fmt = WaveFormat.CreateIeeeFloatWaveFormat(TargetSampleRate, TargetChannels);
        _mixer = new MixingSampleProvider(fmt) { ReadFully = true };
        _ = _server.BroadcastAudioStreamControlAsync(start: true, _cts.Token);
        _pumpTask = Task.Run(() => PumpLoopAsync(_cts.Token));
        _logger.LogInformation("Audio pump started ({Rate}Hz mono PCM, {Ms}ms frames)",
            TargetSampleRate, FrameDurationMs);
    }

    private void StopPumpLocked()
    {
        _cts?.Cancel();
        try { _pumpTask?.Wait(2000); } catch { }
        _pumpTask = null;

        RemoveMicLocked();
        RemoveLoopbackLocked();
        _mixer = null;

        _ = _server.BroadcastAudioStreamControlAsync(start: false, CancellationToken.None);
        _logger.LogInformation("Audio pump stopped (sent {Frames} frames)", _frameSeq);
    }

    private void AddMicLocked()
    {
        try
        {
            if (WaveInEvent.DeviceCount <= 0)
            {
                _logger.LogWarning("No microphone device available");
                return;
            }

            var fmt = new WaveFormat(TargetSampleRate, 16, 1);
            _micCapture = new WaveInEvent { WaveFormat = fmt, BufferMilliseconds = 50 };
            _micBuffer = new BufferedWaveProvider(fmt)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
            };
            _micCapture.DataAvailable += (_, e) => _micBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
            _micMixerInput = new VolumeSampleProvider(_micBuffer.ToSampleProvider()) { Volume = 0.5f };
            _mixer!.AddMixerInput(_micMixerInput);
            _micCapture.StartRecording();
            _logger.LogInformation("Mic source added: {Rate}Hz", fmt.SampleRate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mic source start failed");
            DisposeMicLocked();
        }
    }

    private void RemoveMicLocked()
    {
        if (_micMixerInput != null)
        {
            try { _mixer?.RemoveMixerInput(_micMixerInput); } catch { }
            _micMixerInput = null;
        }
        DisposeMicLocked();
    }

    private void DisposeMicLocked()
    {
        try { _micCapture?.StopRecording(); } catch { }
        _micCapture?.Dispose(); _micCapture = null;
        _micBuffer = null;
    }

    private void AddLoopbackLocked()
    {
        try
        {
            _loopbackCapture = new WasapiLoopbackCapture();
            var native = _loopbackCapture.WaveFormat;
            _loopbackBuffer = new BufferedWaveProvider(native)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
            };
            _loopbackCapture.DataAvailable += (_, e) => _loopbackBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);

            ISampleProvider sp = _loopbackBuffer.ToSampleProvider();
            if (sp.WaveFormat.Channels > 1) sp = sp.ToMono();
            if (sp.WaveFormat.SampleRate != TargetSampleRate)
                sp = new WdlResamplingSampleProvider(sp, TargetSampleRate);

            _loopbackMixerInput = new VolumeSampleProvider(sp) { Volume = 0.5f };
            _mixer!.AddMixerInput(_loopbackMixerInput);
            _loopbackCapture.StartRecording();
            _logger.LogInformation("Loopback source added: {Rate}Hz native", native.SampleRate);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Loopback source start failed");
            DisposeLoopbackLocked();
        }
    }

    private void RemoveLoopbackLocked()
    {
        if (_loopbackMixerInput != null)
        {
            try { _mixer?.RemoveMixerInput(_loopbackMixerInput); } catch { }
            _loopbackMixerInput = null;
        }
        DisposeLoopbackLocked();
    }

    private void DisposeLoopbackLocked()
    {
        try { _loopbackCapture?.StopRecording(); } catch { }
        _loopbackCapture?.Dispose(); _loopbackCapture = null;
        _loopbackBuffer = null;
    }

    private async Task PumpLoopAsync(CancellationToken ct)
    {
        var sampleBuf = new float[SamplesPerFrame];
        var pcmBuf = new byte[BytesPerFrame];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var mixer = _mixer;
                if (mixer == null) break;

                int read = mixer.Read(sampleBuf, 0, SamplesPerFrame);
                if (read > 0)
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

                    // Phase 5a: tee to recording before broadcasting.
                    try { AudioFrameProduced?.Invoke(this, (data, TargetSampleRate, TargetChannels, 16)); }
                    catch (Exception ex) { _logger.LogWarning(ex, "AudioFrameProduced subscriber threw"); }

                    _frameSeq++;
                    var msg = new AudioStreamFrameMessage
                    {
                        PcmData = data,
                        SampleRate = TargetSampleRate,
                        Channels = TargetChannels,
                        BitsPerSample = 16,
                        TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        FrameSeq = _frameSeq,
                    };
                    await _server.BroadcastAudioFrameAsync(msg, ct);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Audio pump frame failed");
            }

            try { await Task.Delay(FrameDurationMs, ct); }
            catch (OperationCanceledException) { break; }
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            _micEnabled = false;
            _systemAudioEnabled = false;
            if (IsRunning) StopPumpLocked();
        }
        _cts?.Dispose();
    }
}
