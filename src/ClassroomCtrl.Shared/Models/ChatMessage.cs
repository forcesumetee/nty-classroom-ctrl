using System;
using ClassroomCtrl.Shared.Localization;

namespace ClassroomCtrl.Shared.Models;

/// <summary>
/// Phase 2 Section E — chat-log entry shown in the right rail.  This is a UI/log model;
/// the over-the-wire <see cref="ClassroomCtrl.Shared.Protocol.ChatMessage"/> stays as-is so
/// no protocol bump is needed.  Kind drives the bubble styling (system/teacher/student/DM).
/// </summary>
public enum ChatMessageKind
{
    System,
    Teacher,
    Student,
    DM,
}

public class ChatMessage
{
    public string SenderName { get; init; } = "";
    public DateTime Timestamp { get; init; } = DateTime.Now;
    public string MessageText { get; init; } = "";
    public ChatMessageKind Kind { get; init; }

    /// <summary>First letter of SenderName for the avatar circle; "S" if name is empty.</summary>
    public string SenderInitial => string.IsNullOrEmpty(SenderName)
        ? "S"
        : SenderName.Substring(0, 1).ToUpperInvariant();

    /// <summary>"HH:mm" — used in the Activity tab where space is tighter.</summary>
    public string TimeDisplay => Timestamp.ToString("HH:mm");

    /// <summary>"Just now" / "5 min ago" / "2 h ago" / "May 6" — relative to DateTime.Now.</summary>
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

    public bool IsSystem  => Kind == ChatMessageKind.System;
    public bool IsTeacher => Kind == ChatMessageKind.Teacher;
    public bool IsStudent => Kind == ChatMessageKind.Student;
    public bool IsDM      => Kind == ChatMessageKind.DM;
}
