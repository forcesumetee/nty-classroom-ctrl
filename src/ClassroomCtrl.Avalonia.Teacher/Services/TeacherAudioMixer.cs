using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ClassroomCtrl.Shared.Protocol;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>
/// TT-9-C — teacher-side multi-student audio mixer. The managed policy layer over the
/// native N-source mixer (<c>nty_mix_*</c>, native/NtyCapture/Sources/Audio.swift). It
/// subscribes to the ControlServer's student-audio events — <c>StudentAudioFrameReceived</c>
/// et al., which had ZERO subscribers before (the port deserialized student mic PCM and
/// dropped it) — and feeds each student's PCM into the native mix, which sums N
/// <c>AVAudioPlayerNode</c>s so the teacher hears everyone at once.
///
/// <para>Two DELIBERATE DIVERGENCES from the shipped Windows <c>StudentAudioMixer</c>
/// (both fix real gaps; see docs/TT-9-FINDINGS.md):</para>
/// <list type="number">
/// <item><b>CAP</b> — the shipped mixer has NO cap (NAudio <c>MaxInputCount</c> =
///   <c>int.MaxValue</c>; the intended server gate <c>_micMonitorTargets</c> is dead code),
///   so N unbounded ~256 kbps PCM streams sum into one output. We cap the MIXED set
///   (default 12, configurable) and DEGRADE VISIBLY: sources past the cap are counted and
///   surfaced via <see cref="StatusChanged"/> ("N of M open — mixing 12"), never silently
///   dropped (the TT-6 bulk-skip-report principle — never drop a student the teacher
///   believes they can hear without saying so). A freed slot auto-fills from overflow on
///   that source's next frame.</item>
/// <item><b>GAIN NORMALIZATION</b> — the shipped mixer sums un-normalized inputs and clips
///   at high N; the native core scales each source by 1/sqrt(activeCount).</item>
/// </list>
///
/// <para>The load-bearing NON-BLOCKING invariant (one stalled student never silences the
/// class) lives in the native core (independent player nodes → silence when starved) and
/// is asserted by TT-9-D's kill-one-sender stall test.</para>
/// </summary>
public sealed partial class TeacherAudioMixer : IDisposable
{
    private const string Lib = "NtyCapture"; // → libNtyCapture.dylib

    [LibraryImport(Lib)] private static partial int nty_mix_start(int sampleRate, int channels);
    [LibraryImport(Lib)] private static partial void nty_mix_add(int sourceId);
    [LibraryImport(Lib)] private static partial void nty_mix_push(int sourceId, byte[] data, int length);
    [LibraryImport(Lib)] private static partial void nty_mix_remove(int sourceId);
    [LibraryImport(Lib)] private static partial void nty_mix_stop();
    [LibraryImport(Lib)] private static partial int nty_mix_active_count();
    [LibraryImport(Lib)] private static partial long nty_mix_rendered_frames();
    [LibraryImport(Lib)] private static partial int nty_mix_output_rms();
    [LibraryImport(Lib)] private static partial long nty_mix_source_played(int sourceId);

    public static bool IsSupported => OperatingSystem.IsMacOS();

    /// <summary>Max simultaneously-mixed students. Beyond this, further open mics are
    /// counted as overflow and surfaced, not mixed.</summary>
    public int Cap { get; }

    /// <summary>(mixed, open, cap) whenever the mix membership changes. <c>open</c> counts
    /// every streaming mic (mixed + overflow); <c>mixed</c> ≤ <c>cap</c>.</summary>
    public event Action<int, int, int>? StatusChanged;

    private readonly object _lock = new();
    private readonly Dictionary<Guid, int> _keys = new();   // guid → stable native source id
    private readonly HashSet<Guid> _mixed = new();          // in the native mix (≤ Cap)
    private readonly HashSet<Guid> _overflow = new();       // streaming but past the cap
    private int _nextKey = 1;
    private bool _startAttempted;
    private bool _mixOk;
    private bool _disposed;

