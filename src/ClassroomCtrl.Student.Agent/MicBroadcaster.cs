using ClassroomCtrl.Shared.Protocol;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 13-D (Tier 3) — student-side mic capture for per-group voice chat.
/// Mirrors the teacher's <c>AudioBroadcaster</c> pump shape but with a single
/// source (the student's mic) and the Tier 3 wire shape (16 kHz mono 16-bit
/// PCM, 100 ms frames → <c>VoiceAudioFrameMessage</c> with
/// <c>Envelope.TargetGroupId</c> set to the student's current room).
///
/// Capture device: the default <c>Communications</c>-role capture endpoint —
/// engaging this endpoint role activates Windows' built-in AEC + noise
/// suppression in the audio engine pipeline on supported drivers.  This is
/// Layer 2 of the 3-layer AEC strategy in
/// docs/breakout-rooms-tier3-design.md §5; the Step 0 AEC spike confirmed
/// this works on the dev box (Δ between baseline / loopback ≈ +1.5 dBFS mean).
///
/// Capture format is NOT guaranteed to be 16 kHz mono 16-bit — it negotiates
/// the device's mix format (commonly 48 kHz stereo float).  A resample +
/// downmix + sample-format convert stage produces the Tier 3 wire format.
///
/// Lifecycle (set by <see cref="MainWindow"/>):
///   IsMuted = true (default) → no capture device opened
///   IsMuted = false + PttMode = true + IsPttDown = false → device open,
///                                                          frames dropped
///   IsMuted = false + PttMode = true + IsPttDown = true  → device open,
///                                                          frames emitting
///   IsMuted = false + PttMode = false                    → device open,
///                                                          frames always
///                                                          emitting
/// MicStateUpdate heartbeat fires every 1.5 s OR on any state transition so
/// the teacher's per-student mic indicator stays current.
/// </summary>
public sealed class MicBroadcaster : IDisposable
{
    public const int TargetSampleRate = 16000;
    public const int TargetChannels = 1;
    public const int FrameDurationMs = 100;
    public const int SamplesPerFrame = TargetSampleRate * FrameDurationMs / 1000; // 1600
    public const int BytesPerFrame = SamplesPerFrame * 2;                          // 3200

    /// <summary>Mirror of AudioBroadcaster's 11-C v2 Step 1 safety cap.  A
    /// healthy data-driven pump keeps capture buffers near 0; this cap exists
    /// only as a bound against the "audio falls seconds behind real time"
    /// failure mode if the pump can't keep up.</summary>
    private const int CaptureBufferCapMs = 300;

    /// <summary>VAD threshold (linear amplitude RMS over a 100 ms frame).
    /// Maps to roughly -40 dBFS.  Tier 3 §7 R7 — first cut, no per-session
    /// calibration; refine in a polish round.</summary>
    private const float VadRmsThreshold = 0.01f;

    private readonly object _stateLock = new();
    private CancellationTokenSource? _cts;
    private Task? _pumpTask;
    private Task? _heartbeatTask;
    private SemaphoreSlim? _captureSignal;

    private WasapiCapture? _capture;
    private BufferedWaveProvider? _captureBuffer;
    private ISampleProvider? _mixerSource;       // resampled / downmixed to 16k mono float
    private int _frameSeq;

    // State surfaced to the UI + emitted via MicStateUpdate.
    private bool _isMuted = true;
    private bool _pttMode = true;
    private bool _isPttDown;
    private bool _isSpeaking;   // RMS > threshold over last frame (cosmetic only)

    public bool IsMuted
    {
        get { lock (_stateLock) return _isMuted; }
        set { ApplyState(muted: value); }
    }
    public bool PttMode
    {
        get { lock (_stateLock) return _pttMode; }
        set { ApplyState(pttMode: value); }
    }
    public bool IsPttDown
    {
        get { lock (_stateLock) return _isPttDown; }
        set { ApplyState(pttDown: value); }
    }
    public bool IsSpeaking { get { lock (_stateLock) return _isSpeaking; } }

    /// <summary>Computed: true when the pump should be EMITTING frames upstream.</summary>
    public bool IsEmitting
    {
        get
        {
            lock (_stateLock)
            {
                if (_isMuted) return false;
                if (_pttMode) return _isPttDown;
                return true;
            }
        }
    }

