using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 5b — Per-student screen recording.
///
/// Spawns one ffmpeg subprocess per student. Pipeline:
///   raw BGRA on stdin → libx264 (ultrafast / zerolatency / crf 23 / yuv420p) → MP4
///
/// Differences from Phase 5a (RecordingService):
///   • No audio path. Per-student audio capture is out of scope for v1; the
///     decoded student frame stream is video-only by definition.
///   • No second-pass mux. Direct video.mp4 is the final output.
///   • Multiple concurrent sessions, keyed by student endpoint Guid.
///
/// Frame source: <see cref="OnFrame"/> is called from <c>StudentScreenWindow</c>
/// every time a frame finishes decoding, gated by <see cref="IsRecording"/> so
/// we don't pay the BGRA-copy tax when nobody's recording.
/// </summary>
public sealed class PerStudentRecordingService : IDisposable
{
    private readonly ILogger<PerStudentRecordingService> _logger;
    private readonly ConcurrentDictionary<Guid, Session> _sessions = new();

    public PerStudentRecordingService(ILogger<PerStudentRecordingService> logger)
    {
        _logger = logger;
    }

    public static string GetFolder()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var dir = Path.Combine(root, "NTY", "ClassroomCtrl", "Recordings", "PerStudent");
        Directory.CreateDirectory(dir);
        return dir;
    }

    public bool IsRecording(Guid studentId) => _sessions.ContainsKey(studentId);

    /// <summary>
    /// Begin a session. Width/height/fps are first-frame guesses; the ffmpeg subprocess
    /// is actually spawned lazily on the FIRST OnFrame call so we can lock in the
    /// real captured dimensions (avoids size-mismatch drops if guesses are wrong).
    /// </summary>
    public bool Start(Guid studentId, string studentName, int hintWidth, int hintHeight, int fps)
    {
        if (_sessions.ContainsKey(studentId)) return true;

        var safeName = SanitizeFilename(studentName);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        var folder = Path.Combine(GetFolder(), $"{safeName}_{stamp}");
        Directory.CreateDirectory(folder);
        var outPath = Path.Combine(folder, "final.mp4");

        var session = new Session(studentId, studentName, outPath, fps);
        if (!_sessions.TryAdd(studentId, session)) return false;

        _logger.LogInformation("Per-student recording START {Id} ({Name}) → {Path}", studentId, studentName, outPath);
        return true;
    }

    public async Task<string?> StopAsync(Guid studentId)
    {
        if (!_sessions.TryRemove(studentId, out var s)) return null;

        try
        {
            // Close stdin → ffmpeg sees EOF → finalizes mp4.
            try { s.Stdin?.Close(); } catch { }
            if (s.FFmpeg != null)
            {
                if (!s.FFmpeg.WaitForExit(15_000))
                {
                    _logger.LogWarning("ffmpeg did not exit in 15s for {Id} — killing", studentId);
                    try { s.FFmpeg.Kill(); } catch { }
                    s.FFmpeg.WaitForExit(2_000);
                }
                s.FFmpeg.Dispose();
            }
            await Task.Yield();

            var fi = new FileInfo(s.OutputPath);
            if (!fi.Exists || fi.Length == 0)
            {
                _logger.LogWarning("Per-student recording {Id}: output missing/empty (no frames captured?)", studentId);
                return null;
            }
            _logger.LogInformation("Per-student recording STOP {Id}: {Frames} frames → {Path}",
                studentId, s.FramesWritten, s.OutputPath);
            return s.OutputPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Per-student recording stop failed for {Id}", studentId);
            return null;
        }
    }

    public async Task StopAllAsync()
    {
        var ids = _sessions.Keys.ToList();
        foreach (var id in ids) await StopAsync(id);
    }

    /// <summary>Pipe a decoded BGRA frame to the per-student ffmpeg subprocess. Lazy-spawn on first frame.</summary>
    public void OnFrame(Guid studentId, byte[] bgra, int width, int height)
    {
        if (!_sessions.TryGetValue(studentId, out var s)) return;
        if (bgra.Length == 0) return;

        // Lazy-spawn ffmpeg on first frame so we know the actual capture size.
        if (s.FFmpeg == null)
        {
            try { SpawnFFmpegLocked(s, width, height); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "ffmpeg spawn failed for {Id}", studentId);
                _sessions.TryRemove(studentId, out _);
                return;
            }
        }

        // Reject mismatched dimensions silently after first frame is locked in.
        if (width != s.Width || height != s.Height)
        {
            s.FramesDropped++;
            return;
        }

        try
        {
            s.Stdin?.Write(bgra, 0, bgra.Length);
            s.FramesWritten++;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ffmpeg pipe write failed for {Id}", studentId);
            // Force stop on pipe failure.
            _ = StopAsync(studentId);
        }
    }

    private void SpawnFFmpegLocked(Session s, int width, int height)
    {
        var ffmpegPath = RecordingService.ResolveFFmpegPath()
            ?? throw new InvalidOperationException("ffmpeg.exe not resolvable (RecordingService warm-up missing?)");

        s.Width = width;
        s.Height = height;

        var args =
            $"-y -hide_banner -loglevel warning " +
            $"-f rawvideo -pixel_format bgra -video_size {width}x{height} -framerate {s.Fps} " +
            $"-i pipe:0 -an " +
            $"-c:v libx264 -preset ultrafast -tune zerolatency -crf 23 -pix_fmt yuv420p " +
            $"-movflags +faststart \"{s.OutputPath}\"";

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg subprocess");
        proc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                lock (s.Stderr) s.Stderr.AppendLine(e.Data);
        };
        proc.BeginErrorReadLine();

        s.FFmpeg = proc;
        s.Stdin = proc.StandardInput.BaseStream;

        _logger.LogInformation("Spawned ffmpeg for {Name} @ {W}x{H}@{Fps}fps → {Path}",
            s.StudentName, width, height, s.Fps, s.OutputPath);
    }

    private static string SanitizeFilename(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var clean = new string(s.Where(c => !bad.Contains(c)).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "Student" : clean;
    }

    public void Dispose()
    {
        // Best-effort flush of any active sessions.
        var ids = _sessions.Keys.ToList();
        foreach (var id in ids)
        {
            if (_sessions.TryRemove(id, out var s))
            {
                try { s.Stdin?.Close(); } catch { }
                try { s.FFmpeg?.WaitForExit(2_000); } catch { }
                try { s.FFmpeg?.Dispose(); } catch { }
            }
        }
    }

    private sealed class Session
    {
        public Guid StudentId;
        public string StudentName;
        public string OutputPath;
        public int Fps;
        public int Width;
        public int Height;
        public Process? FFmpeg;
        public Stream? Stdin;
        public int FramesWritten;
        public int FramesDropped;
        public readonly StringBuilder Stderr = new();

        public Session(Guid id, string name, string path, int fps)
        {
            StudentId = id;
            StudentName = name;
            OutputPath = path;
            Fps = fps;
        }
    }
}
