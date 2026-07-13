using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ClassroomCtrl.Avalonia.Sandbox.ViewModels;

namespace ClassroomCtrl.Avalonia.Sandbox.Services;

/// <summary>
/// Phase 32-C — owns the macOS menubar status item (Avalonia <see cref="TrayIcon"/> → NSStatusItem,
/// no native code). Reflects connection + lock state via <see cref="TrayPresenter"/>, and offers
/// Show-Debug-Window + Quit. Quit is a CLEAN process exit = dead-man unlock (the M21/M22 layer-1
/// guarantee: the shield's presentation options + the input-guard tap are released by the OS on
/// process death). Under an ENFORCED lock the menu bar is hidden (the kiosk .hideMenuBar option),
/// so the tray is intentionally unreachable then — the dead-man switch handles recovery; we don't
/// fight that. Icons are drawn (colored dot per connection state, a padlock for locked); the tooltip
/// and the menu status line always carry the unambiguous text.
/// </summary>
public sealed class TrayController
{
    private readonly TrayIcon _tray = new();
    private readonly NativeMenuItem _statusItem = new() { IsEnabled = false };
    private readonly ConnectionViewModel _conn;

    private readonly WindowIcon _iconConnected;
    private readonly WindowIcon _iconConnecting;
    private readonly WindowIcon _iconDisconnected;
    private readonly WindowIcon _iconLocked;

    /// <summary>The underlying TrayIcon to register via <c>TrayIcon.SetIcons</c>.</summary>
    public TrayIcon Native => _tray;

    public TrayController(ConnectionViewModel conn, Action showWindow, Action showPermissions,
                          Func<bool> isAutoStartEnabled, Action toggleAutoStart, Action quit)
    {
        _conn = conn;
        _iconConnected    = MakeCircle(Color.FromRgb(0x2E, 0xCC, 0x71));   // 🟢 green
        _iconConnecting   = MakeCircle(Color.FromRgb(0xF3, 0x9C, 0x12));   // 🟡 amber
        _iconDisconnected = MakeCircle(Color.FromRgb(0xE7, 0x4C, 0x3C));   // 🔴 red
        _iconLocked       = MakePadlock(Color.FromRgb(0xF1, 0xC4, 0x0F));  // 🔒 gold

        var menu = new NativeMenu();
        menu.Add(_statusItem);
        menu.Add(new NativeMenuItemSeparator());
        var showItem = new NativeMenuItem { Header = "Show Debug Window" };
        showItem.Click += (_, _) => showWindow();
        menu.Add(showItem);
        var permItem = new NativeMenuItem { Header = "Permissions…" };
        permItem.Click += (_, _) => showPermissions();
        menu.Add(permItem);

        // Start at Login (LaunchAgent). Reflects/toggles the plist; in dev (not a .app bundle) the
        // toggle refuses (Enable is gated on IsBundled), so it simply stays unchecked.
        var autoStart = new NativeMenuItem
        {
            Header = "Start at Login",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = isAutoStartEnabled(),
        };
        autoStart.Click += (_, _) => { toggleAutoStart(); autoStart.IsChecked = isAutoStartEnabled(); };
        menu.Add(autoStart);

        menu.Add(new NativeMenuItemSeparator());
        var quitItem = new NativeMenuItem { Header = "Quit NTY ClassroomCtrl" };
        quitItem.Click += (_, _) => quit();
        menu.Add(quitItem);

        _tray.Menu = menu;
        _tray.IsVisible = true;

        // The VM raises these on the UI thread (inside Post), so Update() touches the tray safely.
        _conn.PropertyChanged += OnConnChanged;
        _conn.SelfTile.PropertyChanged += OnSelfTileChanged;
        Update();
    }

    private void OnConnChanged(object? _, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ConnectionViewModel.Status) or nameof(ConnectionViewModel.TeacherIp))
            Update();
    }

    private void OnSelfTileChanged(object? _, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(StudentSelfTileViewModel.IsLocked))
            Update();
    }

    private void Update()
    {
        var (kind, tooltip, statusLine) =
            TrayPresenter.Describe(_conn.Status, _conn.SelfTile.IsLocked, _conn.TeacherIp);
        _tray.ToolTipText = tooltip;
        _statusItem.Header = statusLine;
        _tray.Icon = kind switch
        {
            TrayPresenter.Kind.Connected => _iconConnected,
            TrayPresenter.Kind.Connecting => _iconConnecting,
            TrayPresenter.Kind.Locked => _iconLocked,
            _ => _iconDisconnected,
        };
    }

    // ── drawn icons (deterministic, no emoji-font dependency) ────────────────────────────────
    private static WindowIcon MakeCircle(Color c)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(36, 36), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
            ctx.DrawEllipse(new SolidColorBrush(c), null, new Point(18, 18), 8.5, 8.5);
        return new WindowIcon(rtb);
    }

    private static WindowIcon MakePadlock(Color c)
    {
        var rtb = new RenderTargetBitmap(new PixelSize(36, 36), new Vector(96, 96));
        using (var ctx = rtb.CreateDrawingContext())
        {
            var brush = new SolidColorBrush(c);
            // Shackle: an upward arc above the body.
            var shackle = new StreamGeometry();
            using (var g = shackle.Open())
            {
                g.BeginFigure(new Point(13.5, 17), false);
                g.ArcTo(new Point(22.5, 17), new Size(4.5, 4.5), 0, false, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            ctx.DrawGeometry(null, new Pen(brush, 3), shackle);
            // Body: rounded rectangle.
            ctx.DrawRectangle(brush, null, new RoundedRect(new Rect(11, 16, 14, 12), 2.5));
        }
        return new WindowIcon(rtb);
    }
}
