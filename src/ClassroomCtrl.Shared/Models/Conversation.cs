using System.Collections.ObjectModel;
using System.ComponentModel;

namespace ClassroomCtrl.Shared.Models;

/// <summary>
/// Phase 3 Section E — chat panel splits into per-conversation tabs (Zoom-style).  One
/// always-present Everyone conversation handles broadcast messages; per-student DM
/// conversations are spawned on demand (right-click → Chat, or when a student DMs in).
/// </summary>
public enum ConversationKind
{
    Everyone,
    DM,
}

public class Conversation : INotifyPropertyChanged
{
    /// <summary>"everyone" or "dm:{StudentPCName}" — used to dedupe lookups.</summary>
    public string Id { get; init; } = "";

    private string _displayName = "";
    public string DisplayName
    {
        get => _displayName;
        set
        {
            if (_displayName == value) return;
            _displayName = value;
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Initial));
        }
    }
    public ConversationKind Kind { get; init; }

    /// <summary>Null for the Everyone conversation; the student's PCName for DM tabs.</summary>
    public string? StudentPCName { get; init; }

    /// <summary>Per-tab message log.  Outgoing teacher messages and incoming student
    /// replies both append here so the bubble flow stays in one place.</summary>
    public ObservableCollection<ChatMessage> Messages { get; } = new();

    /// <summary>Per-tab draft so switching tabs doesn't lose typed-but-unsent text.</summary>
    private string _draftInput = "";
    public string DraftInput
    {
        get => _draftInput;
        set
        {
            if (_draftInput == value) return;
            _draftInput = value;
            OnPropertyChanged(nameof(DraftInput));
        }
    }

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        set
        {
            if (_unreadCount == value) return;
            _unreadCount = value;
            OnPropertyChanged(nameof(UnreadCount));
            OnPropertyChanged(nameof(HasUnread));
        }
    }
    public bool HasUnread => _unreadCount > 0;

    public string Initial => string.IsNullOrEmpty(DisplayName)
        ? "?"
        : DisplayName.Substring(0, 1).ToUpperInvariant();

    public bool IsEveryone => Kind == ConversationKind.Everyone;
    public bool IsDM       => Kind == ConversationKind.DM;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string name)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
