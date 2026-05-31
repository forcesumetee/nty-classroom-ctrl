using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using ClassroomCtrl.Shared.Branding;
using ClassroomCtrl.Shared.Localization;
using ClassroomCtrl.Shared.Protocol;
using ClassroomCtrl.Shared.Wpf.ViewModels;
using ClassroomCtrl.Teacher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
// Phase 2 Section E — alias resolves the name clash with Protocol.ChatMessage (the
// over-the-wire type).  Bare ChatMessage refers to the UI-side log entry from now on.
using ChatMessage = ClassroomCtrl.Shared.Models.ChatMessage;
using ChatMessageKind = ClassroomCtrl.Shared.Models.ChatMessageKind;
// Phase 3 Section C — bell + Activity tab feed (system/error/hand-raised events).
using Notification = ClassroomCtrl.Shared.Models.Notification;
using NotificationKind = ClassroomCtrl.Shared.Models.NotificationKind;
// Phase 3 Section E — chat panel splits into per-conversation tabs (Zoom-style).
using Conversation = ClassroomCtrl.Shared.Models.Conversation;
using ConversationKind = ClassroomCtrl.Shared.Models.ConversationKind;

namespace ClassroomCtrl.Teacher.ViewModels;

public partial class MainViewModel : ObservableObject, IConferenceSidebarHost
{
    public ObservableCollection<StudentViewModel> Students { get; } = new();
    public ObservableCollection<RoomViewModel> Rooms { get; } = new();

    // Phase 3 Section E — conversation tabs replace the single flat ChatMessages list.
    // EveryoneConversation always exists and can't be closed.  DM conversations are
    // spawned on demand via OpenDMConversation / inbound DMs.  ActiveConversation drives
    // the visible message list and the input box's two-way DraftInput binding.
    public ObservableCollection<Conversation> Conversations { get; } = new();
    public Conversation EveryoneConversation { get; } = new Conversation
    {
        Id = "everyone",
        // DisplayName is updated in RefreshLocalizedTexts so the language switch picks it up.
        DisplayName = "Everyone",
        Kind = ConversationKind.Everyone,
    };

    /// <summary>Phase 16-X (Bug G fix, 2026-06-01) — dedicated conversation
    /// for chat composed/received inside the Conference sidebar.  Separate
    /// from <see cref="EveryoneConversation"/> so a Conference chat doesn't
    /// leak into the Classroom rail (and vice versa).  Routing on receive:
    /// chat.IsConferenceContext=true lands here; everything else uses the
    /// existing Classroom rail path in OnChatReceived.  ConferenceSidebar
    /// XAML binds its chat tab to this conversation directly.</summary>
    public Conversation ConferenceConversation { get; } = new Conversation
    {
        Id = "conference",
        DisplayName = "Conference",
        Kind = ConversationKind.Everyone,
    };

    [ObservableProperty] private Conversation? activeConversation;

    // Phase 3 Section G/H — main content area view router.  StudentGrid is the default;
    // QuizManager swaps in the embedded QuizManagerView. New embedded views go here as
    // they're added in later phases (Class Roster etc.).
    // Phase 15-B (MVP) — Conference value added.  CurrentMainView flips to
    // Conference when the header mode-toggle pill is clicked OR when a
    // ConferenceStart envelope arrives (mode-exclusive with the other two
    // values; same ViewKindToVisibilityConverter pattern as StudentGrid /
    // QuizManager).
    public enum MainViewKind { StudentGrid, QuizManager, Conference }

    [ObservableProperty] private MainViewKind currentMainView = MainViewKind.StudentGrid;

    // Phase 15-B (MVP) — Conference Mode state.  IsInConference is the
    // single source of truth for "the teacher's shell is in Conference mode".
    // OnIsInConferenceChanged flips CurrentMainView so the
    // ViewKindToVisibilityConverter swaps the content cell.
    //
    // ConferenceSessionId is Guid.Empty when no session is active; set to
    // a fresh Guid by StartConferenceCommand and broadcast as the
    // ConferenceStartMessage.SessionId.  ConferenceStartedAt is the wall-
    // clock UTC anchor used by the future elapsed-time chip (Phase 15-C/D).
    [ObservableProperty] private bool isInConference;
    [ObservableProperty] private Guid conferenceSessionId = Guid.Empty;
    [ObservableProperty] private DateTime? conferenceStartedAt;

    /// <summary>Phase 15-B — true while ConferenceSessionId is a real Guid (set
    /// by StartConferenceCommand, cleared by EndConferenceCommand).  The
    /// ConferenceView XAML uses this to swap between the Start-CTA empty state
    /// and the "in progress" placeholder.</summary>
    public bool IsConferenceSessionActive => ConferenceSessionId != Guid.Empty;

    /// <summary>Phase 15-C — gallery view-model for the active Conference.
    /// Singleton per MainViewModel instance; tiles are rebuilt whenever the
    /// session enters/leaves Conference mode.  Bound to the ConferenceView's
    /// embedded ConferenceGalleryView (live-session state).</summary>
    public ConferenceGalleryViewModel ConferenceGallery { get; } = new();

    /// <summary>Phase 16-D — Conference role.  Teacher is the Host today;
    /// drives Visibility gating on the toolbar / sidebar / tile admin
    /// actions via the standard <c>Role.CanX</c> binding chain.  Singleton
    /// per VM lifetime — the host identity doesn't change mid-session in
    /// this tier (a future CoHost handoff lands fresh code, not a mutation
    /// here).</summary>
    public ClassroomCtrl.Shared.Wpf.Roles.IConferenceRole Role { get; } =
        new ClassroomCtrl.Shared.Wpf.Roles.HostRole();

    // Phase 15-D step 1 — slide-in sidebar state for the Meet-style shell.
    // The sidebar hosts two tabs (Chat | Participants) that toggle via
    // ConferenceSidebarTabIndex; visibility itself rides
    // IsConferenceSidebarVisible.  Both default to "closed, Chat tab".
    // ToggleConferenceSidebarCommand opens to whichever tab was last shown;
    // ShowConferenceHandQueueCommand jumps straight to Participants so the
    // teacher can see who's raised a hand without an extra click.
    [ObservableProperty] private bool isConferenceSidebarVisible;
    [ObservableProperty] private int conferenceSidebarTabIndex;
    public bool IsConferenceChatTabSelected         => ConferenceSidebarTabIndex == 0;
    public bool IsConferenceParticipantsTabSelected => ConferenceSidebarTabIndex == 1;

