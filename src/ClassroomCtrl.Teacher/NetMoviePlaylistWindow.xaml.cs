using ClassroomCtrl.Shared.Protocol;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace ClassroomCtrl.Teacher;

/// <summary>
/// Phase 9.6: Sync video playback. Distributes the file via the existing
/// FileTransfer pipeline (FileAnnounce/FileChunk/FileComplete), then triggers
/// MoviePlay on all students.
/// </summary>
public partial class NetMoviePlaylistWindow : Window
{
    private readonly List<string> _playlist = new();
    private bool _isUploading;
    private bool _isPaused;
    private string? _activeFile;

    // Phase 7 Section C — Pause bug fix.  The student's MoviePause handler does
    // `Player.Pause(); Player.Position = TimeSpan.FromSeconds(seekTime)`, so the
    // hard-coded SeekTime=0 we used to send rewound the playhead every Pause
    // click.  Track when t=0 of the active video aligns with UTC ("virtual
    // start"), then on Pause compute elapsed = now - virtual_start and send
    // that as the seek target so the student pauses in place.  On Resume,
    // re-broadcast MoviePlay starting from _pausedAtSeconds and shift the
    // virtual start to compensate for the future PlayAtTimestampMs offset.
    private DateTime? _virtualStartUtc;
    private double _pausedAtSeconds;

    public NetMoviePlaylistWindow()
    {
        InitializeComponent();
        PlaylistBox.ItemsSource = _playlist;
    }

    private void AddFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "Video files (*.mp4;*.avi;*.wmv;*.mkv)|*.mp4;*.avi;*.wmv;*.mkv|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog() == true)
        {
            foreach (var f in dlg.FileNames) _playlist.Add(f);
            RefreshList();
        }
    }

    private void RemoveFile_Click(object sender, RoutedEventArgs e)
    {
        if (PlaylistBox.SelectedItem is string s)
        {
            _playlist.Remove(s);
            RefreshList();
        }
    }

    private void RefreshList()
    {
        PlaylistBox.ItemsSource = null;
        PlaylistBox.ItemsSource = _playlist;
    }

    private async void Play_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        if (_isUploading) { StatusText.Text = "Already uploading..."; return; }
        if (PlaylistBox.SelectedItem is not string filePath || !File.Exists(filePath))
        {
            MessageBox.Show("Select a file first.");
            return;
        }

        _isUploading = true;
        StatusText.Text = "Uploading file to all students...";
        try
        {
            // Reuse existing file transfer to distribute the .mp4 to %PUBLIC%\Documents\Classroom
            await SendFileToAllAsync(filePath, CancellationToken.None);

            // Issue MoviePlay scheduled 1 sec in the future to give students time to load.
            var msg = new MoviePlayMessage
            {
                FileName = Path.GetFileName(filePath),
                SeekTime = 0,
                PlayAtTimestampMs = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds(),
            };
            await App.Server.BroadcastMoviePlayAsync(msg, CancellationToken.None);
            _activeFile = msg.FileName;
            _isPaused = false;
            _pausedAtSeconds = 0;
            _virtualStartUtc = DateTimeOffset.FromUnixTimeMilliseconds(msg.PlayAtTimestampMs).UtcDateTime;
            StatusText.Text = $"Playing: {msg.FileName}";
        }
        catch (Exception ex) { StatusText.Text = $"Error: {ex.Message}"; }
        finally { _isUploading = false; }
    }

    /// <summary>
    /// Pause/Resume toggle.  Phase 7 Section C — sends the *current* elapsed
    /// playback time so the student pauses in place; on resume, re-issues
    /// MoviePlay with SeekTime = paused position and updates the virtual start
    /// time to keep the elapsed-seconds math consistent for the next pause.
    /// </summary>
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        if (_isPaused)
        {
            if (string.IsNullOrEmpty(_activeFile)) return;
            var playAt = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds();
            var msg = new MoviePlayMessage
            {
                FileName = _activeFile,
                SeekTime = _pausedAtSeconds,
                PlayAtTimestampMs = playAt,
            };
            await App.Server.BroadcastMoviePlayAsync(msg, CancellationToken.None);
            // Anchor virtual start so (now+1) → SeekTime = _pausedAtSeconds.
            _virtualStartUtc = DateTimeOffset.FromUnixTimeMilliseconds(playAt).UtcDateTime
                                .AddSeconds(-_pausedAtSeconds);
            _isPaused = false;
            StatusText.Text = $"Resumed: {_activeFile}";
        }
        else
        {
            // Compute current playback position from virtual start.  Clamp to
            // 0 so a click before the 1-second start delay doesn't go negative.
            double elapsed = _virtualStartUtc.HasValue
                ? Math.Max(0, (DateTime.UtcNow - _virtualStartUtc.Value).TotalSeconds)
                : 0;
            await App.Server.BroadcastMoviePauseAsync(
                new MovieSeekMessage { SeekTime = elapsed },
                CancellationToken.None);
            _pausedAtSeconds = elapsed;
            _isPaused = true;
            StatusText.Text = $"Paused @ {TimeSpan.FromSeconds(elapsed):mm\\:ss}";
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        await App.Server.BroadcastMovieStopAsync(CancellationToken.None);
        _isPaused = false;
        _activeFile = null;
        _virtualStartUtc = null;
        _pausedAtSeconds = 0;
        StatusText.Text = "Stopped";
    }

    /// <summary>Reuses the existing FileTransfer protocol to push the movie file to all students.</summary>
    private static Task SendFileToAllAsync(string path, CancellationToken ct)
        => App.Server!.BroadcastFileAsync(path, ct);
}
