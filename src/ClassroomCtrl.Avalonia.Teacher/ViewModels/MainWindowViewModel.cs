using System;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClassroomCtrl.Avalonia.Teacher.ViewModels;

/// <summary>
/// TT-2-D — the window VM: the student grid + the status affordances (the listen
/// address so you know what IP to point a student at, the connected count, and the
/// empty-state flag). Recomputes on every grid change (which arrive on the UI thread
/// — the grid marshals its mutations, so this handler is UI-thread-safe).
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    public TeacherGridViewModel Grid { get; }

    [ObservableProperty] private string listenAddress;
    [ObservableProperty] private string statusText = "No students connected";
    [ObservableProperty] private bool isEmpty = true;

    /// <summary>TT-7-C — the chat rail VM. Set by App once the session/sound are built
    /// (it needs the messaging seam). Observable so the rail binds when it arrives.</summary>
    [ObservableProperty] private ChatViewModel? chat;

    /// <summary>TT-8-C — the "Share My Screen" VM (set by App). Observable so the header
    /// button binds when it arrives.</summary>
    [ObservableProperty] private ScreenShareViewModel? screenShare;

    /// <summary>TT-10-B — the "Talk to Class" VM (set by App). Observable so the header
    /// button binds when it arrives.</summary>
    [ObservableProperty] private TalkViewModel? talk;

    /// <summary>TT-10-C — the "Share Computer Audio" VM (set by App).</summary>
    [ObservableProperty] private ShareAudioViewModel? shareAudio;

    /// <summary>TT-7-C — the transient notification banner (hand-raise / incoming chat).
    /// null = hidden. Auto-clears after a few seconds.</summary>
    [ObservableProperty] private string? toast;

    /// <summary>TT-12 — "Send File to Class" progress / result line (null = idle). Set from App's
    /// send handler on a background thread → marshal via SetFileStatus.</summary>
    [ObservableProperty] private string? fileStatus;

    /// <summary>Thread-safe setter for <see cref="FileStatus"/> (the send runs off the UI thread).</summary>
    public void SetFileStatus(string? text) => Dispatcher.UIThread.Post(() => FileStatus = text);

    public MainWindowViewModel(TeacherGridViewModel grid, string listenAddress)
    {
        Grid = grid;
        this.listenAddress = listenAddress;
        Grid.Students.CollectionChanged += OnStudentsChanged;
        Refresh();
    }

    /// <summary>Show a transient toast. Marshaled to the UI thread and self-clearing; the
    /// grid/chat call this from already-UI-thread handlers, but the marshal keeps it safe
    /// from any caller.</summary>
    public void ShowToast(string text) => Dispatcher.UIThread.Post(() =>
    {
        Toast = text;
        DispatcherTimer.RunOnce(() => { if (Toast == text) Toast = null; }, TimeSpan.FromSeconds(4));
    });

    private void OnStudentsChanged(object? sender, NotifyCollectionChangedEventArgs e) => Refresh();

    private void Refresh()
    {
        int n = Grid.Students.Count;
        IsEmpty = n == 0;
        StatusText = n switch
        {
            0 => "No students connected",
            1 => "1 student connected",
            _ => $"{n} students connected",
        };
    }
}
