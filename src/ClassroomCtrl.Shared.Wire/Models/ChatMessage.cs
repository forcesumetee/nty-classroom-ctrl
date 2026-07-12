using System;
using System.ComponentModel;
using ClassroomCtrl.Shared.Attachments;
using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Protocol;

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

public class ChatMessage : INotifyPropertyChanged
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

    /// <summary>Phase 19 (v1.1) — chat-embedded file attachment.  Null for
    /// plain text chats.  When non-null, the bubble template renders a
    /// download / open card above (or instead of) the message text;
    /// AttachmentManager has already persisted the bytes by the time this
    /// VM is added to the conversation's Messages collection (so the Open
    /// button always works as soon as the bubble appears).</summary>
    public FileAttachment? Attachment { get; init; }
    public bool HasAttachment => Attachment != null;
    public string AttachmentFileName => Attachment?.FileName ?? "";
    public string AttachmentSizeFormatted => Attachment == null
        ? ""
        : AttachmentManager.FormatSize(Attachment.FileSize);
    /// <summary>Heuristic icon glyph from the file extension; falls back to
    /// the generic 📄 for unknown types.</summary>
    public string AttachmentIcon => Attachment?.FileType?.ToLowerInvariant() switch
    {
        ".pdf"                                  => "📕",
        ".doc" or ".docx" or ".rtf" or ".txt"   => "📝",
        ".xls" or ".xlsx" or ".csv"             => "📊",
        ".ppt" or ".pptx"                       => "📈",
        ".zip" or ".rar" or ".7z"               => "🗜",
        ".png" or ".jpg" or ".jpeg" or ".gif"
            or ".bmp" or ".webp"                => "🖼",
        ".mp3" or ".wav" or ".m4a"              => "🎵",
        ".mp4" or ".mov" or ".avi" or ".mkv"    => "🎬",
        _                                       => "📄",
    };

    // Phase 3 Section A — bubble re-renders TimeAgoDisplay when MainViewModel's 30-second
    // DispatcherTimer ticks NotifyTimeChanged on each instance.  TimeAgoDisplay has no
    // setter so re-evaluation only happens via this PropertyChanged signal.
    public event PropertyChangedEventHandler? PropertyChanged;

    public void NotifyTimeChanged()
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TimeAgoDisplay)));
}
