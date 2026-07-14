using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;

namespace ClassroomCtrl.Student.Mac.Agent.Views;

/// <summary>Visual category of a toast — drives only the accent-bar color (restrained palette).</summary>
public enum ToastKind { Info, Success, Warning }

/// <summary>
/// A lightweight, self-dismissing notification window in the "modern government" style: one
/// high-contrast card, a status accent bar, a two-line hierarchy, no chrome. Stacks down the
/// top-right of the primary display and auto-closes after <see cref="LifetimeSeconds"/> (or on click).
///
/// Must be created on the UI thread — the App marshals every IPC event through Dispatcher before
/// calling <see cref="Show"/>.
/// </summary>
public partial class ToastWindow : Window
{
    private const int LifetimeSeconds = 6;
    private const int MarginDip = 12;
    private const int StepDip = 100;   // vertical slot per stacked toast

    // All currently-open toasts, oldest first — UI-thread only.
    private static readonly List<ToastWindow> Open = new();

    private DispatcherTimer? _timer;

    public ToastWindow()
    {
        InitializeComponent();
        PointerPressed += (_, _) => Dismiss();      // click anywhere to dismiss early
        Closed += OnClosed;
    }

    /// <summary>Create, position, and show a toast. Call on the UI thread.</summary>
    public static void Show(string title, string message, ToastKind kind = ToastKind.Info)
    {
        var toast = new ToastWindow();
        toast.TitleText.Text = title;
        toast.MessageText.Text = message;
        toast.MessageText.IsVisible = !string.IsNullOrEmpty(message);
        toast.Accent.Background = new SolidColorBrush(AccentColor(kind));

        Open.Add(toast);
        toast.Opened += (_, _) => RepositionAll();
        toast.Show();

        toast._timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(LifetimeSeconds) };
        toast._timer.Tick += (_, _) => toast.Dismiss();
        toast._timer.Start();
    }

    private void Dismiss()
    {
        _timer?.Stop();
        _timer = null;
        try { Close(); } catch { /* already closing */ }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Open.Remove(this);
        RepositionAll();
    }

    /// <summary>Lay the open toasts out top-right of the primary screen, newest at the bottom of the stack.</summary>
    private static void RepositionAll()
    {
        var live = Open.Where(t => t.Screens?.Primary is not null || t.Screens?.All.Count > 0).ToList();
        for (int i = 0; i < live.Count; i++)
        {
            var t = live[i];
            var screen = t.Screens?.Primary ?? t.Screens?.All.FirstOrDefault();
            if (screen is null) continue;

            var wa = screen.WorkingArea;                       // physical pixels
            double scale = screen.Scaling <= 0 ? 1.0 : screen.Scaling;
            int wpx = (int)Math.Ceiling(t.Bounds.Width * scale);
            if (wpx <= 0) wpx = (int)Math.Ceiling(t.Width * scale);

            int marginPx = (int)(MarginDip * scale);
            int stepPx = (int)(StepDip * scale);
            int x = wa.X + wa.Width - wpx - marginPx;
            int y = wa.Y + marginPx + i * stepPx;
            t.Position = new PixelPoint(x, y);
        }
    }

    private static Color AccentColor(ToastKind kind) => kind switch
    {
        ToastKind.Success => Color.FromRgb(0x1E, 0x7E, 0x34),   // deep green
        ToastKind.Warning => Color.FromRgb(0xB3, 0x2A, 0x2A),   // deep red
        _ => Color.FromRgb(0x1B, 0x3A, 0x6B),                   // government navy
    };
}
