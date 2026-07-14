namespace ClassroomCtrl.Avalonia.Teacher.ViewModels;

/// <summary>TT-7-C — one line in the chat rail. Immutable; the rail is an append-only log.</summary>
public sealed class ChatLineViewModel
{
    public string Sender { get; }
    public string Text { get; }

    /// <summary>The teacher's own message (right-aligned bubble) vs an incoming one.</summary>
    public bool IsOwn { get; }

    /// <summary>A direct message (either the teacher's own DM, or an incoming DM to the teacher).</summary>
    public bool IsDirect { get; }

    /// <summary>Scope label: "→ Everyone" / "→ Alice" for own sends, "direct" for an incoming DM,
    /// or "" for an incoming broadcast.</summary>
    public string Scope { get; }

    public ChatLineViewModel(string sender, string text, bool isOwn, bool isDirect, string scope)
    {
        Sender = sender;
        Text = text;
        IsOwn = isOwn;
        IsDirect = isDirect;
        Scope = scope;
    }
}