    /// <summary>Set by MainWindow on BreakoutAssign.  Null = student is in
    /// main classroom (no group), voice frames are not emitted.</summary>
    public Guid? CurrentGroupId { get; set; }

    /// <summary>Set by MainWindow after Hello-ack captures it.  Null = we
    /// haven't learnt our own EndpointId yet; payloads cannot be tagged.</summary>
    public Guid? SelfEndpointId { get; set; }

    /// <summary>Fires whenever IsEmitting / PttMode / IsMuted / IsSpeaking
    /// transitions so the privacy banner can update.</summary>
    public event EventHandler? StateChanged;

    /// <summary>One MicStateUpdate to send to teacher.  Wired by MainWindow
    /// in Step 4 to route via App.Ipc.</summary>
    public event EventHandler<MicStateUpdateMessage>? StateUpdateReady;

    /// <summary>One VoiceAudioFrame to send to teacher.  Wired by MainWindow.</summary>
    public event EventHandler<(VoiceAudioFrameMessage Frame, Guid GroupId)>? VoiceFrameReady;

    public MicBroadcaster() { }

    private void ApplyState(bool? muted = null, bool? pttMode = null, bool? pttDown = null)
    {
        bool changed = false;
        bool wantCaptureBefore, wantCaptureAfter;
        lock (_stateLock)
        {
            wantCaptureBefore = !_isMuted;
            if (muted.HasValue && _isMuted != muted.Value) { _isMuted = muted.Value; changed = true; }
            if (pttMode.HasValue && _pttMode != pttMode.Value) { _pttMode = pttMode.Value; changed = true; }
            if (pttDown.HasValue && _isPttDown != pttDown.Value) { _isPttDown = pttDown.Value; changed = true; }
            wantCaptureAfter = !_isMuted;
        }
        if (!changed) return;

        if (wantCaptureAfter && !wantCaptureBefore) StartCapture();
        else if (!wantCaptureAfter && wantCaptureBefore) StopCapture();

        StateChanged?.Invoke(this, EventArgs.Empty);
        EmitStateUpdate();
    }

    /// <summary>Force-emit a MicStateUpdate immediately (used after teacher's
    /// MicMuteRequest is applied to confirm the new state).</summary>
    public void EmitStateUpdate()
    {
        if (SelfEndpointId is not Guid id) return;
        bool muted, ptt, speaking, live;
        lock (_stateLock)
        {
            muted = _isMuted;
            ptt = _pttMode;
            speaking = _isSpeaking;
            live = !_isMuted && (!_pttMode || _isPttDown);
        }
        StateUpdateReady?.Invoke(this, new MicStateUpdateMessage
        {
            EndpointId = id,
            MicLive = live,
            PttMode = ptt,
            IsSpeaking = speaking,
        });
    }

