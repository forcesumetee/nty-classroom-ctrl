using ClassroomCtrl.Shared.Protocol;
using Microsoft.Extensions.Logging;
using NAudio.Wave;
using NReco.VideoConverter;
using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 5a + Phase 5 bug-fix: Recording service that mirrors the Python reference
/// (cv2.VideoWriter + pyaudio + post-mux) using FFmpeg via stdin pipe.
///
/// <para>
/// Pipeline:
///   1. Start():
///      • Spawn ffmpeg subprocess that reads raw BGRA from stdin and encodes
///        H.264 video-only MP4 in real time (libx264 ultrafast + zerolatency).
///      • Open NAudio WaveFileWriter to a sibling .wav file for the mic+system mix.
///   2. OnRawFrame(): write BGRA bytes to ffmpeg stdin (drop frame on pipe error).
///   3. OnAudioFrame(): append PCM to the WAV file.
///   4. StopAsync():
///      • Close stdin → ffmpeg sees EOF → finalizes video.mp4.
///      • Close WaveFileWriter.
///      • Run a SECOND ffmpeg pass (NReco.Invoke) to mux video.mp4 + audio.wav
///        into final.mp4 with -c:v copy (no re-encode) -c:a aac.
///      • Delete temps on success; keep them for manual recovery on failure.
/// </para>
/// Why two passes? Because we cannot feed a still-growing .wav file to a single
/// ffmpeg subprocess together with a stdin pipe — the WAV header isn't finalized
/// until the recording stops, so ffmpeg would mis-detect the duration. Splitting
/// gives us a clean encode + a clean mux, just like the Python OpenCV pattern.
/// </summary>
public sealed class RecordingService : IDisposable
{
    private readonly ILogger<RecordingService> _logger;
    private readonly object _lock = new();

    private string? _sessionId;
    private string? _videoTempPath;
    private string? _audioTempPath;
    private DateTimeOffset _startedAt;
    private bool _disposed;

    private WaveFileWriter? _audioWriter;
    private Process? _videoProc;
    private Stream? _videoStdin;
    private readonly StringBuilder _videoStderr = new();

    private int _videoFramesSeen;
    private long _videoBytesSeen;
    private int _videoFramesDropped;
    private int _capturedWidth;
    private int _capturedHeight;
    private int _capturedFps = 4;

    public bool IsRecording { get; private set; }
    public string? CurrentSessionId => _sessionId;
    public DateTimeOffset StartedAtUtc => _startedAt;

    public event EventHandler<string>? RecordingStarted;     // sessionId
    public event EventHandler<string>? RecordingStopped;     // final mp4 path
    public event EventHandler<string>? RecordingError;       // message

    public RecordingService(ILogger<RecordingService> logger)
    {
        _logger = logger;
    }

    public static string GetRecordingsFolder()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var dir = Path.Combine(root, "NTY", "ClassroomCtrl", "Recordings");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// Start a session. Caller must already know the broadcast resolution (read from
    /// <c>App.ScreenBroadcaster</c>); we trust that and lock it in for the session.
    /// </summary>
    public bool Start(int width, int height, int fps)
    {
        lock (_lock)
        {
            if (IsRecording) return true;

            try
            {
                var folder = GetRecordingsFolder();
                _startedAt = DateTimeOffset.Now;
                _sessionId = $"classroom-{_startedAt:yyyy-MM-dd-HHmmss}";
                _videoTempPath = Path.Combine(folder, $"{_sessionId}.video.mp4");
                _audioTempPath = Path.Combine(folder, $"{_sessionId}.wav");

                _capturedWidth = width;
                _capturedHeight = height;
                _capturedFps = Math.Max(1, fps);
                _videoFramesSeen = 0;
                _videoBytesSeen = 0;
                _videoFramesDropped = 0;
                _videoStderr.Clear();

                _audioWriter = new WaveFileWriter(_audioTempPath, new WaveFormat(16000, 16, 1));

                StartVideoFFmpegLocked();

                IsRecording = true;
                _logger.LogInformation("Recording started: session={Session} {W}x{H}@{Fps}",
                    _sessionId, width, height, fps);
                App.LogDebug($"[Rec] Start session={_sessionId} {width}x{height}@{fps} videoTemp={_videoTempPath}");
                RecordingStarted?.Invoke(this, _sessionId);
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to start recording");
                App.LogDebug($"[Rec] Start FAILED: {ex.Message}");
                CleanupLocked();
                RecordingError?.Invoke(this, ex.Message);
                return false;
            }
        }
    }

