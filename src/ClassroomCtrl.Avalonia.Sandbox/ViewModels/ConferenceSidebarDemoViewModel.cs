using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClassroomCtrl.Shared.Models;      // ChatMessageKind (ported to Shared.Wire)
using ClassroomCtrl.Shared.Protocol;    // FileAttachment
// ChatMessage exists in both Models and Protocol — the sidebar binds the Models one.
using ChatMessage = ClassroomCtrl.Shared.Models.ChatMessage;

namespace ClassroomCtrl.Avalonia.Sandbox.ViewModels;

/// <summary>
/// Sandbox demo VM reproducing the binding contract ConferenceSidebar.xaml expects
/// from its host shell VM (Teacher.MainViewModel / StudentConferenceShellViewModel).
/// No dedicated ConferenceSidebarViewModel exists in the shipped code — the sidebar
/// binds to whatever parent DataContext it's dropped into.
///
/// Reuses the REAL ported ChatMessage model (Shared.Wire) for chat bubbles.
/// Participant rows use a small demo model (the shipped participant VM lives in the
/// host shells and exposes WPF Visibility members; here MicLive/CamLive/HandRaised
/// are bools mapped to Avalonia IsVisible).
/// </summary>
public partial class ConferenceSidebarDemoViewModel : ObservableObject
{
    public sealed class ConferenceRole
    {
        public bool CanMuteOthers { get; init; }
        public bool CanRecognizeHand { get; init; }
    }

    /// <summary>Chat backing store (Messages + editable draft). Mirrors the shipped
    /// ConferenceConversation the sidebar binds.</summary>
    public partial class ConversationDemo : ObservableObject
    {
        public ObservableCollection<ChatMessage> Messages { get; } = new();
        [ObservableProperty] private string draftInput = "";
    }

    /// <summary>Raised-hands queue holder. HasRaisedHands drives section visibility
    /// (WPF used a DataTrigger on RaisedHandQueue.Count == 0).</summary>
    public partial class GalleryDemo : ObservableObject
    {
        public ObservableCollection<ParticipantDemo> RaisedHandQueue { get; } = new();
        public bool HasRaisedHands => RaisedHandQueue.Count > 0;
        public void RaiseChanged() => OnPropertyChanged(nameof(HasRaisedHands));
    }

    public ConferenceRole Role { get; } = new() { CanMuteOthers = true, CanRecognizeHand = true };
    public ConversationDemo ConferenceConversation { get; } = new();
    public GalleryDemo ConferenceGallery { get; } = new();
    public ObservableCollection<ParticipantDemo> Students { get; } = new();

    // Sidebar chrome / tabs.
    [ObservableProperty] private bool isConferenceSidebarVisible = true;
    [ObservableProperty] private bool isConferenceChatTabSelected = true;
    [ObservableProperty] private bool isConferenceParticipantsTabSelected;

    // Draft attachment state (strip hidden in the demo; picker deferred).
    public bool HasConferenceDraftAttachment => false;

    public ConferenceSidebarDemoViewModel()
    {
        // Seed chat: a few text bubbles + one with an inert attachment card.
        ConferenceConversation.Messages.Add(new ChatMessage
        { SenderName = "Teacher", MessageText = "Welcome to the conference, everyone!", Kind = ChatMessageKind.Teacher });
        ConferenceConversation.Messages.Add(new ChatMessage
        { SenderName = "Somchai", MessageText = "สวัสดีครับ 👋", Kind = ChatMessageKind.Student });
        ConferenceConversation.Messages.Add(new ChatMessage
        {
            SenderName = "Teacher",
            MessageText = "Here are today's slides.",
            Kind = ChatMessageKind.Teacher,
            Attachment = new FileAttachment { Id = Guid.NewGuid(), FileName = "lesson-3.pdf", FileSize = 2_412_544, FileType = ".pdf" },
        });
        ConferenceConversation.Messages.Add(new ChatMessage
        { SenderName = "Ploy", MessageText = "Thank you kha!", Kind = ChatMessageKind.Student });

        // Seed participants (mic/cam/hand states cover the indicator paths).
        Students.Add(new ParticipantDemo { DisplayName = "Somchai", MachineName = "LAB-01", MicLive = true, CamLive = true });
        Students.Add(new ParticipantDemo { DisplayName = "Ploy", MachineName = "LAB-02", MicLive = false, CamLive = false, HandRaised = true });
        Students.Add(new ParticipantDemo { DisplayName = "Anong", MachineName = "LAB-03", MicLive = true, CamLive = false });
        Students.Add(new ParticipantDemo { DisplayName = "Kittipong", MachineName = "LAB-04", MicLive = false, CamLive = true });

        // Seed one raised hand.
        ConferenceGallery.RaisedHandQueue.Add(Students[1]); // Ploy
        ConferenceGallery.RaiseChanged();
    }

    [RelayCommand] private void SelectConferenceChatTab()
    { IsConferenceChatTabSelected = true; IsConferenceParticipantsTabSelected = false; }

    [RelayCommand] private void SelectConferenceParticipantsTab()
    { IsConferenceParticipantsTabSelected = true; IsConferenceChatTabSelected = false; }

    [RelayCommand]
    private void SendConferenceChat()
    {
        var text = ConferenceConversation.DraftInput?.Trim();
        if (string.IsNullOrEmpty(text)) return;
        ConferenceConversation.Messages.Add(new ChatMessage
        { SenderName = "You", MessageText = text, Kind = ChatMessageKind.Teacher });
        ConferenceConversation.DraftInput = "";
    }

    [RelayCommand]
    private void MuteParticipant(ParticipantDemo? p)
    { if (p != null) p.MicLive = false; }

    [RelayCommand]
    private void RecognizeHand(ParticipantDemo? p)
    {
        if (p == null) return;
        p.HandRaised = false;
        ConferenceGallery.RaisedHandQueue.Remove(p);
        ConferenceGallery.RaiseChanged();
    }
}

/// <summary>Demo participant row model. WPF exposed HandRaisedVisibility (Visibility);
/// Avalonia uses the HandRaised bool → IsVisible.</summary>
public partial class ParticipantDemo : ObservableObject
{
    public string DisplayName { get; init; } = "";
    public string MachineName { get; init; } = "";
    [ObservableProperty] private bool micLive;
    [ObservableProperty] private bool camLive;
    [ObservableProperty] private bool handRaised;
}
