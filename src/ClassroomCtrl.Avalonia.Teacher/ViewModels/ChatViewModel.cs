using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ClassroomCtrl.Avalonia.Teacher.Services;
using ClassroomCtrl.Shared.Protocol;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClassroomCtrl.Avalonia.Teacher.ViewModels;

/// <summary>
/// TT-7-C — the teacher's chat rail. Sends broadcast chat (to Everyone) or a direct message
/// to one selected student (routed reliable:true by the seam), and logs incoming student chat
/// attributed to the sender. DM privacy is receiver-side (the student IsForMe filter, TT-6-D):
/// a ChatDirect to A is a targeted envelope that every other student drops.
/// </summary>
public partial class ChatViewModel : ObservableObject
{
    private readonly ITeacherMessaging _messaging;
    private readonly ISoundService _sound;
    private readonly Action<string>? _notify;

    /// <summary>Exposed so the DM target picker can bind to the live tile list.</summary>
    public TeacherGridViewModel Grid { get; }

    public ObservableCollection<ChatLineViewModel> Messages { get; } = new();

    [ObservableProperty] private string draft = "";

    /// <summary>null = broadcast to Everyone; otherwise a DM to this student.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TargetLabel))]
    private StudentTileViewModel? dmTarget;

    public string TargetLabel => DmTarget is null ? "Everyone" : DmTarget.DisplayName;

    public ChatViewModel(ITeacherMessaging messaging, ISoundService sound, TeacherGridViewModel grid, Action<string>? notify = null)
    {
        _messaging = messaging;
        _sound = sound;
        Grid = grid;
        _notify = notify;
        _messaging.ChatReceived += OnChatReceived;
    }

    // Inbound fires on a transport background thread → marshal before touching Messages.
    private void OnChatReceived(object? sender, ChatMessage c) =>
        Dispatcher.UIThread.Post(() =>
        {
            bool direct = c.RecipientId.HasValue;
            Messages.Add(new ChatLineViewModel(c.SenderName, c.Text, isOwn: false, isDirect: direct,
                                               scope: direct ? "direct" : ""));
            _sound.Play(NotificationSound.Chat);
            _notify?.Invoke($"💬 {c.SenderName}: {Trunc(c.Text)}");
        });

    [RelayCommand]
    private void ClearDmTarget() => DmTarget = null;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task Send()
    {
        var text = Draft?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        var target = DmTarget;
        Draft = "";
        try
        {
            if (target is null)
            {
                await _messaging.BroadcastChatAsync(text, CancellationToken.None);
                Messages.Add(new ChatLineViewModel("You", text, isOwn: true, isDirect: false, scope: "→ Everyone"));
            }
            else
            {
                await _messaging.SendDirectMessageAsync(target.EndpointId, text, CancellationToken.None);
                Messages.Add(new ChatLineViewModel("You", text, isOwn: true, isDirect: true, scope: $"→ {target.DisplayName}"));
            }
        }
        catch (Exception ex)
        {
            Messages.Add(new ChatLineViewModel("System", $"send failed: {ex.Message}", isOwn: false, isDirect: false, scope: ""));
        }
    }

    private bool CanSend() => !string.IsNullOrWhiteSpace(Draft);

    partial void OnDraftChanged(string value) => SendCommand.NotifyCanExecuteChanged();

    private static string Trunc(string s) => s.Length <= 40 ? s : s[..40] + "…";
}
