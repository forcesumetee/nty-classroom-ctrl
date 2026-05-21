using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 4 Part 3a: Plays back PCM audio frames received from teacher's broadcast.
///
/// BufferedWaveProvider buffers incoming PCM data, WaveOutEvent plays it back.
/// AutoStart on first frame; lazy-creates output device when format is known.
///
/// Phase 10.14 (Item 2a) — wrapped pipeline with VolumeSampleProvider so the
/// student can adjust playback level from the Agent UI.  Pattern mirrors
/// Teacher's StudentAudioMixer; _pendingVolume buffers the setting across the
/// reset that happens when stream format changes mid-session.
/// </summary>
public class AudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;
    private VolumeSampleProvider? _volume;
    private WaveFormat? _currentFormat;
    private float _pendingVolume = 1.0f;

    public bool IsPlaying => _output != null;

    /// <summary>
    /// Phase 10.14 (Item 2a) — playback gain (0.0–2.0; 1.0 = unity).  Set before the first
    /// frame to apply at init; set after to adjust live.  Survives format-change resets via
    /// _pendingVolume.  Above 1.0 may clip per NAudio docs (gain is unbounded; clipping at WaveOut).
    /// </summary>
    public float Volume
    {
        get => _volume?.Volume ?? _pendingVolume;
        set
        {
            _pendingVolume = value;
            if (_volume != null) _volume.Volume = value;
        }
    }

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
                    BufferDuration = TimeSpan.FromSeconds(2),
                    DiscardOnBufferOverflow = true,
                };
                // Phase 10.14 (Item 2a) — pipeline: buffer → ToSampleProvider → VolumeSampleProvider
                // → ToWaveProvider16 → _output.  ToSampleProvider() converts 16-bit PCM → float,
                // gain applies in float space, ToWaveProvider16() converts back to 16-bit PCM for WaveOut.
                _volume = new VolumeSampleProvider(_buffer.ToSampleProvider()) { Volume = _pendingVolume };
                _output = new WaveOutEvent { DesiredLatency = 200 };
                _output.Init(_volume.ToWaveProvider16());
                _output.Play();
                IpcClient.LogToFile($"[AudioPlayer] Started playback {sampleRate}Hz/{bitsPerSample}-bit/{channels}ch vol={_pendingVolume:F2}");
            }

            _buffer!.AddSamples(pcmData, 0, pcmData.Length);
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
        _volume = null;
        _currentFormat = null;
    }

    public void Dispose() => Reset();
}
