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
            Player.Source = new Uri(path);
            Player.Position = TimeSpan.FromSeconds(seekTime);

            var delayMs = playAtUtcMs - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (delayMs > 0 && delayMs < 5000)
            {
                var timer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(delayMs) };
                timer.Tick += (_, _) => { timer.Stop(); Player.Play(); };
                timer.Start();
            }
            else { Player.Play(); }
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
