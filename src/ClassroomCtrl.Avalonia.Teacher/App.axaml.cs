using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Teacher.Services;
using ClassroomCtrl.Avalonia.Teacher.ViewModels;
using ClassroomCtrl.Avalonia.Teacher.Views;
using ClassroomCtrl.Teacher.Core;

namespace ClassroomCtrl.Avalonia.Teacher;

public partial class App : Application
{
    private TeacherSession? _session;
    private ScreenViewController? _screenViews;
    private StudentCommandController? _commands;
    private TeacherScreenBroadcaster? _screenBroadcaster;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // TT-2-D: run the REAL Teacher.Core server (0.0.0.0:7777) + roster + grid.
            // StartAsync binds synchronously before returning, so ListenAddress (which
            // reads BoundPort) is populated by the time the window VM is built.
            _session = new TeacherSession();
            _session.StartAsync();

            // TT-3-B: per-student screen views. The controller owns the open windows;
            // the session is the frame source (IStudentStreamSource).
            _screenViews = new ScreenViewController(_session);

            var sound = new SoundService();
            var mainVm = new MainWindowViewModel(_session.Grid, _session.ListenAddress);
            var window = new MainWindow { DataContext = mainVm };

            // TT-5-B/C: per-student commands. The session is the command sink
            // (IStudentCommandSink); reliable:true is baked into the controller. Power
            // actions are confirmed via the Avalonia ConfirmDialog (modal over the main
            // window) — the controller only sends when the teacher confirms.
            _commands = new StudentCommandController(
                _session,
                confirmAsync: msg => ConfirmDialog.ShowAsync(window, msg),
                log: msg => Console.Error.WriteLine($"[cmd] {msg}"));
            // TT-6-C: the grid's bulk commands fan out through the same controller (so the
            // reliable-channel guard covers bulk too). Attached here — the controller needs the
            // window (for the confirm dialog), which is created after the grid VM.
            _session.Grid.AttachCommands(_commands);

            // TT-7-C: chat rail + hand-raise/reaction routing + notification sounds + toasts.
            // The session is the ITeacherMessaging seam; the grid attributes each hand-raise/
            // reaction to the right tile by EndpointId, and the chat VM sends broadcast/DM
            // (DM reliable:true). ShowToast surfaces a transient banner for both.
            var chat = new ChatViewModel(_session, sound, _session.Grid, mainVm.ShowToast);
            mainVm.Chat = chat;
            _session.Grid.AttachMessaging(_session, sound, mainVm.ShowToast);

            // TT-9-C: teacher mic-monitor + multi-student mix. The tile's "Listen to mic" toggle
            // opens/closes a student's mic (MicMonitorStart/Stop); inbound StudentAudioStreamFrame
            // then feeds the session's TeacherAudioMixer (native N-source mix). The mix status
            // ("N of M open — mixing 12" when the cap bites) is surfaced on the header, never a
            // silent drop. MixStatusChanged fires on a transport thread → marshal to the UI.
            _session.Grid.MicMonitorAction = (id, listen, ct) =>
                listen ? _session.ListenToStudentAsync(id, ct) : _session.StopListeningToStudentAsync(id, ct);
            _session.MixStatusChanged += (mixed, open, cap) =>
                Dispatcher.UIThread.Post(() => _session.Grid.SetMixStatus(mixed, open, cap));

            // TT-8-C: Share My Screen — capture the teacher's screen + broadcast to all students
            // (frames lossy; STOP reliable — bug #5). The session is the ITeacherScreenSink.
            _screenBroadcaster = new TeacherScreenBroadcaster(_session);
            mainVm.ScreenShare = new ScreenShareViewModel(_screenBroadcaster);

            // Double-tap a tile → open (or focus) that student's live screen view.
            window.StudentActivated += tile =>
                _screenViews.OpenOrFocus(tile.EndpointId, tile.DisplayName, tile.MachineName);
            // Right-click a tile → issue the chosen command (fire-and-forget; the controller
            // swallows/logs errors so a failed send can't crash the Teacher).
            window.StudentCommandRequested += t =>
                _ = _commands.ExecuteAsync(t.Vm.EndpointId, t.Command, t.Vm.DisplayName);
            // A student leaving (roster removal, a background thread) closes their open
            // screen view → stops the stream. HandleStudentDisconnected marshals to UI.
            _session.Roster.StudentRemoved += (_, id) => _screenViews.HandleStudentDisconnected(id);

            desktop.MainWindow = window;

            // Guaranteed teardown: close every screen view FIRST (each stops its stream
            // while the server is still alive to send the stop), THEN release :7777 —
            // never leave a student streaming or the port bound. Both hooks are
            // idempotent, so firing both is safe.
            window.Closing += (_, _) => { _ = _screenBroadcaster?.StopAsync(); _screenViews?.CloseAll(); _session?.Dispose(); };
            desktop.ShutdownRequested += (_, _) => { _ = _screenBroadcaster?.StopAsync(); _screenViews?.CloseAll(); _session?.Dispose(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
