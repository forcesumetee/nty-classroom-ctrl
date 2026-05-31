using System;
using System.Collections.ObjectModel;
using System.Linq;
using ClassroomCtrl.Shared.Models;
using ClassroomCtrl.Shared.Protocol;
using ClassroomCtrl.Shared.Wpf.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ProtoChatMessage = ClassroomCtrl.Shared.Protocol.ChatMessage;
using ModelChatMessage = ClassroomCtrl.Shared.Models.ChatMessage;

namespace ClassroomCtrl.Student.Agent.ViewModels;

/// <summary>
/// Phase 16-B step 7 — student-side host VM for the Shared.Wpf Conference
/// gallery + toolbar + sidebar.  Tier 1 minimum: just enough wiring to
/// make the gallery render, the sidebar toggle work, and the End/Leave
/// button close the window.  Mic / cam / share / chat / reaction commands
/// land in Phase 16-C (peer cam) and 16-D (full role-aware shell).
///
/// This VM intentionally implements only <see cref="IConferenceSidebarHost"/>
/// at the interface level — the full <c>IConferenceShellViewModel</c>
/// contract arrives in Phase 16-D when role-gated UI lands.  Today the
/// toolbar's bindings resolve at runtime by name (WPF DataContext), so
/// commands that don't yet exist on this VM render as disabled buttons
/// in the student window — acceptable for the 16-B foundation gate.
/// </summary>
public partial class StudentConferenceShellViewModel : ObservableObject, IConferenceSidebarHost
{
    public Guid SessionId { get; }
    public Guid TeacherEndpointId { get; }

    /// <summary>Phase 16-C — student's own endpoint id, populated by MainWindow
    /// once Hello-ack lands.  Used as the self-tile EndpointId for camera
    /// preview rendering and as the self-loopback filter key.</summary>
    public Guid SelfEndpointId { get; set; }

    /// <summary>Phase 16-C — display name carried in the Start envelope.
    /// Today defaults to the local machine name; future polish round will
    /// pull the per-class roster name.</summary>
    public string SelfDisplayName { get; set; } = Environment.MachineName;

    public ConferenceGalleryViewModel ConferenceGallery { get; } = new();

    /// <summary>Phase 16-X (Bug G fix, 2026-06-01) — dedicated chat surface
    /// for the Conference sidebar.  Separate from the classic floating-
    /// window chat path so Conference chat (IsConferenceContext=true on
    /// the wire) doesn't double-display.  ConferenceSidebar XAML binds
    /// DraftInput / Messages / SendChatCommand against this conversation;
    /// MainWindow appends incoming Conference-context chats here via
    /// AppendConferenceChat.</summary>
    public Conversation ConferenceConversation { get; } = new Conversation
    {
        Id = "conference",
        DisplayName = "Conference",
        Kind = ConversationKind.Everyone,
    };

    /// <summary>Phase 16-X (Bug G fix) — append a received chat to the
    /// Conference conversation.  Called by MainWindow on inbound
    /// ChatBroadcast with IsConferenceContext=true.</summary>
    public void AppendConferenceChat(string senderName, string text,
        ClassroomCtrl.Shared.Protocol.FileAttachment? attachment = null)
    {
        ConferenceConversation.Messages.Add(new ModelChatMessage
        {
            SenderName = string.IsNullOrEmpty(senderName) ? "Participant" : senderName,
            Kind = ChatMessageKind.Teacher,
            MessageText = text,
            Attachment = attachment,
        });
    }

    // Phase 19 (v1.1) — Conference sidebar attach button state.  Mirrors the
    // Teacher VM design: metadata DTO + cached byte buffer; cleared on Send
    // or explicit dismiss.  The OpenAttachmentPicker bridge fires the
    // OpenFileDialog from MainWindow's UI thread context (App.Current
    // .Dispatcher walk in PickAttachment helper).
    [ObservableProperty] private ClassroomCtrl.Shared.Protocol.FileAttachment? draftAttachment;
    private byte[]? _draftAttachmentBytes;

