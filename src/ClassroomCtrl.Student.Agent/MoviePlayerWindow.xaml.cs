using System;
using System.IO;
using System.Windows;

namespace ClassroomCtrl.Student.Agent;

public partial class MoviePlayerWindow : Window
{
    public MoviePlayerWindow()
    {
        InitializeComponent();
    }

    public void Play(string fileName, double seekTime, long playAtUtcMs)
    {
        var path = Path.Combine(
            Environment.GetEnvironmentVariable("PUBLIC") ?? @"C:\Users\Public",
            "Documents", "Classroom", fileName);
        if (!File.Exists(path))
        {
            StatusText.Text = $"Waiting for {fileName}...";
            return;
        }
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
                var timer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(delayMs) };
                timer.Tick += (_, _) => { timer.Stop(); startPlayback(); };
                timer.Start();
            }
            else { startPlayback(); }
            StatusText.Text = "";
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    public void Pause(double seekTime)
    {
        Player.Pause();
        Player.Position = TimeSpan.FromSeconds(seekTime);
    }

    public void Seek(double seekTime) => Player.Position = TimeSpan.FromSeconds(seekTime);

    public void StopPlay()
    {
        Player.Stop();
        Close();
    }
}
