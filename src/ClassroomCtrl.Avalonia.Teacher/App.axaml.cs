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

            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel(_session.Grid, _session.ListenAddress),
            };

            // Double-tap a tile → open (or focus) that student's live screen view.
            window.StudentActivated += tile =>
                _screenViews.OpenOrFocus(tile.EndpointId, tile.DisplayName, tile.MachineName);
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