    partial void OnConferenceSidebarTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsConferenceChatTabSelected));
        OnPropertyChanged(nameof(IsConferenceParticipantsTabSelected));
    }

    /// <summary>Phase 15-D step 1 — mirrors <c>App.Camera?.IsActive</c> as an
    /// observable bool so the ConferenceToolbar's mic / cam DataTriggers can
    /// react to broadcast-state changes without polling.  Kept in sync from
    /// <see cref="UpdateCameraButtonText"/> (called everywhere the cam state
    /// flips today, including the 14-B StoppedDueToError handler).</summary>
    [ObservableProperty] private bool isBroadcastingCamera;

    /// <summary>Phase 17.1 step 2 — bound by <c>Window.TaskbarItemInfo.Overlay</c>
    /// to paint a red-dot badge on the Teacher's taskbar icon when one or
    /// more notifications are unread.  Generated programmatically by
    /// <see cref="NotificationService.CreateBadgeOverlay"/>; refreshed on every
    /// <see cref="NotificationService.UnreadCount"/> transition (subscription
    /// wired in the constructor).  Null = badge cleared.</summary>
    [ObservableProperty] private System.Windows.Media.ImageSource? taskbarOverlayImage;

    /// <summary>Phase 17.1 step 2 — tooltip shown over the taskbar badge.
    /// Reads "3 unread notifications" / "ไม่ได้อ่าน 3 รายการ" in the active
    /// locale; null when there are zero unread (Windows skips overlay tooltip
    /// when the overlay image itself is null).</summary>
    public string? TaskbarOverlayTooltip =>
        App.Notifications == null || App.Notifications.UnreadCount == 0
            ? null
            : Loc.Format("Notif_UnreadTooltip", App.Notifications.UnreadCount);

    partial void OnConferenceSessionIdChanged(Guid value)
    {
        OnPropertyChanged(nameof(IsConferenceSessionActive));
        RebuildConferenceGallery();
    }

    /// <summary>Phase 15-C — rebuild the gallery tiles from the current
    /// <see cref="Students"/> collection plus a self-tile for the teacher.
    /// Called on session start/end + when the Students collection changes
    /// while a session is live (so a late-joining student gets a tile).
    /// Idempotent: if the tile already exists for an endpoint, we keep it
    /// (so its JpegFrame + IsSpeaking continuity survives).</summary>
    private void RebuildConferenceGallery()
    {
        var gallery = ConferenceGallery;
        if (!IsConferenceSessionActive)
        {
            gallery.Tiles.Clear();
            gallery.PinnedEndpointId = null;
            return;
        }

        // Build the desired tile set: self-tile first (teacher), then all
        // connected students in their existing order.
        var desired = new System.Collections.Generic.List<(Guid Id, string Name, bool IsSelf)>();
        var selfId = App.Server?.TeacherEndpointId ?? Guid.Empty;
        desired.Add((selfId, OrganizationSubtitle ?? "Teacher", true));
        foreach (var s in Students)
        {
            desired.Add((s.EndpointId, s.DisplayName, false));
        }

        // Drop tiles whose endpoints aren't in the desired set.
        for (int i = gallery.Tiles.Count - 1; i >= 0; i--)
        {
            var t = gallery.Tiles[i];
            if (!desired.Exists(d => d.Id == t.EndpointId))
                gallery.Tiles.RemoveAt(i);
        }
        // Add tiles for new endpoints + keep names current on existing ones.
        for (int idx = 0; idx < desired.Count; idx++)
        {
            var d = desired[idx];
            var existing = gallery.Tiles.FirstOrDefault(x => x.EndpointId == d.Id);
            if (existing == null)
            {
                gallery.Tiles.Insert(Math.Min(idx, gallery.Tiles.Count),
                    new ConferenceTileViewModel(d.Id, d.Name, d.IsSelf));
            }
            else if (existing.DisplayName != d.Name)
            {
                existing.DisplayName = d.Name;
            }
        }
        // If the pinned endpoint left, clear the pin.
        if (gallery.PinnedEndpointId.HasValue &&
            !gallery.Tiles.Any(t => t.EndpointId == gallery.PinnedEndpointId.Value))
        {
            gallery.PinnedEndpointId = null;
        }
    }

    partial void OnIsInConferenceChanged(bool value)
    {
        // Flipping the mode flag flips the content-cell view.  Flipping back
        // out of Conference returns to the StudentGrid default; the
        // QuizManager view is only reachable via its explicit OpenQuizManager
        // command, so we never auto-route there.
        CurrentMainView = value ? MainViewKind.Conference : MainViewKind.StudentGrid;
    }

    // Phase 3 Section C — system / error / hand-raised events.  Newest-first (Insert at 0)
    // so the bell popup and Activity tab show recent activity without reversing.  Capped at
    // 100 to bound memory across long classes.  AddNotification is the single mutator.
    public ObservableCollection<Notification> Notifications { get; } = new();

    public int UnreadNotificationsCount => Notifications.Count(n => !n.IsRead);
    public bool HasUnreadNotifications  => UnreadNotificationsCount > 0;
    public bool HasAnyNotifications     => Notifications.Count > 0;

    internal void AddNotification(string title, string body, NotificationKind kind = NotificationKind.System)
    {
        Notifications.Insert(0, new Notification
        {
            Title = title,
            Body = body,
            Kind = kind,
        });

        // Cap at 100 to prevent unbounded growth (newest at front, drop oldest at tail).
        while (Notifications.Count > 100) Notifications.RemoveAt(Notifications.Count - 1);

        OnPropertyChanged(nameof(UnreadNotificationsCount));
        OnPropertyChanged(nameof(HasUnreadNotifications));
        OnPropertyChanged(nameof(HasAnyNotifications));
        OnPropertyChanged(nameof(ActivityFeed));
    }

    internal void MarkAllNotificationsRead()
    {
        var anyChanged = false;
        foreach (var n in Notifications)
        {
            if (!n.IsRead) { n.IsRead = true; anyChanged = true; }
        }
        if (anyChanged)
        {
            OnPropertyChanged(nameof(UnreadNotificationsCount));
            OnPropertyChanged(nameof(HasUnreadNotifications));
        }
    }

    // Phase 2 Section E — chat append helpers.  All bubble construction lives here so the
    // call sites stay readable and the SenderName / Kind invariants hold without duplication.
    // Phase 3 Section C — system / error route to Notifications.
    // Phase 3 Section E — Teacher / Student / DM helpers route to per-tab conversations.
    internal void AppendSystemChat(string text)
        => AddNotification(Loc.Get("Chat_SystemPrefix"), text, NotificationKind.System);

    internal void AppendErrorChat(string text)
        => AddNotification(Loc.Get("Chat_SystemPrefix"), text, NotificationKind.Error);

    internal void AppendTeacherChat(string text)
        => EveryoneConversation.Messages.Add(new ChatMessage
        {
            SenderName = Loc.Get("Chat_MePrefix"),
            Kind = ChatMessageKind.Teacher,
            MessageText = text,
        });

    internal void AppendStudentChat(string senderName, string text)
    {
        EveryoneConversation.Messages.Add(new ChatMessage
        {
            SenderName = senderName,
            Kind = ChatMessageKind.Student,
            MessageText = text,
        });
        // If the user is reading some other tab, mark Everyone as having unread.
        if (ActiveConversation != EveryoneConversation) EveryoneConversation.UnreadCount++;
    }

    /// <summary>Outgoing DM the teacher just sent (no inbound counterpart) — appended to
    /// the matching DM conversation; auto-creates the tab when missing.</summary>
    internal void AppendDMChat(string senderName, string text, string? studentPCName = null)
    {
        var conv = string.IsNullOrEmpty(studentPCName)
            ? null
            : Conversations.FirstOrDefault(c => c.Kind == ConversationKind.DM && c.StudentPCName == studentPCName);

        // Fallback when caller didn't supply PCName — mirror legacy behavior and dump in Everyone.
        if (conv == null)
        {
            EveryoneConversation.Messages.Add(new ChatMessage
            {
                SenderName = senderName,
                Kind = ChatMessageKind.DM,
                MessageText = text,
            });
            return;
        }

        conv.Messages.Add(new ChatMessage
        {
            SenderName = senderName,
            Kind = ChatMessageKind.DM,
            MessageText = text,
        });
    }

    // Phase 4 Part 4: codec dropdown
    // Phase 11-B inc4.1: default reverted to MJPEG for ship — matches App.SelectedCodec.
    // SW H.264 is CPU-bound (< 10 FPS) on the customer hardware class; MJPEG runs 20-25 FPS
    // smooth on a gigabit LAN.  H.264 stays in the list (selectable) so the option works
    // the moment HW H.264 (Quick Sync) is validated on Intel and can be the new default (Tier 2).
    public ObservableCollection<VideoCodec> CodecOptions { get; } = new() { VideoCodec.Mjpeg, VideoCodec.H264 };
    [ObservableProperty] private VideoCodec selectedCodec = VideoCodec.Mjpeg;

    partial void OnSelectedCodecChanged(VideoCodec value)
    {
        App.SelectedCodec = value;
        AppendSystemChat(Loc.Format("Chat_CodecChanged", value));
    }

    [ObservableProperty] private int connectedCount;
    [ObservableProperty] private string connectedCountText = "";
    // Phase 2 Section A: header subtitle. Pulled from BrandingService.Current.OrganizationName,
    // refreshed on construction + on every branding-config save (BrandingService.Changed).
    [ObservableProperty] private string organizationSubtitle = "";

    // Phase 2 Section B: search filter for the student grid.  StudentsView wraps the Students
    // collection through an ICollectionView so the same source can be filtered without mutating
    // it.  SearchTerm change triggers Refresh(); Filter falls open when the term is whitespace.
    [ObservableProperty] private string searchTerm = "";
    public ICollectionView StudentsView { get; }

    // Phase 2 Section C: header bell badge + popup.  Reads HandRaisedVisibility because the
    // existing model never grew a bool — Phase 13 wired the badge straight to a Visibility
    // property.  Refreshed by RaiseNotificationsChanged() whenever hand-raise state mutates.
    public int RaisedHandsCount =>
        Students.Count(s => s.HandRaisedVisibility == System.Windows.Visibility.Visible);
    public bool HasNotifications => RaisedHandsCount > 0;
    public IEnumerable<StudentViewModel> RaisedHandsStudents =>
        Students.Where(s => s.HandRaisedVisibility == System.Windows.Visibility.Visible);

    private void RaiseNotificationsChanged()
    {
        OnPropertyChanged(nameof(RaisedHandsCount));
        OnPropertyChanged(nameof(HasNotifications));
        OnPropertyChanged(nameof(RaisedHandsStudents));
    }

    // Phase 3 Section C — Activity tab now sources from Notifications (full log of system /
    // error / hand-raised events including ones the user already acknowledged via the bell).
    // Notifications is already newest-first so no Reverse() needed; cap at 50 visible.
    public IEnumerable<Notification> ActivityFeed
        => Notifications.Take(50);

    // Phase 2 Section F — right-rail tab selector (0 = Chat, 1 = Activity).  TabSelectedIndex
    // change drives the Visibility binding via BoolToVisibility on tab body Borders.
    [ObservableProperty] private int rightRailTabIndex;
    public bool IsChatTabSelected => RightRailTabIndex == 0;
    public bool IsActivityTabSelected => RightRailTabIndex == 1;
    partial void OnRightRailTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsChatTabSelected));
        OnPropertyChanged(nameof(IsActivityTabSelected));
    }

    public IRelayCommand SelectChatTabCommand { get; }
    public IRelayCommand SelectActivityTabCommand { get; }
    // Phase 1.5: live local-IP banner
    [ObservableProperty] private string localIPDisplay = "";
    private System.Windows.Threading.DispatcherTimer? _ipRefreshTimer;
    // Phase 3 Section A — re-renders TimeAgoDisplay on every visible chat/notification bubble
    // every 30s.  Pumps PropertyChanged on each instance so DataTemplate bindings re-evaluate
    // ("Just now" → "1 min ago" → "5 min ago" without user action).
    private System.Windows.Threading.DispatcherTimer? _timeAgoRefreshTimer;
    [ObservableProperty] private bool screensLocked;
    [ObservableProperty] private string lockButtonText = "";
    [ObservableProperty] private bool isScreenSharing;
    [ObservableProperty] private string shareScreenButtonText = "";
    [ObservableProperty] private bool isMicOn;
    [ObservableProperty] private string micButtonText = "";
    [ObservableProperty] private bool isSystemAudioOn;
    [ObservableProperty] private string systemAudioButtonText = "";

    // Toolbar tooltips that follow on/off state.
    public string MicTooltipText => Loc.Get(IsMicOn ? "Tooltip_MicOn" : "Tooltip_MicOff");
    public string SpeakerTooltipText => Loc.Get(IsSystemAudioOn ? "Tooltip_SpeakerOn" : "Tooltip_SpeakerOff");

    [ObservableProperty] private double masterVolume = 1.0;

    // Phase 4 Part 5: live bitrate label
    [ObservableProperty] private string currentBitrateText = "";

    // Phase 5a: recording state
    [ObservableProperty] private bool isRecording;
    [ObservableProperty] private string recordingButtonText = "";
    [ObservableProperty] private string recordingElapsedText = "";
    [ObservableProperty] private System.Windows.Visibility recordingIndicatorVisibility = System.Windows.Visibility.Collapsed;
    private System.Windows.Threading.DispatcherTimer? _recordingTimer;
    private DateTimeOffset _recordingStartedAt;

    [ObservableProperty] private bool currentBlockUsbStorage;
    [ObservableProperty] private bool currentBlockOpticalDrive;
    [ObservableProperty] private bool currentBlockPrinting;
    [ObservableProperty] private List<string> currentBlockedProcessNames = new();
    [ObservableProperty] private List<string> currentBlockedHostnames = new();

    // Phase 8 (Bug D) — applied-policy state survives app restart. Lives next to branding.json
    // under %ProgramData% so it's machine-wide and visible across teacher logins. Saved on
    // Apply (whole-class path), cleared on Revert. Per-student policy is intentionally NOT
    // persisted here — only the broadcast policy, since that's the customer-reported bug.
    private static readonly string AppliedPolicyJsonPath = Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.CommonApplicationData),
        "NTY", "ClassroomCtrl", "applied-policy.json");

    public IRelayCommand LockAllCommand { get; }
    public IRelayCommand ShareScreenCommand { get; }
    public IRelayCommand ToggleMicCommand { get; }
    public IRelayCommand ToggleSystemAudioCommand { get; }
    public IRelayCommand MuteAllStudentsCommand { get; }
    public IRelayCommand SendFileCommand { get; }
    public IRelayCommand StartRecordingCommand { get; }
    public IRelayCommand OpenRecordingsFolderCommand { get; }
    public IRelayCommand OpenBreakoutCommand { get; }
    public IRelayCommand ApplyPolicyCommand { get; }
    public IRelayCommand OpenLanguageCommand { get; }
    public IRelayCommand SendChatCommand { get; }

    /// <summary>Phase 16-X (Bug G fix) — chat send from the Conference
    /// sidebar.  Wire emit goes through
    /// <see cref="Services.ControlServer.BroadcastConferenceChatAsync"/>
    /// which stamps IsConferenceContext=true; locally appends to
    /// <see cref="ConferenceConversation"/> only so the Classroom rail
    /// (bound to <see cref="ActiveConversation"/>) stays clean.</summary>
    public IRelayCommand SendConferenceChatCommand { get; }
    public IRelayCommand<StudentViewModel?> LockOneCommand { get; }
    public IRelayCommand<StudentViewModel?> UnlockOneCommand { get; }
    public IRelayCommand<StudentViewModel?> ApplyPolicyToStudentCommand { get; }
    public IRelayCommand<StudentViewModel?> RevertPolicyForStudentCommand { get; }
    public IRelayCommand<StudentViewModel?> SendDirectMessageCommand { get; }
    public IRelayCommand<StudentViewModel?> RemoveFromRoomCommand { get; }
    public IRelayCommand<StudentViewModel?> ViewStudentScreenCommand { get; }

    // Phase 8.5: host assignment
    public IRelayCommand<StudentViewModel?> SetAsHostCommand { get; }
    public IRelayCommand<StudentViewModel?> RemoveHostCommand { get; }

    // Phase 9.1: Student Demonstration
    public IRelayCommand<StudentViewModel?> StartDemoCommand { get; }
    public IRelayCommand StopDemoCommand { get; }

    // Phase 9.2: Screen Pen
    public IRelayCommand OpenScreenPenCommand { get; }

    // Phase 9.3: Class Roster
    public IRelayCommand OpenClassRosterCommand { get; }
    public IRelayCommand MarkAttendanceCommand { get; }
    // Phase 7 Section D — let the teacher exit attendance mode without
    // routing through the Roster Manager; HasActiveRoster mirrors the
    // visibility flag so sidebar can disable Mark Attendance + show its
    // tooltip when nothing is active.
    public IRelayCommand DeactivateRosterCommand { get; }
    [ObservableProperty] private bool hasActiveRoster;

    // Phase 3.5: Sound effects toggle
    public IRelayCommand ToggleSoundsCommand { get; }
    [ObservableProperty] private string soundsButtonText = "";

    // Phase 9.5: Camera Broadcast
    public IRelayCommand ToggleCameraCommand { get; }
    [ObservableProperty] private string cameraButtonText = "";

    // Phase 15-B (MVP) — header mode-toggle pill commands.  These ONLY flip
    // the UI mode preference (CurrentMainView via OnIsInConferenceChanged);
    // they do NOT broadcast ConferenceStart/End.  The actual session
    // lifecycle commands StartConferenceCommand / EndConferenceCommand
    // live alongside the ConferenceView's Start CTA and are wired in
    // step 5.
    public IRelayCommand EnterConferenceModeCommand { get; }
    public IRelayCommand ExitConferenceModeCommand { get; }

    /// <summary>Phase 15-B (MVP) — Start/End the conference SESSION (distinct from
    /// the mode-pill commands above).  Step 5 wires the actual ConferenceStart /
    /// ConferenceEnd broadcasts + the breakout-dissolve confirm; today these
    /// stubs only mutate the local session state so the ConferenceView's CTAs
    /// react correctly under 2-PC manual smoke-testing.</summary>
    public IRelayCommand StartConferenceCommand { get; }
    public IRelayCommand EndConferenceCommand { get; }

    // Phase 15-D step 1 — Meet-style bottom-toolbar commands.  Chat / Hand
    // route through the slide-in sidebar (chat tab + participants tab); the
    // ⋮ More button opens its picker directly from the toolbar's code-behind
    // (no command — local UI state).
    public IRelayCommand ToggleConferenceSidebarCommand { get; }
    public IRelayCommand ShowConferenceHandQueueCommand { get; }

    /// <summary>Phase 16-X (Bug H fix, 2026-06-01) — toolbar ✋ command shared
    /// with Student.StudentConferenceShellViewModel.RaiseHandCommand.  Teacher
    /// role semantics: the host doesn't "raise hand" — they see who else has,
    /// so the command just opens the queue sidebar (delegates to the existing
    /// 15-D ShowConferenceHandQueueCommand behavior).  Student role flips this
    /// to a real raise / lower toggle.</summary>
    public IRelayCommand RaiseHandCommand { get; }
    public IRelayCommand SelectConferenceChatTabCommand { get; }
    public IRelayCommand SelectConferenceParticipantsTabCommand { get; }

    // Phase 15-E step 3 — teacher Recognize action.  Targets a Conference tile
    // and sends 0x0111 HandLower at the matching student; the student-side
    // dispatch arm flips _handRaised back to false so the next click on
    // their Raise Hand button raises cleanly.  Also clears the local tile
    // badge + queue entry so the sidebar updates without round-tripping.
    public IRelayCommand<ConferenceTileViewModel?> RecognizeHandCommand { get; }

    /// <summary>Phase 16-D — host-only mute on a specific participant.  Reuses
    /// the 13-D MicMuteRequest wire (0x0641) — no new wire codes for the
    /// host-mute action, just a fresh UI command bound to the Sidebar's
    /// per-row "Mute" admin button.  Visibility on the button itself binds
    /// to Role.CanMuteOthers so students never see it.  Parameter is the
    /// target StudentViewModel from the participants list.</summary>
    public IRelayCommand<StudentViewModel?> MuteParticipantCommand { get; }

    /// <summary>Phase 15-E step 4 — Reaction toolbar picker.  Takes the
    /// emoji string and broadcasts a ReactionMessage; the teacher's own
    /// self-tile animates locally + ControlServer fans out to all
    /// students.</summary>
    public IRelayCommand<string?> SendReactionCommand { get; }

    // Phase 9.6: Net Movie
    public IRelayCommand OpenNetMovieCommand { get; }

    // Phase 6.5: Remote Control + Phase 4.6: Mic Monitor + Multi-room
    public IRelayCommand OpenMicMonitorCommand { get; }
    public IRelayCommand OpenMultiRoomCommand { get; }
    [ObservableProperty] private string activeClassName = "";
    [ObservableProperty] private System.Windows.Visibility activeClassVisibility = System.Windows.Visibility.Collapsed;
    [ObservableProperty] private StudentViewModel? activeDemoStudent;
    [ObservableProperty] private System.Windows.Visibility demoBannerVisibility = System.Windows.Visibility.Collapsed;
    [ObservableProperty] private string demoBannerText = "";

    // Phase 5b: per-student recording
    public IRelayCommand<StudentViewModel?> StartStudentRecordingCommand { get; }
    public IRelayCommand<StudentViewModel?> StopStudentRecordingCommand { get; }
    /// <summary>Single button toggle: start if not recording, stop if recording.</summary>
    public IRelayCommand<StudentViewModel?> ToggleStudentRecordingCommand { get; }

    // Phase 11.3: branding settings
    public IRelayCommand OpenBrandingSettingsCommand { get; }

    // Phase 5D: admin password settings
    // Phase 8 Section D — OpenAdminPasswordSettingsCommand + OpenAdminPasswordSettings()
    // removed along with the AdminPasswordSettingsDialog file.

    /// <summary>Phase 5b: set of student IDs currently viewed via StudentScreenWindow (REC enabled when present).</summary>
    public readonly HashSet<System.Guid> ViewingStudents = new();

    // Phase 6: Power-state commands
    public IRelayCommand ShutdownAllCommand { get; }
    public IRelayCommand RestartAllCommand { get; }
    public IRelayCommand LogoffAllCommand { get; }
    public IRelayCommand<StudentViewModel?> ShutdownOneCommand { get; }
    public IRelayCommand<StudentViewModel?> RestartOneCommand { get; }
    public IRelayCommand<StudentViewModel?> LogoffOneCommand { get; }

    // Phase 13: Open Quiz Manager
    public IRelayCommand OpenQuizManagerCommand { get; }

    public IRelayCommand EndBreakoutCommand { get; }
    public IRelayCommand AutoBalanceCommand { get; }
    public IRelayCommand RenameRoomsCommand { get; }

    public IRelayCommand<RoomViewModel?> AssignToRoomCommand { get; }

    private StudentViewModel? _pendingAssignStudent;

    public MainViewModel()
    {
        // Phase 2 Section F — tab-select commands.  Setting RightRailTabIndex drives
        // IsChatTabSelected / IsActivityTabSelected which the XAML binds for visibility.
        SelectChatTabCommand = new RelayCommand(() => RightRailTabIndex = 0);
        SelectActivityTabCommand = new RelayCommand(() => RightRailTabIndex = 1);

        // Phase 2 Section B — wrap Students with a CollectionView so the search box can filter
        // without mutating the underlying collection (which is mutated by Server callbacks).
        StudentsView = CollectionViewSource.GetDefaultView(Students);
        StudentsView.Filter = obj =>
        {
            if (string.IsNullOrWhiteSpace(SearchTerm)) return true;
            if (obj is not StudentViewModel s) return true;
            var q = SearchTerm.Trim();
            return (s.DisplayName?.Contains(q, System.StringComparison.OrdinalIgnoreCase) == true)
                || (s.MachineName?.Contains(q, System.StringComparison.OrdinalIgnoreCase) == true);
        };

        LockAllCommand = new RelayCommand(ToggleLockAll);
        ShareScreenCommand = new RelayCommand(ToggleShareScreen);
        ToggleMicCommand = new RelayCommand(ToggleMic);
        ToggleSystemAudioCommand = new RelayCommand(ToggleSystemAudio);
        MuteAllStudentsCommand = new RelayCommand(MuteAllStudents);
        SendFileCommand = new RelayCommand(SendFile);
        StartRecordingCommand = new RelayCommand(ToggleRecording);
        OpenRecordingsFolderCommand = new RelayCommand(OpenRecordingsFolder);
        // Phase 13-B (Tier 1) — both breakout entry points now open GroupManagerView.
        // The old CreateRooms input-box flow is unreachable (kept as legacy for
        // grep-history; can be removed in a follow-up cleanup).
        OpenBreakoutCommand = new RelayCommand(OpenGroupManager);
        ApplyPolicyCommand = new RelayCommand(OpenApplyPolicy);
        OpenLanguageCommand = new RelayCommand(OpenLanguage);
        SendChatCommand = new RelayCommand(SendChat);
        SendConferenceChatCommand = new RelayCommand(SendConferenceChat);
        LockOneCommand = new RelayCommand<StudentViewModel?>(s => LockOne(s, true));
        UnlockOneCommand = new RelayCommand<StudentViewModel?>(s => LockOne(s, false));
        ApplyPolicyToStudentCommand = new RelayCommand<StudentViewModel?>(OpenApplyPolicyForStudent);
        RevertPolicyForStudentCommand = new RelayCommand<StudentViewModel?>(RevertPolicyForStudent);
        SendDirectMessageCommand = new RelayCommand<StudentViewModel?>(SendDirectMessage);
        RemoveFromRoomCommand = new RelayCommand<StudentViewModel?>(RemoveFromRoom);
        ViewStudentScreenCommand = new RelayCommand<StudentViewModel?>(ViewStudentScreen);

        SetAsHostCommand = new RelayCommand<StudentViewModel?>(SetAsHost);
        RemoveHostCommand = new RelayCommand<StudentViewModel?>(RemoveHost);

        StartDemoCommand = new RelayCommand<StudentViewModel?>(StartDemo);
        StopDemoCommand = new RelayCommand(StopDemo);

        OpenScreenPenCommand = new RelayCommand(OpenScreenPen);

        OpenClassRosterCommand = new RelayCommand(OpenClassRoster);
        MarkAttendanceCommand = new RelayCommand(MarkAttendance);
        DeactivateRosterCommand = new RelayCommand(DeactivateRoster);

        ToggleSoundsCommand = new RelayCommand(ToggleSounds);
        UpdateSoundsButtonText();

        ToggleCameraCommand = new RelayCommand(ToggleCamera);
        UpdateCameraButtonText();

        // Phase 15-B (MVP) — mode-pill commands.  Flip IsInConference; the
        // OnIsInConferenceChanged partial swaps CurrentMainView.  Step 5 adds
        // the broadcast-emitting StartConferenceCommand / EndConferenceCommand
        // bound to the ConferenceView's own Start CTA.
        EnterConferenceModeCommand = new RelayCommand(() => IsInConference = true);
        ExitConferenceModeCommand  = new RelayCommand(() => IsInConference = false);
        // Step 5: real session-lifecycle commands.  StartConference dissolves
        // breakouts first (modal confirm) per architecture § 5 risk #2, then
        // emits ConferenceStart (0x0670).  EndConference emits ConferenceEnd
        // (0x0671) and resets local session state.
        StartConferenceCommand = new RelayCommand(StartConference);
        EndConferenceCommand   = new RelayCommand(EndConference);

        // Phase 15-D step 1 — sidebar toggle + tab-select wiring.  Toggle
        // closes the sidebar if it's already on the Chat tab; otherwise opens
        // it and forces Chat.  Show-hand-queue opens directly to Participants.
        // SelectChatTab / SelectParticipantsTab are no-ops while the sidebar
        // is closed — they're called from the in-sidebar tab pill (Phase 15-D
        // step 3) and we don't want a stray click outside the sidebar to
        // surface it.
        ToggleConferenceSidebarCommand = new RelayCommand(() =>
        {
            if (IsConferenceSidebarVisible && IsConferenceChatTabSelected)
            {
                IsConferenceSidebarVisible = false;
            }
            else
            {
                ConferenceSidebarTabIndex = 0;
                IsConferenceSidebarVisible = true;
            }
        });
        ShowConferenceHandQueueCommand = new RelayCommand(() =>
        {
            ConferenceSidebarTabIndex = 1;
            IsConferenceSidebarVisible = true;
        });
        // Phase 16-X (Bug H fix) — toolbar ✋ shared command.  Teacher
        // semantics: same as ShowConferenceHandQueueCommand (open the
        // queue sidebar).  Student VM's RaiseHandCommand toggles the
        // student's own hand raise + emits the wire envelope.
        RaiseHandCommand = ShowConferenceHandQueueCommand;
        SelectConferenceChatTabCommand = new RelayCommand(() =>
        {
            if (IsConferenceSidebarVisible) ConferenceSidebarTabIndex = 0;
        });
        SelectConferenceParticipantsTabCommand = new RelayCommand(() =>
        {
            if (IsConferenceSidebarVisible) ConferenceSidebarTabIndex = 1;
        });

        // Phase 15-E step 3 — Recognize.  Sends targeted HandLower, then
        // locally clears the tile + queue so the UI feels immediate without
        // waiting for the student-side echo (a S→T HandLower would loop
        // back today, but our local cleanup makes the round-trip optional).
        RecognizeHandCommand = new RelayCommand<ConferenceTileViewModel?>(async tile =>
        {
            if (tile == null) return;
            tile.IsHandRaised = false;
            tile.HandRaisedAt = null;
            ConferenceGallery?.RefreshRaisedHandQueue();
            // Mirror onto the Classroom-side state so the bell badge clears too.
            var s = Students.FirstOrDefault(x => x.EndpointId == tile.EndpointId);
            if (s != null) s.HandRaisedVisibility = System.Windows.Visibility.Collapsed;
            RaiseNotificationsChanged();
            if (App.Server != null)
            {
                try { await App.Server.SendHandLowerAsync(tile.EndpointId, System.Threading.CancellationToken.None); }
                catch (Exception ex) { AppendSystemChat(string.Format(Loc.Get("Err_GenericFmt", "Error: {0}"), ex.Message)); }
            }
        });

        // Phase 16-D step 10 — host-only Mute on a specific participant.
        // Reuses the 13-D MicMuteRequest wire (0x0641) so no new wire
        // codes; just a fresh UI command bound to the Sidebar's per-row
        // "Mute" admin button.  Sidebar's Visibility on that button
        // already gates by Role.CanMuteOthers so this command can't be
        // invoked from a ParticipantRole shell.
        MuteParticipantCommand = new RelayCommand<StudentViewModel?>(async s =>
        {
            if (s == null || App.Server == null) return;
            var reason = Loc.Get("Conf_HostMutedReason", "Muted by the host");
            try
            {
                await App.Server.SendMicMuteRequestAsync(s.EndpointId, muted: true,
                    reason, System.Threading.CancellationToken.None);
            }
            catch (Exception ex) { AppendSystemChat(string.Format(Loc.Get("Err_GenericFmt", "Error: {0}"), ex.Message)); }
        });

        // Phase 15-E step 4 — Reaction send.  Optimistically render the
        // emoji over the teacher's self-tile locally so there's no
        // round-trip delay, then broadcast.  ExpiresAtMs is now + 3 s.
        SendReactionCommand = new RelayCommand<string?>(async emoji =>
        {
            if (string.IsNullOrEmpty(emoji)) return;
            var selfId = App.Server?.TeacherEndpointId ?? System.Guid.Empty;
            ShowReactionOnTile(selfId, emoji);
            if (App.Server != null)
            {
                var msg = new ReactionMessage
                {
                    Emoji = emoji,
                    ExpiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3000,
                };
                try { await App.Server.BroadcastReactionAsync(msg, System.Threading.CancellationToken.None); }
                catch (Exception ex) { AppendSystemChat(string.Format(Loc.Get("Err_GenericFmt", "Error: {0}"), ex.Message)); }
            }
        });

        OpenNetMovieCommand = new RelayCommand(OpenNetMovie);
        OpenMicMonitorCommand = new RelayCommand(OpenMicMonitor);
        OpenMultiRoomCommand = new RelayCommand(OpenGroupManager);
        if (App.Roster != null)
        {
            App.Roster.ActiveRosterChanged += OnActiveRosterChanged;
            OnActiveRosterChanged(this, App.Roster.ActiveRoster);
        }

        StartStudentRecordingCommand = new RelayCommand<StudentViewModel?>(StartStudentRecording);
        StopStudentRecordingCommand = new RelayCommand<StudentViewModel?>(StopStudentRecording);
        ToggleStudentRecordingCommand = new RelayCommand<StudentViewModel?>(ToggleStudentRecording);

        OpenBrandingSettingsCommand = new RelayCommand(OpenBrandingSettings);
        // Phase 8 Section D — OpenAdminPasswordSettingsCommand wiring removed.

        ShutdownAllCommand = new RelayCommand(() => BroadcastPowerWithConfirm(ClassroomCtrl.Shared.Protocol.MessageType.ForceShutdown, "Confirm_ShutdownAll"));
        RestartAllCommand = new RelayCommand(() => BroadcastPowerWithConfirm(ClassroomCtrl.Shared.Protocol.MessageType.ForceRestart, "Confirm_RestartAll"));
        LogoffAllCommand = new RelayCommand(() => BroadcastPowerWithConfirm(ClassroomCtrl.Shared.Protocol.MessageType.ForceLogoff, "Confirm_LogoffAll"));
        ShutdownOneCommand = new RelayCommand<StudentViewModel?>(s => PowerOneWithConfirm(s, ClassroomCtrl.Shared.Protocol.MessageType.ForceShutdown, "Confirm_ShutdownOne"));
        RestartOneCommand = new RelayCommand<StudentViewModel?>(s => PowerOneWithConfirm(s, ClassroomCtrl.Shared.Protocol.MessageType.ForceRestart, "Confirm_RestartOne"));
        LogoffOneCommand = new RelayCommand<StudentViewModel?>(s => PowerOneWithConfirm(s, ClassroomCtrl.Shared.Protocol.MessageType.ForceLogoff, "Confirm_LogoffOne"));

        OpenQuizManagerCommand = new RelayCommand(OpenQuizManager);
        EndBreakoutCommand = new RelayCommand(EndBreakout);
        AutoBalanceCommand = new RelayCommand(AutoBalance);
        RenameRoomsCommand = new RelayCommand(RenameRooms);
        AssignToRoomCommand = new RelayCommand<RoomViewModel?>(r => AssignStudentToRoom(_pendingAssignStudent, r));

        Loc.LanguageChanged += RefreshLocalizedTexts;
        RefreshLocalizedTexts();

        // Phase 3 Section E — initialize the conversation list with the always-on
        // Everyone tab.  Must run after RefreshLocalizedTexts so DisplayName picks up
        // the localized "Everyone" label on first paint.
        InitConversations();

        // Phase 2 Section A: header subtitle pulls from branding so admin-customized
        // organization name shows under the title.  Subscribed once for the VM lifetime.
        BrandingService.Changed += RefreshBrandingTexts;
        RefreshBrandingTexts();

        // Phase 3 Section C: ActivityFeed now sources from Notifications and AddNotification
        // raises the change event itself — no ChatMessages subscription needed anymore.

        // Phase 8 (Bug D) — restore last-applied broadcast policy from disk so the dialog
        // pre-checks the previously-set boxes after a Teacher restart. Per-student policy
        // is intentionally skipped — only whole-class state is persisted.
        LoadAppliedPolicyState();

        // Phase 10.14 (Item 1A) — restore MasterVolume from HKCU.  Sets the backing field
        // via the generated property so OnMasterVolumeChanged fires and pushes the value
        // into App.StudentAudioMixer (if it exists yet).  Default stays at 1.0 on failure.
        var savedVolume = LoadMasterVolume();
        if (savedVolume.HasValue) MasterVolume = savedVolume.Value;

        if (App.Server != null)
        {
            App.Server.StudentJoined += OnStudentJoined;
            App.Server.StudentLeft += OnStudentLeft;
            App.Server.ChatReceived += OnChatReceived;
            App.Server.HandRaiseReceived += OnHandRaiseReceived;
            // Phase 15-E step 4 — reaction fan-out fires the floating emoji.
            App.Server.ReactionReceived += OnReactionReceived;
            App.Server.ScreenshotReceived += OnScreenshotReceived;
            App.Server.StudentAudioStreamStarted += OnStudentAudioStarted;
            App.Server.StudentAudioStreamStopped += OnStudentAudioStopped;
            App.Server.QualityReportReceived += OnQualityReportReceived;
            App.Server.HostChanged += OnHostChanged;
            App.Server.DemoStateChanged += OnDemoStateChanged;
            // Phase 13-D (Tier 3) — per-student mic indicator.  Heartbeat from
            // each student arrives ~every 1.5 s + on every state change; we
            // route to the right StudentViewModel so per-student chips /
            // tiles can show "Muted / Live / Speaking" without polling.
            App.Server.MicStateUpdated += OnMicStateUpdated;
            // Phase 14-B (Tier 1) — per-student webcam-presence heartbeat.
            // Drives StudentViewModel.HasWebcam (data only in Tier 1; Tier 2
            // wires the visual chip alongside the actual cam capture).
            App.Server.WebcamStateUpdated += OnWebcamStateUpdated;
            // Phase 15-C — route the teacher's own cam JPEG into the self-tile
            // so the Conference gallery shows the teacher's own preview.
            App.Server.TeacherCameraFrameSent += OnTeacherCameraFrameSent;
            // Phase 16-C — same self-tile preview path for the new Conference
            // cam wire (0x0681).  When the teacher is in Conference mode the
            // CameraBroadcastService routes through this event instead.  Single
            // handler reuse — decode + push to self-tile is identical.
            App.Server.TeacherConferenceCameraFrameSent += OnTeacherCameraFrameSent;
            // Phase 16-C — peer cam reception.  Teacher subscribes to its own
            // ControlServer dispatch events so a student's cam frames land on
            // the matching tile in the teacher's gallery (not just relayed to
            // other peers).  All three handlers marshal to UI dispatcher.
            App.Server.ConferenceCameraStartReceived += OnConferenceCameraStartReceived;
            App.Server.ConferenceCameraFrameReceived += OnConferenceCameraFrameReceived;
            App.Server.ConferenceCameraStopReceived  += OnConferenceCameraStopReceived;
            // Phase 13-B (Tier 1) — sync local Rooms collection from canonical
            // server state on every mutation.  GroupManagerView + the Step-7
            // badges + status chip all read off Rooms.
            App.Server.RoomsChanged += OnServerRoomsChanged;
        }

        // Phase 14-B (Tier 1) — cam-unplugged-mid-stream auto-recovery.
        // CameraBroadcastService fires StoppedDueToError; we refresh the
        // toolbar button + post an AppendSystemChat with the cause so the
        // teacher knows why the broadcast disappeared.
        if (App.Camera != null)
        {
            App.Camera.StoppedDueToError += OnCameraStoppedDueToError;
        }

        // Phase 17.1 step 2 — paint the taskbar badge whenever the unread
        // count changes.  App.Notifications is initialised before MainWindow
        // is constructed (App.OnStartup), so it's non-null in the production
        // path; the guard is for the WPF designer + future test rigs.
        if (App.Notifications != null)
        {
            App.Notifications.PropertyChanged += OnNotificationsPropertyChanged;
            RefreshTaskbarOverlay();
        }

        if (App.AdaptiveBitrate != null)
        {
            App.AdaptiveBitrate.BitrateChanged += (_, e) => RefreshBitrateLabel(e.NewBitrateBps);
            RefreshBitrateLabel(App.AdaptiveBitrate.CurrentBitrateBps);
        }
        else
        {
            RefreshBitrateLabel(500_000);
        }

        if (App.Recording != null)
        {
            App.Recording.RecordingStarted += OnRecordingStarted;
            App.Recording.RecordingStopped += OnRecordingStopped;
            App.Recording.RecordingError += OnRecordingError;
        }

        // Phase 1.5: live IP banner.
        RefreshLocalIPDisplay();
        System.Net.NetworkInformation.NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
        // Backup poll for missed events (some VPN clients trigger weirdly).
        _ipRefreshTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _ipRefreshTimer.Tick += (_, _) => RefreshLocalIPDisplay();
        _ipRefreshTimer.Start();

        // Phase 3 Section A — start the time-ago refresh.  Background priority so it never
        // pre-empts user input; 30s cadence keeps "5 min ago" labels from drifting more than
        // half a step.  Notifications collection is added in Section C; defensively coalesced.
        _timeAgoRefreshTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _timeAgoRefreshTimer.Tick += (_, _) => RefreshTimeAgoDisplays();
        _timeAgoRefreshTimer.Start();
    }

    private void RefreshTimeAgoDisplays()
    {
        // Pump per-instance PropertyChanged so DataTemplate bindings re-evaluate.  Raising
        // OnPropertyChanged on the collection itself does NOT re-render items — bindings
        // are tied to each ChatMessage / Notification instance's own PropertyChanged.
        foreach (var conv in Conversations)
            foreach (var msg in conv.Messages)
                msg.NotifyTimeChanged();
        foreach (var notif in Notifications) notif.NotifyTimeChanged();
    }

    private void OnNetworkAddressChanged(object? sender, EventArgs e)
    {
        // Fires on a non-UI thread — marshal to UI thread for the property update.
        System.Windows.Application.Current?.Dispatcher.Invoke(RefreshLocalIPDisplay);
    }

    private void RefreshLocalIPDisplay()
    {
        try { LocalIPDisplay = NetworkInfoService.GetFormattedIPInfo(); }
        catch { LocalIPDisplay = "(error)"; }
    }

    private async void ToggleRecording()
    {
        if (App.Recording == null) return;

        if (IsRecording)
        {
            // Stop tee BEFORE awaiting; ScreenBroadcaster sees the flag immediately on its next loop.
            if (App.ScreenBroadcaster != null) App.ScreenBroadcaster.TeeRawFrames = false;
            await App.Recording.StopAsync();
            return;
        }

        // Phase 5 bug-fix: recording now uses raw-BGRA pipe to ffmpeg (no codec dependency on H.264).
        // We still require an active broadcast — capture dimensions/FPS come from ScreenBroadcaster.
        var bc = App.ScreenBroadcaster;
        if (bc?.IsBroadcasting != true)
        {
            AppendSystemChat(Loc.Get("Err_RecordingNoBroadcast"));
            return;
        }

        if (App.Recording.Start(bc.TargetWidth, bc.TargetHeight, bc.FramesPerSecond))
        {
            // Tell ScreenBroadcaster to start teeing raw frames — only when this flag is set
            // does it allocate the ~8 MB BGRA buffer per frame.
            bc.TeeRawFrames = true;
        }
    }

    private void OpenRecordingsFolder()
    {
        var folder = RecordingService.GetRecordingsFolder();
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex)
        {
            AppendErrorChat(ex.Message);
        }
    }

    private void OnRecordingStarted(object? sender, string sessionId)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            IsRecording = true;
            RecordingIndicatorVisibility = System.Windows.Visibility.Visible;
            _recordingStartedAt = DateTimeOffset.Now;
            UpdateRecordingTexts();

            _recordingTimer?.Stop();
            _recordingTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _recordingTimer.Tick += (_, _) => UpdateRecordingTexts();
            _recordingTimer.Start();

            AppendSystemChat(Loc.Get("Chat_RecordingStarted"));
        });
    }

    private void OnRecordingStopped(object? sender, string outputPath)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            IsRecording = false;
            RecordingIndicatorVisibility = System.Windows.Visibility.Collapsed;
            _recordingTimer?.Stop();
            _recordingTimer = null;
            UpdateRecordingTexts();
            AppendSystemChat(Loc.Format("Chat_RecordingStopped", System.IO.Path.GetFileName(outputPath)));
        });
    }

    private void OnRecordingError(object? sender, string message)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            IsRecording = false;
            RecordingIndicatorVisibility = System.Windows.Visibility.Collapsed;
            _recordingTimer?.Stop();
            _recordingTimer = null;
            UpdateRecordingTexts();
            AppendErrorChat(message);
        });
    }

    private void UpdateRecordingTexts()
    {
        RecordingButtonText = Loc.Get(IsRecording ? "Btn_StopRecording" : "Btn_StartRecording");
        if (IsRecording)
        {
            var elapsed = DateTimeOffset.Now - _recordingStartedAt;
            RecordingElapsedText = Loc.Format("Lbl_RecordingIndicator", $"{(int)elapsed.TotalMinutes:D2}:{elapsed.Seconds:D2}");
        }
        else
        {
            RecordingElapsedText = "";
        }
    }

    private void RefreshBitrateLabel(int bps)
    {
        var kbps = bps / 1000;
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            CurrentBitrateText = Loc.Format("Lbl_CurrentBitrate", kbps);
        });
    }

    private void OnQualityReportReceived(object? sender, (System.Guid StudentId, ScreenStreamQualityReportMessage Report) e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var s = Students.FirstOrDefault(x => x.EndpointId == e.StudentId);
            if (s != null) s.QualityScore = e.Report.QualityScore;
        });
    }

    public void SetPendingAssignStudent(StudentViewModel? s) => _pendingAssignStudent = s;

    private void RefreshLocalizedTexts()
    {
        LockButtonText = Loc.Get(ScreensLocked ? "Btn_UnlockAll" : "Btn_LockAll");
        ConnectedCountText = Loc.Format("Lbl_StudentsConnected", ConnectedCount);
        ShareScreenButtonText = Loc.Get(IsScreenSharing ? "Btn_StopSharing" : "Btn_ShareScreen");
        MicButtonText = Loc.Get(IsMicOn ? "Btn_MuteMic" : "Btn_UnmuteMic");
        SystemAudioButtonText = Loc.Get(IsSystemAudioOn ? "Btn_StopShareSystemAudio" : "Btn_ShareSystemAudio");
        if (App.AdaptiveBitrate != null) RefreshBitrateLabel(App.AdaptiveBitrate.CurrentBitrateBps);
        UpdateRecordingTexts();
        UpdateSoundsButtonText();
        // Phase 4 Section A — camera button text was set once at startup via
        // UpdateCameraButtonText() but never refreshed on language switch, so it stuck
        // in the boot-time language (often Thai).  Drive it through the same pump.
        UpdateCameraButtonText();
        // Phase 3 Section E — Everyone tab label follows the active language.
        EveryoneConversation.DisplayName = Loc.Get("Hdr_Everyone");
    }

    private void RefreshBrandingTexts()
    {
        OrganizationSubtitle = BrandingService.Current?.OrganizationName ?? "";
    }

    partial void OnSearchTermChanged(string value) => StudentsView?.Refresh();

    partial void OnConnectedCountChanged(int value)
    {
        ConnectedCountText = Loc.Format("Lbl_StudentsConnected", value);
    }

    partial void OnScreensLockedChanged(bool value)
    {
        LockButtonText = Loc.Get(value ? "Btn_UnlockAll" : "Btn_LockAll");
    }

    partial void OnIsScreenSharingChanged(bool value)
    {
        ShareScreenButtonText = Loc.Get(value ? "Btn_StopSharing" : "Btn_ShareScreen");
    }

    partial void OnIsMicOnChanged(bool value)
    {
        MicButtonText = Loc.Get(value ? "Btn_MuteMic" : "Btn_UnmuteMic");
        OnPropertyChanged(nameof(MicTooltipText));
    }

    partial void OnIsSystemAudioOnChanged(bool value)
    {
        SystemAudioButtonText = Loc.Get(value ? "Btn_StopShareSystemAudio" : "Btn_ShareSystemAudio");
        OnPropertyChanged(nameof(SpeakerTooltipText));
    }

    partial void OnMasterVolumeChanged(double value)
    {
        if (App.StudentAudioMixer != null)
            App.StudentAudioMixer.Volume = (float)value;
        // Phase 10.14 (Item 1A) — persist so slider doesn't reset on each Teacher launch.
        SaveMasterVolume(value);
    }

    // Phase 10.14 (Item 1A) — MasterVolume persistence helpers.  Pattern mirrors
    // ChannelIdRegistry but inlined since this is a single per-user preference
    // (not a classroom-wide infrastructure value); HKCU only, no HKLM mirror.
    // Stored as string under InvariantCulture so a Thai/comma-decimal regional
    // setting on the customer machine doesn't corrupt the value at write time.
    private const string TeacherSettingsRegSubKey = @"Software\NTY\ClassroomCtrl\Teacher\Settings";
    private const string MasterVolumeRegValueName = "MasterVolume";

    private static double? LoadMasterVolume()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(TeacherSettingsRegSubKey);
            if (key?.GetValue(MasterVolumeRegValueName) is string s
                && double.TryParse(s, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var v)
                && v >= 0 && v <= 4.0)
            {
                return v;
            }
        }
        catch { /* fall back to default */ }
        return null;
    }

    private static void SaveMasterVolume(double value)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(TeacherSettingsRegSubKey);
            key?.SetValue(MasterVolumeRegValueName,
                value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Microsoft.Win32.RegistryValueKind.String);
        }
        catch
        {
            // Non-fatal: slider falls back to default on next launch.
        }
    }

    private async void MuteAllStudents()
    {
        if (App.Server == null) return;
        try
        {
            await App.Server.BroadcastForceMuteAllAsync(System.Threading.CancellationToken.None);
            foreach (var s in Students) s.IsTalking = false;
            AppendSystemChat(Loc.Get("Chat_TeacherMutedAll"));
        }
        catch (System.Exception ex)
        {
            AppendErrorChat(ex.Message);
        }
    }

    private void ToggleMic()
    {
        if (App.AudioBroadcaster == null) return;

        var newValue = !IsMicOn;
        App.AudioBroadcaster.MicEnabled = newValue;
        IsMicOn = newValue;
        AppendSystemChat(Loc.Get(newValue ? "Chat_AudioStarted" : "Chat_AudioStopped"));
    }

    private void ToggleSystemAudio()
    {
        if (App.AudioBroadcaster == null) return;

        var newValue = !IsSystemAudioOn;
        App.AudioBroadcaster.SystemAudioEnabled = newValue;
        IsSystemAudioOn = newValue;
        AppendSystemChat(Loc.Get(newValue ? "Chat_SystemAudioStarted" : "Chat_SystemAudioStopped"));
    }

    private void ToggleShareScreen()
    {
        if (App.ScreenBroadcaster == null) return;

        if (IsScreenSharing)
        {
            // Phase 16-B+ — capture mode BEFORE Stop() clears IsConferenceShare,
            // so the teacher-side gallery cleanup below knows whether to clear
            // ActiveShareEndpointId.
            bool wasConferenceShare = App.ScreenBroadcaster.IsConferenceShare;
            App.ScreenBroadcaster.Stop();
            IsScreenSharing = false;
            if (wasConferenceShare)
            {
                App.ScreenBroadcaster.FrameEncoded -= OnConferenceShareFrameEncoded;
                ConferenceGallery.ActiveShareEndpointId = null;
                ConferenceGallery.ActiveShareSourceName = "";
                ConferenceGallery.ActiveShareFrame = null;
            }
            // Phase 16-B+ step 11 — Conference-specific chat string clearly
            // distinguishes from Classroom share in the dev's chat log.
            AppendSystemChat(Loc.Get(wasConferenceShare
                ? "Conf_ChatShareStopped"
                : "Chat_ScreenShareStopped"));
        }
        else
        {
            // Phase 11-B inc1 — wire the codec dropdown into the broadcast.  Pre-11-B
            // the broadcaster always used its default (Mjpeg) because nothing copied
            // App.SelectedCodec across; the H.264 init branch at
            // ScreenBroadcaster.Start was unreachable.  Codec is captured at Start
            // time only — changing the dropdown mid-share has no effect until the
            // teacher stops and restarts the share (intentional; live-switching the
            // encoder mid-stream would need a fresh IDR, viewer reset on each peer,
            // and a re-send of ScreenStreamStart with the new codec to all peers).
            App.ScreenBroadcaster.Codec = App.SelectedCodec;
            // Phase 16-B+ — Conference Mode routes share through the new
            // in-frame view (ConferenceShareView) instead of the Classroom
            // full-takeover path.  Set the routing flag + source name BEFORE
            // Start() so the broadcaster's Start branch emits the right
            // signaling envelope (ConferenceShareStart vs ScreenStreamStart).
            if (IsInConference)
            {
                var sourceName = Loc.Get("Conf_TeacherDisplayName", "Teacher");
                App.ScreenBroadcaster.IsConferenceShare = true;
                App.ScreenBroadcaster.ConferenceShareSourceName = sourceName;
                // Set local gallery state so the teacher's own view flips to
                // share-mode immediately (TCP doesn't echo to self, so the
                // dispatch path wouldn't trigger here).  Subscribe to
                // FrameEncoded so the teacher's own gallery shows a live
                // self-preview of what's being shared.
                if (App.Server != null)
                {
                    ConferenceGallery.ActiveShareEndpointId = App.Server.TeacherEndpointId;
                }
                ConferenceGallery.ActiveShareSourceName = sourceName;
                ConferenceGallery.ActiveShareFrame = null;
                App.ScreenBroadcaster.FrameEncoded += OnConferenceShareFrameEncoded;
            }
            App.ScreenBroadcaster.Start();
            IsScreenSharing = true;
            AppendSystemChat(Loc.Get("Chat_ScreenShareStarted"));
        }
    }

    /// <summary>Phase 16-B+ step 10 — teacher self-preview hook for the
    /// in-frame Conference share.  Subscribed only while a Conference share
    /// is active; decodes MJPEG bytes back to a BitmapImage and pushes it
    /// into <see cref="ConferenceGallery"/>.ActiveShareFrame so the teacher
    /// sees what every participant sees.  H.264 path falls back to the
    /// "Waiting…" placeholder (same as student side) until a future polish
    /// round wires the 11-B H264Decoder here.</summary>
    private void OnConferenceShareFrameEncoded(object? sender,
        (byte[] FrameData, bool IsKeyframe, ClassroomCtrl.Shared.Protocol.VideoCodec Codec) e)
    {
        if (e.Codec != ClassroomCtrl.Shared.Protocol.VideoCodec.Mjpeg) return;
        try
        {
            var bytes = e.FrameData;
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null) return;
            dispatcher.Invoke(() =>
            {
                try
                {
                    var bmp = new System.Windows.Media.Imaging.BitmapImage();
                    using var ms = new System.IO.MemoryStream(bytes);
                    bmp.BeginInit();
                    bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    ConferenceGallery.ActiveShareFrame = bmp;
                }
                catch { /* transient decode failure: leave last frame in place */ }
            });
        }
        catch { /* dispatcher torn down: ignore */ }
    }

    private async void ToggleLockAll()
    {
        if (App.Server == null) return;

        // Phase 15-D step 5 — Conference mode is mode-exclusive with classroom
        // lock per architecture § 3.  No-op + system-chat toast when invoked
        // while a session is live so the dev sees feedback in the chat history.
        if (IsInConference)
        {
            AppendSystemChat(Loc.Get("Conf_BlockedLockAll",
                "Lock All is unavailable in Conference mode."));
            return;
        }

        ScreensLocked = !ScreensLocked;

        try
        {
            await App.Server.BroadcastLockAsync(ScreensLocked, System.Threading.CancellationToken.None);
            AppendSystemChat(Loc.Get(ScreensLocked ? "Chat_AllScreensLocked" : "Chat_AllScreensUnlocked"));
        }
        catch (System.Exception ex)
        {
            AppendSystemChat(Loc.Format("Err_LockFailed", ex.Message));
        }
    }

    private async void LockOne(StudentViewModel? s, bool locked)
    {
        if (s == null || App.Server == null) return;
        try
        {
            await App.Server.LockOneAsync(s.EndpointId, locked, System.Threading.CancellationToken.None);
            AppendSystemChat(Loc.Format(
                locked ? "Chat_LockedScreen" : "Chat_UnlockedScreen",
                s.DisplayName));
        }
        catch (System.Exception ex)
        {
            AppendErrorChat(ex.Message);
        }
    }

    private async void SendFile()
    {
        if (App.Server == null) return;

        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = Loc.Get("Btn_SendFile"),
            Filter = "All files (*.*)|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;

        var name = Path.GetFileName(dlg.FileName);
        AppendSystemChat(Loc.Format("Chat_SendingFile", name));
        try
        {
            await App.Server.BroadcastFileAsync(dlg.FileName, System.Threading.CancellationToken.None);
            AppendSystemChat(Loc.Format("Chat_FileSent", name));
        }
        catch (System.Exception ex)
        {
            AppendSystemChat(Loc.Format("Err_SendFileFailed", ex.Message));
        }
    }

    // Phase 8 (Bug D) — applied-policy persistence helpers. Read on MainViewModel construction,
    // written after every Apply / cleared on Revert. Best-effort: any IO failure leaves the
    // in-memory Current* fields as the source of truth.
    private void LoadAppliedPolicyState()
    {
        try
        {
            if (!File.Exists(AppliedPolicyJsonPath)) return;
            var json = File.ReadAllText(AppliedPolicyJsonPath);
            var s = System.Text.Json.JsonSerializer.Deserialize<AppliedPolicyState>(json);
            if (s == null) return;
            CurrentBlockUsbStorage = s.BlockUsbStorage;
            CurrentBlockOpticalDrive = s.BlockOpticalDrive;
            CurrentBlockPrinting = s.BlockPrinting;
            CurrentBlockedProcessNames = s.BlockedProcessNames ?? new List<string>();
            CurrentBlockedHostnames = s.BlockedHostnames ?? new List<string>();
        }
        catch { /* corrupt or unreadable — fall back to defaults */ }
    }

    private void SaveAppliedPolicyState()
    {
        try
        {
            var s = new AppliedPolicyState
            {
                BlockUsbStorage = CurrentBlockUsbStorage,
                BlockOpticalDrive = CurrentBlockOpticalDrive,
                BlockPrinting = CurrentBlockPrinting,
                BlockedProcessNames = new List<string>(CurrentBlockedProcessNames),
                BlockedHostnames = new List<string>(CurrentBlockedHostnames),
            };
            Directory.CreateDirectory(Path.GetDirectoryName(AppliedPolicyJsonPath)!);
            File.WriteAllText(AppliedPolicyJsonPath,
                System.Text.Json.JsonSerializer.Serialize(s,
                    new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* persistence is best-effort */ }
    }

    private static void ClearAppliedPolicyState()
    {
        try { File.Delete(AppliedPolicyJsonPath); } catch { }
    }

    private void OpenApplyPolicy()
    {
        var dlg = new Dialogs.ApplyPolicyDialog
        {
            Owner = System.Windows.Application.Current.MainWindow,
            InitialBlockUsbStorage = CurrentBlockUsbStorage,
            InitialBlockOpticalDrive = CurrentBlockOpticalDrive,
            InitialBlockPrinting = CurrentBlockPrinting,
            InitialBlockedProcessNames = new List<string>(CurrentBlockedProcessNames),
            InitialBlockedHostnames = new List<string>(CurrentBlockedHostnames),
        };
        var result = dlg.ShowDialog();
        if (result != true || App.Server == null) return;

        if (dlg.RevertRequested)
        {
            CurrentBlockUsbStorage = false;
            CurrentBlockOpticalDrive = false;
            CurrentBlockPrinting = false;
            CurrentBlockedProcessNames = new List<string>();
            CurrentBlockedHostnames = new List<string>();
            ClearAppliedPolicyState();
            _ = App.Server.BroadcastPolicyRevertAsync(System.Threading.CancellationToken.None);
            AppendSystemChat(Loc.Get("Chat_PolicyReverted"));
        }
        else
        {
            CurrentBlockUsbStorage = dlg.BlockUsbStorage;
            CurrentBlockOpticalDrive = dlg.BlockOpticalDrive;
            CurrentBlockPrinting = dlg.BlockPrinting;
            CurrentBlockedProcessNames = new List<string>(dlg.BlockedProcessNames);
            CurrentBlockedHostnames = new List<string>(dlg.BlockedHostnames);
            SaveAppliedPolicyState();

            var expiresAt = dlg.DurationSeconds > 0
                ? System.DateTimeOffset.UtcNow.AddSeconds(dlg.DurationSeconds).ToUnixTimeMilliseconds()
                : 0L;

            var msg = new ClassroomCtrl.Shared.Protocol.PolicyApplyMessage
            {
                BlockUsbStorage = dlg.BlockUsbStorage,
                BlockOpticalDrive = dlg.BlockOpticalDrive,
                BlockPrinting = dlg.BlockPrinting,
                BlockedProcessNames = new List<string>(dlg.BlockedProcessNames),
                BlockedHostnames = new List<string>(dlg.BlockedHostnames),
                ExpiresAtUtcMs = expiresAt,
            };
            _ = App.Server.BroadcastPolicyAsync(msg, System.Threading.CancellationToken.None);

            var summary = BuildPolicySummary(msg);
            var durationLabel = dlg.DurationSeconds > 0
                ? Loc.Format("Chat_PolicyExpiresIn", dlg.DurationSeconds / 60)
                : "";
            AppendSystemChat(Loc.Format("Chat_PolicyApplied2", summary) + durationLabel);
        }
    }

    private void OpenApplyPolicyForStudent(StudentViewModel? s)
    {
        if (s == null) return;

        var dlg = new Dialogs.ApplyPolicyDialog
        {
            Owner = System.Windows.Application.Current.MainWindow,
            InitialBlockUsbStorage = s.PerStudentBlockUsb,
            InitialBlockOpticalDrive = s.PerStudentBlockOptical,
            InitialBlockPrinting = s.PerStudentBlockPrint,
            InitialBlockedProcessNames = new List<string>(s.PerStudentBlockedProcessNames),
            InitialBlockedHostnames = new List<string>(s.PerStudentBlockedHostnames),
            Title = $"{Loc.Get("Dlg_ApplyPolicyTitle")} — {s.DisplayName}",
        };
        var result = dlg.ShowDialog();
        if (result != true || App.Server == null) return;

        if (dlg.RevertRequested)
        {
            RevertPolicyForStudent(s);
            return;
        }

        s.PerStudentBlockUsb = dlg.BlockUsbStorage;
        s.PerStudentBlockOptical = dlg.BlockOpticalDrive;
        s.PerStudentBlockPrint = dlg.BlockPrinting;
        s.PerStudentBlockedProcessNames = new List<string>(dlg.BlockedProcessNames);
        s.PerStudentBlockedHostnames = new List<string>(dlg.BlockedHostnames);

        bool any = dlg.BlockUsbStorage || dlg.BlockOpticalDrive || dlg.BlockPrinting
                || dlg.BlockedProcessNames.Count > 0 || dlg.BlockedHostnames.Count > 0;
        s.HasPerStudentPolicy = any;
        s.PerStudentPolicyBadgeVisibility = any
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;

        var expiresAt = dlg.DurationSeconds > 0
            ? System.DateTimeOffset.UtcNow.AddSeconds(dlg.DurationSeconds).ToUnixTimeMilliseconds()
            : 0L;

        var msg = new ClassroomCtrl.Shared.Protocol.PolicyApplyMessage
        {
            BlockUsbStorage = dlg.BlockUsbStorage,
            BlockOpticalDrive = dlg.BlockOpticalDrive,
            BlockPrinting = dlg.BlockPrinting,
            BlockedProcessNames = new List<string>(dlg.BlockedProcessNames),
            BlockedHostnames = new List<string>(dlg.BlockedHostnames),
            ExpiresAtUtcMs = expiresAt,
        };
        _ = App.Server.ApplyPolicyToOneAsync(s.EndpointId, msg, System.Threading.CancellationToken.None);

        var summary = BuildPolicySummary(msg);
        var durationLabel = dlg.DurationSeconds > 0
            ? Loc.Format("Chat_PolicyExpiresIn", dlg.DurationSeconds / 60)
            : "";
        AppendSystemChat(Loc.Format("Chat_PerStudentPolicyApplied", s.DisplayName, summary) + durationLabel);
    }

    private void RevertPolicyForStudent(StudentViewModel? s)
    {
        if (s == null || App.Server == null) return;

        s.PerStudentBlockUsb = false;
        s.PerStudentBlockOptical = false;
        s.PerStudentBlockPrint = false;
        s.PerStudentBlockedProcessNames = new List<string>();
        s.PerStudentBlockedHostnames = new List<string>();
        s.HasPerStudentPolicy = false;
        s.PerStudentPolicyBadgeVisibility = System.Windows.Visibility.Collapsed;

        _ = App.Server.RevertPolicyForOneAsync(s.EndpointId, System.Threading.CancellationToken.None);
        AppendSystemChat(Loc.Format("Chat_PerStudentPolicyReverted", s.DisplayName));
    }

    private async void ViewStudentScreen(StudentViewModel? s)
    {
        if (s == null || App.Server == null) return;

        // Open viewer first so it's ready when frames arrive
        var window = new StudentScreenWindow(s.EndpointId, s.DisplayName)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        window.Show();

        try
        {
            // Phase 11-B inc1 — BUG-001 status update.
            //
            // The Phase 4 Part 4 BUG-001 had two pieces.  The bitrate/mutex pieces
            // were fixed during the original investigation; the remaining "View
            // Student under H.264 just hangs" piece was the decoder silently
            // returning false on every frame.  Phase 10.15.2 fixed it by
            // pre-allocating the H264Sharp RgbImage with explicit ImageFormat +
            // size (see H264DecoderWrapper ctor doc).  The pre-11-B "workaround
            // forces MJPEG here" comment block is therefore stale — App.SelectedCodec
            // is, and was, the codec actually negotiated below; "MJPEG only" was
            // really just "the dropdown defaults to MJPEG and nobody flipped it."
            //
            // TODO(11-B inc1): once the developer's 2-PC validation confirms a
            // student viewer actually renders H.264 frames here, delete this block.
            await App.Server.RequestStudentStreamAsync(s.EndpointId, App.SelectedCodec, System.Threading.CancellationToken.None);
            AppendSystemChat(Loc.Format("Chat_ViewingStudentScreen", s.DisplayName));
        }
        catch (System.Exception ex)
        {
            AppendErrorChat(ex.Message);
            window.Close();
        }
    }

    // ─────── Phase 6: Power commands ───────

    private async void BroadcastPowerWithConfirm(ClassroomCtrl.Shared.Protocol.MessageType type, string confirmKey)
    {
        if (App.Server == null) return;
        if (Students.Count == 0)
        {
            AppendSystemChat(Loc.Get("Lbl_AppName") + ": no students connected");
            return;
        }

        var prompt = Loc.Format(confirmKey, Students.Count);
        var result = System.Windows.MessageBox.Show(prompt, Loc.Get("Lbl_AppName"),
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            await App.Server.BroadcastPowerAsync(type, System.Threading.CancellationToken.None);
            AppendSystemChat(Loc.Format(GetIssuedChatKey(type), Loc.Get("Lbl_AllStudentsTarget")));
        }
        catch (System.Exception ex)
        {
            AppendErrorChat(ex.Message);
        }
    }

    private async void PowerOneWithConfirm(StudentViewModel? s, ClassroomCtrl.Shared.Protocol.MessageType type, string confirmKey)
    {
        if (s == null || App.Server == null) return;

        var prompt = Loc.Format(confirmKey, s.DisplayName);
        var result = System.Windows.MessageBox.Show(prompt, Loc.Get("Lbl_AppName"),
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (result != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            await App.Server.PowerOneAsync(s.EndpointId, type, System.Threading.CancellationToken.None);
            AppendSystemChat(Loc.Format(GetIssuedChatKey(type), s.DisplayName));
        }
        catch (System.Exception ex)
        {
            AppendErrorChat(ex.Message);
        }
    }

    private static string GetIssuedChatKey(ClassroomCtrl.Shared.Protocol.MessageType type) => type switch
    {
        ClassroomCtrl.Shared.Protocol.MessageType.ForceShutdown => "Chat_ShutdownIssued",
        ClassroomCtrl.Shared.Protocol.MessageType.ForceRestart => "Chat_RestartIssued",
        ClassroomCtrl.Shared.Protocol.MessageType.ForceLogoff => "Chat_LogoffIssued",
        _ => "",
    };

    private async void SendDirectMessage(StudentViewModel? s)
    {
        if (s == null || App.Server == null) return;

        var input = Microsoft.VisualBasic.Interaction.InputBox(
            Loc.Format("Dlg_DMHint", s.DisplayName),
            Loc.Get("Dlg_DMTitle"),
            "");
        if (string.IsNullOrWhiteSpace(input)) return;

        try
        {
            await App.Server.SendDirectMessageAsync(s.EndpointId, input, System.Threading.CancellationToken.None);
            // Phase 3 Section E — funnel DM into the per-student conversation tab so the
            // history is preserved alongside any future inbound replies on the same thread.
            OpenDMConversation(s);
            AppendDMChat(s.DisplayName, input, s.MachineName);
        }
        catch (System.Exception ex)
        {
            AppendSystemChat(Loc.Format("Err_SendFailed", ex.Message));
        }
    }

    // ─────── Phase 8: Breakout Rooms ───────

    private void CreateRooms()
    {
        if (App.Server == null) return;

        var input = Microsoft.VisualBasic.Interaction.InputBox(
            Loc.Get("Dlg_BreakoutCreateHint"),
            Loc.Get("Dlg_BreakoutCreateTitle"),
            "3");
        if (string.IsNullOrWhiteSpace(input)) return;

        var trimmed = input.Trim();
        Rooms.Clear();

        if (int.TryParse(trimmed, out int n))
        {
            if (n < 1 || n > 20)
            {
                AppendSystemChat(Loc.Get("Err_BreakoutInvalidCount"));
                return;
            }
            for (int i = 1; i <= n; i++)
            {
                Rooms.Add(new RoomViewModel
                {
                    RoomId = System.Guid.NewGuid(),
                    RoomName = $"Room {i}",
                    ColorHex = GetRoomColor(i - 1),
                });
            }
        }
        else
        {
            var names = trimmed
                .Split(new[] { ',', ';', '\n', '\r' }, System.StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Take(20)
                .ToList();

            if (names.Count == 0)
            {
                AppendSystemChat(Loc.Get("Err_BreakoutInvalidCount"));
                return;
            }

            for (int i = 0; i < names.Count; i++)
            {
                Rooms.Add(new RoomViewModel
                {
                    RoomId = System.Guid.NewGuid(),
                    RoomName = names[i],
                    ColorHex = GetRoomColor(i),
                });
            }
        }

        AppendSystemChat(Loc.Format("Chat_BreakoutRoomsCreated", Rooms.Count));
    }

    private async void AssignStudentToRoom(StudentViewModel? s, RoomViewModel? room)
    {
        if (s == null || room == null || App.Server == null) return;
        try
        {
            await App.Server.AssignToRoomAsync(s.EndpointId, room.RoomId, room.RoomName,
                System.Threading.CancellationToken.None);
            s.RoomId = room.RoomId;
            s.RoomName = room.RoomName;
            s.RoomBadgeColorHex = room.ColorHex;
            s.RoomBadgeVisibility = System.Windows.Visibility.Visible;
            AppendSystemChat(Loc.Format("Chat_StudentMovedToRoom", s.DisplayName, room.RoomName));
        }
        catch (System.Exception ex)
        {
            AppendErrorChat(ex.Message);
        }
    }

    private async void RemoveFromRoom(StudentViewModel? s)
    {
        if (s == null || App.Server == null) return;
        try
        {
            await App.Server.AssignToRoomAsync(s.EndpointId, null, "",
                System.Threading.CancellationToken.None);
            s.RoomId = null;
            s.RoomName = "";
            s.RoomBadgeVisibility = System.Windows.Visibility.Collapsed;
            AppendSystemChat(Loc.Format("Chat_StudentReturnedToMain", s.DisplayName));
        }
        catch (System.Exception ex)
        {
            AppendErrorChat(ex.Message);
        }
    }

    private async void AutoBalance()
    {
        if (App.Server == null || Rooms.Count == 0)
        {
            AppendSystemChat(Loc.Get("Err_BreakoutNoRooms"));
            return;
        }
        if (Students.Count == 0) return;

        int idx = 0;
        foreach (var s in Students)
        {
            var room = Rooms[idx % Rooms.Count];
            try
            {
                await App.Server.AssignToRoomAsync(s.EndpointId, room.RoomId, room.RoomName,
                    System.Threading.CancellationToken.None);
                s.RoomId = room.RoomId;
                s.RoomName = room.RoomName;
                s.RoomBadgeColorHex = room.ColorHex;
                s.RoomBadgeVisibility = System.Windows.Visibility.Visible;
            }
            catch { }
            idx++;
        }
        AppendSystemChat(Loc.Format("Chat_AutoBalanced", Students.Count, Rooms.Count));
    }

    private async void RenameRooms()
    {
        if (Rooms.Count == 0)
        {
            AppendSystemChat(Loc.Get("Err_BreakoutNoRooms"));
            return;
        }

        foreach (var room in Rooms.ToList())
        {
            var newName = Microsoft.VisualBasic.Interaction.InputBox(
                Loc.Format("Dlg_RenameRoomHint", room.RoomName),
                Loc.Get("Dlg_RenameRoomTitle"),
                room.RoomName);
            if (string.IsNullOrWhiteSpace(newName)) continue;
            newName = newName.Trim();
            if (newName == room.RoomName) continue;

            room.RoomName = newName;

            foreach (var s in Students.Where(x => x.RoomId == room.RoomId))
            {
                try
                {
                    await App.Server!.AssignToRoomAsync(s.EndpointId, room.RoomId, newName,
                        System.Threading.CancellationToken.None);
                    s.RoomName = newName;
                }
                catch { }
            }
        }
        AppendSystemChat(Loc.Get("Chat_RoomsRenamed"));
    }

    private async void EndBreakout()
    {
        if (App.Server == null) return;
        try
        {
            await App.Server.DissolveAllRoomsAsync(System.Threading.CancellationToken.None);
            foreach (var st in Students)
            {
                st.RoomId = null;
                st.RoomName = "";
                st.RoomBadgeVisibility = System.Windows.Visibility.Collapsed;
            }
            Rooms.Clear();
            AppendSystemChat(Loc.Get("Chat_BreakoutEnded"));
        }
        catch (System.Exception ex)
        {
            AppendErrorChat(ex.Message);
        }
    }

    private static string GetRoomColor(int index)
    {
        var palette = new[] { "#3B82F6", "#10B981", "#F59E0B", "#EF4444", "#8B5CF6",
                              "#EC4899", "#14B8A6", "#F97316", "#6366F1", "#84CC16" };
        return palette[index % palette.Length];
    }

    private static string BuildPolicySummary(ClassroomCtrl.Shared.Protocol.PolicyApplyMessage p)
    {
        var parts = new List<string>();
        if (p.BlockUsbStorage)   parts.Add("USB");
        if (p.BlockOpticalDrive) parts.Add("CD/DVD");
        if (p.BlockPrinting)     parts.Add("Print");
        if (p.BlockedProcessNames.Count > 0) parts.Add($"Apps({p.BlockedProcessNames.Count})");
        if (p.BlockedHostnames.Count > 0)    parts.Add($"Sites({p.BlockedHostnames.Count})");
        return parts.Count == 0 ? "—" : string.Join(", ", parts);
    }

    // Phase 3 Section G — Quiz Manager is now an embedded view.  Sidebar's "ระบบข้อสอบ"
    // command flips CurrentMainView; the Window-level QuizManagerWindow file is kept as
    // a deprecated shim until Phase 5 cleanup.
    private void OpenQuizManager()
    {
        // Phase 15-D step 5 — Quiz Manager is mode-exclusive with Conference
        // per architecture § 3 (cam frames + exam UI on a single PC at the
        // same time would compete for attention).  Toast + return.
        if (IsInConference)
        {
            AppendSystemChat(Loc.Get("Conf_BlockedQuiz",
                "Quiz Manager is unavailable in Conference mode."));
            return;
        }
        CurrentMainView = MainViewKind.QuizManager;
    }

    [RelayCommand]
    private void OpenStudentGridView() => CurrentMainView = MainViewKind.StudentGrid;

    private void OpenLanguage()
    {
        var dlg = new Dialogs.LanguageDialog
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        if (dlg.ShowDialog() == true && !string.IsNullOrEmpty(dlg.SelectedLanguage))
        {
            Loc.SetLanguage(dlg.SelectedLanguage);
            App.SavePreferredLanguage(dlg.SelectedLanguage);
        }
    }

    private void OnStudentJoined(object? sender, ClassroomCtrl.Shared.Protocol.HelloMessage hello)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Students.Add(new StudentViewModel
            {
                EndpointId = hello.EndpointId,
                DisplayName = hello.DisplayName,
                MachineName = hello.MachineName,
            });
            ConnectedCount = Students.Count;
            // Phase 15-C — a late-joining student during a live Conference
            // gets a tile so the teacher sees them appear in the gallery.
            if (IsConferenceSessionActive) RebuildConferenceGallery();
        });
    }

    private void OnStudentLeft(object? sender, System.Guid peerId)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var s = Students.FirstOrDefault(x => x.EndpointId == peerId);
            if (s != null) s.IsTalking = false;

            if (Students.Count > 0)
            {
                Students.RemoveAt(Students.Count - 1);
                ConnectedCount = Students.Count;
            }
            // Phase 2 Section C — a student leaving might have had their hand raised.
            RaiseNotificationsChanged();
            // Phase 15-C — purge the departed student's tile from the gallery.
            if (IsConferenceSessionActive) RebuildConferenceGallery();
        });
    }

    private void OnStudentAudioStarted(object? sender, System.Guid peerId)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var s = Students.FirstOrDefault(x => x.EndpointId == peerId);
            if (s != null) s.IsTalking = true;
        });
    }

    private void OnStudentAudioStopped(object? sender, System.Guid peerId)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var s = Students.FirstOrDefault(x => x.EndpointId == peerId);
            if (s != null) s.IsTalking = false;
        });
    }

    private void OnChatReceived(object? sender, ClassroomCtrl.Shared.Protocol.ChatMessage chat)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            // Phase 17.2 step 1 (Bug I fix, 2026-05-31) — diagnostic so the
            // next 2-PC suppression bug has a trace line.  Captures the three
            // suppression-relevant signals at decision time.  Debug.WriteLine
            // is the Teacher equivalent of IpcClient.LogToFile on the Student
            // side: visible in attached debuggers + DebugView, zero perf cost
            // in Release without a debugger attached.
            var isActive = IsWindowActivelyVisible();
            System.Diagnostics.Debug.WriteLine($"[MainViewModel] OnChatReceived sender={chat.SenderName} " +
                                                $"isConf={chat.IsConferenceContext} hasRecipient={chat.RecipientId.HasValue} " +
                                                $"isWindowActive={isActive}");

            // Phase 16-X (Bug G fix, 2026-06-01) — Conference-context chats
            // land in ConferenceConversation only.  Earliest filter point so
            // the existing DM / Room / Everyone branches stay simple.  A
            // pre-16-X sender doesn't set the flag, so legacy broadcasts
            // fall through to the existing path unchanged.
            if (chat.IsConferenceContext)
            {
                ConferenceConversation.Messages.Add(new ChatMessage
                {
                    SenderName = string.IsNullOrEmpty(chat.SenderName) ? "Participant" : chat.SenderName,
                    Kind = ChatMessageKind.Student,
                    MessageText = chat.Text,
                });

                // Phase 17.2 step 1 (Bug I fix) — relaxed suppression rule.
                // OLD (overly aggressive): suppress whenever the sidebar's
                // chat tab is open, regardless of whether the window has
                // focus — meant unfocused-window cases never notified.
                // NEW (LINE-style): suppress only when ALL of
                //   (window actively focused) AND
                //   (sidebar visible) AND
                //   (chat tab selected)
                // are true — i.e. the teacher is provably watching this
                // exact surface in real time.  Any minimize / Alt-Tab away
                // means notify.
                bool suppress = isActive
                                && IsConferenceSidebarVisible
                                && IsConferenceChatTabSelected;
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] Conference-chat branch suppress={suppress} " +
                                                    $"sidebarVisible={IsConferenceSidebarVisible} chatTab={IsConferenceChatTabSelected}");
                if (!suppress)
                    ShowChatNotification(chat.SenderName, chat.Text);
                return;
            }

            // Phase 3 Section E — direct messages from a student route to that student's
            // DM tab (auto-created if missing).  Broadcast / breakout-room chat continues
            // to land in the Everyone conversation with the existing [Room] prefix.
            if (chat.RecipientId.HasValue)
            {
                var sender = Students.FirstOrDefault(x => x.EndpointId == chat.SenderId);
                var pcName = sender?.MachineName;
                var displayName = sender?.DisplayName ?? chat.SenderName;
                if (string.IsNullOrEmpty(pcName))
                {
                    // Unknown sender — fall back to Everyone so the message isn't dropped.
                    AppendStudentChat(chat.SenderName, chat.Text);
                    return;
                }
                var conv = Conversations.FirstOrDefault(c =>
                    c.Kind == ConversationKind.DM && c.StudentPCName == pcName);
                if (conv == null)
                {
                    conv = new Conversation
                    {
                        Id = $"dm:{pcName}",
                        DisplayName = displayName,
                        Kind = ConversationKind.DM,
                        StudentPCName = pcName,
                    };
                    Conversations.Add(conv);
                }
                conv.Messages.Add(new ChatMessage
                {
                    SenderName = chat.SenderName,
                    Kind = ChatMessageKind.Student,
                    MessageText = chat.Text,
                });
                if (ActiveConversation != conv) conv.UnreadCount++;

                // Phase 17.2 step 1 (Bug I fix) — relaxed suppression.  Only
                // skip the slide-in when the window is provably focused AND
                // the active conversation IS this DM (teacher is reading the
                // DM tab right now).  Window unfocused → always notify.
                bool dmSuppress = isActive && ActiveConversation == conv;
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] DM branch suppress={dmSuppress} " +
                                                    $"activeMatches={(ActiveConversation == conv)}");
                if (!dmSuppress)
                    ShowChatNotification(displayName, chat.Text);
                return;
            }

            // Phase 13-C (Tier 2) Step 5 — group-name prefix so the teacher
            // can tell which breakout the chat is coming from instead of a
            // generic "[Room]" tag.  Falls back to the generic prefix only
            // if the RoomId is unknown (room rebuilt / not yet in Rooms).
            string prefix = "";
            if (chat.RoomId.HasValue)
            {
                var room = Rooms.FirstOrDefault(r => r.RoomId == chat.RoomId.Value);
                prefix = room != null ? $"[{room.RoomName}] " : "[Room] ";
            }
            AppendStudentChat(chat.SenderName, prefix + chat.Text);

            // Phase 17.2 step 1 (Bug I fix) — relaxed suppression for whole-
            // class / room chats.  OLD rule was "suppress whenever
            // ActiveConversation == EveryoneConversation" — but Everyone IS
            // the default tab, so unfocused-window broadcasts were silently
            // dropped (THE primary symptom of Bug I reported on 2-PC test).
            // NEW rule: suppress only when ALL of (window actively focused)
            // AND (Everyone tab is active) — i.e. teacher is provably reading
            // the rail right now.  Unfocused / minimized → always notify.
            bool broadcastSuppress = isActive && ActiveConversation == EveryoneConversation;
            System.Diagnostics.Debug.WriteLine($"[MainViewModel] Broadcast/Room branch suppress={broadcastSuppress} " +
                                                $"everyoneActive={(ActiveConversation == EveryoneConversation)}");
            if (!broadcastSuppress)
                ShowChatNotification(chat.SenderName, prefix + chat.Text);
        });
    }

    /// <summary>Phase 17.2 step 1 (Bug I fix, 2026-05-31) — single source of
    /// truth for "is the teacher currently looking at this app?"  Used by
    /// every notification-suppression branch in OnChatReceived.  Returns true
    /// iff the main window is visible, focused, AND not minimized — i.e. the
    /// teacher's eyes can plausibly land on the corner notification overlay
    /// + chat rail.  False = notify regardless of which conversation tab is
    /// active, since the teacher won't see in-window UI anyway.</summary>
    private static bool IsWindowActivelyVisible()
    {
        var w = System.Windows.Application.Current?.MainWindow;
        return w != null
            && w.IsVisible
            && w.IsActive
            && w.WindowState != System.Windows.WindowState.Minimized;
    }

    /// <summary>Phase 17 step 4 — push a Chat-kind LINE-style card onto the
    /// notification overlay.  Truncates the body at 50 chars (LINE-style
    /// preview length) with "…" suffix; sender falls back to localized
    /// "Student" when the wire didn't carry a name (matches the bell-badge
    /// fallback used in OnHandRaiseReceived for symmetry).  Suppression
    /// decisions are made by the caller — this helper trusts the dispatch
    /// arm to skip the call when the teacher is already viewing the chat
    /// surface.</summary>
    private static void ShowChatNotification(string senderName, string text)
    {
        const int PreviewMaxChars = 50;
        var preview = (text ?? "").Trim();
        if (preview.Length > PreviewMaxChars)
            preview = preview.Substring(0, PreviewMaxChars - 1) + "…";
        // Phase 17 step 5 polish — whitespace-only / empty chat still gets a
        // legible card body (localized "sent a message" / "ส่งข้อความ").
        // Prevents a blank-bottom card if a student fat-fingers Enter.
        if (preview.Length == 0)
            preview = Loc.Get("Notif_Chat_Body", "sent a message");

        App.Notifications?.Show(new ClassroomCtrl.Teacher.Services.NotificationItem
        {
            Type = ClassroomCtrl.Teacher.Services.NotificationType.Chat,
            SenderName = string.IsNullOrEmpty(senderName) ? "Student" : senderName,
            Message = preview,
        });
    }

    /// <summary>Phase 17.1 step 2 — repaint the TaskbarItemInfo.Overlay image
    /// from the current unread count.  Called once at construction (after
    /// subscribing to NotificationService.PropertyChanged) and on every
    /// UnreadCount transition.  Generation is on the UI thread because
    /// RenderTargetBitmap requires it; the OnNotificationsPropertyChanged
    /// shim does the dispatch.</summary>
    private void RefreshTaskbarOverlay()
    {
        var count = App.Notifications?.UnreadCount ?? 0;
        TaskbarOverlayImage = ClassroomCtrl.Teacher.Services.NotificationService.CreateBadgeOverlay(count);
        OnPropertyChanged(nameof(TaskbarOverlayTooltip));
    }

    /// <summary>Phase 17.1 step 2 — bridge NotificationService PropertyChanged
    /// onto the WPF dispatcher.  The service fires on whatever thread called
    /// Show / MarkAllRead (today always the UI thread because Show marshals
    /// internally, but the contract is "any thread"); RenderTargetBitmap +
    /// ObservableProperty setter both require the UI thread.</summary>
    private void OnNotificationsPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ClassroomCtrl.Teacher.Services.NotificationService.UnreadCount)) return;
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null) { RefreshTaskbarOverlay(); return; }
        if (dispatcher.CheckAccess()) RefreshTaskbarOverlay();
        else dispatcher.BeginInvoke(new System.Action(RefreshTaskbarOverlay));
    }

    // Phase 17.1 (2026-05-31) — removed ShowChatToastIfNotActive (Phase 10.14
    // Item 9).  The H.NotifyIcon.Wpf tray-toast surface produced an OS-level
    // Windows notification on top of the LINE-style in-app card from Phase 17,
    // which the user rejected on the first 2-PC validation pass.  All chat
    // notification UX now lives exclusively inside the Teacher window via
    // NotificationOverlay + the TaskbarItemInfo.Overlay red-dot badge added in
    // Phase 17.1 step 2.

    private void OnHandRaiseReceived(object? sender, ClassroomCtrl.Shared.Protocol.HandRaiseMessage hr)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var s = Students.FirstOrDefault(x => x.EndpointId == hr.StudentId);
            if (s != null)
            {
                s.HandRaisedVisibility = hr.IsRaised
                    ? System.Windows.Visibility.Visible
                    : System.Windows.Visibility.Collapsed;
            }
            // Phase 15-E step 2 — mirror onto the Conference tile so the badge
            // shows in the gallery + the queue picks the participant up.
            // HandRaisedAt drives queue ordering (first-raised at top).
            var tile = ConferenceGallery?.Tiles.FirstOrDefault(t => t.EndpointId == hr.StudentId);
            if (tile != null)
            {
                tile.IsHandRaised = hr.IsRaised;
                tile.HandRaisedAt = hr.IsRaised ? DateTime.UtcNow : (DateTime?)null;
                ConferenceGallery!.RefreshRaisedHandQueue();
            }
            // Phase 3 Section D — raise events get a HandRaised-kind notification (special
            // icon, prominent in bell popup); lower events use plain System kind so they're
            // still visible in Activity but don't re-pulse the bell badge meaningfully.
            if (hr.IsRaised)
                AddNotification(Loc.Get("Lbl_HandRaised"), hr.StudentName, NotificationKind.HandRaised);
            else
                AddNotification(Loc.Get("Chat_SystemPrefix"),
                    Loc.Format("Chat_HandLoweredBy", hr.StudentName));

            // Phase 17 step 3 — LINE-style in-app slide-in card.  Raise events
            // only (lowering shouldn't pulse the corner overlay); message is a
            // localized "raised hand" body so the card reads "Sumetee: ยกมือ"
            // in Thai and "Sumetee: raised hand" in English.  Fires regardless
            // of mode (Classroom rail / Conference gallery both surface the
            // same HandRaise wire per 16-A foundation discovery #4).
            if (hr.IsRaised)
            {
                App.Notifications?.Show(new ClassroomCtrl.Teacher.Services.NotificationItem
                {
                    Type = ClassroomCtrl.Teacher.Services.NotificationType.HandRaise,
                    SenderName = string.IsNullOrEmpty(hr.StudentName) ? "Student" : hr.StudentName,
                    Message = Loc.Get("Notif_HandRaise_Body", "raised hand"),
                });
            }

            // Phase 2 Section C — RaisedHandsStudents (legacy) still backs other UIs.
            RaiseNotificationsChanged();
        });
    }

    private void OnScreenshotReceived(object? sender, ClassroomCtrl.Shared.Protocol.ScreenshotResponseMessage shot)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            var student = Students.FirstOrDefault(x => x.EndpointId == shot.StudentId);
            if (student == null) return;

            try
            {
                var bmp = new BitmapImage();
                using (var ms = new MemoryStream(shot.JpegData))
                {
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                }
                student.ThumbnailImage = bmp;
            }
            catch { }
        });
    }

    // ─────── Phase 8.5: Host commands ───────

    private async void SetAsHost(StudentViewModel? s)
    {
        if (s == null || App.Server == null) return;
        if (s.RoomId == null)
        {
            AppendSystemChat(Loc.Get("Err_HostNotInRoom"));
            return;
        }
        try
        {
            await App.Server.SetRoomHostAsync(s.RoomId.Value, s.EndpointId, System.Threading.CancellationToken.None);
            AppendSystemChat(Loc.Format("Chat_HostAssigned", s.DisplayName, s.RoomName));
        }
        catch (System.Exception ex) { AppendErrorChat(ex.Message); }
    }

    private async void RemoveHost(StudentViewModel? s)
    {
        if (s == null || App.Server == null || s.RoomId == null) return;
        try
        {
            await App.Server.SetRoomHostAsync(s.RoomId.Value, null, System.Threading.CancellationToken.None);
            AppendSystemChat(Loc.Format("Chat_HostRemoved", s.RoomName));
        }
        catch (System.Exception ex) { AppendErrorChat(ex.Message); }
    }

    // ─────── Phase 9.1: Student Demonstration ───────

    private async void StartDemo(StudentViewModel? s)
    {
        if (s == null || App.Server == null) return;
        try
        {
            await App.Server.BroadcastDemoStartAsync(s.EndpointId, s.DisplayName, System.Threading.CancellationToken.None);
            AppendSystemChat(Loc.Format("Chat_DemoStarted", s.DisplayName));
        }
        catch (System.Exception ex) { AppendErrorChat(ex.Message); }
    }

    private async void StopDemo()
    {
        if (App.Server == null) return;
        try
        {
            await App.Server.BroadcastDemoStopAsync(System.Threading.CancellationToken.None);
            AppendSystemChat(Loc.Get("Chat_DemoStopped"));
        }
        catch (System.Exception ex) { AppendErrorChat(ex.Message); }
    }

    // ─────── Phase 9.2: Screen Pen ───────

    private void OpenScreenPen()
    {
        var w = new ScreenPenWindow { Owner = System.Windows.Application.Current.MainWindow };
        w.Show();
    }

    // ─────── Phase 3.5: Sound effects ───────

    private void ToggleSounds()
    {
        SoundService.SetEnabled(!SoundService.IsEnabled);
        UpdateSoundsButtonText();
    }

    private void UpdateSoundsButtonText()
    {
        SoundsButtonText = SoundService.IsEnabled
            ? Loc.Get("Btn_SoundsOn")
            : Loc.Get("Btn_SoundsOff");
    }

    // ─────── Phase 9.5 / Phase 14-B (Tier 1): Camera Broadcast ───────

    // Phase 14-B step 6 — privacy banner held by the ViewModel because the
    // Camera toggle lives here (and ownership simplifies show/hide pairing
    // with Start/Stop).  Singleton per session; nulled out on Hide.
    private CamLiveBanner? _camBanner;

    private void ToggleCamera()
    {
        if (App.Camera == null) return;
        if (App.Camera.IsActive)
        {
            App.Camera.Stop();
            HideCamBanner();
            // Phase 15-C — clear the conference self-tile's live frame on stop
            // so the gallery shows the cam-off placeholder again.
            ClearConferenceSelfTileFrame();
            AppendSystemChat(Loc.Get("Chat_CameraStopped"));
        }
        else
        {
            // Phase 14-B step 5 — no-cam pre-check.  Surface a localized toast
            // and skip the selector dialog if there's nothing to pick.
            var devices = App.Camera.EnumerateDevices();
            if (devices.Count == 0)
            {
                System.Windows.MessageBox.Show(
                    Loc.Get("Conf_NoWebcam", "No webcam detected on this PC"),
                    Loc.Get("Btn_Camera", "Camera"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Information);
                return;
            }
            // Phase 16-C — mode-aware cam emission.  Set Routing + the
            // Conference session id / source name BEFORE the dialog's Start
            // path runs.  Cleared on Stop() so a stale flag doesn't carry
            // into the next Start.  Mode pick-up happens here (not inside
            // the dialog) so the dialog stays mode-agnostic.
            if (IsInConference)
            {
                App.Camera.Routing = ClassroomCtrl.Teacher.Services.CamRouting.Conference;
                App.Camera.ConferenceSessionId = ConferenceSessionId;
                App.Camera.ConferenceSourceName = Loc.Get("Conf_TeacherDisplayName", "Teacher");
            }
            else
            {
                App.Camera.Routing = ClassroomCtrl.Teacher.Services.CamRouting.Classroom;
                App.Camera.ConferenceSessionId = System.Guid.Empty;
                App.Camera.ConferenceSourceName = "";
            }
            var dlg = new CameraSelectorDialog { Owner = System.Windows.Application.Current.MainWindow };
            if (dlg.ShowDialog() == true)
            {
                AppendSystemChat(Loc.Get("Chat_CameraStarted"));
                ShowCamBanner();
            }
            else if (!string.IsNullOrEmpty(App.Camera.LastError))
            {
                // The selector either succeeded (DialogResult=true, handled above)
                // or the start failed and LastError was set inside the dialog's
                // Start handler — surface verbatim.
                System.Windows.MessageBox.Show(
                    string.Format(Loc.Get("Conf_CamStartFailFmt", "Failed to start camera: {0}"), App.Camera.LastError),
                    Loc.Get("Btn_Camera", "Camera"),
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Warning);
            }
        }
        UpdateCameraButtonText();
    }

    private void ShowCamBanner()
    {
        if (_camBanner != null) return;
        try
        {
            _camBanner = new CamLiveBanner();
            // Owner intentionally null so the banner stays visible even when
            // the Teacher main window is minimized — the banner exists to
            // remind the teacher the cam is live regardless of focus.
            _camBanner.Closed += (_, _) => _camBanner = null;
            _camBanner.Show();
        }
        catch
        {
            _camBanner = null;
        }
    }

    private void HideCamBanner()
    {
        try { _camBanner?.Close(); } catch { }
        _camBanner = null;
    }

    private void UpdateCameraButtonText()
    {
        var active = App.Camera?.IsActive ?? false;
        CameraButtonText = active
            ? Loc.Get("Btn_StopCamera")
            : Loc.Get("Btn_Camera");
        // Phase 15-D step 1 — mirror cam-active state onto an observable bool
        // so the Conference toolbar can react via DataTrigger.
        IsBroadcastingCamera = active;
    }

    /// <summary>Phase 15-B (MVP) — Start the Conference session.  Pre-flight
    /// dissolves any active breakouts (modal confirm per architecture § 5
    /// risk #2 — running Conference alongside breakouts multiplies relay
    /// paths and confuses routing).  After confirm: emit ConferenceStart
    /// (0x0670) to all connected students; students transition to
    /// ConferenceGalleryWindow on dispatch (Phase 15-B step 6).</summary>
    private async void StartConference()
    {
        // Pre-flight: dissolve breakouts if any exist.
        if (App.Server != null && Rooms.Count > 0)
        {
            var msg = string.Format(
                Loc.Get("Conf_DissolveBreakoutsConfirm",
                    "Starting Conference will dissolve {0} active breakout room(s). Continue?"),
                Rooms.Count);
            var result = System.Windows.MessageBox.Show(
                msg,
                Loc.Get("Conf_StartConference", "Start Conference"),
                System.Windows.MessageBoxButton.OKCancel,
                System.Windows.MessageBoxImage.Question);
            if (result != System.Windows.MessageBoxResult.OK) return;
            try { await App.Server.DissolveAllRoomsAsync(System.Threading.CancellationToken.None); }
            catch (Exception ex)
            {
                AppendSystemChat(string.Format(Loc.Get("Err_GenericFmt", "Error: {0}"), ex.Message));
                return;
            }
        }

        ConferenceSessionId = Guid.NewGuid();
        ConferenceStartedAt = DateTime.UtcNow;
        // Make sure the shell is in Conference mode (idempotent if the teacher
        // already toggled the mode pill before clicking Start).
        IsInConference = true;

        if (App.Server != null)
        {
            try
            {
                var payload = new ConferenceStartMessage
                {
                    SessionId = ConferenceSessionId,
                    HostName = OrganizationSubtitle ?? "",
                    StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                };
                await App.Server.BroadcastConferenceStartAsync(payload, System.Threading.CancellationToken.None);
            }
            catch (Exception ex)
            {
                AppendSystemChat(string.Format(Loc.Get("Err_GenericFmt", "Error: {0}"), ex.Message));
            }
        }
    }

    /// <summary>Phase 15-B (MVP) — End the Conference session.  Emits
    /// ConferenceEnd (0x0671) so students close their ConferenceGalleryWindow,
    /// then resets local session state and returns the shell to Classroom mode.</summary>
    private async void EndConference()
    {
        if (App.Server != null)
        {
            try { await App.Server.BroadcastConferenceEndAsync(System.Threading.CancellationToken.None); }
            catch (Exception ex)
            {
                AppendSystemChat(string.Format(Loc.Get("Err_GenericFmt", "Error: {0}"), ex.Message));
            }
        }
        ConferenceSessionId = Guid.Empty;
        ConferenceStartedAt = null;
        IsInConference = false;
        // Phase 15-D step 5 — drop any sidebar / tab state that was left
        // open during the session so the next Start opens a fresh shell.
        IsConferenceSidebarVisible = false;
        ConferenceSidebarTabIndex = 0;
    }

    /// <summary>Phase 14-B (Tier 1) — runtime cam failure handler.  Wired in
    /// the MainViewModel ctor so a sudden device error (cam unplugged etc.)
    /// returns the toolbar to "Start" and triggers a localized notification
    /// rather than leaving the button stuck on "Stop".</summary>
    private void OnCameraStoppedDueToError()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            HideCamBanner();
            // Phase 15-C — same clean-up as the user-driven Stop path: drop
            // the live frame from the gallery self-tile so the placeholder
            // shows on the next render.
            ClearConferenceSelfTileFrame();
            UpdateCameraButtonText();
            AppendSystemChat(string.Format(
                Loc.Get("Conf_CamStartFailFmt", "Camera stopped: {0}"),
                App.Camera?.LastError ?? ""));
        });
    }

    /// <summary>Phase 15-E step 4 — display the floating emoji over the
    /// matching tile for ~3 seconds.  Idempotent: a second reaction from
    /// the same sender within the window replaces the first.  No-op when
    /// no Conference session is active or the sender has no tile.</summary>
    internal void ShowReactionOnTile(Guid senderId, string emoji)
    {
        if (!IsConferenceSessionActive || ConferenceGallery == null) return;
        var tile = ConferenceGallery.Tiles.FirstOrDefault(t => t.EndpointId == senderId);
        if (tile == null) return;
        tile.CurrentReactionEmoji = emoji;
        var clearTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(3),
        };
        clearTimer.Tick += (s, e) =>
        {
            clearTimer.Stop();
            // Only clear if no newer reaction has replaced this one.
            if (tile.CurrentReactionEmoji == emoji) tile.CurrentReactionEmoji = "";
        };
        clearTimer.Start();
    }

    /// <summary>Phase 15-E step 4 — fired by ControlServer when a Reaction
    /// envelope arrives (student-originated; teacher-side has already
    /// rendered via the optimistic local path in SendReactionCommand).</summary>
    private void OnReactionReceived(object? sender, (Guid SenderId, ReactionMessage Msg) e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            ShowReactionOnTile(e.SenderId, e.Msg.Emoji);
        });
    }

    /// <summary>Phase 15-C — drop the teacher's self-tile frame when the cam
    /// stops (user click OR runtime error).  No-op when no active conference
    /// or no self-tile.  Called from <see cref="ToggleCamera"/> and
    /// <see cref="OnCameraStoppedDueToError"/>.</summary>
    private void ClearConferenceSelfTileFrame()
    {
        if (!IsConferenceSessionActive || ConferenceGallery == null) return;
        var selfId = App.Server?.TeacherEndpointId ?? System.Guid.Empty;
        if (selfId == System.Guid.Empty) return;
        var tile = ConferenceGallery.Tiles.FirstOrDefault(t => t.EndpointId == selfId);
        if (tile == null) return;
        tile.JpegFrame = null;
        tile.IsCamLive = false;
    }

    // ─────── Phase 9.6: Net Movie ───────

    private void OpenNetMovie()
    {
        var w = new NetMoviePlaylistWindow { Owner = System.Windows.Application.Current.MainWindow };
        w.Show();
    }

    // ─────── Phase 4.6: Mic Monitor ───────

    private void OpenMicMonitor()
    {
        var w = new MicMonitorWindow { Owner = System.Windows.Application.Current.MainWindow };
        w.Show();
    }

    // ─────── Phase 13-B (Tier 1): Group Manager ───────
    // Replaces both the old CreateRooms input-box flow (Btn_BreakoutRooms) and
    // the MultiRoomTeacherView entry (Btn_MultiRoom).  The new GroupManagerView
    // owns create/rename/dissolve/assign/host/templates and reads canonical
    // state from App.Server (mirrored into Rooms via OnServerRoomsChanged).

    /// <summary>Phase 13-B (Tier 1) — fired whenever the Rooms collection is
    /// rebuilt from a server snapshot.  GroupManagerView subscribes to refresh
    /// its Unassigned section in addition to the implicit ObservableCollection
    /// notifications.</summary>
    public event System.EventHandler? RoomsCollectionChanged;

    /// <summary>Phase 13-B (Tier 1) — Step 6 wires this to a method that opens
    /// GroupControllerWindow for the chosen room.  Step 4's Join button calls
    /// it via null-check, so it's safe to be unset before Step 6 lands.</summary>
    public System.Action<RoomViewModel>? OpenGroupControllerForRoom { get; set; }

    private void OpenGroupManager()
    {
        var w = new GroupManagerView { Owner = System.Windows.Application.Current.MainWindow };
        // Phase 13-B (Tier 1) Step 6 — wire the Join button on each group row to
        // the new controller window.  Set on every open so the latest callback
        // (which captures the current GroupManagerView's lifecycle) wins.
        OpenGroupControllerForRoom = OpenGroupControllerInternal;
        w.Show();
    }

    /// <summary>Phase 13-B (Tier 1) Step 6 — open the floating
    /// <see cref="GroupControllerWindow"/> for the chosen room.  Emits
    /// TeacherJoinGroupAsync (which auto-leaves a previously-joined group),
    /// then shows the controller (which opens N tiled StudentScreenWindows
    /// on Loaded).</summary>
    private async void OpenGroupControllerInternal(RoomViewModel room)
    {
        if (App.Server == null) return;
        await App.Server.TeacherJoinGroupAsync(room.RoomId, System.Threading.CancellationToken.None);
        var ctrl = new GroupControllerWindow(room) { Owner = System.Windows.Application.Current.MainWindow };
        ctrl.Show();
    }

    /// <summary>Phase 13-B (Tier 1) — invoked on the dispatcher when App.Server.RoomsChanged
    /// fires.  Rebuilds the Rooms collection from the server's canonical
    /// snapshot so badges, status chip, and GroupManagerView all converge.</summary>
    private void OnServerRoomsChanged(object? sender, System.EventArgs e)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            if (App.Server == null) return;
            var descriptors = App.Server.BuildGroupDescriptors();
            var teacherJoined = App.Server.TeacherJoinedGroupId;
            var activeShare = App.Server.ActiveGroupShareGroupId;

            // Reuse existing RoomViewModel instances by id to preserve UI state
            // (e.g. expansion).  Rebuild MemberIds inside each.
            var keepIds = new System.Collections.Generic.HashSet<System.Guid>(descriptors.Select(d => d.Id));
            for (int i = Rooms.Count - 1; i >= 0; i--)
                if (!keepIds.Contains(Rooms[i].RoomId)) Rooms.RemoveAt(i);

            for (int i = 0; i < descriptors.Count; i++)
            {
                var d = descriptors[i];
                var vm = Rooms.FirstOrDefault(r => r.RoomId == d.Id);
                if (vm == null)
                {
                    vm = new RoomViewModel
                    {
                        RoomId = d.Id,
                        RoomName = d.Name,
                        ColorHex = GetRoomColor(i),
                    };
                    Rooms.Add(vm);
                }
                else
                {
                    vm.RoomName = d.Name;
                }
                vm.HostId = d.HostId;
                vm.IsTeacherJoined = (teacherJoined == d.Id);
                vm.IsShareActive = (activeShare == d.Id);

                // Sync MemberIds (incremental: add missing, remove gone).
                var wanted = new System.Collections.Generic.HashSet<System.Guid>(d.MemberIds);
                for (int j = vm.MemberIds.Count - 1; j >= 0; j--)
                    if (!wanted.Contains(vm.MemberIds[j])) vm.MemberIds.RemoveAt(j);
                foreach (var mid in d.MemberIds)
                    if (!vm.MemberIds.Contains(mid)) vm.MemberIds.Add(mid);
            }

            // Phase 13-B (Tier 1) Step 7 — sync per-student tile badges from
            // the freshly-rebuilt Rooms collection.  Drives the small G1/G2/—
            // chip in the main classroom grid.
            foreach (var s in Students)
            {
                var assignedRoom = Rooms.FirstOrDefault(r => r.MemberIds.Contains(s.EndpointId));
                if (assignedRoom == null)
                {
                    s.RoomId = null;
                    s.RoomName = "";
                    s.RoomBadgeVisibility = System.Windows.Visibility.Collapsed;
                }
                else
                {
                    s.RoomId = assignedRoom.RoomId;
                    s.RoomName = assignedRoom.RoomName;
                    s.RoomBadgeColorHex = assignedRoom.ColorHex;
                    s.RoomBadgeVisibility = System.Windows.Visibility.Visible;
                }
            }

            // Phase 13-B (Tier 1) Step 7 — recompute the main-toolbar status chip.
            if (teacherJoined.HasValue)
            {
                var jname = descriptors.FirstOrDefault(d => d.Id == teacherJoined.Value)?.Name ?? "?";
                StatusChipText = string.Format(Loc.Get("StatusChip_JoinedFmt"), jname);
                StatusChipVisibility = System.Windows.Visibility.Visible;
            }
            else if (activeShare.HasValue)
            {
                var sname = descriptors.FirstOrDefault(d => d.Id == activeShare.Value)?.Name ?? "?";
                StatusChipText = string.Format(Loc.Get("StatusChip_SharingFmt"), sname);
                StatusChipVisibility = System.Windows.Visibility.Visible;
            }
            else
            {
                StatusChipText = "";
                StatusChipVisibility = System.Windows.Visibility.Collapsed;
            }

            RoomsCollectionChanged?.Invoke(this, System.EventArgs.Empty);
        });
    }

    /// <summary>Phase 13-B (Tier 1) Step 7 — main-toolbar status chip text:
    /// "Sharing → {GroupName}" while group-share active, "Joined {GroupName}"
    /// while teacher joined.  Empty + collapsed visibility when whole-class.</summary>
    [ObservableProperty] private string statusChipText = "";
    [ObservableProperty] private System.Windows.Visibility statusChipVisibility = System.Windows.Visibility.Collapsed;

    // ─────── Phase 9.3: Class Roster ───────

    private void OpenClassRoster()
    {
        var w = new ClassRosterManagerWindow { Owner = System.Windows.Application.Current.MainWindow };
        w.ShowDialog();
    }

    /// <summary>
    /// Phase 7 Section D — opens the manual Attendance dialog so the teacher
    /// can mark per-student status (Present/Absent/Late/Excused) and export
    /// to .xlsx.  Pre-marks each student Present if their MachineName is in
    /// the currently-connected set, otherwise Absent — replaces the old
    /// auto-mark-only flow which logged a Chat_AttendanceMarked summary
    /// without giving the teacher a chance to override per-student.
    /// </summary>
    private void MarkAttendance()
    {
        if (App.Roster == null || App.Roster.ActiveRoster == null)
        {
            AppendSystemChat(Loc.Get("Err_NoActiveRoster"));
            return;
        }
        var connected = new HashSet<string>(
            Students.Select(s => s.MachineName ?? "")
                    .Where(m => !string.IsNullOrWhiteSpace(m)),
            StringComparer.OrdinalIgnoreCase);

        // Phase 7.3 diagnostic — log the ActiveRoster reference and each
        // EnrolledStudent's HasBeenMarked + AttendanceStatus before opening
        // the dialog, so we can verify mutations made by Save_Click survive
        // until the next dialog open within the same session.
        var ar = App.Roster.ActiveRoster;
        System.Diagnostics.Debug.WriteLine(
            $"[ATTENDANCE INVOKE] ActiveRoster='{ar.ClassName}' " +
            $"hash={ar.GetHashCode()} students={ar.Students.Count} " +
            $"connectedCount={connected.Count}");
        for (int i = 0; i < ar.Students.Count; i++)
        {
            var s = ar.Students[i];
            System.Diagnostics.Debug.WriteLine(
                $"[ATTENDANCE INVOKE] idx={i} name='{s.FullName}' " +
                $"marked={s.HasBeenMarked} status={s.AttendanceStatus} " +
                $"machine='{s.MachineName}' studentHash={s.GetHashCode()}");
        }

        var win = new AttendanceWindow(ar, connected)
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        win.ShowDialog();
    }

    private void DeactivateRoster()
    {
        if (App.Roster == null) return;
        App.Roster.SetActive(null);  // raises ActiveRosterChanged → OnActiveRosterChanged updates flags
    }

    private void OnActiveRosterChanged(object? sender, ClassroomCtrl.Shared.Models.Roster.ClassRoster? roster)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (roster == null)
            {
                ActiveClassName = "";
                ActiveClassVisibility = System.Windows.Visibility.Collapsed;
                HasActiveRoster = false;
            }
            else
            {
                ActiveClassName = $"📚 {roster.ClassName}";
                ActiveClassVisibility = System.Windows.Visibility.Visible;
                HasActiveRoster = true;
            }
        });
    }

    private void OnDemoStateChanged(object? sender, (System.Guid? SourceId, string SourceName) e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            if (e.SourceId.HasValue)
            {
                ActiveDemoStudent = Students.FirstOrDefault(x => x.EndpointId == e.SourceId.Value);
                DemoBannerText = Loc.Format("Lbl_DemoActive", e.SourceName);
                DemoBannerVisibility = System.Windows.Visibility.Visible;
            }
            else
            {
                ActiveDemoStudent = null;
                DemoBannerText = "";
                DemoBannerVisibility = System.Windows.Visibility.Collapsed;
            }
        });
    }

    private void OnHostChanged(object? sender, (System.Guid RoomId, System.Guid? NewHostId) e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            // Clear host flag from anyone in this room, then set on the new host.
            foreach (var s in Students.Where(x => x.RoomId == e.RoomId))
                s.IsHost = false;
            if (e.NewHostId.HasValue)
            {
                var newHost = Students.FirstOrDefault(x => x.EndpointId == e.NewHostId.Value);
                if (newHost != null) newHost.IsHost = true;
            }
        });
    }

    /// <summary>Phase 13-D (Tier 3) — route MicStateUpdate heartbeat to the
    /// right StudentViewModel.  No-op if the sender isn't in our Students
    /// list (left the class between emit and arrival).</summary>
    private void OnMicStateUpdated(object? sender, (System.Guid StudentId, ClassroomCtrl.Shared.Protocol.MicStateUpdateMessage State) e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var s = Students.FirstOrDefault(x => x.EndpointId == e.StudentId);
            if (s == null) return;
            s.MicLive = e.State.MicLive;
            s.MicPttMode = e.State.PttMode;
            s.MicIsSpeaking = e.State.IsSpeaking;

            // Phase 15-C — mirror state onto the matching gallery tile.
            // Pinned tiles win on accent (purple) over speaking (green),
            // handled by the tile XAML style triggers.  No hysteresis here;
            // the mic-broadcaster already produces a smoothed IsSpeaking
            // signal (Phase 13-D VAD ~100 ms window with rate-limiting).
            if (IsConferenceSessionActive)
            {
                var tile = ConferenceGallery?.Tiles.FirstOrDefault(t => t.EndpointId == e.StudentId);
                if (tile != null)
                {
                    tile.IsMicLive = e.State.MicLive;
                    tile.IsSpeaking = e.State.IsSpeaking;
                }
            }
        });
    }

    /// <summary>Phase 16-C — peer cam Start arriving from a student.  Ensures
    /// the matching gallery tile exists (RebuildConferenceGallery already
    /// added student tiles on join; this just picks up any display-name
    /// refresh carried in the Start envelope).  Marshals to UI dispatcher.</summary>
    private void OnConferenceCameraStartReceived(object? sender,
        (System.Guid SourceId, ClassroomCtrl.Shared.Protocol.ConferenceCameraStartMessage Msg) e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new System.Action(() =>
        {
            if (!IsConferenceSessionActive || ConferenceGallery == null) return;
            var tile = ConferenceGallery.Tiles.FirstOrDefault(t => t.EndpointId == e.SourceId);
            if (tile == null)
            {
                // Tile not yet in gallery — unusual since student must be
                // connected to send the envelope, but defensively add it so a
                // late RebuildConferenceGallery race doesn't drop the frame.
                tile = new ConferenceTileViewModel(e.SourceId,
                    string.IsNullOrWhiteSpace(e.Msg.SourceName) ? "Participant" : e.Msg.SourceName,
                    isSelf: false);
                ConferenceGallery.Tiles.Add(tile);
            }
            else if (!string.IsNullOrWhiteSpace(e.Msg.SourceName) && tile.DisplayName != e.Msg.SourceName)
            {
                tile.DisplayName = e.Msg.SourceName;
            }
        }));
    }

    /// <summary>Phase 16-C — peer cam Frame arriving from a student.  Decode
    /// JPEG, route to the matching gallery tile by sender id, flip
    /// IsCamLive=true.  Marshals to UI dispatcher; transient decode failures
    /// leave the prior frame in place (the next frame lands seconds later).</summary>
    private void OnConferenceCameraFrameReceived(object? sender,
        (System.Guid SourceId, ClassroomCtrl.Shared.Protocol.ConferenceCameraFrameMessage Msg) e)
    {
        if (!IsConferenceSessionActive || ConferenceGallery == null) return;
        var jpeg = e.Msg.JpegData;
        var sourceId = e.SourceId;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new System.Action(() =>
        {
            var tile = ConferenceGallery.Tiles.FirstOrDefault(t => t.EndpointId == sourceId);
            if (tile == null) return;
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                using var ms = new System.IO.MemoryStream(jpeg);
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                tile.JpegFrame = bmp;
                tile.IsCamLive = true;
            }
            catch { /* transient decode: next frame lands seconds later */ }
        }));
    }

    /// <summary>Phase 16-C — peer cam Stop arriving from a student (or
    /// implicit Stop fired by the ControlServer PeerDisconnected handler).
    /// Clears IsCamLive + JpegFrame so the cam-off placeholder swaps in;
    /// preserves the tile + display name so the participant stays in the
    /// gallery until they actually leave the Conference.</summary>
    private void OnConferenceCameraStopReceived(object? sender, System.Guid sourceId)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new System.Action(() =>
        {
            if (!IsConferenceSessionActive || ConferenceGallery == null) return;
            var tile = ConferenceGallery.Tiles.FirstOrDefault(t => t.EndpointId == sourceId);
            if (tile == null) return;
            tile.IsCamLive = false;
            tile.JpegFrame = null;
        }));
    }

    /// <summary>Phase 15-C — surface the teacher's broadcast cam JPEG into
    /// the Conference self-tile.  Fires on every <c>BroadcastCameraFrameAsync</c>
    /// call; marshal to UI dispatcher; decode JPEG into a frozen BitmapImage
    /// and assign to <c>self.JpegFrame</c>.  Skipped if there's no active
    /// conference (no self-tile exists yet).</summary>
    private void OnTeacherCameraFrameSent(object? sender, byte[] jpeg)
    {
        if (!IsConferenceSessionActive || ConferenceGallery == null) return;
        var selfId = App.Server?.TeacherEndpointId ?? System.Guid.Empty;
        if (selfId == System.Guid.Empty) return;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(new System.Action(() =>
        {
            var tile = ConferenceGallery.Tiles.FirstOrDefault(t => t.EndpointId == selfId);
            if (tile == null) return;
            try
            {
                var bmp = new System.Windows.Media.Imaging.BitmapImage();
                using var ms = new System.IO.MemoryStream(jpeg);
                bmp.BeginInit();
                bmp.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                bmp.StreamSource = ms;
                bmp.EndInit();
                bmp.Freeze();
                tile.JpegFrame = bmp;
                tile.IsCamLive = true;
            }
            catch { /* decode failures land the next frame; no point logging. */ }
        }));
    }

    /// <summary>Phase 14-B (Tier 1) — route WebcamStateUpdate heartbeat to the
    /// right StudentViewModel.  Mirrors OnMicStateUpdated shape.  Tier 1 only
    /// receives DeviceAvailable=true|false (student-side Tier 1 never
    /// captures); Tier 2 will surface CamLive + CamMode transitions here.</summary>
    private void OnWebcamStateUpdated(object? sender, (System.Guid StudentId, ClassroomCtrl.Shared.Protocol.WebcamStateUpdateMessage State) e)
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            var s = Students.FirstOrDefault(x => x.EndpointId == e.StudentId);
            if (s == null) return;
            s.HasWebcam = e.State.DeviceAvailable;
            s.CamLive = e.State.CamLive;
            s.CamMode = e.State.Mode;
            s.CamLastError = e.State.LastError ?? "";
        });
    }

    // ─────── Phase 5b: Per-student recording commands ───────

    private async void StartStudentRecording(StudentViewModel? s)
    {
        if (s == null || App.PerStudentRecording == null) return;
        if (!ViewingStudents.Contains(s.EndpointId))
        {
            AppendSystemChat(Loc.Get("Err_RecordingRequiresView"));
            return;
        }
        if (App.PerStudentRecording.IsRecording(s.EndpointId)) return;

        // Use a default 1280×720 @ 4 fps until first frame arrives — service ignores
        // mismatched dimensions on first frame and re-inits to actual size.
        if (App.PerStudentRecording.Start(s.EndpointId, s.DisplayName, 1280, 720, 4))
        {
            s.IsBeingRecorded = true;
            try
            {
                if (App.Server != null)
                    await App.Server.NotifyStudentRecordingAsync(s.EndpointId, true, System.Threading.CancellationToken.None);
            }
            catch { }
            AppendSystemChat(Loc.Format("Toast_RecordingStarted", s.DisplayName));
        }
    }

    /// <summary>Toggle helper: 1 button on tile, dispatches to Start or Stop based on state.</summary>
    private void ToggleStudentRecording(StudentViewModel? s)
    {
        if (s == null) return;
        if (s.IsBeingRecorded) StopStudentRecording(s);
        else StartStudentRecording(s);
    }

    /// <summary>
    /// Refresh CanRecord flag on every StudentVM based on the live ViewingStudents set.
    /// Called by StudentScreenWindow on Loaded/Closed so the REC button dims/lights up immediately.
    /// </summary>
    public void NotifyViewingStudentsChanged()
    {
        System.Windows.Application.Current?.Dispatcher.Invoke(() =>
        {
            foreach (var s in Students)
                s.CanRecord = ViewingStudents.Contains(s.EndpointId);
        });
    }

    private async void StopStudentRecording(StudentViewModel? s)
    {
        if (s == null || App.PerStudentRecording == null) return;
        if (!App.PerStudentRecording.IsRecording(s.EndpointId)) return;

        var path = await App.PerStudentRecording.StopAsync(s.EndpointId);
        s.IsBeingRecorded = false;
        try
        {
            if (App.Server != null)
                await App.Server.NotifyStudentRecordingAsync(s.EndpointId, false, System.Threading.CancellationToken.None);
        }
        catch { }
        AppendSystemChat(Loc.Format("Toast_RecordingStopped", s.DisplayName, path ?? ""));
    }

    // ─────── Phase 11.3: Branding settings dialog ───────

    private void OpenBrandingSettings()
    {
        var win = new ClassroomCtrl.Teacher.Branding.BrandingSettingsWindow
        {
            Owner = System.Windows.Application.Current.MainWindow,
        };
        win.ShowDialog();
    }

    // Phase 8 Section D — OpenAdminPasswordSettings() removed.

    /// <summary>Phase 16-X (Bug G fix, 2026-06-01) — chat send from the
    /// Conference sidebar.  Mirrors the Everyone-tab path in SendChat below
    /// but uses BroadcastConferenceChatAsync (stamps IsConferenceContext=
    /// true on the wire) and appends LOCALLY to ConferenceConversation
    /// only — Classroom rail (bound to ActiveConversation) stays clean.</summary>
    private async void SendConferenceChat()
    {
        var conv = ConferenceConversation;
        var text = (conv.DraftInput ?? "").Trim();
        if (string.IsNullOrEmpty(text)) return;
        conv.DraftInput = "";

        if (App.Server == null) return;

        // Append outgoing bubble locally so the user sees their own message
        // immediately without waiting for any echo (broadcast doesn't echo
        // back to teacher anyway).
        conv.Messages.Add(new ChatMessage
        {
            SenderName = Loc.Get("Chat_MePrefix"),
            Kind = ChatMessageKind.Teacher,
            MessageText = text,
        });

        var senderName = Loc.Get("Conf_TeacherDisplayName", "Teacher");
        try
        {
            await App.Server.BroadcastConferenceChatAsync(text, senderName,
                System.Threading.CancellationToken.None);
        }
        catch (System.Exception ex)
        {
            AppendSystemChat(Loc.Format("Err_SendFailed", ex.Message));
        }
    }

    private async void SendChat()
    {
        // Phase 3 Section E — input lives on the active conversation now.  Everyone tab
        // broadcasts; DM tab routes through SendDirectMessageAsync to the matching student
        // resolved by PCName (since that's what Conversation.StudentPCName stores).
        var conv = ActiveConversation;
        if (conv == null) return;
        var text = (conv.DraftInput ?? "").Trim();
        if (string.IsNullOrEmpty(text)) return;
        conv.DraftInput = "";

        if (App.Server == null) return;

        if (conv.Kind == ConversationKind.Everyone)
        {
            AppendTeacherChat(text);
            try { await App.Server.BroadcastChatAsync(text, System.Threading.CancellationToken.None); }
            catch (System.Exception ex) { AppendSystemChat(Loc.Format("Err_SendFailed", ex.Message)); }
        }
        else if (conv.Kind == ConversationKind.DM && !string.IsNullOrEmpty(conv.StudentPCName))
        {
            var s = Students.FirstOrDefault(x => x.MachineName == conv.StudentPCName);
            if (s == null)
            {
                AppendErrorChat(Loc.Format("Err_SendFailed", "student offline"));
                return;
            }
            // Append outgoing bubble locally (Me → student) before the network round-trip.
            conv.Messages.Add(new ChatMessage
            {
                SenderName = Loc.Get("Chat_MePrefix"),
                Kind = ChatMessageKind.DM,
                MessageText = text,
            });
            try { await App.Server.SendDirectMessageAsync(s.EndpointId, text, System.Threading.CancellationToken.None); }
            catch (System.Exception ex) { AppendErrorChat(Loc.Format("Err_SendFailed", ex.Message)); }
        }
    }

    // ─────── Phase 3 Section E: Conversation tab commands ───────

    private void InitConversations()
    {
        var everyone = EveryoneConversation;
        Conversations.Add(everyone);
        ActiveConversation = everyone;
    }

    /// <summary>Open or focus the DM tab for the given student.  Called from the
    /// per-student ContextMenu (Section F) and used as the entry point for new DM threads.</summary>
    [RelayCommand]
    private void OpenDMConversation(StudentViewModel? student)
    {
        if (student == null) return;
        var pcName = student.MachineName;
        var existing = Conversations.FirstOrDefault(c =>
            c.Kind == ConversationKind.DM && c.StudentPCName == pcName);
        if (existing != null)
        {
            ActiveConversation = existing;
            existing.UnreadCount = 0;
            return;
        }
        var conv = new Conversation
        {
            Id = $"dm:{pcName}",
            DisplayName = student.DisplayName,
            Kind = ConversationKind.DM,
            StudentPCName = pcName,
        };
        Conversations.Add(conv);
        ActiveConversation = conv;
    }

    /// <summary>Close a DM tab.  Everyone is non-closable so we no-op there.  Falls back to
    /// Everyone when the active tab is the one being closed.</summary>
    [RelayCommand]
    private void CloseConversation(Conversation? conv)
    {
        if (conv == null || conv.Kind == ConversationKind.Everyone) return;
        var wasActive = ActiveConversation == conv;
        Conversations.Remove(conv);
        if (wasActive) ActiveConversation = EveryoneConversation;
    }

    /// <summary>Switch the active tab; clears unread on the destination so the badge drops.</summary>
    [RelayCommand]
    private void SwitchConversation(Conversation? conv)
    {
        if (conv == null) return;
        ActiveConversation = conv;
        conv.UnreadCount = 0;
    }
}

