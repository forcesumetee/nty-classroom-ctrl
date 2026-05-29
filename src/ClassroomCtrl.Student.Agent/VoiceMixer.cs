using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;
using System.Threading;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 13-D (Tier 3) — student-side multi-source voice playback.
/// Per-source BufferedWaveProvider buffers + MixingSampleProvider combines +
/// SampleToWaveProvider16 converts float mix → PCM16 → single WaveOutEvent
/// drives the output device.  Independent of the existing AudioPlayer
/// (Phase 11-C teacher loopback) — both run their own WaveOutEvent so the
/// student hears class audio AND group voice simultaneously without
/// contention on a shared output.
///
/// Source lifecycle: a new <see cref="BufferedWaveProvider"/> is created on
/// first frame from a previously-unseen <c>SourceEndpointId</c>.  Sources
/// that haven't received a frame in <see cref="IdleEvictionMs"/> are pruned
/// (handles a peer leaving the group without sending an explicit signal,
/// per design doc §3.2 "auto-removed when a source goes idle > ~3 s").
///
/// Format is fixed at the Tier 3 wire shape (16 kHz mono 16-bit) — the
/// MicBroadcaster has already done the resample/downmix on the sending side.
/// </summary>
public sealed class VoiceMixer : IDisposable
{
    public const int SampleRate = 16000;
    public const int Channels = 1;
    public const int IdleEvictionMs = 5000;

    /// <summary>Mirrors AudioPlayer's pre-buffer threshold — 200 ms before
    /// WaveOut.Play so a brief frame-arrival jitter doesn't cause underrun
    /// pop at the start of a speaker.</summary>
    private const int PrebufferMs = 200;
    /// <summary>Per-source soft cap.  When the BufferedWaveProvider for one
    /// source crosses this, clear and resync to live.  Stale voice (older
    /// than ~400 ms) is unintelligible anyway — a brief skip beats creeping
    /// per-source desync.</summary>
    private const int SoftCapMs = 400;
    /// <summary>WaveOutEvent latency — same floor as AudioPlayer (100 ms).
    /// Below this WASAPI shared mode glitches on many devices.</summary>
    private const int WaveOutLatencyMs = 100;

    private readonly object _lock = new();
    private readonly WaveFormat _sourceFormat = new(SampleRate, 16, Channels);
    private readonly MixingSampleProvider _mixer;
    private readonly Dictionary<Guid, SourceState> _sources = new();
    private WaveOutEvent? _output;
    private bool _started;
    private bool _disposed;
    private System.Threading.Timer? _evictionTimer;

    public VoiceMixer()
    {
        // MixingSampleProvider works in float (ISampleProvider).  ReadFully so
        // the mixer pads silence when no source is producing — keeps WaveOut
        // happy between speech bursts.
        var floatFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
        _mixer = new MixingSampleProvider(floatFormat) { ReadFully = true };
    }

    /// <summary>Push one inbound voice frame.  Routes to the per-source
    /// BufferedWaveProvider (creating it on first frame from this source),
    /// starts the WaveOutEvent on first frame overall.</summary>
    public void PushFrame(Guid sourceId, byte[] pcm, long timestampMs)
    {
        if (_disposed) return;
        if (pcm == null || pcm.Length == 0) return;

        SourceState src;
        lock (_lock)
        {
            if (!_sources.TryGetValue(sourceId, out src!))
            {
                src = NewSourceLocked(sourceId);
            }
            src.LastFrameTickMs = Environment.TickCount64;
            src.Buffer.AddSamples(pcm, 0, pcm.Length);

            // Per-source soft-cap resync.
            if (src.Buffer.BufferedDuration.TotalMilliseconds > SoftCapMs)
            {
                src.Buffer.ClearBuffer();
                src.SkipAheadCount++;
                if (src.SkipAheadCount <= 3 || src.SkipAheadCount % 50 == 0)
                    IpcClient.LogToFile($"[VoiceMixer] source {sourceId} soft-cap resync #{src.SkipAheadCount}");
            }
        }

        EnsureOutputRunning();
    }