    private void StartCapture()
    {
        try
        {
            // Communications-role capture endpoint engages Windows AEC + NS
            // on supported drivers (Step 0 spike verdict: WORKS on this box).
            using var enumr = new MMDeviceEnumerator();
            MMDevice device;
            try
            {
                device = enumr.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            catch (Exception ex)
            {
                IpcClient.LogToFile($"[MicBroadcaster] No default Communications capture endpoint: {ex.Message}");
                return;
            }

            _capture = new WasapiCapture(device);
            var native = _capture.WaveFormat;
            _captureBuffer = new BufferedWaveProvider(native)
            {
                BufferDuration = TimeSpan.FromMilliseconds(CaptureBufferCapMs),
                DiscardOnBufferOverflow = true,
            };

            // Convert chain: native → mono → 16 kHz → 16-bit PCM at the pump.
            ISampleProvider sp = _captureBuffer.ToSampleProvider();
            if (sp.WaveFormat.Channels > 1) sp = sp.ToMono();
            if (sp.WaveFormat.SampleRate != TargetSampleRate)
                sp = new WdlResamplingSampleProvider(sp, TargetSampleRate);
            _mixerSource = sp;

            _captureSignal = new SemaphoreSlim(0, 1);
            _capture.DataAvailable += OnCaptureData;

            _cts = new CancellationTokenSource();
            _frameSeq = 0;
            _pumpTask = Task.Run(() => PumpLoopAsync(_cts.Token));
            _heartbeatTask = Task.Run(() => HeartbeatLoopAsync(_cts.Token));

            _capture.StartRecording();
            IpcClient.LogToFile($"[MicBroadcaster] Capture started ({device.FriendlyName}, native={native.SampleRate}Hz {native.Channels}ch {native.BitsPerSample}-bit {native.Encoding})");
        }
        catch (Exception ex)
        {
            IpcClient.LogToFile($"[MicBroadcaster] StartCapture failed: {ex.GetType().Name}: {ex.Message}");
            StopCapture();
        }
    }

    private void StopCapture()
    {
        try { _cts?.Cancel(); } catch { }
        try { _captureSignal?.Release(); } catch (SemaphoreFullException) { }
        try { _capture?.StopRecording(); } catch { }
        try { _pumpTask?.Wait(2000); } catch { }
        try { _heartbeatTask?.Wait(500); } catch { }
        _pumpTask = null;
        _heartbeatTask = null;
        _capture?.Dispose(); _capture = null;
        _captureBuffer = null;
        _mixerSource = null;
        _captureSignal?.Dispose();
        _captureSignal = null;
        IpcClient.LogToFile("[MicBroadcaster] Capture stopped");
    }

    private void OnCaptureData(object? sender, WaveInEventArgs e)
    {
        _captureBuffer?.AddSamples(e.Buffer, 0, e.BytesRecorded);
        try { _captureSignal?.Release(); } catch (SemaphoreFullException) { }
    }

    private async Task PumpLoopAsync(CancellationToken ct)
    {
        var sampleBuf = new float[SamplesPerFrame];
        var pcmBuf = new byte[BytesPerFrame];

        while (!ct.IsCancellationRequested)
        {
            try
            {
                try { await _captureSignal!.WaitAsync(200, ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                var src = _mixerSource;
                var buf = _captureBuffer;
                if (src == null || buf == null) break;

                while (!ct.IsCancellationRequested
                       && buf.BufferedDuration.TotalMilliseconds >= FrameDurationMs)
                {
                    int read = src.Read(sampleBuf, 0, SamplesPerFrame);
                    if (read <= 0) break;

                    // Convert float→PCM16 + compute RMS for VAD.
                    double sumSquares = 0;
                    for (int i = 0; i < read; i++)
                    {
                        var s = sampleBuf[i];
                        if (s > 1.0f) s = 1.0f;
                        else if (s < -1.0f) s = -1.0f;
                        sumSquares += s * s;
                        short pcm = (short)(s * 32767f);
                        pcmBuf[i * 2] = (byte)(pcm & 0xFF);
                        pcmBuf[i * 2 + 1] = (byte)((pcm >> 8) & 0xFF);
                    }
                    float rms = (float)Math.Sqrt(sumSquares / read);
                    bool nowSpeaking = rms > VadRmsThreshold;
                    bool wasSpeaking;
                    lock (_stateLock) { wasSpeaking = _isSpeaking; _isSpeaking = nowSpeaking; }
                    if (nowSpeaking != wasSpeaking) StateChanged?.Invoke(this, EventArgs.Empty);

                    // Drop frame if PTT mode + not pressed (or muted, or no group).
                    if (!IsEmitting) continue;
                    if (CurrentGroupId is not Guid gid) continue;
                    if (SelfEndpointId is not Guid sid) continue;

                    var data = new byte[read * 2];
                    Buffer.BlockCopy(pcmBuf, 0, data, 0, data.Length);

                    _frameSeq++;
                    var msg = new VoiceAudioFrameMessage
                    {
                        SourceEndpointId = sid,
                        GroupId = gid,
                        Pcm = data,
                        Ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    };
                    VoiceFrameReady?.Invoke(this, (msg, gid));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                IpcClient.LogToFile($"[MicBroadcaster] pump frame failed: {ex.Message}");
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1500, ct).ConfigureAwait(false);
                EmitStateUpdate();
            }
        }
        catch (OperationCanceledException) { }
    }

    public void Dispose()
    {
        try { StopCapture(); } catch { }
        _cts?.Dispose();
    }
}