public partial class StudentViewModel : ObservableObject
{
    [ObservableProperty] private System.Guid endpointId;
    [ObservableProperty] private string displayName = "";
    [ObservableProperty] private string machineName = "";
    [ObservableProperty] private object? thumbnailImage;
    [ObservableProperty] private System.Windows.Visibility handRaisedVisibility = System.Windows.Visibility.Collapsed;

    [ObservableProperty] private bool hasPerStudentPolicy;
    [ObservableProperty] private bool perStudentBlockUsb;
    [ObservableProperty] private bool perStudentBlockOptical;
    [ObservableProperty] private bool perStudentBlockPrint;
    [ObservableProperty] private System.Collections.Generic.List<string> perStudentBlockedProcessNames = new();
    [ObservableProperty] private System.Collections.Generic.List<string> perStudentBlockedHostnames = new();
    [ObservableProperty] private System.Windows.Visibility perStudentPolicyBadgeVisibility = System.Windows.Visibility.Collapsed;

    [ObservableProperty] private System.Guid? roomId;
    [ObservableProperty] private string roomName = "";
    [ObservableProperty] private string roomBadgeColorHex = "#3B82F6";
    [ObservableProperty] private System.Windows.Visibility roomBadgeVisibility = System.Windows.Visibility.Collapsed;

