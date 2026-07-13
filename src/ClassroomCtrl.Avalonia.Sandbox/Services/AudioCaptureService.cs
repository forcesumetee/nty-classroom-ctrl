using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

/// <summary>Tri-state microphone authorization (distinct TCC bucket from camera/screen).</summary>
public enum AudioPermission { NotDetermined, Granted, Denied }

/// <summary>
/// Phase 29-B/D — managed side of the native AVAudioEngine mic helper
/// (native/NtyCapture/Audio.swift). Parallel to Screen/Camera capture services but
/// INDEPENDENT (own native session state), so mic can run alongside screen + camera.
///
/// Same interop template: a static <see cref="UnmanagedCallersOnlyAttribute"/> PCM
/// callback whose <c>ctx</c> is a <see cref="GCHandle"/> to this instance; the GCHandle
/// roots the service for the capture's lifetime. Frames are raw PCM16 (16 kHz mono,
/// 100 ms / 3200 bytes) — no codec, matching the shipped wire format.
/// </summary>
public sealed partial class AudioCaptureService
{
    private const string Lib = "NtyCapture"; // → libNtyCapture.dylib on macOS

    [LibraryImport(Lib)] private static partial int nty_audio_check_permission();
    [LibraryImport(Lib)] private static partial int nty_audio_request_permission();
    [LibraryImport(Lib)] private static partial int nty_audio_start_pcm(int sampleRate, int channels, nint cb, nint ctx);
    [LibraryImport(Lib)] private static partial void nty_audio_stop();
    [LibraryImport(Lib)] private static partial long nty_audio_frame_count();
    [LibraryImport(Lib)] private static partial int nty_audio_last_rms();
    // Playback (29-F, path A) — separate native engine + state from capture.
    [LibraryImport(Lib)] private static partial int nty_audio_play_start(int sampleRate, int channels);
    [LibraryImport(Lib)] private static partial void nty_audio_play_pcm(byte[] data, int length);
    [LibraryImport(Lib)] private static partial void nty_audio_play_stop();

    /// <summary>Raised per 100 ms frame with a freshly-copied PCM16 buffer + its format.
    /// Fires on the native (AVAudioEngine) real-time thread — marshal to the UI thread.</summary>
    public event Action<byte[], int, int>? PcmFrameReceived;

    private GCHandle _self;
    public bool IsCapturing { get; private set; }

    public static bool IsSupported => OperatingSystem.IsMacOS();

    // ── permission ──────────────────────────────────────────────────────────────
    public AudioPermission CheckPermission()
    {
        if (!IsSupported) return AudioPermission.Denied;
        return nty_audio_check_permission() switch
        {
            1 => AudioPermission.Granted,
            0 => AudioPermission.NotDetermined,
            _ => AudioPermission.Denied,
        };
    }

    public Task<AudioPermission> RequestPermissionAsync()
    {
        if (!IsSupported) return Task.FromResult(AudioPermission.Denied);
        return Task.Run(() => nty_audio_request_permission() == 1
            ? AudioPermission.Granted : AudioPermission.Denied);
    }

    // ── capture ─────────────────────────────────────────────────────────────────
    /// <summary>Start mic capture at sampleRate×channels (0 → 16000/1, the shipped
    /// format). Returns 0 or a native negative error code.</summary>
    public Task<int> StartAsync(int sampleRate = 16000, int channels = 1)
    {
        if (!IsSupported) return Task.FromResult(-1000);
        if (IsCapturing) return Task.FromResult(-3);
        return Task.Run(() =>
        {
            _self = GCHandle.Alloc(this);
            int rc;
            unsafe
            {
                delegate* unmanaged[Cdecl]<nint, nint, int, int, int, void> fp = &OnPcmStatic;
                rc = nty_audio_start_pcm(sampleRate, channels, (nint)fp, GCHandle.ToIntPtr(_self));
            }
            if (rc == 0) IsCapturing = true;
            else if (_self.IsAllocated) _self.Free();
            return rc;
        });
    }

    public Task StopAsync()
    {
        if (!IsSupported || !IsCapturing) return Task.CompletedTask;
        return Task.Run(() =>
        {
            nty_audio_stop();
            IsCapturing = false;
            if (_self.IsAllocated) _self.Free();
        });
    }

    /// <summary>Last-frame RMS level (0..100) for a live meter.</summary>
    public int LastRms() => IsSupported ? nty_audio_last_rms() : 0;
    public long FrameCount() => IsSupported ? nty_audio_frame_count() : 0;

    // ── playback (29-F, path A) ─────────────────────────────────────────────────
    public bool IsPlaying { get; private set; }

    /// <summary>Start the playback engine (own native state — coexists with capture).
    /// A jitter buffer prebuffers ~300 ms before audio starts. Returns 0 or a native error.</summary>
    public Task<int> StartPlaybackAsync(int sampleRate = 16000, int channels = 1)
    {
        if (!IsSupported) return Task.FromResult(-1000);
        if (IsPlaying) return Task.FromResult(0);
        return Task.Run(() =>
        {
            int rc = nty_audio_play_start(sampleRate, channels);
            if (rc == 0) IsPlaying = true;
            return rc;
        });
    }

    /// <summary>Enqueue one PCM16-LE frame for playback (fast, non-blocking — the native
    /// side schedules on the audio thread). Safe to call from the network dispatch thread.</summary>
    public void EnqueuePcm(byte[] data)
    {
        if (IsSupported && IsPlaying && data.Length > 0) nty_audio_play_pcm(data, data.Length);
    }

    public Task StopPlaybackAsync()
    {
        if (!IsSupported || !IsPlaying) return Task.CompletedTask;
        return Task.Run(() =>
        {
            nty_audio_play_stop();
            IsPlaying = false;
        });
    }

    // ── native callback (real-time audio thread) ────────────────────────────────
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnPcmStatic(nint ctx, nint pcm, int length, int sampleRate, int channels)
    {
        if (ctx == 0 || pcm == 0 || length <= 0) return;
        if (GCHandle.FromIntPtr(ctx).Target is AudioCaptureService svc)
        {
            var buf = new byte[length];
            Marshal.Copy(pcm, buf, 0, length);
            svc.PcmFrameReceived?.Invoke(buf, sampleRate, channels);
        }
    }
}
