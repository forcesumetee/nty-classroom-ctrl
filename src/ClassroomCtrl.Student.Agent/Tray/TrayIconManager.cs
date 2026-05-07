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
            // Icon = ... (TODO: embed .ico via resources)
        };

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
        // TODO: show Settings window with "Quit" option behind admin-password prompt (Spec §7.4)
        MessageBox.Show("Settings — admin password required to quit.");
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