    // Phase 4 Part 3c: Talking badge
    [ObservableProperty] private bool isTalking;
    [ObservableProperty] private System.Windows.Visibility talkingBadgeVisibility = System.Windows.Visibility.Collapsed;

    partial void OnIsTalkingChanged(bool value)
    {
        TalkingBadgeVisibility = value
            ? System.Windows.Visibility.Visible
            : System.Windows.Visibility.Collapsed;
    }

    // Phase 8.5: Host of a breakout room
    [ObservableProperty] private bool isHost;
    [ObservableProperty] private System.Windows.Visibility hostBadgeVisibility = System.Windows.Visibility.Collapsed;

    // Phase 13-D (Tier 3) — last-known mic state from MicStateUpdate heartbeat.
    // Updated by MainViewModel.OnMicStateUpdated.  MicIndicatorText carries an
    // emoji + label ready for the per-student chip / row.  Sticky: if a
    // student goes offline, the last state stays until StudentLeft purges them.
    [ObservableProperty] private bool micLive;
    [ObservableProperty] private bool micPttMode = true;
    [ObservableProperty] private bool micIsSpeaking;
    [ObservableProperty] private string micIndicatorText = "";
    [ObservableProperty] private System.Windows.Visibility micIndicatorVisibility = System.Windows.Visibility.Collapsed;

