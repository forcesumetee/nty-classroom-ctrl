using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClassroomCtrl.Avalonia.Teacher.Services;
using ClassroomCtrl.Avalonia.Teacher.ViewModels;

namespace ClassroomCtrl.Avalonia.Teacher;

public partial class App : Application
{
    private TeacherSession? _session;

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

            var window = new MainWindow
            {
                DataContext = new MainWindowViewModel(_session.Grid, _session.ListenAddress),
            };
            desktop.MainWindow = window;

            // Guaranteed teardown: window close / app quit → Dispose → socket released
            // (never leave :7777 bound — the same discipline as TeacherHost). Dispose
            // is idempotent, so both hooks firing is safe.
            window.Closing += (_, _) => _session?.Dispose();
            desktop.ShutdownRequested += (_, _) => _session?.Dispose();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
