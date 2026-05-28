using NAudio.Wave;
using System;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 4 Part 3a: Plays back PCM audio frames received from teacher's broadcast.
///
/// Phase 11-C — replaced the unbounded "2 s capacity + discard-on-overflow" buffer
/// policy with a bounded jitter buffer:
///   • <see cref="PrebufferMs"/> (~200 ms) pre-buffer before calling
///     <c>WaveOut.Play</c> so initial network jitter doesn't pop / underrun.
///   • <see cref="SoftCapMs"/> (~400 ms) soft cap.  When the BufferedWaveProvider
///     accumulates more than that — burst arrival, peer-side stall now resolved,
///     any source — we <c>ClearBuffer</c> and resync to "now" instead of letting
///     the lag creep up.  Stale audio is musically worthless; one small skip-
///     ahead click beats multi-second creeping desync.
///   • <see cref="WaveOutLatencyMs"/> (~100 ms) WaveOut output device latency
///     (down from 200 ms) so the playback-side floor is closer to "real time".
/// Net steady-state offset ≈ PrebufferMs + WaveOutLatencyMs ≈ 300 ms, constant.
/// </summary>
public class AudioPlayer : IDisposable
{
    /// <summary>Phase 11-C — pre-buffer depth before starting WaveOut.Play.
    /// 200 ms is enough to ride out one or two missing 100 ms frames without
    /// underrunning, but well under the old uncapped behavior.</summary>
    private const int PrebufferMs      = 200;
    /// <summary>Phase 11-C — soft cap on the BufferedWaveProvider depth.  When
    /// exceeded, the buffer is cleared and we resync to live.  Picked to be
    /// 2× pre-buffer so a single missed wake-up doesn't trigger spurious
    /// skip-aheads, while still bounding total latency to ~400 ms.</summary>
    private const int SoftCapMs        = 400;
    /// <summary>Phase 11-C — WaveOutEvent.DesiredLatency.  100 ms is the
    /// practical floor on Windows WASAPI shared-mode; below that produces
    /// audible glitches on many devices.</summary>
    private const int WaveOutLatencyMs = 100;

    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;
    private WaveFormat? _currentFormat;
    /// <summary>Phase 11-C — false until we've accumulated <see cref="PrebufferMs"/>
    /// in the buffer and called <c>WaveOut.Play()</c>.  Avoids the old behavior
    /// of starting playback on the first frame (which produced a pop + immediate
    /// underrun risk).</summary>
    private bool _started;
    /// <summary>Phase 11-C — running count of soft-cap skip-ahead events.
    /// Used to throttle the per-event log so a chronic-burst peer can't spam.</summary>
    private int _skipAheadCount;

    public bool IsPlaying => _output != null;

    /// <summary>
    /// Push a PCM frame for playback. Initializes the output device on the first frame.
    /// Frames are silently discarded if the format changes mid-stream.
    /// </summary>
    public void PushFrame(byte[] pcmData, int sampleRate, int channels, int bitsPerSample)
    {
        if (pcmData == null || pcmData.Length == 0) return;

        try
        {
            if (_output == null || _currentFormat == null
                || _currentFormat.SampleRate != sampleRate
                || _currentFormat.Channels != channels
                || _currentFormat.BitsPerSample != bitsPerSample)
            {
                Reset();
                _currentFormat = new WaveFormat(sampleRate, bitsPerSample, channels);
                _buffer = new BufferedWaveProvider(_currentFormat)
                {
                    // Phase 11-C — ring capacity dropped 2000ms → 1000ms.  The soft
                    // cap (400 ms) drops to live well before this hard limit fires,
                    // so the ring is just headroom against a transient burst between
                    // PushFrame calls.
                    BufferDuration = TimeSpan.FromSeconds(1),
                    // If a burst somehow does saturate even 1 s, NAudio's discard
                    // policy avoids throwing; we lose the OLDEST frame in the burst.
                    DiscardOnBufferOverflow = true,
                };
                _output = new WaveOutEvent { DesiredLatency = WaveOutLatencyMs };
                _output.Init(_buffer);
                _started = false;
                _skipAheadCount = 0;
                IpcClient.LogToFile(
                    $"[AudioPlayer] Initialized {sampleRate}Hz/{bitsPerSample}-bit/{channels}ch " +
                    $"(prebuffer={PrebufferMs}ms, softCap={SoftCapMs}ms, waveOutLatency={WaveOutLatencyMs}ms)");
            }

            // Phase 11-C — soft-cap skip-ahead.  If the backlog has grown past the
            // soft cap (transient network burst, brief student-side stall now
            // resolved, the upstream channel still doing the right thing but with
            // jitter), drop the backlog to resync to "now".  Without this the
            // buffer would slowly creep up to ~2 s as in the pre-11-C behavior.
            if (_buffer!.BufferedDuration.TotalMilliseconds > SoftCapMs)
            {
                _buffer.ClearBuffer();
                _skipAheadCount++;
                // Throttle: log first few + every 25th to avoid log spam if a
                // peer is in a chronic-burst condition (different bug then).
                if (_skipAheadCount <= 3 || _skipAheadCount % 25 == 0)
                {
                    IpcClient.LogToFile(
                        $"[AudioPlayer] skip-ahead #{_skipAheadCount}: backlog exceeded {SoftCapMs}ms, cleared to resync");
                }
            }
            _buffer.AddSamples(pcmData, 0, pcmData.Length);

            // Phase 11-C — defer WaveOut.Play until pre-buffer depth is reached.
            // Starting on the first frame (the pre-11-C behavior) caused a pop
            // plus near-immediate underrun risk if frames 2-3 arrived late.
            if (!_started && _buffer.BufferedDuration.TotalMilliseconds >= PrebufferMs)
            {
                _output!.Play();
                _started = true;
                IpcClient.LogToFile($"[AudioPlayer] Playback started after {PrebufferMs}ms pre-buffer");
            }
        }
        catch (Exception ex)
        {
            IpcClient.LogToFile($"[AudioPlayer] PushFrame error: {ex.Message}");
        }
    }

    public void Stop()
    {
        Reset();
        IpcClient.LogToFile("[AudioPlayer] Stopped");
    }

    private void Reset()
    {
        try { _output?.Stop(); } catch { }
        _output?.Dispose();
        _output = null;
        _buffer = null;
        _currentFormat = null;
        _started = false;
        _skipAheadCount = 0;
    }

    public void Dispose() => Reset();
}