    private void StartVideoFFmpegLocked()
    {
        var ffmpegPath = ResolveFFmpegPath();
        if (ffmpegPath == null || !File.Exists(ffmpegPath))
            throw new InvalidOperationException($"ffmpeg.exe not found (resolved: {ffmpegPath ?? "(null)"})");

        // Real-time encode pipeline: raw BGRA → libx264 ultrafast → MP4 video-only
        var args =
            $"-y -hide_banner -loglevel warning " +
            $"-f rawvideo -pixel_format bgra -video_size {_capturedWidth}x{_capturedHeight} -framerate {_capturedFps} " +
            $"-i pipe:0 -an " +
            $"-c:v libx264 -preset ultrafast -tune zerolatency -crf 23 -pix_fmt yuv420p " +
            $"-movflags +faststart \"{_videoTempPath}\"";

        App.LogDebug($"[Rec] Spawning ffmpeg: {ffmpegPath} {args}");

        var psi = new ProcessStartInfo
        {
            FileName = ffmpegPath,
            Arguments = args,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        _videoProc = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start ffmpeg process");

        _videoProc.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
            {
                lock (_videoStderr) _videoStderr.AppendLine(e.Data);
            }
        };
        _videoProc.BeginErrorReadLine();

        _videoStdin = _videoProc.StandardInput.BaseStream;
    }

    /// <summary>
    /// Forward a raw BGRA frame to ffmpeg stdin. Drops the frame silently if the
    /// pipe is closed (ffmpeg crash, recording already stopped).
    /// </summary>
    public void OnRawFrame(byte[] bgra, int width, int height)
    {
        if (!IsRecording || bgra.Length == 0) return;

        lock (_lock)
        {
            if (!IsRecording || _videoStdin == null) return;

            // Reject mismatched dimensions — ffmpeg was started with a fixed -video_size
            if (width != _capturedWidth || height != _capturedHeight)
            {
                if (_videoFramesDropped < 3)
                    App.LogDebug($"[Rec] OnRawFrame size mismatch incoming={width}x{height} expected={_capturedWidth}x{_capturedHeight}");
                _videoFramesDropped++;
                return;
            }

            try
            {
                _videoStdin.Write(bgra, 0, bgra.Length);
                _videoFramesSeen++;
                _videoBytesSeen += bgra.Length;
                if (_videoFramesSeen <= 5 || _videoFramesSeen % 50 == 0)
                    App.LogDebug($"[Rec] OnRawFrame wrote frame {_videoFramesSeen} ({bgra.Length} bytes, total={_videoBytesSeen})");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recording video pipe write failed");
                App.LogDebug($"[Rec] OnRawFrame pipe write FAILED: {ex.Message}");
                FailLocked(ex.Message);
            }
        }
    }

