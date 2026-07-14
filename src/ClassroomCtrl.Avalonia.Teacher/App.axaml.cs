using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClassroomCtrl.Avalonia.Teacher.Services;
using ClassroomCtrl.Avalonia.Teacher.ViewModels;

namespace ClassroomCtrl.Avalonia.Teacher;

public partial class App : Application
{
    private TeacherSession? _session;
    private ScreenViewController? _screenViews;
    private StudentCommandController? _commands;

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

            // TT-5-B: per-student commands (lock/unlock; power in TT-5-C). The session is
            // the command sink (IStudentCommandSink); reliable:true is baked into the
            // controller. The confirm delegate stays default here (auto-confirm) — power
            // isn't in the menu until TT-5-C wires the real Avalonia confirm dialog.
            _commands = new StudentCommandController(_session, log: msg => Console.Error.WriteLine($"[cmd] {msg}"));

            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel(_session.Grid, _session.ListenAddress),
            };

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
            window.Closing += (_, _) => { _screenViews?.CloseAll(); _session?.Dispose(); };
            desktop.ShutdownRequested += (_, _) => { _screenViews?.CloseAll(); _session?.Dispose(); };
        }
        base.OnFrameworkInitializationCompleted();
    }
}
