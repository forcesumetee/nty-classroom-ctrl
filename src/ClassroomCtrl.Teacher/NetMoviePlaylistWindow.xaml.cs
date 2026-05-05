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
            StatusText.Text = $"Playing: {msg.FileName}";
        }
        catch (Exception ex) { StatusText.Text = $"Error: {ex.Message}"; }
        finally { _isUploading = false; }
    }

    /// <summary>
    /// Pause/Resume toggle — re-issues a MoviePlay scheduled 1 sec out when resuming
    /// since there is no dedicated Resume message; students start at SeekTime=0
    /// each toggle (best-effort sync given the existing protocol).
    /// </summary>
    private async void Pause_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        if (_isPaused)
        {
            if (string.IsNullOrEmpty(_activeFile)) return;
            var msg = new MoviePlayMessage
            {
                FileName = _activeFile,
                SeekTime = 0,
                PlayAtTimestampMs = DateTimeOffset.UtcNow.AddSeconds(1).ToUnixTimeMilliseconds(),
            };
            await App.Server.BroadcastMoviePlayAsync(msg, CancellationToken.None);
            _isPaused = false;
            StatusText.Text = $"Resumed: {_activeFile}";
        }
        else
        {
            await App.Server.BroadcastMoviePauseAsync(new MovieSeekMessage { SeekTime = 0 }, CancellationToken.None);
            _isPaused = true;
            StatusText.Text = "Paused";
        }
    }

    private async void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (App.Server == null) return;
        await App.Server.BroadcastMovieStopAsync(CancellationToken.None);
        _isPaused = false;
        _activeFile = null;
        StatusText.Text = "Stopped";
    }

    /// <summary>Reuses the existing FileTransfer protocol to push the movie file to all students.</summary>
    private static Task SendFileToAllAsync(string path, CancellationToken ct)
        => App.Server!.BroadcastFileAsync(path, ct);
}
