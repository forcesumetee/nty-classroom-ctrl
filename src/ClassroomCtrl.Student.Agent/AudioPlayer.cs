using NAudio.Wave;
using System;

namespace ClassroomCtrl.Student.Agent;

/// <summary>
/// Phase 4 Part 3a: Plays back PCM audio frames received from teacher's broadcast.
///
/// BufferedWaveProvider buffers incoming PCM data, WaveOutEvent plays it back.
/// AutoStart on first frame; lazy-creates output device when format is known.
/// </summary>
public class AudioPlayer : IDisposable
{
    private WaveOutEvent? _output;
    private BufferedWaveProvider? _buffer;
    private WaveFormat? _currentFormat;

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
                    BufferDuration = TimeSpan.FromSeconds(2),
                    DiscardOnBufferOverflow = true,
                };
                _output = new WaveOutEvent { DesiredLatency = 200 };
                _output.Init(_buffer);
                _output.Play();
                IpcClient.LogToFile($"[AudioPlayer] Started playback {sampleRate}Hz/{bitsPerSample}-bit/{channels}ch");
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
        _currentFormat = null;
    }

    public void Dispose() => Reset();
}