    /// <summary>Append PCM audio (16 kHz mono 16-bit, matching AudioBroadcaster output).</summary>
    public void OnAudioFrame(byte[] pcm, int sampleRate, int channels, int bitsPerSample)
    {
        if (!IsRecording || pcm.Length == 0) return;

        lock (_lock)
        {
            if (!IsRecording || _audioWriter == null) return;
            try
            {
                _audioWriter.Write(pcm, 0, pcm.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Recording audio write failed");
                App.LogDebug($"[Rec] OnAudioFrame write FAILED: {ex.Message}");
                FailLocked(ex.Message);
            }
        }
    }

    /// <summary>
    /// Close stdin → wait for ffmpeg → mux video+audio → emit final MP4 path.
    /// </summary>
    public async Task<string?> StopAsync()
    {
        string? videoTemp;
        string? audioTemp;
        string sessionId;
        Process? proc;
        Stream? stdin;
        int frames;
        long bytes;

        lock (_lock)
        {
            if (!IsRecording) return null;
            IsRecording = false;
            videoTemp = _videoTempPath;
            audioTemp = _audioTempPath;
            sessionId = _sessionId ?? "unknown";
            proc = _videoProc;
            stdin = _videoStdin;
            frames = _videoFramesSeen;
            bytes = _videoBytesSeen;

            try { _audioWriter?.Flush(); _audioWriter?.Dispose(); } catch { }
            _audioWriter = null;

            // We deliberately DO NOT null _videoProc / _videoStdin yet — finalization happens below
            // outside the lock to avoid blocking other event paths.
        }

        App.LogDebug($"[Rec] Stop session={sessionId} totalVideoFrames={frames} totalVideoBytes={bytes} dropped={_videoFramesDropped}");

        // Finalize the video subprocess: close stdin, wait for clean exit, capture stderr.
        try
        {
            if (stdin != null)
            {
                try { stdin.Close(); } catch { }
            }

            if (proc != null)
            {
                if (!proc.WaitForExit(15_000))
                {
                    App.LogDebug("[Rec] ffmpeg did not exit in 15s — killing");
                    try { proc.Kill(); } catch { }
                    proc.WaitForExit(2_000);
                }
                proc.WaitForExit();  // ensure async stderr drained

                string stderrSnapshot;
                lock (_videoStderr) stderrSnapshot = _videoStderr.ToString();
                App.LogDebug($"[Rec] ffmpeg exit code={proc.ExitCode}\n[Rec] ffmpeg stderr (truncated):\n{Truncate(stderrSnapshot, 4000)}");

                proc.Dispose();
            }
        }
        catch (Exception ex)
        {
            App.LogDebug($"[Rec] Video subprocess teardown error: {ex.Message}");
        }
        finally
        {
            lock (_lock)
            {
                _videoProc = null;
                _videoStdin = null;
            }
        }

        if (videoTemp == null || audioTemp == null) return null;

        // Sanity-check temp file sizes
        try
        {
            var vSize = new FileInfo(videoTemp).Exists ? new FileInfo(videoTemp).Length : 0;
            var aSize = new FileInfo(audioTemp).Exists ? new FileInfo(audioTemp).Length : 0;
            App.LogDebug($"[Rec] Temp file sizes after encode: video={vSize} bytes, audio={aSize} bytes");
            if (vSize == 0)
            {
                App.LogDebug("[Rec] WARNING: video temp is empty — encode failed (check stderr above)");
                RecordingError?.Invoke(this, "Recording failed: video file is empty");
                return null;
            }
        }
        catch { }

        var folder = GetRecordingsFolder();
        var outputPath = Path.Combine(folder, $"{sessionId}.mp4");

        // Pass 2: mux video.mp4 + audio.wav → final.mp4 (no video re-encode)
        try
        {
            await Task.Run(() => MuxFinal(videoTemp, audioTemp, outputPath));

            TryDelete(videoTemp);
            TryDelete(audioTemp);

            _logger.LogInformation("Recording saved: {Path}", outputPath);
            App.LogDebug($"[Rec] Mux SUCCESS → {outputPath}");
            RecordingStopped?.Invoke(this, outputPath);
            return outputPath;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mux pass failed — keeping temp files for manual recovery");
            App.LogDebug($"[Rec] Mux FAILED: {ex.Message} — temps kept at {folder}");
            RecordingError?.Invoke(this, $"Mux failed: {ex.Message} — temp files kept at {folder}");
            return null;
        }
    }

    private static void MuxFinal(string videoMp4, string audioWav, string outputMp4)
    {
        var ff = new FFMpegConverter();
        var stderrBuf = new StringBuilder();
        ff.LogReceived += (_, ev) => { if (!string.IsNullOrEmpty(ev.Data)) stderrBuf.AppendLine(ev.Data); };

        var args =
            $"-y -hide_banner -loglevel warning " +
            $"-i \"{videoMp4}\" -i \"{audioWav}\" " +
            $"-c:v copy -c:a aac -b:a 128k -movflags +faststart \"{outputMp4}\"";

        App.LogDebug($"[Rec] Mux cmd: {args}");
        try
        {
            ff.Invoke(args);
            App.LogDebug($"[Rec] Mux stderr (truncated):\n{Truncate(stderrBuf.ToString(), 4000)}");
        }
        catch (Exception)
        {
            App.LogDebug($"[Rec] Mux threw. stderr:\n{Truncate(stderrBuf.ToString(), 4000)}");
            throw;
        }
    }

    /// <summary>Pre-extract ffmpeg.exe (one-time cost, off the UI thread).</summary>
    public static void WarmUpFFmpeg(ILogger? logger = null)
    {
        try
        {
            var ff = new FFMpegConverter();
            ff.ExtractFFmpeg();
            var path = ResolveFFmpegPath();
            logger?.LogInformation("FFmpeg warmed up at {Path}", path ?? "(unresolved)");
            App.LogDebug($"[Rec] FFmpeg warm-up resolved to: {path ?? "(unresolved)"}");
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "FFmpeg warm-up failed");
            App.LogDebug($"[Rec] FFmpeg warm-up failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Locate the ffmpeg.exe extracted by NReco. Checks the converter's reported tool path,
    /// the app base directory, and the current working directory in that order.
    /// </summary>
    public static string? ResolveFFmpegPath()
    {
        try
        {
            var ff = new FFMpegConverter();
            ff.ExtractFFmpeg();  // idempotent
            var exeName = string.IsNullOrEmpty(ff.FFMpegExeName) ? "ffmpeg.exe" : ff.FFMpegExeName;

            var candidates = new[]
            {
                string.IsNullOrEmpty(ff.FFMpegToolPath) ? null : Path.Combine(ff.FFMpegToolPath, exeName),
                Path.Combine(AppContext.BaseDirectory, exeName),
                Path.Combine(Directory.GetCurrentDirectory(), exeName),
            };
            foreach (var c in candidates)
            {
                if (!string.IsNullOrEmpty(c) && File.Exists(c)) return c;
            }
        }
        catch { }
        return null;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...[truncated]";

    private void FailLocked(string message)
    {
        IsRecording = false;
        CleanupLocked();
        RecordingError?.Invoke(this, message);
    }

    private void CleanupLocked()
    {
        try { _videoStdin?.Close(); } catch { }
        _videoStdin = null;

        try
        {
            if (_videoProc != null)
            {
                if (!_videoProc.HasExited) _videoProc.Kill();
                _videoProc.Dispose();
            }
        }
        catch { }
        _videoProc = null;

        try { _audioWriter?.Dispose(); } catch { }
        _audioWriter = null;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsRecording)
        {
            try { StopAsync().GetAwaiter().GetResult(); } catch { }
        }
    }
}
