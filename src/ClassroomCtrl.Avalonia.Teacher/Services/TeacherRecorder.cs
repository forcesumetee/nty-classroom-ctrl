using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ClassroomCtrl.Avalonia.Teacher.Services;

/// <summary>
/// TT-13 / TOR 11.2.9 — record the teacher's SCREEN + system AUDIO to a .mov via the native
/// AVAssetWriter recorder (libNtyCapture: nty_record_*). Every capture piece was proven LIVE today;
/// this only starts/stops the file writer and reports where it landed. Screen Recording TCC (same
/// grant as TT-8 Share My Screen) — a signed bundle with the grant; a denied grant surfaces as a
/// VISIBLE error (rc -3), never a silent no-op.
///
/// Audio = SYSTEM audio (what the Mac plays, e.g. a video). Mic / mic+system-mix is a documented
/// follow-up (see PROJECT-HANDOVER); the single-SCStream screen+system path is the proven one.
/// </summary>
public sealed partial class TeacherRecorder
{
    private const string Lib = "NtyCapture";

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int nty_record_start(string path);
    [LibraryImport(Lib)] private static partial int nty_record_stop();
    [LibraryImport(Lib)] private static partial int nty_record_is_active();
    [LibraryImport(Lib)] private static partial long nty_record_video_frames();
    [LibraryImport(Lib)] private static partial long nty_record_audio_frames();

    public string? CurrentPath { get; private set; }

    public bool IsRecording => OperatingSystem.IsMacOS() && nty_record_is_active() == 1;

    public long VideoFrames => OperatingSystem.IsMacOS() ? nty_record_video_frames() : 0;
    public long AudioFrames => OperatingSystem.IsMacOS() ? nty_record_audio_frames() : 0;

    /// <summary>Where recordings land: ~/Movies/NTY Recordings (Movies for QuickTime discoverability).</summary>
    public static string RecordingsDir
    {
        get
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var movies = Path.Combine(home, "Movies");
            var baseDir = Directory.Exists(movies) ? movies : home;
            return Path.Combine(baseDir, "NTY Recordings");
        }
    }

    /// <summary>Start recording. Returns (ok, message/path). The message is the file path on success,
    /// or a human-readable reason on failure.</summary>
    public (bool ok, string message) Start()
    {
        if (!OperatingSystem.IsMacOS()) return (false, "Recording is macOS-only");
        Directory.CreateDirectory(RecordingsDir);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var path = Path.Combine(RecordingsDir, $"NTY-Lesson-{stamp}.mov");
        var rc = nty_record_start(path);
        if (rc == 0) { CurrentPath = path; return (true, path); }
        CurrentPath = null;
        return (false, Describe(rc));
    }

    /// <summary>Stop recording. Returns (ok, path, videoFrames, audioFrames) for confirmation.</summary>
    public (bool ok, string? path, long video, long audio) Stop()
    {
        if (!OperatingSystem.IsMacOS()) return (false, null, 0, 0);
        long v = VideoFrames, a = AudioFrames;
        var rc = nty_record_stop();
        var path = CurrentPath;
        CurrentPath = null;
        return (rc == 0, path, v, a);
    }

    private static string Describe(int rc) => rc switch
    {
        -1 => "Not recording",
        -2 => "No display found",
        -3 => "Screen Recording permission needed — System Settings ▸ Privacy ▸ Screen Recording, then RELAUNCH",
        -4 => "Recording needs macOS 13 or later",
        -5 => "Already recording",
        -6 => "Could not create the recording file",
        _  => $"Could not start recording (code {rc})",
    };
}
