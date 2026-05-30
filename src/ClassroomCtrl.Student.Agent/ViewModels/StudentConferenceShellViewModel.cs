using System;
using System.Collections.ObjectModel;
using System.Linq;
using ClassroomCtrl.Shared.Protocol;
using ClassroomCtrl.Shared.Wpf.ViewModels;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
    }
}