    partial void OnMicLiveChanged(bool value) => RefreshMicIndicator();
    partial void OnMicIsSpeakingChanged(bool value) => RefreshMicIndicator();

    private void RefreshMicIndicator()
    {
        // Per primer: muted (gray) / live (green) / speaking (animated).
        // First cut surfaces as a localized emoji text; future polish can
        // bind to a per-state brush + visibility.
        if (MicIsSpeaking)
        {
            MicIndicatorText = Loc.Get("Voice_MicIndicator_Speaking", "🟢 Speaking");
            MicIndicatorVisibility = System.Windows.Visibility.Visible;
        }
        else if (MicLive)
        {
            MicIndicatorText = Loc.Get("Voice_MicIndicator_Live", "🎤 Live");
            MicIndicatorVisibility = System.Windows.Visibility.Visible;
        }
        else
        {
            MicIndicatorText = "";
            MicIndicatorVisibility = System.Windows.Visibility.Collapsed;
        }
    }
    // Phase 14-B (Tier 1) — last-known webcam state from WebcamStateUpdate
    // (Student.Agent's WebcamDeviceWatcher).  Tier 1 data plumbing only; the
    // visual chip is wired in Tier 2 alongside the actual student-side capture.
    // Sticky: last state persists until StudentLeft purges the row.
    [ObservableProperty] private bool hasWebcam;
    [ObservableProperty] private bool camLive;
    [ObservableProperty] private WebcamMode camMode = WebcamMode.Off;
    [ObservableProperty] private string camLastError = "";

