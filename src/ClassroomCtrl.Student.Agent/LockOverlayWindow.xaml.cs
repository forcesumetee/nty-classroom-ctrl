using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ClassroomCtrl.Student.Agent;

public partial class LockOverlayWindow : Window
{
    private DispatcherTimer? _clockTimer;

    public LockOverlayWindow()
    {
        InitializeComponent();
        KeyDown += OnKeyDown;
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    public void SetCustomMessage(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            CustomMessageText.Text = text;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateClock();
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _clockTimer.Tick += (_, _) => UpdateClock();
        _clockTimer.Start();

        StartPulse(PulseRing1, PulseScale1, TimeSpan.Zero);
        StartPulse(PulseRing2, PulseScale2, TimeSpan.FromSeconds(1));
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _clockTimer?.Stop();
        _clockTimer = null;
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        TimeText.Text = now.ToString("HH:mm:ss");
        try
        {
            // Localized date — falls back to invariant if culture is unavailable.
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            DateText.Text = now.ToString("dddd, d MMMM yyyy", culture);
        }
        catch { DateText.Text = now.ToString("yyyy-MM-dd"); }
    }

    private static void StartPulse(System.Windows.UIElement ring, System.Windows.Media.ScaleTransform scale, TimeSpan beginDelay)
    {
        var fade = new DoubleAnimation
        {
            From = 0.6,
            To = 0.0,
            Duration = TimeSpan.FromSeconds(2),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = beginDelay,
        };
        ring.BeginAnimation(System.Windows.UIElement.OpacityProperty, fade);

        var grow = new DoubleAnimation
        {
            From = 1.0,
            To = 1.5,
            Duration = TimeSpan.FromSeconds(2),
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = beginDelay,
        };
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        // Emergency unlock — Ctrl+Shift+Alt+U
        bool ctrl = Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl);
        bool shift = Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift);
        bool alt = Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt);

        if (ctrl && shift && alt && e.Key == Key.U)
        {
            IpcClient.LogToFile("[LockOverlay] Emergency unlock combo pressed");
            Close();
        }
    }
}
