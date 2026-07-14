using System.Threading.Tasks;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Teacher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Teacher.ViewModels;

/// <summary>
/// TT-8-C — the "Share My Screen" toggle. Wraps <see cref="TeacherScreenBroadcaster"/> and surfaces
/// the permission/error status so a denied Screen Recording grant is VISIBLE, not a silent no-op.
/// </summary>
public partial class ScreenShareViewModel : ObservableObject
{
    private readonly TeacherScreenBroadcaster _broadcaster;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private bool isSharing;

    [ObservableProperty] private string status = "";

    public string ButtonText => IsSharing ? "Stop Sharing" : "Share My Screen";

    public ScreenShareViewModel(TeacherScreenBroadcaster broadcaster)
    {
        _broadcaster = broadcaster;
        // FrameSent fires on the native delivery thread → marshal the status update to the UI thread.
        _broadcaster.FrameSent += seq =>
            Dispatcher.UIThread.Post(() => { if (IsSharing) Status = $"Sharing your screen · {seq} frames"; });
    }

    [RelayCommand]
    private async Task Toggle()
    {
        if (IsSharing)
        {
            await _broadcaster.StopAsync();
            IsSharing = false;
            Status = "";
            return;
        }

        if (!_broadcaster.HasPermission)
        {
            Status = "Requesting Screen Recording permission…";
            var granted = await _broadcaster.RequestPermissionAsync();
            if (!granted)
            {
                Status = "Screen Recording needed (System Settings ▸ Privacy) — grant, then relaunch";
                return;
            }
        }

        var rc = await _broadcaster.StartAsync();
        if (rc == 0)
        {
            IsSharing = true;
            Status = "Sharing your screen…";
        }
        else
        {
            Status = rc == -1
                ? "Screen Recording not permitted — grant it, then relaunch"
                : $"Could not start screen share (code {rc})";
        }
    }
}