    partial void OnIsHostChanged(bool value)
    {
        HostBadgeVisibility = value ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    }

    // Phase 5b: Per-student recording active
    [ObservableProperty] private bool isBeingRecorded;
    [ObservableProperty] private System.Windows.Visibility recordingDotVisibility = System.Windows.Visibility.Collapsed;
    /// <summary>True if a StudentScreenWindow is currently open for this student (REC requires it).</summary>
    [ObservableProperty] private bool canRecord;
    /// <summary>Opacity for the REC button — full when CanRecord, dim otherwise.</summary>
    [ObservableProperty] private double recOpacity = 0.4;
    /// <summary>Display label for the REC button toggle.</summary>
    [ObservableProperty] private string recButtonText = "🔴 REC";
    /// <summary>Background brush hex for REC button (red when recording, dark grey otherwise).</summary>
    [ObservableProperty] private string recButtonColorHex = "#475569";

    partial void OnIsBeingRecordedChanged(bool value)
    {
        RecordingDotVisibility = value ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
        RecButtonText = value ? "● REC" : "🔴 REC";
        RecButtonColorHex = value ? "#DC2626" : "#475569";
    }
    partial void OnCanRecordChanged(bool value)
    {
        RecOpacity = value ? 1.0 : 0.4;
    }

    // Phase 4 Part 5: Reception quality (0-100). 100 = no data yet (assume good).
    [ObservableProperty] private int qualityScore = 100;
    [ObservableProperty] private string qualityDotColorHex = "#10B981"; // green

