using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace ClassroomCtrl.Teacher.Views;

/// <summary>
/// Phase 17 step 2 — code-behind for the NotificationOverlay UserControl.
/// Wiring is intentionally thin: DataContext is set externally (MainWindow
/// hands in App.Notifications), and the only interactive behavior is
/// click-to-dismiss on individual cards.  The slide-in animation lives in
/// XAML; auto-dismiss + stack-cap live in <see cref="Services.NotificationService"/>.
/// </summary>
public partial class NotificationOverlay : UserControl
{
    public NotificationOverlay()
    {
        InitializeComponent();
    }

    /// <summary>Click-to-dismiss handler — the card's Border carries
    /// <c>Tag="{Binding Id}"</c> so we can look up the card without a
    /// back-reference.  Dispatch back into the service so the auto-dismiss
    /// timer race is handled there (Remove on an already-removed item is a
    /// no-op on ObservableCollection).</summary>
    private void OnNotificationClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is Guid id)
        {
            App.Notifications?.Dismiss(id);
            // Phase 17.1 step 3 — clicking any card counts as "I've seen the
            // stack" (LINE / Slack semantics: acknowledge one → clear the
            // app-icon badge for the whole bunch).  The remaining cards stay
            // visible on the corner overlay until their own auto-dismiss
            // timer ticks; the badge just zeroes regardless.
            App.Notifications?.MarkAllRead();
            // Mark handled so the click doesn't bubble up and steal focus
            // from whatever the teacher is currently looking at.
            e.Handled = true;
        }
    }
}
