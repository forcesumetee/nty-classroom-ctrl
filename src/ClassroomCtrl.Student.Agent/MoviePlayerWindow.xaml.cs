using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace ClassroomCtrl.Student.Agent;

public partial class MoviePlayerWindow : Window
{
    // Phase 10.20 — file-arrival polling state.  See Play().
    private DispatcherTimer? _waitTimer;
    private int _waitTicks;
    private const int WaitIntervalMs = 200;
    private const int WaitTimeoutMs = 30_000;
    private const int WaitMaxTicks = WaitTimeoutMs / WaitIntervalMs;   // 150

    public MoviePlayerWindow()
    {
        InitializeComponent();
        Closed += (_, _) => StopWaiting();
    }

    /// <summary>
    /// Phase 10.20 — file path the player looks at for incoming Net Movie content.
    /// Must match where <c>Student.Service.Modules.FileReceiver.Complete</c> actually
    /// writes the file.  Phase 10.13 moved the FileReceiver destination from
    /// <c>%PUBLIC%\Documents\Classroom\</c> to <c>%USERPROFILE%\Desktop\ClassroomFiles\</c>
    /// (Service runs in user session since Phase 10.9), but this player was never
    /// updated to match — the resulting File.Exists check always failed, the player
    /// set "Waiting for..." once and returned, and there was no recovery path.
    /// </summary>
    private static string GetExpectedFilePath(string fileName)
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        return Path.Combine(desktop, "ClassroomFiles", fileName);
    }

    public void Play(string fileName, double seekTime, long playAtUtcMs)
    {
        var path = GetExpectedFilePath(fileName);
        IpcClient.LogToFile($"[MoviePlayer] Play request: fileName='{fileName}' " +
            $"seekTime={seekTime} playAtMs={playAtUtcMs} expectedPath='{path}'");

        if (!File.Exists(path))
        {
            // Phase 10.20 — race-safe waiting + 30 s timeout.  Previously the
            // overlay was set once and the function returned; nothing ever polled
            // the disk again, so the only "fix" was for the customer to close +
            // reopen and hope the file showed up next time.  Now we poll every
            // 200 ms for up to 30 s; on arrival we recurse into Play() and the
            // playback path runs.  On timeout we replace the overlay with a
            // user-actionable error.
            StatusText.Text = $"Waiting for {fileName}... (0 s / 30 s)";
            StartWaitingForFile(fileName, seekTime, playAtUtcMs, path);
            return;
        }

        StopWaiting();
        try
        {
            // Phase 7.4 — Resume rewinding to 0:00 fix.
            //
            // Two bugs combined to make every Resume restart at 0:
            //   (1) `Player.Source = new Uri(path)` was reassigned even when
            //       resuming the same file.  WPF's MediaElement compares Source
            //       by Uri reference — every `new Uri(path)` is a fresh ref, so
            //       the player tore down and reloaded the buffer, resetting
            //       position to 0 in the process.
            //   (2) `Player.Position = TimeSpan.FromSeconds(seekTime)` was set
            //       BEFORE `Player.Play()`.  WPF MediaElement only honors
            //       Position once playback has started; pre-Play Position
            //       assignments are effectively no-ops on a freshly-loaded
            //       source.  So even teacher-sent SeekTime=30 landed on a
            //       player still at 0:00, then Play started — from 0.
            //
            // Fix: skip Source reassignment when the path is unchanged (resume
            // case) and seek AFTER Play() inside the timer callback so the
            // position assignment runs once the player has actually begun.
            var samePath = Player.Source != null
                && string.Equals(Player.Source.LocalPath, path,
                                 StringComparison.OrdinalIgnoreCase);
            if (!samePath) Player.Source = new Uri(path);

            var delayMs = playAtUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            Action startPlayback = () =>
            {
                Player.Play();
                if (seekTime > 0)
                    Player.Position = TimeSpan.FromSeconds(seekTime);
            };

            if (delayMs > 0 && delayMs < 5000)
            {
                var timer = new DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(delayMs) };
                timer.Tick += (_, _) => { timer.Stop(); startPlayback(); };
                timer.Start();
            }
            else { startPlayback(); }
            StatusText.Text = "";
            IpcClient.LogToFile($"[MoviePlayer] Playback armed: path='{path}' delayMs={delayMs}");
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            IpcClient.LogToFile($"[MoviePlayer] Play threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Phase 10.20 — kick off the per-200ms polling timer that waits for the file
    /// to land at the expected path.  Disposes any prior wait first.  See Play().
    /// </summary>
    private void StartWaitingForFile(string fileName, double seekTime, long playAtUtcMs, string expectedPath)
    {
        StopWaiting();
        _waitTicks = 0;
        _waitTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(WaitIntervalMs) };
        _waitTimer.Tick += (_, _) =>
        {
            _waitTicks++;
            if (File.Exists(expectedPath))
            {
                IpcClient.LogToFile($"[MoviePlayer] File arrived after {_waitTicks * WaitIntervalMs} ms: '{expectedPath}'");
                StopWaiting();
                Play(fileName, seekTime, playAtUtcMs);  // recursion: file now exists, hits the play branch
                return;
            }
            if (_waitTicks >= WaitMaxTicks)
            {
                IpcClient.LogToFile($"[MoviePlayer] Wait timed out after {WaitTimeoutMs} ms; file still not at '{expectedPath}'");
                StopWaiting();
                StatusText.Text =
                    $"Could not load {fileName} after {WaitTimeoutMs / 1000}s.\n" +
                    $"The teacher's file transfer did not arrive at:\n{expectedPath}\n" +
                    $"Check that the file is shared and the network allows the transfer.";
                return;
            }
            // Progress counter in the overlay so the user can see the wait isn't frozen.
            var elapsedSec = (_waitTicks * WaitIntervalMs) / 1000;
            StatusText.Text = $"Waiting for {fileName}... ({elapsedSec} s / {WaitTimeoutMs / 1000} s)";
        };
        _waitTimer.Start();
    }

    private void StopWaiting()
    {
        if (_waitTimer != null)
        {
            try { _waitTimer.Stop(); } catch { }
            _waitTimer = null;
        }
    }

    public void Pause(double seekTime)
    {
        Player.Pause();
        Player.Position = TimeSpan.FromSeconds(seekTime);
    }

    public void Seek(double seekTime) => Player.Position = TimeSpan.FromSeconds(seekTime);

    public void StopPlay()
    {
        StopWaiting();
        Player.Stop();
        Close();
    }
}