    public TeacherAudioMixer(int cap = 12)
    {
        Cap = cap < 1 ? 1 : cap;
    }

    /// <summary>Handle one inbound student mic frame. Registers the source into the mix
    /// (subject to the cap) on first frame and pushes its PCM. Safe to call from the
    /// server's receive threads.</summary>
    public void OnFrame(Guid studentId, AudioStreamFrameMessage frame)
    {
        if (!IsSupported || _disposed) return;
        var pcm = frame?.PcmData;
        if (pcm is null || pcm.Length == 0) return;

        int key;
        bool push = false, changed = false;
        lock (_lock)
        {
            EnsureStartedLocked();
            if (!_mixOk) return;
            key = KeyLocked(studentId);

            if (_mixed.Contains(studentId))
            {
                push = true;
            }
            else if (_mixed.Count < Cap)
            {
                // room in the mix — add (promotes an overflow source when a slot frees).
                _overflow.Remove(studentId);
                _mixed.Add(studentId);
                nty_mix_add(key);
                push = true;
                changed = true;
            }
            else if (_overflow.Add(studentId))
            {
                changed = true;   // newly overflow: counted + surfaced, not mixed
            }
        }

        // Push OUTSIDE the lock — the native enqueue is fast + self-locked, and a race with
        // a concurrent remove is a harmless dropped frame (native no-ops on an unknown id).
        if (push) nty_mix_push(key, pcm, pcm.Length);
        if (changed) RaiseStatus();
    }

    /// <summary>A student's mic closed (StudentAudioStreamStop) or the student left
    /// (disconnect) — remove its node so it can't leak CPU. Idempotent. The freed mix slot
    /// auto-fills from overflow on the next frame.</summary>
    public void OnStopped(Guid studentId)
    {
        if (!IsSupported || _disposed) return;
        int key;
        bool wasMixed, changed;
        lock (_lock)
        {
            if (!_keys.TryGetValue(studentId, out key)) return;   // never streamed
            wasMixed = _mixed.Remove(studentId);
            bool wasOverflow = _overflow.Remove(studentId);
            changed = wasMixed || wasOverflow;
        }
        if (wasMixed && _mixOk) nty_mix_remove(key);
        if (changed) RaiseStatus();
    }

    // ── native diagnostics passthrough (TT-9-D stall test / meters) ──────────────────
    public int NativeActiveCount() => IsSupported && _mixOk ? nty_mix_active_count() : 0;
    public long RenderedFrames() => IsSupported && _mixOk ? nty_mix_rendered_frames() : 0;
    public int OutputRms() => IsSupported && _mixOk ? nty_mix_output_rms() : 0;
    public long SourcePlayed(Guid studentId)
    {
        int key;
        lock (_lock) { if (!_keys.TryGetValue(studentId, out key)) return -1; }
        return IsSupported && _mixOk ? nty_mix_source_played(key) : -1;
    }

    private void EnsureStartedLocked()
    {
        if (_startAttempted) return;
        _startAttempted = true;
        int rc = nty_mix_start(16000, 1);
        _mixOk = rc == 0 || rc == -3;   // -3 == already running (idempotent)
        if (!_mixOk)
            Console.Error.WriteLine($"[TeacherAudioMixer] nty_mix_start rc={rc} — teacher will not hear student mics (no audio output device?).");
    }

    private int KeyLocked(Guid id)
    {
        if (!_keys.TryGetValue(id, out var k)) { k = _nextKey++; _keys[id] = k; }
        return k;
    }

    private void RaiseStatus()
    {
        int mixed, open;
        lock (_lock) { mixed = _mixed.Count; open = _mixed.Count + _overflow.Count; }
        StatusChanged?.Invoke(mixed, open, Cap);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_lock) { _mixed.Clear(); _overflow.Clear(); }
        if (IsSupported && _startAttempted && _mixOk)
        {
            try { nty_mix_stop(); } catch { /* teardown best-effort */ }
        }
        _mixOk = false;
    }
}