    partial void OnQualityScoreChanged(int value)
    {
        QualityDotColorHex = value >= 90 ? "#10B981"   // green
                           : value >= 70 ? "#F59E0B"   // amber
                                         : "#EF4444"; // red
    }
}

public partial class RoomViewModel : ObservableObject
{
    [ObservableProperty] private System.Guid roomId;
    [ObservableProperty] private string roomName = "";
    [ObservableProperty] private string colorHex = "#3B82F6";

    // Phase 13-B (Tier 1) — extras populated from server's GroupDescriptor.
    /// <summary>Member endpoint ids (subset of online students; offline members
    /// silently dropped at template-load and on disconnect cleanup).</summary>
    public System.Collections.ObjectModel.ObservableCollection<System.Guid> MemberIds { get; }
        = new System.Collections.ObjectModel.ObservableCollection<System.Guid>();
    [ObservableProperty] private System.Guid? hostId;
    /// <summary>True iff teacher is currently joined to this group.</summary>
    [ObservableProperty] private bool isTeacherJoined;
    /// <summary>True iff group-targeted teacher screen share is currently targeting this group.</summary>
    [ObservableProperty] private bool isShareActive;
}

// Phase 8 (Bug D) — JSON-persisted shape of the broadcast policy. Mirrors the Current*
// fields on MainViewModel and the public getters on ApplyPolicyDialog so on-disk schema
// matches the in-memory model 1:1; deliberate non-MessagePack since this is local-only
// (no IPC) and System.Text.Json gives readable diffs for support.
internal class AppliedPolicyState
{
    public bool BlockUsbStorage { get; set; }
    public bool BlockOpticalDrive { get; set; }
    public bool BlockPrinting { get; set; }
    public List<string> BlockedProcessNames { get; set; } = new();
    public List<string> BlockedHostnames { get; set; } = new();
}