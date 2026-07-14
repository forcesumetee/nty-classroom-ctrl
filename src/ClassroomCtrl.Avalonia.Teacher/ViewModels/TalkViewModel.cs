using System.Threading.Tasks;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Teacher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Teacher.ViewModels;

/// <summary>
/// TT-10-B — the "Talk to Class" toggle (mirrors ScreenShareViewModel). A single toggle, not
/// push-to-talk: a teacher addressing a class talks in stretches. The ON state is LOUD —
/// <see cref="IsTalking"/> drives a red button + a live status line — because a teacher must
/// NEVER be broadcasting without knowing it. A denied Microphone grant is VISIBLE, not a
/// silent no-op.
/// </summary>
public partial class TalkViewModel : ObservableObject
{
    private readonly TeacherMicBroadcaster _broadcaster;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private bool isTalking;

    [ObservableProperty] private string status = "";

    public string ButtonText => IsTalking ? "🔴 Talking — Stop" : "🎙 Talk to Class";

    public TalkViewModel(TeacherMicBroadcaster broadcaster)
    {
        _broadcaster = broadcaster;
        // FrameSent fires on the native audio thread → marshal the status update to the UI thread.
        _broadcaster.FrameSent += seq =>
        {
            if (seq % 10 != 0) return;   // 10 frames = 1 s — don't churn the UI at 10 Hz
            Dispatcher.UIThread.Post(() => { if (IsTalking) Status = $"🔴 LIVE — class hears you · {seq / 10}s"; });
        };
    }

    [RelayCommand]
    private async Task Toggle()
    {
        if (IsTalking)
        {
            await _broadcaster.StopAsync();
            IsTalking = false;
            Status = "";
            return;
        }

        if (!_broadcaster.HasPermission)
        {
            Status = "Requesting Microphone permission…";
            var granted = await _broadcaster.RequestPermissionAsync();
            if (!granted)
            {
                Status = "Microphone needed (System Settings ▸ Privacy) — grant, then try again";
                return;
            }
        }

        var rc = await _broadcaster.StartAsync();
        if (rc == 0)
        {
            IsTalking = true;
            Status = "🔴 LIVE — class hears you";
        }
        else
        {
            Status = rc == -2
                ? "No microphone input (permission or device) — check System Settings ▸ Privacy"
                : $"Could not start the mic (code {rc})";
        }
    }
}
