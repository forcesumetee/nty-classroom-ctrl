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

    /// <summary>Phase 20 (v1.1) — Approve a ShareRequest card.  Reads the
    /// requester EndpointId from Button.Tag (NotificationItem.ActionTargetId
    /// bound in XAML), routes to MainViewModel.ApproveShareRequestAsync
    /// via the DataContext walk.  Routed-event Handled stops the click
    /// from bubbling to the parent card's MouseLeftButtonDown which would
    /// otherwise dismiss-and-mark-read before the verdict goes out.</summary>
    private async void ApproveShare_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.Tag is not Guid studentId) return;
        var mw = System.Windows.Application.Current?.MainWindow;
        if (mw?.DataContext is ViewModels.MainViewModel vm)
        {
            await vm.ApproveShareRequestAsync(studentId);
        }
        // Dismiss the card so the teacher doesn't see a stale Approve/Deny
        // affordance on a request they already answered.
        DismissCardFromButton(fe);
    }

    private async void DenyShare_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not FrameworkElement fe || fe.Tag is not Guid studentId) return;
        var mw = System.Windows.Application.Current?.MainWindow;
        if (mw?.DataContext is ViewModels.MainViewModel vm)
        {
            await vm.DenyShareRequestAsync(studentId);
        }
        DismissCardFromButton(fe);
    }

    /// <summary>Walk up the visual tree from an Approve / Deny button to the
    /// parent card Border (which has its NotificationItem.Id stored in
    /// Tag), then forward to NotificationService.Dismiss.</summary>
    private static void DismissCardFromButton(System.Windows.DependencyObject start)
    {
        var node = start;
        while (node != null)
        {
            if (node is System.Windows.Controls.Border b && b.Tag is Guid cardId)
            {
                App.Notifications?.Dismiss(cardId);
                return;
            }
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
    }
}
