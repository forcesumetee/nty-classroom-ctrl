using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Teacher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Teacher.ViewModels;

/// <summary>
/// TT-13 / TOR 11.2.9 — the "Record" toggle (mirrors TalkViewModel). Records the teacher's screen +
/// system audio to ~/Movies/NTY Recordings. The ON state is LOUD (red button + live "● REC ·Ns"
/// elapsed) — a teacher must never be recording without knowing. Stop shows the saved path; a denied
/// Screen-Recording grant is a VISIBLE message, not a silent no-op.
/// </summary>
public partial class RecordViewModel : ObservableObject
{
    private readonly TeacherRecorder _recorder;
    private DispatcherTimer? _timer;
    private int _elapsed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ButtonText))]
    private bool isRecording;

    [ObservableProperty] private string status = "";

    public string ButtonText => IsRecording ? "⏺ Recording — Stop" : "⏺ Record";

    public RecordViewModel(TeacherRecorder recorder) => _recorder = recorder;

    [RelayCommand]
    private void Toggle()
    {
        if (IsRecording)
        {
            var (ok, path, video, audio) = _recorder.Stop();
            StopTimer();
            IsRecording = false;
            Status = ok
                ? $"✅ Saved to {path}  ({video} video / {audio} audio frames)"
                : "⚠ Stopped (nothing written)";
            return;
        }

        var (started, message) = _recorder.Start();
        if (started)
        {
            IsRecording = true;
            _elapsed = 0;
            Status = "🔴 REC — screen + computer audio";
            StartTimer();
        }
        else
        {
            Status = message;   // visible failure (e.g. "grant Screen Recording, then RELAUNCH")
        }
    }

    private void StartTimer()
    {
        _timer = new DispatcherTimer { Interval = System.TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => { _elapsed++; if (IsRecording) Status = $"🔴 REC · {_elapsed}s — screen + computer audio"; };
        _timer.Start();
    }

    private void StopTimer() { _timer?.Stop(); _timer = null; }
}
