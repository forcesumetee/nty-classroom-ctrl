using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>
/// TT-10-B — the Teacher's "Talk to Class": capture the teacher's mic (native AVAudioEngine, the
/// M20 path — the same nty_audio_* ABI the Student's AudioCaptureService uses) and broadcast each
/// 100 ms PCM16 frame (16 kHz mono, 3200 B — the shipped wire format) to ALL students via
/// <see cref="ITeacherAudioSink"/>. Mirrors TeacherScreenBroadcaster's shape exactly.
///
/// CHANNELS (the stuck-state invariant): AudioStreamStart/Stop → RELIABLE (bug #7 — a dropped Stop
/// leaves every student's playback session open); frames → the dedicated lossy-class audio channel
/// (hot, ephemeral — reliable would head-of-line stall the class behind one slow peer). Both are
/// decided inside ControlServer; this driver just pumps.
///
/// TCC: Microphone (nty_audio_check/request_permission) — declared in Info.Teacher.plist since
/// TT-8; the grant is effective immediately (no relaunch, unlike Screen Recording).
/// NO AEC (TT-11's job): a co-located teacher + student WILL feed back — use headphones in a
/// one-room rig.
/// </summary>
public sealed partial class TeacherMicBroadcaster
{
    private const string Lib = "NtyCapture"; // → libNtyCapture.dylib on macOS

    [LibraryImport(Lib)] private static partial int nty_audio_check_permission();
    [LibraryImport(Lib)] private static partial int nty_audio_request_permission();
    [LibraryImport(Lib)] private static partial int nty_audio_start_pcm(int sampleRate, int channels, nint cb, nint ctx);
    [LibraryImport(Lib)] private static partial void nty_audio_stop();

    private readonly ITeacherAudioSink _sink;
    private GCHandle _self;
    private int _seq;

    public bool IsTalking { get; private set; }
    public static bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>Raised (native audio thread) after a frame is broadcast — the running frame seq.</summary>
    public event Action<int>? FrameSent;

    public TeacherMicBroadcaster(ITeacherAudioSink sink) => _sink = sink;

    public bool HasPermission => IsSupported && nty_audio_check_permission() == 1;

    /// <summary>Prompts if undetermined (blocks a worker thread, not the UI). Mic grants apply
    /// immediately — no relaunch needed (unlike Screen Recording).</summary>
    public Task<bool> RequestPermissionAsync()
        => IsSupported ? Task.Run(() => nty_audio_request_permission() == 1) : Task.FromResult(false);

    /// <summary>Start capturing + broadcasting the teacher's mic. Returns 0 on success or a native
    /// negative code (-2 no input/permission, -3 already running, -1000 not macOS).</summary>
    public async Task<int> StartAsync()
    {
        if (!IsSupported) return -1000;
        if (IsTalking) return 0;
        _seq = 0;
        int rc = await Task.Run(() =>
        {
            _self = GCHandle.Alloc(this);
            int r;
            unsafe
            {
                delegate* unmanaged[Cdecl]<nint, nint, int, int, int, void> fp = &OnPcmStatic;
                r = nty_audio_start_pcm(16000, 1, (nint)fp, GCHandle.ToIntPtr(_self));
            }
            if (r != 0 && _self.IsAllocated) _self.Free();
            return r;
        });
        if (rc != 0) return rc;
        IsTalking = true;
        await _sink.BroadcastAudioStreamControlAsync(true, CancellationToken.None);    // reliable START (bug #7)
        return 0;
    }

    public async Task StopAsync()
    {
        if (!IsTalking) return;
        IsTalking = false;
        await Task.Run(() => nty_audio_stop());
        if (_self.IsAllocated) _self.Free();
        await _sink.BroadcastAudioStreamControlAsync(false, CancellationToken.None);   // reliable STOP (bug #7)
    }

    // Native (real-time audio thread): copy the call-scoped PCM, then broadcast.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnPcmStatic(nint ctx, nint pcm, int length, int sampleRate, int channels)
    {
        if (ctx == 0 || pcm == 0 || length <= 0) return;
        if (GCHandle.FromIntPtr(ctx).Target is TeacherMicBroadcaster self)
        {
            var buf = new byte[length];
            Marshal.Copy(pcm, buf, 0, length);
            self.OnPcm(buf, sampleRate, channels);
        }
    }

    private void OnPcm(byte[] pcm, int sampleRate, int channels)
    {
        var msg = new AudioStreamFrameMessage
        {
            PcmData = pcm,
            SampleRate = sampleRate,
            Channels = channels,
            BitsPerSample = 16,
            TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            FrameSeq = Interlocked.Increment(ref _seq),
        };
        _ = SendSafeAsync(msg);
    }

    private async Task SendSafeAsync(AudioStreamFrameMessage msg)
    {
        try
        {
            await _sink.BroadcastAudioFrameAsync(msg, CancellationToken.None);
            FrameSent?.Invoke(msg.FrameSeq);
        }
        catch { /* a dropped frame is fine — the audio channel is lossy-class by design */ }
    }
}
