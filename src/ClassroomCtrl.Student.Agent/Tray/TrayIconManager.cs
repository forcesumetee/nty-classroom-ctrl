using H.NotifyIcon;
using System.Windows;
using System.Windows.Controls;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using Application = System.Windows.Application;

namespace ClassroomCtrl.Student.Agent.Tray;

/// <summary>
/// Tray-icon manager. Per Spec §7.4 there is NO Exit menu item;
/// quitting the agent requires the approved admin-password flow.
/// </summary>
public class TrayIconManager : IDisposable
{
    private readonly Window _mainWindow;
    private TaskbarIcon? _icon;

    public TrayIconManager(Window mainWindow) => _mainWindow = mainWindow;

    public void Initialize()
    {
        _icon = new TaskbarIcon
        {
            ToolTipText = "Classroom Control (active)",
        };

        // Phase 10.13 — the previous TODO left _icon.Icon unset, so the tray showed
        // an empty box. Load classroom_icon.ico from the embedded WPF Resource added
        // in the .csproj. H.NotifyIcon.TaskbarIcon.Icon is a System.Drawing.Icon; once
        // assigned, WinForms deep-copies the handle, so disposing the source stream
        // immediately is safe.
        try
        {
            var iconUri = new Uri("pack://application:,,,/Assets/classroom_icon.ico", UriKind.Absolute);
            var iconStreamInfo = Application.GetResourceStream(iconUri);
            if (iconStreamInfo != null)
            {
                using var iconStream = iconStreamInfo.Stream;
                _icon.Icon = new System.Drawing.Icon(iconStream);
            }
        }
        catch
        {
            // Non-fatal: tray still works (just shows default empty box) if the
            // resource is missing in an unusual build configuration.
        }

        var menu = new ContextMenu();

        var openItem = new MenuItem { Header = "Open Classroom Window" };
        openItem.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(openItem);

        var settingsItem = new MenuItem { Header = "Settings…" };
        settingsItem.Click += (_, _) => OpenSettings();
        menu.Items.Add(settingsItem);

        // INTENTIONALLY: no "Exit" menu item — Spec §7.4
        _icon.ContextMenu = menu;

        _icon.TrayLeftMouseDown += (_, _) => ShowMainWindow();
        _icon.ForceCreate();
    }

    public void ShowMainWindow()
    {
        _mainWindow.Show();
        _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void OpenSettings()
    {
        // Phase 10.16 — Settings now opens TeacherIPDialog so IT can re-point the
        // student at a new Teacher IP without editing config.txt by hand.  The
        // dialog ctor already pre-fills from TeacherIPConfig.Read(), so it
        // doubles as first-run setup and later edit.  Quit-behind-password is
        // still pending (Spec §7.4) and would be a separate menu item.
        var dlg = new ClassroomCtrl.Student.Agent.Setup.TeacherIPDialog
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
        };

        // Phase 10.17 — only set Owner when the MainWindow has actually been shown.
        // The Agent normally lives in the tray with MainWindow never shown, and WPF
        // throws "Cannot set Owner ... not been shown previously" otherwise — which
        // crashed the entire Agent process the first time customer IT clicked
        // tray → Settings.  An ownerless modal is allowed in WPF; we just lose the
        // CenterOwner placement (handled by the CenterScreen default above).
        if (_mainWindow is { IsVisible: true })
        {
            dlg.Owner = _mainWindow;
            dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }

        var result = dlg.ShowDialog();

        if (result == true && dlg.Saved)
        {
            // config.txt updated.  The Service picks up the new IP on its next
            // retry cycle (Phase 10.16 Part B re-reads inside the connect loop).
            // Tell the user it may take a few seconds rather than appear instant.
            ShowBalloon(
                ClassroomCtrl.Shared.Localization.Loc.Get("Toast_TeacherIPSaved_Title"),
                ClassroomCtrl.Shared.Localization.Loc.Get("Toast_TeacherIPSaved_Body"));
        }
    }

    /// <summary>Phase 5b: show a Windows toast / balloon from the tray icon.</summary>
    public void ShowBalloon(string title, string message)
    {
        try
        {
            _icon?.ShowNotification(title, message);
        }
        catch
        {
            // Fall back silently — balloon is non-critical.
        }
    }

    public void Dispose() => _icon?.Dispose();
}
