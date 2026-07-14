using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Student.Mac.Agent.ViewModels;

/// <summary>
/// Backing state for the ChatWindow: the message history and the draft/send interaction. Identity-free —
/// it appends what it's told and raises <see cref="MessageSent"/> when the student sends, so the App can
/// relay the text to the daemon over IPC (the daemon stamps the real sender). All mutation happens on the
/// UI thread (the App marshals incoming IPC via Dispatcher before calling <see cref="AppendIncoming"/>).
/// </summary>
public sealed partial class ChatViewModel : ObservableObject
{
    /// <summary>Raised when the student sends a line (text already appended locally + draft cleared).</summary>
    public event Action<string>? MessageSent;

    public ObservableCollection<ChatEntry> Messages { get; } = new();

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _draft = "";

    /// <summary>Append a message received from the daemon (Teacher or a classmate).</summary>
    public void AppendIncoming(string sender, string text)
        => Messages.Add(new ChatEntry(string.IsNullOrWhiteSpace(sender) ? "Teacher" : sender, text, Now(), isMine: false));

    private bool CanSend() => !string.IsNullOrWhiteSpace(Draft);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
        var text = Draft.Trim();
        if (text.Length == 0) return;
        Messages.Add(new ChatEntry("You", text, Now(), isMine: true));   // optimistic local echo
        Draft = "";
        MessageSent?.Invoke(text);
    }

    private static string Now() => DateTime.Now.ToString("HH:mm");
}
