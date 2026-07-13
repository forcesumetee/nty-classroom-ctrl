using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClassroomCtrl.Avalonia.Sandbox.Services;
using ClassroomCtrl.Avalonia.Sandbox.ViewModels;

namespace ClassroomCtrl.Avalonia.Sandbox;

public partial class App : Application
{
    private TrayController? _tray;
    private bool _exiting;   // set only by the tray's Quit → allows the window Close to proceed

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // Phase 32-C — menubar app: the app lives in the tray, so closing the window HIDES it
            // (it can be re-shown via the tray) and only the tray's Quit exits explicitly. In the
            // shippable bundle (32-F) LSUIElement will also drop the Dock icon; in `dotnet run` dev
            // the Dock icon + window stay, and the full Sandbox tabs remain reachable for testing.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var window = new MainWindow();
            desktop.MainWindow = window;

            // Keep the window alive on close so "Show Debug Window" can bring it back.
            window.Closing += (_, e) =>
            {
                if (!_exiting) { e.Cancel = true; window.Hide(); }
            };

            var conn = ((MainWindowViewModel)window.DataContext!).Connection;
            _tray = new TrayController(
                conn,
                showWindow: () => { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); },
                quit: () => { _exiting = true; desktop.Shutdown(); });   // clean exit = dead-man unlock
            TrayIcon.SetIcons(this, new TrayIcons { _tray.Native });
        }

        base.OnFrameworkInitializationCompleted();
    }
}