    /// <summary>Force-remove a source (e.g. on explicit MicStateUpdate
    /// {MicLive=false}).  Idempotent.</summary>
    public void RemoveSource(Guid sourceId)
    {
        lock (_lock)
        {
            if (!_sources.TryGetValue(sourceId, out var src)) return;
            try { _mixer.RemoveMixerInput(src.MixerInput); } catch { }
            _sources.Remove(sourceId);
            IpcClient.LogToFile($"[VoiceMixer] source {sourceId} removed");
        }
    }

    private SourceState NewSourceLocked(Guid sourceId)
    {
        var buf = new BufferedWaveProvider(_sourceFormat)
        {
            BufferDuration = TimeSpan.FromMilliseconds(1000), // generous head; SoftCapMs trims
            DiscardOnBufferOverflow = true,
        };
        var mixerInput = (ISampleProvider)buf.ToSampleProvider();
        _mixer.AddMixerInput(mixerInput);
        var src = new SourceState
        {
            SourceId = sourceId,
            Buffer = buf,
            MixerInput = mixerInput,
            LastFrameTickMs = Environment.TickCount64,
        };
        _sources[sourceId] = src;
        IpcClient.LogToFile($"[VoiceMixer] source {sourceId} added (sources={_sources.Count})");
        return src;
    }

    private void EnsureOutputRunning()
    {
        if (_started || _disposed) return;
        lock (_lock)
        {
            if (_started || _disposed) return;
            // Wait until we have at least one source with prebuffered audio
            // before starting WaveOut to avoid pop + underrun on a freshly
            // joined speaker's first 100 ms.
            bool anyPrebuffered = false;
            foreach (var s in _sources.Values)
            {
                if (s.Buffer.BufferedDuration.TotalMilliseconds >= PrebufferMs) { anyPrebuffered = true; break; }
            }
            if (!anyPrebuffered) return;

            try
            {
                _output = new WaveOutEvent { DesiredLatency = WaveOutLatencyMs };
                _output.Init(new SampleToWaveProvider16(_mixer));
                _output.Play();
                _started = true;
                _evictionTimer = new System.Threading.Timer(_ => EvictIdleSources(), null, 1000, 1000);
                IpcClient.LogToFile($"[VoiceMixer] WaveOut started ({SampleRate}Hz mono, {WaveOutLatencyMs}ms latency)");
            }
            catch (Exception ex)
            {
                IpcClient.LogToFile($"[VoiceMixer] WaveOut start failed: {ex.GetType().Name}: {ex.Message}");
                _output?.Dispose();
                _output = null;
            }
        }
    }

    private void EvictIdleSources()
    {
        if (_disposed) return;
        var now = Environment.TickCount64;
        var toRemove = new List<Guid>();
        lock (_lock)
        {
            foreach (var (id, s) in _sources)
            {
                if (now - s.LastFrameTickMs > IdleEvictionMs) toRemove.Add(id);
            }
            foreach (var id in toRemove)
            {
                if (_sources.TryGetValue(id, out var s))
                {
                    try { _mixer.RemoveMixerInput(s.MixerInput); } catch { }
                    _sources.Remove(id);
                    IpcClient.LogToFile($"[VoiceMixer] source {id} evicted (idle > {IdleEvictionMs}ms)");
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _evictionTimer?.Dispose(); } catch { }
        _evictionTimer = null;
        lock (_lock)
        {
            try { _output?.Stop(); } catch { }
            try { _output?.Dispose(); } catch { }
            _output = null;
            foreach (var s in _sources.Values)
            {
                try { _mixer.RemoveMixerInput(s.MixerInput); } catch { }
            }
            _sources.Clear();
        }
    }

    private sealed class SourceState
    {
        public Guid SourceId;
        public BufferedWaveProvider Buffer = null!;
        public ISampleProvider MixerInput = null!;
        public long LastFrameTickMs;
        public int SkipAheadCount;
    }
}
