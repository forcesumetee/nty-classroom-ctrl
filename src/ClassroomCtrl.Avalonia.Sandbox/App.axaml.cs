using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ClassroomCtrl.Avalonia.Sandbox.Services;
using ClassroomCtrl.Avalonia.Sandbox.ViewModels;
using ClassroomCtrl.Avalonia.Sandbox.Views;

namespace ClassroomCtrl.Avalonia.Sandbox;

public partial class App : Application
{
    private TrayController? _tray;
    private PermissionsWindow? _perm;                          // single onboarding window instance
    private readonly LaunchAgentManager _launchAgent = new();  // auto-start (LaunchAgent) toggle
    private bool _exiting;                                     // set only by the tray's Quit → allows the window Close to proceed

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

            // Menubar-only when launched from the packaged .app (LSUIElement, 32-F): don't auto-show
            // the window — the tray is the UI and "Show Debug Window" reveals the Sandbox tabs on
            // demand. In `dotnet run` dev, show the full window as before (for tab testing).
            bool bundled = (Environment.ProcessPath ?? "").Contains("/Contents/MacOS/", StringComparison.Ordinal);

            var window = new MainWindow();

            // Keep the window alive on close so "Show Debug Window" can bring it back.
            window.Closing += (_, e) =>
            {
                if (!_exiting) { e.Cancel = true; window.Hide(); }
            };

            if (!bundled)
                desktop.MainWindow = window;   // dev: the classic lifetime shows it on start

            var conn = ((MainWindowViewModel)window.DataContext!).Connection;
            _tray = new TrayController(
                conn,
                showWindow: () => { window.Show(); window.WindowState = WindowState.Normal; window.Activate(); },
                showPermissions: ShowPermissions,
                isAutoStartEnabled: () => _launchAgent.IsEnabled,
                toggleAutoStart: () => { if (_launchAgent.IsEnabled) _launchAgent.Disable(); else _launchAgent.Enable(); },
                quit: () => { _exiting = true; desktop.Shutdown(); });   // clean exit = dead-man unlock
            TrayIcon.SetIcons(this, new TrayIcons { _tray.Native });

            // Phase 32-D — first-run onboarding: if the primary permission (Screen Recording) isn't
            // granted, surface the permissions window. Subsequent runs skip it (no nagging); the tray
            // "Permissions…" item reopens it on demand.
            if (Permissions.Check(PermId.Screen) != PermState.Granted)
                ShowPermissions();
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Show (or focus) the single permissions onboarding window.</summary>
    private void ShowPermissions()
    {
        if (_perm is not null) { _perm.Activate(); return; }
        _perm = new PermissionsWindow { DataContext = new PermissionsViewModel() };
        _perm.Closed += (_, _) => _perm = null;
        _perm.Show();
    }
}
