using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
public enum NotificationType
{
    HandRaise,
    Chat,
    Info,
    /// <summary>Phase 20 (v1.1) — student requesting screen-share permission.
    /// Renders with inline Approve / Deny buttons; the overlay code-behind
    /// reads <see cref="NotificationItem.ActionTargetId"/> to know which
    /// student to respond to when a button is clicked.</summary>
    ShareRequest,
}

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
        NotificationType.HandRaise    => "✋",
        NotificationType.Chat         => "💬",
        NotificationType.ShareRequest => "🖥",
        _                             => "ℹ️",
    };

    /// <summary>Phase 20 (v1.1) — only meaningful for
    /// <see cref="NotificationType.ShareRequest"/> cards.  Carries the
    /// requester's EndpointId so the Approve / Deny click handlers in
    /// the overlay can route through MainViewModel.ApproveShareRequestAsync /
    /// DenyShareRequestAsync without a second lookup.</summary>
    public Guid ActionTargetId { get; init; }

    /// <summary>Phase 20 (v1.1) — true iff the card should render its
    /// inline Approve / Deny action row.  Computed so the XAML template
    /// has a single boolean trigger instead of switching on the enum.</summary>
    public bool HasActionRow => Type == NotificationType.ShareRequest;
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
public class NotificationService : INotifyPropertyChanged
{
    private const int MaxVisible = 3;
    private static readonly TimeSpan AutoDismissAfter = TimeSpan.FromSeconds(5);

    private readonly ObservableCollection<NotificationItem> _activeItems = new();

    /// <summary>Bound by <c>NotificationOverlay</c> ItemsControl.  Read-only
    /// projection so external code can subscribe to CollectionChanged but
    /// can't mutate the stack out from under the auto-dismiss timers.</summary>
    public ReadOnlyObservableCollection<NotificationItem> ActiveItems { get; }

    private int _unreadCount;
    /// <summary>Phase 17.1 step 2 — count of notifications fired since the user
    /// last "read" the stack.  Incremented on every <see cref="Show"/>; reset
    /// to zero by <see cref="MarkAllRead"/> (called on Window.Activated and on
    /// per-card click-to-dismiss).  MainViewModel observes
    /// <c>PropertyChanged(nameof(UnreadCount))</c> to repaint the
    /// TaskbarItemInfo.Overlay red-dot badge.</summary>
    public int UnreadCount
    {
        get => _unreadCount;
        private set
        {
            if (_unreadCount == value) return;
            _unreadCount = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UnreadCount)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

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

            // Phase 17.1 step 2 — bump the unread counter.  Note that an
            // evicted card (cap-overflow above) does NOT decrement; the user
            // hasn't seen it but the auto-clear semantics ("Window.Activated"
            // means the user is back at the desk) will zero the badge anyway.
            UnreadCount++;

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

    /// <summary>Phase 17.1 step 2 — reset the unread counter.  Called by
    /// MainWindow.Activated (user focused the app, so by definition they're
    /// looking at the corner overlay now) and by the per-card click-to-dismiss
    /// path in <c>NotificationOverlay</c> (the user has acknowledged at least
    /// one — the simple model is "acknowledge any → clear all"; matches LINE,
    /// Slack, and the WhatsApp app-icon badge behaviour).</summary>
    public void MarkAllRead()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            UnreadCount = 0;
            return;
        }
        dispatcher.Invoke(() => UnreadCount = 0);
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

    /// <summary>Phase 17.1 step 2 — render a 16×16 red-dot badge image suitable
    /// for binding to <c>TaskbarItemInfo.Overlay</c>.  A count > 0 draws a
    /// solid red circle with the count centered in white (clamped to "9+" for
    /// double-digit overflow so the glyph stays legible at 16 px).  Count == 0
    /// returns null so callers can pass the result straight through and let
    /// WPF clear the badge.
    ///
    /// Generated programmatically so no PNG asset needs to ship in the
    /// installer; the bitmap is frozen before return so MainViewModel can hold
    /// it across UI-thread updates without freezing/cloning per refresh.</summary>
    public static ImageSource? CreateBadgeOverlay(int count)
    {
        if (count <= 0) return null;

        const int Size = 16;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // Solid red circle.  Radius 7.5 (with no stroke) keeps a 1 px
            // breathing margin from the 16×16 canvas edge so anti-aliasing
            // doesn't clip against the taskbar icon background.
            dc.DrawEllipse(Brushes.Red, null, new Point(Size / 2.0, Size / 2.0), 7.5, 7.5);

            // Count glyph, clamped to "9+" for overflow.
            var label = count > 9 ? "9+" : count.ToString(CultureInfo.InvariantCulture);
            var typeface = new Typeface(new FontFamily("Segoe UI"),
                                        FontStyles.Normal,
                                        FontWeights.Bold,
                                        FontStretches.Normal);
            var text = new FormattedText(label,
                                         CultureInfo.InvariantCulture,
                                         FlowDirection.LeftToRight,
                                         typeface,
                                         9.5,
                                         Brushes.White,
                                         1.0);
            var origin = new Point((Size - text.Width) / 2.0, (Size - text.Height) / 2.0);
            dc.DrawText(text, origin);
        }

        var bitmap = new RenderTargetBitmap(Size, Size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