    public bool HasDraftAttachment => DraftAttachment != null;
    public string DraftAttachmentLabel => DraftAttachment == null
        ? ""
        : $"{DraftAttachment.FileName} ({ClassroomCtrl.Shared.Attachments.AttachmentManager.FormatSize(DraftAttachment.FileSize)})";

    partial void OnDraftAttachmentChanged(ClassroomCtrl.Shared.Protocol.FileAttachment? value)
    {
        OnPropertyChanged(nameof(HasDraftAttachment));
        OnPropertyChanged(nameof(DraftAttachmentLabel));
    }

    /// <summary>Phase 19 — file picker invoked from the 📎 button in the
    /// Conference sidebar.  Caps at 10 MB + blocks .exe/.bat/etc; failure
    /// surfaces via best-effort LogToFile (the sidebar doesn't have a
    /// system-chat surface to dump errors into like Teacher does).</summary>
    public void OpenAttachmentPicker()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = ClassroomCtrl.Shared.Localization.Loc.Get("Chat_AttachFile_Tooltip", "Attach file"),
                Filter = "Documents|*.pdf;*.docx;*.xlsx;*.pptx;*.txt;*.rtf|" +
                         "Images|*.png;*.jpg;*.jpeg;*.gif;*.bmp;*.webp|" +
                         "Archives|*.zip;*.rar;*.7z|" +
                         "All files|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog() != true) return;

            var info = new System.IO.FileInfo(dlg.FileName);
            if (info.Length > 10 * 1024 * 1024)
            {
                IpcClient.LogToFile($"[StudentConferenceShellViewModel] Attach blocked: size {info.Length} > 10MB");
                return;
            }
            var ext = info.Extension.ToLowerInvariant();
            var blocked = new[] { ".exe", ".bat", ".scr", ".com", ".cmd", ".vbs", ".ps1", ".msi", ".dll" };
            if (System.Array.IndexOf(blocked, ext) >= 0)
            {
                IpcClient.LogToFile($"[StudentConferenceShellViewModel] Attach blocked: type {ext}");
                return;
            }

