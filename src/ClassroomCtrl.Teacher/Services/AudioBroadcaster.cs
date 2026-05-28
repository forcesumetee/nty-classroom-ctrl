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
///
/// Phase 11-C v2 Step 1 — data-driven pump.  The original pump woke on a fixed
/// <c>Task.Delay(100ms)</c> while reading 100 ms of mixer time per tick.  Under
/// CPU load each tick ran &gt; 100 ms wall, so the pump consumed audio slower
/// than the WASAPI capture sources produced it; the 2-second capture
/// BufferedWaveProvider saturated and the emitted audio fell ~2 s behind real
/// time (confirmed empirically by a pause-test: audio kept playing ~3 s after
/// the source stopped, independent of video codec).  The pump is now woken by
/// the capture clock itself — mic/loopback <c>DataAvailable</c> events
/// <c>Release</c> a SemaphoreSlim — and drains every full 100 ms frame already
/// buffered before going back to wait, so production tracks real wall-clock
/// time and stops drifting under load.  Capture buffer cap dropped 2 s → 300 ms
/// so it physically cannot hold seconds of stale audio.
/// </summary>
public class AudioBroadcaster : IDisposable
{
    private readonly ILogger<AudioBroadcaster> _logger;
    private readonly ControlServer _server;
    private readonly object _stateLock = new();

    /// <summary>Phase 11-C v2 Step 1 — bound on the capture-side
    /// BufferedWaveProvider depth.  Picked to be ~3× one frame so a transient
    /// pump stall doesn't immediately overflow, but well under the 2-second
    /// limit that allowed seconds of stale audio under the timer-based pump.
    /// If the data-driven pump is healthy, steady-state depth oscillates close
    /// to 0; a sustained value near this cap indicates the pump can't keep up
    /// (CPU-bound or downstream stall — a different bug to investigate).</summary>
    private const int CaptureBufferCapMs = 300;

    // Pump state
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private MixingSampleProvider? _mixer;
    private int _frameSeq;
    /// <summary>Phase 11-C v2 Step 1 — signaled by capture-source
    /// <c>DataAvailable</c> handlers; awaited by the pump loop in place of the
    /// old <c>Task.Delay(100ms)</c>.  Capacity 1 because the pump drains
    /// all available frames per wake — extra signals while draining are
    /// redundant and harmlessly swallowed by the TryRelease-and-ignore
    /// pattern in <see cref="SignalCaptureLocked"/>.</summary>
    private SemaphoreSlim? _captureSignal;

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
        // Phase 11-C v2 Step 1 — capture-clock signal.  Initial count 0:
        // pump blocks until a source delivers data.
        _captureSignal = new SemaphoreSlim(0, 1);
        _ = _server.BroadcastAudioStreamControlAsync(start: true, _cts.Token);
        _pumpTask = Task.Run(() => PumpLoopAsync(_cts.Token));
        _logger.LogInformation("Audio pump started ({Rate}Hz mono PCM, {Ms}ms frames, capture cap={Cap}ms)",
            TargetSampleRate, FrameDurationMs, CaptureBufferCapMs);
    }

    private void StopPumpLocked()
    {
        _cts?.Cancel();
        // Wake the pump out of its semaphore wait so cancellation takes effect
        // immediately.  Safe because the wait observes the cancellation token.
        try { _captureSignal?.Release(); } catch (SemaphoreFullException) { }
        try { _pumpTask?.Wait(2000); } catch { }
        _pumpTask = null;

        RemoveMicLocked();
        RemoveLoopbackLocked();
        _mixer = null;
        _captureSignal?.Dispose();
        _captureSignal = null;

        _ = _server.BroadcastAudioStreamControlAsync(start: false, CancellationToken.None);
        _logger.LogInformation("Audio pump stopped (sent {Frames} frames)", _frameSeq);
    }

    /// <summary>Phase 11-C v2 Step 1 — invoked from capture <c>DataAvailable</c>
    /// handlers.  TryRelease + swallow SemaphoreFullException: if the pump is
    /// already pending a wake, redundant signals are harmless (the pump drains
    /// every full frame already buffered on each wake, so an extra signal
    /// during a drain would just no-op the next wait).</summary>
    private void SignalCapture()
    {
        try { _captureSignal?.Release(); } catch (SemaphoreFullException) { }
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
                // Phase 11-C v2 Step 1 — 300 ms cap (was 2 s).  A healthy
                // data-driven pump keeps depth near 0; this cap exists only as
                // a safety bound against the catastrophic "audio falls seconds
                // behind real time" failure mode the old timer pump produced.
                BufferDuration = TimeSpan.FromMilliseconds(CaptureBufferCapMs),
                DiscardOnBufferOverflow = true,
            };
            _micCapture.DataAvailable += (_, e) =>
            {
                _micBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
                SignalCapture();
            };
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
                // Phase 11-C v2 Step 1 — same 300 ms safety cap as the mic
                // path.  WASAPI loopback fires DataAvailable roughly every
                // 10 ms with native-rate chunks, so 30 callbacks worth of
                // headroom is plenty against transient pump latency.
                BufferDuration = TimeSpan.FromMilliseconds(CaptureBufferCapMs),
                DiscardOnBufferOverflow = true,
            };
            _loopbackCapture.DataAvailable += (_, e) =>
            {
                _loopbackBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
                SignalCapture();
            };

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
                // Phase 11-C v2 Step 1 — wait for the capture clock instead of
                // a fixed Task.Delay.  Capture DataAvailable handlers release
                // the semaphore as soon as new audio is buffered; we wake,
                // drain every full frame that's already ready, then go back to
                // wait.  The 200 ms timeout is a safety net: it lets the loop
                // periodically observe cancellation and re-evaluate even if
                // signaling somehow gets dropped (or all sources go silent in
                // a way that suppresses DataAvailable on some hardware), but
                // it does NOT pace production — the capture clock does.
                try { await _captureSignal!.WaitAsync(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                var mixer = _mixer;
                if (mixer == null) break;

                // Drain every full 100 ms frame ALL active sources have
                // already produced.  A burst of catch-up samples is emitted
                // immediately rather than dribbled over 10 timer ticks, which
                // is the property that makes the pump track real time under
                // load instead of cumulatively slipping behind it.
                while (!ct.IsCancellationRequested && HasFullFrameBuffered())
                {
                    int read = mixer.Read(sampleBuf, 0, SamplesPerFrame);
                    if (read <= 0) break;

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
        }
    }

    /// <summary>Phase 11-C v2 Step 1 — true when every currently-active capture
    /// source has buffered at least one full 100 ms frame.  The pump uses this
    /// to drain catch-up bursts in one wake rather than across many ticks, and
    /// to avoid reading silence-padded frames from the mixer (which would let
    /// the timer drift back in through the back door).  Reads volatile
    /// references without the state lock: a brief race with Remove*Locked
    /// nulling a buffer is benign because BufferedDuration is safe to query on
    /// a live BufferedWaveProvider and the next call will observe the null.</summary>
    private bool HasFullFrameBuffered()
    {
        bool any = false;
        double minMs = double.MaxValue;

        if (_micEnabled)
        {
            var buf = _micBuffer;
            if (buf == null) return false;
            minMs = Math.Min(minMs, buf.BufferedDuration.TotalMilliseconds);
            any = true;
        }
        if (_systemAudioEnabled)
        {
            var buf = _loopbackBuffer;
            if (buf == null) return false;
            minMs = Math.Min(minMs, buf.BufferedDuration.TotalMilliseconds);
            any = true;
        }

        return any && minMs >= FrameDurationMs;
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
