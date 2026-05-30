using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;

namespace ClassroomCtrl.Teacher.Services;

/// <summary>
/// Phase 17 (v1.x roadmap item #5) — LINE-style in-app notification surface.
/// Teacher-only this round; student-side notifications are v1.1 polish.
///
/// Categorises the per-event triggers that the Conference / Classroom dispatch
/// arms fire — HandRaise spawns a ✋ card, ChatBroadcast spawns a 💬 card, and
/// the generic Info fallback covers future surfaces (file-receive,
/// quiz-finished, etc.).  Adding a new category is two lines: extend the enum
/// + the Icon switch; no XAML change required because the overlay binds the
/// glyph property directly.
/// </summary>
public enum NotificationType { HandRaise, Chat, Info }

/// <summary>
/// Phase 17 — one notification card in the slide-in stack.  Immutable once
/// created (init-only setters) so the auto-dismiss timer + the overlay's
/// ItemsControl can safely race against each other.
/// </summary>
public class NotificationItem
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public NotificationType Type { get; init; }
    public string SenderName { get; init; } = "";
    public string Message { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.Now;

    /// <summary>Glyph painted on the card's left avatar slot.  Bound directly
    /// by the overlay XAML so the swatch updates without a converter.</summary>
    public string Icon => Type switch
    {
        NotificationType.HandRaise => "✋",
        NotificationType.Chat      => "💬",
        _                          => "ℹ️",
    };
}

/// <summary>
/// Phase 17 — singleton orchestrator owning the visible notification stack.
/// Bound by <c>NotificationOverlay</c>'s DataContext to <see cref="ActiveItems"/>;
/// callers (HandRaise dispatch arm, Chat dispatch arm) fire <see cref="Show"/>
/// from any thread and the service marshals to the UI dispatcher itself so the
/// callers don't have to.
///
/// Cap = 3 visible cards, oldest evicted on overflow.  Each card auto-dismisses
/// after 5 s via a per-card <see cref="DispatcherTimer"/>; explicit
/// <see cref="Dismiss"/> handles the click-to-dismiss path from the overlay.
/// </summary>
public class NotificationService
{
    private const int MaxVisible = 3;
    private static readonly TimeSpan AutoDismissAfter = TimeSpan.FromSeconds(5);

    private readonly ObservableCollection<NotificationItem> _activeItems = new();

    /// <summary>Bound by <c>NotificationOverlay</c> ItemsControl.  Read-only
    /// projection so external code can subscribe to CollectionChanged but
    /// can't mutate the stack out from under the auto-dismiss timers.</summary>
    public ReadOnlyObservableCollection<NotificationItem> ActiveItems { get; }

    public NotificationService()
    {
        ActiveItems = new ReadOnlyObservableCollection<NotificationItem>(_activeItems);
    }

    /// <summary>Push a new card onto the stack.  Thread-safe: marshals to the
    /// WPF dispatcher internally so dispatch-arm callers (which already run on
    /// the UI thread today, but might not in a future refactor) don't have to
    /// pre-wrap their call.</summary>
    public void Show(NotificationItem item)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.Invoke(() =>
        {
            _activeItems.Add(item);

            // Cap at MaxVisible — oldest drops off the top so the newest is
            // always visible.  Matches LINE / Meet stacking semantics.
            while (_activeItems.Count > MaxVisible)
                _activeItems.RemoveAt(0);

            // Per-card auto-dismiss.  DispatcherTimer means the Tick fires on
            // the UI thread so the Remove is safe; the timer self-stops on
            // first fire so a manual Dismiss before the timeout is a clean
            // no-op (Remove of an already-removed item is a no-op on
            // ObservableCollection).
            var timer = new DispatcherTimer { Interval = AutoDismissAfter };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _activeItems.Remove(item);
            };
            timer.Start();
        });
    }

    /// <summary>Dismiss a specific card immediately (click-to-dismiss path).
    /// Lookup by Id rather than reference so the overlay code-behind can pass
    /// the card Id from a stale binding without holding a back-reference.</summary>
    public void Dismiss(Guid id)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null) return;
        dispatcher.Invoke(() =>
        {
            var item = _activeItems.FirstOrDefault(x => x.Id == id);
            if (item != null) _activeItems.Remove(item);
        });
    }
}
