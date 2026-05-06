using ClassroomCtrl.Shared.Localization;
using System;
using System.ComponentModel;

namespace ClassroomCtrl.Student.Agent.Models;

/// <summary>
/// Phase 9.1 Section B — one entry in the bell-popup notification list.
///
/// The Phase 9 implementation lumped system events ("teacher viewing your
/// screen", "you raised your hand", "mic muted") into the same ChatList that
/// also showed actual chat from teacher/peers.  Phase 9.1 splits them: real
/// chat stays in the body ChatList (Surface.Card), system events route through
/// this model into a popup tied to the header bell.  Mirrors the Teacher's
/// Phase 3 D Notifications panel pattern.
///
/// TimeAgoText delegates to the same Loc.Time_* keys the chat bubble uses,
/// so the Phase 9 D Thai change ("เพิ่งนี้" → "เมื่อซักครู่") propagates here
/// automatically.  Implements INotifyPropertyChanged so a future
/// DispatcherTimer in MainWindow can re-evaluate TimeAgoText every minute and
/// the popup updates without reconstructing items.
/// </summary>
public class StudentNotification : INotifyPropertyChanged
{
    public string Message { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string Icon { get; init; } = "🔔";

    public string TimeAgoText
    {
        get
        {
            var ago = DateTime.Now - Timestamp;
            if (ago.TotalMinutes < 1) return Loc.Get("Time_JustNow");
            if (ago.TotalMinutes < 60) return Loc.Format("Time_MinAgo", (int)ago.TotalMinutes);
            if (ago.TotalHours < 24) return Loc.Format("Time_HourAgo", (int)ago.TotalHours);
            return Timestamp.ToString("MMM d");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Call from a periodic timer to refresh TimeAgoText bindings.</summary>
    public void NotifyTimeChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeAgoText)));
}
