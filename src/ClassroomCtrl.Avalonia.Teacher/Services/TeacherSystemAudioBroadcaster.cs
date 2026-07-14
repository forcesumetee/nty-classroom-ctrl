using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>
/// TT-10-C — "Share Computer Audio": capture the Mac's SYSTEM audio (ScreenCaptureKit
/// <c>capturesAudio</c>, native <c>nty_sysaudio_*</c>) and broadcast each 100 ms PCM16 frame
/// (16 kHz mono, 3200 B — the shipped wire format; SCK is asked for 16 kHz mono directly, no
/// resampler) to ALL students via <see cref="ITeacherAudioSink"/> as <c>AudioStreamFrame</c> 0x0329.
/// Students play it with their existing playback — NO student-side change, NO wire change.
///
/// This is the productionization of the TT-10 system-audio probe (which confirmed first-party SCK
/// audio capture + screen/audio coexistence). Same broadcaster shape as <see cref="TeacherMicBroadcaster"/>;
/// the only differences are the native source (system audio, not mic) and the TCC gate (**Screen
/// Recording**, not Microphone). Start/Stop route RELIABLE (bug #7); frames on the lossy-class audio
/// channel. Use case: teacher plays a video → "Share My Screen" carries the picture, this carries the
/// sound.
/// </summary>
public sealed partial class TeacherSystemAudioBroadcaster
{
    private const string Lib = "NtyCapture";

    // System-audio capture is gated by Screen Recording (SCK) — reuse the screen-capture permission ABI.
    [LibraryImport(Lib)] private static partial int nty_check_permission();
    [LibraryImport(Lib)] private static partial int nty_request_permission();
    [LibraryImport(Lib)] private static partial int nty_sysaudio_start(nint cb, nint ctx);
    [LibraryImport(Lib)] private static partial void nty_sysaudio_stop();

    private readonly ITeacherAudioSink _sink;
    private GCHandle _self;
    private int _seq;

    public bool IsSharing { get; private set; }
    public static bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>Raised (native audio thread) after a frame is broadcast — the running frame seq.</summary>
    public event Action<int>? FrameSent;

    public TeacherSystemAudioBroadcaster(ITeacherAudioSink sink) => _sink = sink;

    /// <summary>Screen Recording (SCK) — the SAME grant TT-8 Share-My-Screen uses; binds at launch,
    /// so a fresh grant needs a RELAUNCH of the bundle (the TT-8 friction).</summary>
    public bool HasPermission => IsSupported && nty_check_permission() == 1;

    public Task<bool> RequestPermissionAsync()
        => IsSupported ? Task.Run(() => nty_request_permission() == 1) : Task.FromResult(false);

    /// <summary>Start capturing + broadcasting system audio. Returns 0 or a native negative code
    /// (-2 no display, -3 SCK/Screen-Recording error, -5 pre-macOS-13, -1000 not macOS).</summary>
    public async Task<int> StartAsync()
    {
        if (!IsSupported) return -1000;
        if (IsSharing) return 0;
        _seq = 0;
        int rc = await Task.Run(() =>
        {
            _self = GCHandle.Alloc(this);
            int r;
            unsafe
            {
                delegate* unmanaged[Cdecl]<nint, nint, int, int, int, void> fp = &OnPcmStatic;
                r = nty_sysaudio_start((nint)fp, GCHandle.ToIntPtr(_self));
            }
            if (r != 0 && _self.IsAllocated) _self.Free();
            return r;
        });
        if (rc != 0) return rc;
        IsSharing = true;
        await _sink.BroadcastAudioStreamControlAsync(true, CancellationToken.None);    // reliable START (bug #7)
        return 0;
    }

    public async Task StopAsync()
    {
        if (!IsSharing) return;
        IsSharing = false;
        await Task.Run(() => nty_sysaudio_stop());
        if (_self.IsAllocated) _self.Free();
        await _sink.BroadcastAudioStreamControlAsync(false, CancellationToken.None);   // reliable STOP (bug #7)
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static void OnPcmStatic(nint ctx, nint pcm, int length, int sampleRate, int channels)
    {
        if (ctx == 0 || pcm == 0 || length <= 0) return;
        if (GCHandle.FromIntPtr(ctx).Target is TeacherSystemAudioBroadcaster self)
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
        catch { /* lossy-class audio channel by design */ }
    }
}
