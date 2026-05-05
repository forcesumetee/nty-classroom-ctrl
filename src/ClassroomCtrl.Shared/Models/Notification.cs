using System;
using System.ComponentModel;
using ClassroomCtrl.Shared.Localization;

namespace ClassroomCtrl.Shared.Models;

/// <summary>
/// Phase 3 Section C — system / hand-raised / error events surfaced via the bell icon
/// and Activity tab.  Splits the chat log into two streams: <see cref="ChatMessage"/> for
/// human messages, this for bookkeeping events.  IsRead drives the bell badge count and
/// the popup's unread-row highlight.
/// </summary>
public enum NotificationKind
{
    System,
    Error,
    HandRaised,
}

public class Notification : INotifyPropertyChanged
{
    public string Title { get; init; } = "";
    public string Body { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public NotificationKind Kind { get; init; }

    private bool _isRead;
    public bool IsRead
    {
        get => _isRead;
        set
        {
            if (_isRead == value) return;
            _isRead = value;
            OnPropertyChanged(nameof(IsRead));
        }
    }

    public string TimeDisplay => Timestamp.ToString("HH:mm");

    public string TimeAgoDisplay
    {
        get
        {
            var ago = DateTime.Now - Timestamp;
            if (ago.TotalMinutes < 1) return Loc.Get("Time_JustNow");
            if (ago.TotalMinutes < 60) return Loc.Format("Time_MinAgo", (int)ago.TotalMinutes);
            if (ago.TotalHours < 24)   return Loc.Format("Time_HourAgo", (int)ago.TotalHours);
            return Timestamp.ToString("MMM d");
        }
    }

    /// <summary>Glyph painted on the popup row + activity row left.  Hand-raised gets a
    /// hand emoji because that's how Phase 13 trained users to read the bell.</summary>
    public string Icon => Kind switch
    {
        NotificationKind.HandRaised => "✋",
        NotificationKind.Error      => "⚠️",
        _                           => "ℹ️",
    };

    public bool IsError => Kind == NotificationKind.Error;
    public bool IsHandRaised => Kind == NotificationKind.HandRaised;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Phase 3 Section A wiring — pumped by MainViewModel's 30s refresh timer.</summary>
    public void NotifyTimeChanged()
        => OnPropertyChanged(nameof(TimeAgoDisplay));
}