            _draftAttachmentBytes = System.IO.File.ReadAllBytes(dlg.FileName);
            DraftAttachment = new ClassroomCtrl.Shared.Protocol.FileAttachment
            {
                Id = System.Guid.NewGuid(),
                FileName = info.Name,
                FileSize = info.Length,
                FileType = ext,
                Data = System.Array.Empty<byte>(),
            };
        }
        catch (System.Exception ex)
        {
            IpcClient.LogToFile($"[StudentConferenceShellViewModel] Attach failed: {ex.Message}");
        }
    }

    public void ClearDraftAttachment()
    {
        DraftAttachment = null;
        _draftAttachmentBytes = null;
    }

    // ─────── Phase 20 (v1.1) — student-share permission state machine ───────
    //
    // Idle       → 🖥 button reads "Request to share" (default)
    // Requesting → 🖥 button reads "Requesting…" disabled-looking (toggle no-op)
    // Approved   → 🖥 button reads "Start sharing"
    // Sharing    → 🖥 button reads "Stop sharing"
    // Denied     → 🖥 button reads "Request to share" (same affordance as Idle;
    //              transient state surfaced via a system-toast hook later)
    //
    // Wire flow:
    //   Student → Teacher: ConferenceShareRequest (0x0686)
    //   Teacher → Student: ConferenceShareResponse (0x0687)
    //       Approved=true,  RevokeRequestId=null → state = Approved
    //       Approved=false, RevokeRequestId=null → state = Denied (Idle alias)
    //       Approved=false, RevokeRequestId=non-null → state = Idle  (revoke
    //                                                                 mid-share)
    //
    // Scope note: this commit ships the FLOW + STATE MACHINE + UI affordances.
    // Actual screen capture on Student side (the analog of Teacher's Phase
    // 16-B+ ScreenBroadcaster Conference path) is v1.2 scope; clicking
    // "Start sharing" transitions to Sharing + drops a system-chat line
    // documenting the gap.  Wire codes 0x0683-0x0685 are designed
    // bidirectionally so the v1.2 add is purely a Student-side
    // ScreenBroadcaster + emission wire-up, no protocol churn.
    public enum ShareRequestState { Idle, Requesting, Approved, Sharing, Denied }

    [ObservableProperty] private ShareRequestState shareState = ShareRequestState.Idle;

    /// <summary>Phase 20 (v1.1) — same property name as Teacher.MainViewModel
    /// .ShareScreenButtonText so the shared ConferenceToolbar XAML binds
    /// against either DataContext.  Returns the state-driven label on
    /// Student (Request / Requesting / Start / Stop / Request).</summary>
    public string ShareScreenButtonText => ShareState switch
    {
        ShareRequestState.Idle       => ClassroomCtrl.Shared.Localization.Loc.Get("Share_RequestButton", "Request to share"),
        ShareRequestState.Requesting => ClassroomCtrl.Shared.Localization.Loc.Get("Share_Requesting", "Requesting…"),
        ShareRequestState.Approved   => ClassroomCtrl.Shared.Localization.Loc.Get("Share_StartShareButton", "Start sharing"),
        ShareRequestState.Sharing    => ClassroomCtrl.Shared.Localization.Loc.Get("Share_StopShareButton", "Stop sharing"),
        ShareRequestState.Denied     => ClassroomCtrl.Shared.Localization.Loc.Get("Share_RequestButton", "Request to share"),
        _ => "",
    };

    /// <summary>Phase 21 (v1.1) — drives the "You are sharing" pill in the
    /// student conference window header (ConferenceGalleryWindow.xaml).
    /// True only while the broadcaster is actively emitting frames so a
    /// student who's been Approved but hasn't clicked Start yet doesn't
    /// see the indicator.</summary>
    public bool IsSharingActive => ShareState == ShareRequestState.Sharing;

    partial void OnShareStateChanged(ShareRequestState value)
    {
        OnPropertyChanged(nameof(ShareScreenButtonText));
        OnPropertyChanged(nameof(IsSharingActive));
    }

    /// <summary>Phase 20 (v1.1) — toolbar 🖥 click handler.  Single command
    /// covers all four user-driven state transitions; the no-op while
    /// Requesting prevents a double-request race against a slow teacher.</summary>
    public IRelayCommand ShareScreenCommand { get; }

    private async System.Threading.Tasks.Task ToggleShareRequestAsync()
    {
        switch (ShareState)
        {
            case ShareRequestState.Idle:
            case ShareRequestState.Denied:
                // Send request upstream.
                if (App.Ipc == null) return;
                ShareState = ShareRequestState.Requesting;
                var req = new ClassroomCtrl.Shared.Protocol.ConferenceShareRequestMessage
                {
                    RequesterEndpointId = SelfEndpointId,
                    RequesterName = string.IsNullOrWhiteSpace(SelfDisplayName) ? "Student" : SelfDisplayName,
                };
                try
                {
                    var bytes = MessagePack.MessagePackSerializer.Serialize(req);
                    // Teacher is the only recipient; envelope sender id =
                    // self so the Service / Teacher relay can attribute.
                    var env = Envelope.CreateTargeted(
                        MessageType.ConferenceShareRequest, bytes,
                        SelfEndpointId, TeacherEndpointId);
                    await App.Ipc.SendAsync(env);
                }
                catch (Exception ex)
                {
                    IpcClient.LogToFile($"[StudentConferenceShellViewModel] ShareRequest send failed: {ex.Message}");
                    // Revert state so the user can retry.
                    ShareState = ShareRequestState.Idle;
                }
                break;

            case ShareRequestState.Requesting:
                // No-op: prevent double-send.  A timeout-based reset is a
                // v1.2 polish candidate (today the teacher decision is
                // expected within seconds).
                break;

            case ShareRequestState.Approved:
                // Phase 21 (v1.1) — start the broadcaster.  ConferenceShareStart
                // envelope is emitted from inside Start() before the first
                // frame so receivers swap to share-view immediately.
                // Capture loop runs at 10 FPS MJPEG 1280x720 ≈ 200-400 kbps
                // per student (well under the 30-student fan-out budget the
                // teacher already absorbs on her Classroom share).
                ShareState = ShareRequestState.Sharing;
                try
                {
                    var name = string.IsNullOrWhiteSpace(SelfDisplayName) ? "Student" : SelfDisplayName;
                    App.ConferenceShareBroadcaster?.Start(name);
                }
                catch (Exception ex)
                {
                    IpcClient.LogToFile($"[StudentConferenceShellViewModel] Broadcaster.Start failed: {ex.Message}");
                    // Defensive: revert state so the UI doesn't lie about
                    // an active share that never started.
                    ShareState = ShareRequestState.Approved;
                }
                break;

            case ShareRequestState.Sharing:
                // Local stop — revert to Approved so the student can
                // resume without re-requesting.  Broadcaster.Stop() emits
                // ConferenceShareStop so receivers swap back to tile-mode.
                try { App.ConferenceShareBroadcaster?.Stop(); }
                catch (Exception ex)
                {
                    IpcClient.LogToFile($"[StudentConferenceShellViewModel] Broadcaster.Stop failed: {ex.Message}");
                }
                ShareState = ShareRequestState.Approved;
                IpcClient.LogToFile("[StudentConferenceShellViewModel] ShareState=Approved (stopped local share)");
                break;
        }
    }

    /// <summary>Phase 20 (v1.1) — incoming Teacher response.  Called by
    /// MainWindow's dispatch arm with the decoded payload.  Single method
    /// covers all 3 response variants (Approve / Deny / Revoke) so the
    /// state-machine semantics live in one place.</summary>
    public void OnShareResponseReceived(ClassroomCtrl.Shared.Protocol.ConferenceShareResponseMessage response)
    {
        if (response.RevokeRequestId.HasValue)
        {
            // Teacher revoked an active permission.  Phase 21 (v1.1) —
            // stop the broadcaster so frame emission halts immediately;
            // Stop() also fires ConferenceShareStop so the teacher's
            // gallery clears even though the revoke and stop cross paths
            // on the wire (the OnConferenceShareStopReceived guard already
            // tolerates a no-op stop for a non-current sharer).
            try { App.ConferenceShareBroadcaster?.Stop(); }
            catch (Exception ex)
            {
                IpcClient.LogToFile($"[StudentConferenceShellViewModel] Revoke Stop failed: {ex.Message}");
            }
            ShareState = ShareRequestState.Idle;
            IpcClient.LogToFile($"[StudentConferenceShellViewModel] ShareResponse=Revoke (id={response.RevokeRequestId})");
            return;
        }
        ShareState = response.Approved ? ShareRequestState.Approved : ShareRequestState.Denied;
        IpcClient.LogToFile($"[StudentConferenceShellViewModel] ShareResponse={(response.Approved ? "Approve" : "Deny")}");
    }

    /// <summary>Phase 16-D — Conference role.  Student is a Participant today;
    /// drives Visibility gating on the toolbar / sidebar / tile admin
    /// actions via the standard <c>Role.CanX</c> binding chain so the same
    /// Shared.Wpf XAML hides host-only controls when the DataContext is a
    /// student shell.</summary>
    public ClassroomCtrl.Shared.Wpf.Roles.IConferenceRole Role { get; } =
        new ClassroomCtrl.Shared.Wpf.Roles.ParticipantRole();

    // Placeholder Students collection so the sidebar's Participants tab
    // binds cleanly.  Phase 16-C populates this with peer state.
    public ObservableCollection<object> Students { get; } = new();

    // Phase 16-C — own-cam toggle state surfaced to the Conference toolbar.
    // IsBroadcastingCamera mirrors the StudentCameraBroadcaster.IsActive flag
    // so the toolbar's 📷 button lights up (DataTrigger in 16-B+ XAML) when
    // the student is broadcasting.
    [ObservableProperty] private bool isBroadcastingCamera;
    /// <summary>Phase 16-C — wired by ConferenceGalleryWindow ctor to invoke
    /// MainWindow.ToggleConferenceCamera() so the StudentCameraBroadcaster
    /// lifecycle is owned by the singleton on the Agent process.</summary>
    public Action? OnToggleCamera { get; set; }

    // Phase 16-X (Bug F fix, 2026-06-01) — own-mic toggle state surfaced to
    // the Conference toolbar.  Mirrors the existing _micOn flag on
    // MainWindow (Phase 4 Part 3b StudentAudioBroadcaster path).  The
    // toolbar's 🎙 button binds IsMicOn for the active-state highlight;
    // ConferenceGalleryWindow.ctor subscribes MainWindow.MicStateChanged
    // to push every state transition into this property.
    [ObservableProperty] private bool isMicOn;
    /// <summary>Phase 16-X (Bug F fix) — wired by ConferenceGalleryWindow
    /// ctor to invoke MainWindow.ToggleMicrophone() so the singleton
    /// StudentAudioBroadcaster lifecycle stays on the Agent process
    /// regardless of whether the Conference window is open.</summary>
    public Action? OnToggleMic { get; set; }

    // Phase 16-X (Bug H fix, 2026-06-01) — own-hand-raise state surfaced to
    // the Conference toolbar.  Mirrors the existing _handRaised flag on
    // MainWindow (Phase 9 Section B).  Toolbar ✋ binds IsHandRaised for
    // the amber active-state pill; ConferenceGalleryWindow.ctor pushes
    // every transition (including teacher's Recognize-driven HandLower)
    // into this property.
    [ObservableProperty] private bool isHandRaised;
    /// <summary>Phase 16-X (Bug H fix) — wired by ConferenceGalleryWindow
    /// ctor to invoke MainWindow's hand-raise toggle so the singleton
    /// _handRaised state + 0x0110 / 0x0111 wire emit stay owned by the
    /// MainWindow path.</summary>
    public Action? OnToggleHandRaise { get; set; }

    // Sidebar overlay state — mirrors the Teacher.MainViewModel shape so
    // the same ConferenceSidebar XAML works against either DataContext.
    [ObservableProperty] private bool isConferenceSidebarVisible;
    [ObservableProperty] private int conferenceSidebarTabIndex;
    public bool IsConferenceChatTabSelected         => ConferenceSidebarTabIndex == 0;
    public bool IsConferenceParticipantsTabSelected => ConferenceSidebarTabIndex == 1;

    partial void OnConferenceSidebarTabIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsConferenceChatTabSelected));
        OnPropertyChanged(nameof(IsConferenceParticipantsTabSelected));
    }

    // Sidebar / toolbar commands the shared XAML binds by name.  Phase 16-D
    // introduces IConferenceShellViewModel which both Teacher and Student
    // implement; today these are the minimal subset needed to keep the
    // 16-B gallery surface usable.
    public IRelayCommand ToggleConferenceSidebarCommand { get; }
    public IRelayCommand SelectConferenceChatTabCommand { get; }
    public IRelayCommand SelectConferenceParticipantsTabCommand { get; }
    public IRelayCommand ShowConferenceHandQueueCommand { get; }
    public IRelayCommand EndConferenceCommand { get; }

    /// <summary>Phase 16-C — student's own cam toggle.  The toolbar's 📷
    /// button binds this; the actual lifecycle (device pick, AForge start,
    /// IPC emit) lives in MainWindow via <see cref="OnToggleCamera"/>.</summary>
    public IRelayCommand ToggleCameraCommand { get; }

    /// <summary>Phase 16-X (Bug F fix) — student's own mic toggle.  The
    /// toolbar's 🎙 button binds this; the actual lifecycle
    /// (StudentAudioBroadcaster start/stop + IPC emit) lives in MainWindow
    /// via <see cref="OnToggleMic"/>.</summary>
    public IRelayCommand ToggleMicCommand { get; }

    /// <summary>Phase 16-X (Bug H fix) — student's own hand-raise toggle.
    /// Shared command name with Teacher.MainViewModel.RaiseHandCommand so
    /// the same toolbar XAML works against either DataContext; semantics
    /// differ — teacher's just opens the queue, student's actually raises
    /// or lowers the hand + emits a 0x0110 / 0x0111 wire envelope.</summary>
    public IRelayCommand RaiseHandCommand { get; }

    /// <summary>Phase 16-X (Bug G fix) — Conference sidebar chat send.
    /// Shared command name with Teacher.MainViewModel.SendConferenceChatCommand
    /// so the ConferenceSidebar XAML binds the same name on either side.
    /// Stamps IsConferenceContext=true on the wire so receivers route only
    /// to their Conference chat panel (not Classroom rail).</summary>
    public IRelayCommand SendConferenceChatCommand { get; }

    /// <summary>Phase 16-X (Bug D fix, 2026-05-31) — student-side reaction
    /// send.  Toolbar's ⋮ More popup resolves SendReactionCommand by name
    /// via reflection in ConferenceToolbar.ReactionButton_Click — without
    /// this command the student's emoji clicks silently no-op'd.
    /// Optimistically renders on the student's own self-tile + emits a
    /// 0x0674 ReactionMessage envelope upstream; teacher relays to every
    /// peer in the same Conference (including back to the sender, which
    /// MainWindow filters via env.SenderId == _myEndpointId).</summary>
    public IRelayCommand<string?> SendReactionCommand { get; }

    /// <summary>Phase 16-B step 7 — fired when the student clicks End / Leave.
    /// ConferenceGalleryWindow subscribes and closes itself.</summary>
    public event EventHandler? RequestClose;

    public StudentConferenceShellViewModel(Guid sessionId, Guid teacherEndpointId, string hostName)
    {
        SessionId = sessionId;
        TeacherEndpointId = teacherEndpointId;

        // Seed the gallery with the teacher's tile so the very first frame
        // can land on the right participant slot.  Phase 16-C peer cam
        // wiring adds tiles for other students as their
        // ConferenceCameraStart envelopes arrive.
        ConferenceGallery.Tiles.Add(new ConferenceTileViewModel(
            teacherEndpointId,
            string.IsNullOrWhiteSpace(hostName) ? "Host" : hostName,
            isSelf: false));

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
        SelectConferenceChatTabCommand = new RelayCommand(() =>
        {
            if (IsConferenceSidebarVisible) ConferenceSidebarTabIndex = 0;
        });
        SelectConferenceParticipantsTabCommand = new RelayCommand(() =>
        {
            if (IsConferenceSidebarVisible) ConferenceSidebarTabIndex = 1;
        });
        ShowConferenceHandQueueCommand = new RelayCommand(() =>
        {
            ConferenceSidebarTabIndex = 1;
            IsConferenceSidebarVisible = true;
        });
        // Student "End" semantically means Leave for now (16-D adds the role
        // model that flips the label + verb).  Just close our window.
        EndConferenceCommand = new RelayCommand(() => RequestClose?.Invoke(this, EventArgs.Empty));

        // Phase 20 (v1.1) — share-screen toolbar binding.  Async fire-and-
        // forget; ToggleShareRequestAsync handles its own state transitions
        // + error logging so the RelayCommand callback stays trivial.
        ShareScreenCommand = new RelayCommand(() => _ = ToggleShareRequestAsync());

        // Phase 16-C — own-cam toggle routes through MainWindow so the
        // singleton broadcaster + device-selection dialog stay on the Agent
        // process even when the Conference window is closed/re-opened.
        ToggleCameraCommand = new RelayCommand(() => OnToggleCamera?.Invoke());

        // Phase 16-X (Bug F fix) — own-mic toggle delegates to MainWindow's
        // existing ToggleMicrophone (Phase 4 Part 3b StudentAudioBroadcaster
        // path).  ConferenceGalleryWindow wires both OnToggleMic + the
        // MicStateChanged subscription so the VM observable + self-tile
        // mic indicator stay in sync with every transition.
        ToggleMicCommand = new RelayCommand(() => OnToggleMic?.Invoke());

        // Phase 16-X (Bug H fix) — own-hand toggle delegates to
        // MainWindow.ToggleConferenceHandRaise (which wraps the existing
        // Phase 9 RaiseHand_Click logic).  The shell VM's IsHandRaised
        // observable is pushed by MainWindow on every transition (toolbar
        // click, classic floating-window click, teacher Recognize) so the
        // amber pill on the ✋ button + self-tile badge stay in sync.
        RaiseHandCommand = new RelayCommand(() => OnToggleHandRaise?.Invoke());

        // Phase 16-X (Bug D fix) — student-side reaction emit.  Optimistic
        // local render first (so the user sees the emoji float-up
        // immediately without the wire round-trip), then upstream emit.
        // The teacher's ControlServer.OnMessage Reaction arm re-broadcasts
        // to every peer in the same Conference; the sender's
        // MainWindow.OnIpcMessage Reaction arm filters its own echo via
        // env.SenderId == _myEndpointId so it doesn't double-render.
        SendReactionCommand = new RelayCommand<string?>(async emoji =>
        {
            if (string.IsNullOrEmpty(emoji)) return;

            // Optimistic render — find the self-tile and animate.  The
            // ConferenceTile XAML's CurrentReactionEmoji binding triggers
            // the float-up Storyboard.  No clear timer here; the tile's
            // own animation reset wires that in 15-E step 5.
            var selfId = SelfEndpointId;
            if (selfId != Guid.Empty)
            {
                var tile = ConferenceGallery.Tiles.FirstOrDefault(t => t.EndpointId == selfId);
                if (tile != null) tile.CurrentReactionEmoji = emoji;
            }

            // Wire emit.  App.Ipc is initialized in App.OnStartup; only
            // null if the IPC pipe to Student.Service never came up.
            if (App.Ipc == null) return;
            var msg = new ReactionMessage
            {
                Emoji = emoji,
                ExpiresAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 3000,
            };
            try
            {
                var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
                var env = Envelope.Create(MessageType.Reaction, bytes, Guid.Empty);
                await App.Ipc.SendAsync(env);
            }
            catch (Exception ex)
            {
                IpcClient.LogToFile($"[StudentConferenceShellViewModel] SendReaction failed: {ex.Message}");
            }
        });

        // Phase 16-X (Bug G fix) — Conference chat send.  Reads DraftInput
        // from ConferenceConversation, locally appends a Me-bubble, then
        // emits a ChatBroadcast envelope upstream with IsConferenceContext
        // = true.  Service's OnAgentMessage is a pure forwarder (no switch)
        // so no Service-side change required; Teacher routes to its own
        // ConferenceConversation via the IsConferenceContext filter in
        // OnChatReceived.
        SendConferenceChatCommand = new RelayCommand(async () =>
        {
            var text = (ConferenceConversation.DraftInput ?? "").Trim();
            // Phase 19 (v1.1) — allow attachment-only sends (no caption text).
            if (string.IsNullOrEmpty(text) && DraftAttachment == null) return;
            ConferenceConversation.DraftInput = "";

            // Phase 19 — fill the attachment Data slot at Send time + persist
            // to local cache so the sender's own Open button on the
            // optimistic bubble works without a wire echo.
            ClassroomCtrl.Shared.Protocol.FileAttachment? attach = null;
            if (DraftAttachment != null && _draftAttachmentBytes != null)
            {
                attach = DraftAttachment;
                attach.Data = _draftAttachmentBytes;
                if (App.Attachments != null)
                {
                    try { await App.Attachments.SaveAsync(attach).ConfigureAwait(true); } catch { }
                }
            }

            // Optimistic local Me-bubble — broadcast doesn't echo to sender.
            ConferenceConversation.Messages.Add(new ModelChatMessage
            {
                SenderName = "Me",
                Kind = ChatMessageKind.Student,
                MessageText = text,
                Attachment = attach,
            });

            ClearDraftAttachment();

            if (App.Ipc == null) return;
            var msg = new ProtoChatMessage
            {
                SenderId = SelfEndpointId,
                SenderName = string.IsNullOrWhiteSpace(SelfDisplayName) ? "Student" : SelfDisplayName,
                Text = text,
                TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                IsConferenceContext = true,
                Attachment = attach,
            };
            try
            {
                var bytes = MessagePack.MessagePackSerializer.Serialize(msg);
                var env = Envelope.Create(MessageType.ChatBroadcast, bytes, Guid.Empty);
                await App.Ipc.SendAsync(env);
            }
            catch (Exception ex)
            {
                IpcClient.LogToFile($"[StudentConferenceShellViewModel] SendConferenceChat failed: {ex.Message}");
            }
        });
    }
}
