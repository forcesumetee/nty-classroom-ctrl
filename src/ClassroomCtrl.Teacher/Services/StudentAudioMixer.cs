using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System;
using System.Collections.Generic;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 4 Part 3b: Receives PCM frames from multiple students, mixes them in realtime,
/// and plays them through Teacher's default output device.
///
/// Per-student BufferedWaveProvider feeds the master MixingSampleProvider.
/// WaveOutEvent reads from the mixer and plays back. Lazy-init on first frame.
/// </summary>
public class StudentAudioMixer : IDisposable
{
    private readonly ILogger<StudentAudioMixer> _logger;
    private readonly object _lock = new();

    private const int SampleRate = 16000;
    private const int Channels = 1;
    private const int BitsPerSample = 16;

    private readonly Dictionary<Guid, StudentAudioState> _states = new();

    private WaveOutEvent? _output;
    private MixingSampleProvider? _mixer;
    private VolumeSampleProvider? _volume;
    private float _pendingVolume = 1.0f;

    /// <summary>
    /// Master volume scaler applied to the mixed student stream (0.0 – 2.0).
    /// 1.0 = unity. Above 1.0 may clip. Setter is safe before playback starts.
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

    public StudentAudioMixer(ILogger<StudentAudioMixer> logger)
    {
        _logger = logger;
    }

    public void AddStudent(Guid studentId)
    {
        lock (_lock)
        {
            if (_states.ContainsKey(studentId)) return;

            EnsurePlaybackStarted();
            if (_mixer == null) return;

            var format = new WaveFormat(SampleRate, BitsPerSample, Channels);
            var buffer = new BufferedWaveProvider(format)
            {
                BufferDuration = TimeSpan.FromSeconds(2),
                DiscardOnBufferOverflow = true,
                ReadFully = true,
            };
            ISampleProvider sp = buffer.ToSampleProvider();
            _mixer.AddMixerInput(sp);

            _states[studentId] = new StudentAudioState { Buffer = buffer, MixerInput = sp };
            _logger.LogInformation("Student {Id} audio added (now {Count} streams)", studentId, _states.Count);
        }
    }

    public void PushFrame(Guid studentId, AudioStreamFrameMessage frame)
    {
        if (frame.PcmData == null || frame.PcmData.Length == 0) return;

        BufferedWaveProvider? buffer;
        lock (_lock)
        {
            if (!_states.TryGetValue(studentId, out var state))
            {
                // First frame seen without an explicit Start — auto-register
                AddStudentLocked(studentId);
                if (!_states.TryGetValue(studentId, out state)) return;
            }
            buffer = state.Buffer;
        }

        try
        {
            buffer.AddSamples(frame.PcmData, 0, frame.PcmData.Length);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PushFrame failed for student {Id}", studentId);
        }
    }

    public void RemoveStudent(Guid studentId)
    {
        lock (_lock)
        {
            if (!_states.Remove(studentId, out var state)) return;
            try { _mixer?.RemoveMixerInput(state.MixerInput); } catch { }
            _logger.LogInformation("Student {Id} audio removed (now {Count} streams)", studentId, _states.Count);
        }
    }

    private void AddStudentLocked(Guid studentId)
    {
        if (_states.ContainsKey(studentId)) return;
        EnsurePlaybackStarted();
        if (_mixer == null) return;

        var format = new WaveFormat(SampleRate, BitsPerSample, Channels);
        var buffer = new BufferedWaveProvider(format)
        {
            BufferDuration = TimeSpan.FromSeconds(2),
            DiscardOnBufferOverflow = true,
            ReadFully = true,
        };
        ISampleProvider sp = buffer.ToSampleProvider();
        _mixer.AddMixerInput(sp);
        _states[studentId] = new StudentAudioState { Buffer = buffer, MixerInput = sp };
        _logger.LogInformation("Student {Id} audio auto-added on first frame", studentId);
    }

    private void EnsurePlaybackStarted()
    {
        if (_output != null && _mixer != null) return;

        try
        {
            var mixerFormat = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);
            _mixer = new MixingSampleProvider(mixerFormat) { ReadFully = true };
            _volume = new VolumeSampleProvider(_mixer) { Volume = _pendingVolume };

            _output = new WaveOutEvent { DesiredLatency = 200 };
            _output.Init(_volume.ToWaveProvider16());
            _output.Play();
            _logger.LogInformation("Student audio playback started ({Rate}Hz mono, vol={Vol:F2})",
                SampleRate, _pendingVolume);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cannot start student audio playback");
            _output?.Dispose();
            _output = null;
            _mixer = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _states.Clear();
            try { _output?.Stop(); } catch { }
            _output?.Dispose();
            _output = null;
            _mixer = null;
            _volume = null;
        }
    }

    private sealed class StudentAudioState
    {
        public required BufferedWaveProvider Buffer { get; init; }
        public required ISampleProvider MixerInput { get; init; }
    }
}
