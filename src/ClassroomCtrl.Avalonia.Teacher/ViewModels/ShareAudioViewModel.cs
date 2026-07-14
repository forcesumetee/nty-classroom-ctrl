using System.Threading.Tasks;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Teacher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Teacher.ViewModels;

/// <summary>
/// TT-10-C — the "Share Computer Audio" toggle (mirrors TalkViewModel/ScreenShareViewModel). Streams
/// the Mac's system audio to all students. Pairs with "Share My Screen": teacher plays a video →
/// students see the picture (TT-8) AND hear the sound (this). A denied Screen Recording grant is
/// VISIBLE, not a silent no-op; the ON state is LOUD.
/// </summary>
public partial class ShareAudioViewModel : ObservableObject
{
    private readonly TeacherSystemAudioBroadcaster _broadcaster;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private bool isSharing;

    [ObservableProperty] private string status = "";

    public string ButtonText => IsSharing ? "🔴 Sharing Audio — Stop" : "🔊 Share Computer Audio";

    public ShareAudioViewModel(TeacherSystemAudioBroadcaster broadcaster)
    {
        _broadcaster = broadcaster;
        _broadcaster.FrameSent += seq =>
        {
            if (seq % 10 != 0) return;   // ~1 s cadence
            Dispatcher.UIThread.Post(() => { if (IsSharing) Status = $"🔴 Sharing computer audio · {seq / 10}s"; });
        };
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
                Status = "Screen Recording needed (System Settings ▸ Privacy) — grant, then RELAUNCH";
                return;
            }
        }

        var rc = await _broadcaster.StartAsync();
        if (rc == 0)
        {
            IsSharing = true;
            Status = "🔴 Sharing computer audio…";
        }
        else
        {
            Status = rc switch
            {
                -3 => "Screen Recording not permitted — grant it, then RELAUNCH the app",
                -5 => "System-audio capture needs macOS 13+",
                _ => $"Could not start audio share (code {rc})",
            };
        }
    }
}
