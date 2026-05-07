using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace ClassroomCtrl.Teacher;

/// <summary>
/// Phase 5 Task 4: Zoom-style floating toolbar shown while the teacher is broadcasting.
///
/// Behavior:
///   • Click-and-drag from any non-button area (DragMove)
///   • Auto-fade to 0.3 opacity after 3 seconds of no mouse interaction; pops back to 1.0 on hover
///   • AlwaysOnTop, no taskbar entry, no system chrome
///   • DataContext is the same MainViewModel as the main window — buttons reuse existing commands
///   • Position is remembered in the registry (HKCU\Software\NTY\ClassroomCtrl\ToolbarPosition)
/// </summary>
public partial class BroadcastToolbar : Window
{
    private const string RegPath = @"Software\NTY\ClassroomCtrl";
    private const string RegPosKey = "ToolbarPosition";

    private readonly DispatcherTimer _fadeTimer;

    public BroadcastToolbar()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Closed += OnClosed;

        _fadeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _fadeTimer.Tick += (_, _) => { FadeTo(0.3); _fadeTimer.Stop(); };
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        RestorePosition();
        _fadeTimer.Start();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _fadeTimer.Stop();
        SavePosition();
    }

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        // Only drag when the click hits empty toolbar area, not a button (buttons handle their own clicks)
        try { DragMove(); } catch { }
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        _fadeTimer.Stop();
        FadeTo(1.0);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _fadeTimer.Stop();
        _fadeTimer.Start();
    }

    /// <summary>Bring back the main window so the teacher can use the chat panel.</summary>
    private void ShowChat_Click(object sender, RoutedEventArgs e)
    {
        var main = System.Windows.Application.Current?.MainWindow;
        if (main == null) return;
        if (main.WindowState == WindowState.Minimized) main.WindowState = WindowState.Normal;
        main.Show();
        main.Activate();
    }

    private void FadeTo(double targetOpacity)
    {
        // Simple animation via property assignment (no Storyboard needed).
        // For smoother fade we'd use DoubleAnimation, but a hard set is fine for low cadence.
        Opacity = targetOpacity;
    }

    private void RestorePosition()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RegPath);
            var saved = key?.GetValue(RegPosKey) as string;
            if (!string.IsNullOrEmpty(saved))
            {
                var parts = saved.Split(',');
                if (parts.Length == 2 && double.TryParse(parts[0], out var l) && double.TryParse(parts[1], out var t))
                {
                    Left = l;
                    Top = t;
                    return;
                }
            }
        }
        catch { }

        // Default: bottom-center of primary screen
        var screen = SystemParameters.WorkArea;
        Left = screen.Left + (screen.Width - ActualWidth) / 2;
        Top = screen.Bottom - ActualHeight - 60;
    }

    private void SavePosition()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RegPath);
            key.SetValue(RegPosKey, $"{Left},{Top}", Microsoft.Win32.RegistryValueKind.String);
        }
        catch { }
    }
}
