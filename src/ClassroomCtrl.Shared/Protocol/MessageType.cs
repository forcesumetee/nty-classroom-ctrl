namespace ClassroomCtrl.Shared.Protocol;

public enum MessageType : ushort
{
    // System (0x0000-0x00FF)
    Hello = 0x0001,
    HelloAck = 0x0002,
    Heartbeat = 0x0003,
    Goodbye = 0x0004,

    // Phase 10.10 Fix 7 — application-level keepalive.  Heartbeat (0x0003) is
    // legacy/unused; new ping/pong machinery lives at 0x0005/0x0006 to avoid
    // any chance of clashing with stray code that still emits 0x0003.
    Ping = 0x0005,
    Pong = 0x0006,

    // Chat (0x0100-0x01FF)
    ChatBroadcast = 0x0100,
    ChatDirect = 0x0101,
    ChatRoom = 0x0102,
    ChatReaction = 0x0103,
    HandRaise = 0x0110,
    HandLower = 0x0111,

    // File (0x0200-0x02FF)
    FileAnnounce = 0x0200,
    FileChunk = 0x0201,
    FileChunkAck = 0x0202,
    FileComplete = 0x0203,

    // Control (0x0300-0x03FF)
    LockScreen = 0x0300,
    UnlockScreen = 0x0301,
    ForceShutdown = 0x0302,
    ForceRestart = 0x0303,
    ForceLogoff = 0x0304,
    RemoteInput = 0x0310,
    RequestScreenshot = 0x0320,
    ScreenshotResponse = 0x0321,


    // Screen sharing (broadcast / streaming)
    ScreenStreamStart = 0x0322,
    ScreenStreamFrame = 0x0323,
    ScreenStreamStop = 0x0324,

    // Phase 4 Part 2: Student → Teacher view (on-demand)
    StudentStreamStart = 0x0325,   // T→S: command student to start streaming
    StudentStreamFrame = 0x0326,   // S→T: student sends own screen frame
    StudentStreamStop = 0x0327,    // T→S: command student to stop

    // Phase 4 Part 3a: Teacher → Students audio broadcast (one-way)
    AudioStreamStart = 0x0328,
    AudioStreamFrame = 0x0329,
    AudioStreamStop = 0x032A,

    // Phase 4 Part 3b: Student → Teacher audio talkback (S→T)
    StudentAudioStreamStart = 0x032B,
    StudentAudioStreamFrame = 0x032C,
    StudentAudioStreamStop = 0x032D,

    // Phase 4 Part 3c: Teacher master controls
    ForceMuteStudentMic = 0x032E,  // T→S broadcast: students with mic on must turn off

    // Phase 4 Part 5: Adaptive bitrate feedback
    ScreenStreamQualityReport = 0x0331,  // S→T: per-student reception quality every ~2 sec

    // Policy (0x0400-0x04FF)
    PolicyApply = 0x0400,
    PolicyRevert = 0x0401,
    PolicyStatus = 0x0402,
    /// <summary>Phase 5b: Teacher → Student PDPA notification while per-student screen recording is active.</summary>
    StudentRecordingNotify = 0x0403,
    /// <summary>Phase 6.6: Teacher → Student PDPA notification immediately after a screenshot is captured.</summary>
    StudentScreenshotNotify = 0x0404,

    // Media signaling (0x0500-0x05FF)
    MediaOffer = 0x0500,
    MediaAnswer = 0x0501,
    MediaIceCandidate = 0x0502,
    MediaStop = 0x0503,

    // Breakout (0x0600-0x06FF)
    BreakoutCreate = 0x0600,
    BreakoutAssign = 0x0601,
    BreakoutDissolve = 0x0602,
    BreakoutHostSet = 0x0603,

    // Phase 8.5: Host actions inside a breakout room (Host → Teacher relay)
    HostActionMute = 0x0610,             // Host wants to mute a specific peer in same room
    HostActionShare = 0x0611,            // Host wants to share screen (gated by permission)
    HostActionMessageToMain = 0x0612,    // Host wants to send a message to main classroom

    // Phase 9.1: Student Demonstration (Zoom-style spotlight) (0x0440-0x044F)
    DemoStart = 0x0440,    // T→all: tells students to open peer-view window for source
    DemoStop = 0x0441,     // T→all: close peer-view window
    DemoFrame = 0x0442,    // T→all (relayed): one frame from the source student

    // Phase 9.2: Screen Pen — annotation overlay (0x0450-0x045F)
    DrawingStroke = 0x0450,  // T→all: vector stroke (points + color + thickness)
    DrawingClear = 0x0451,   // T→all: clear all strokes
    DrawingUndo = 0x0452,    // T→all: remove last stroke

    // Phase 9.5: Camera Broadcast (0x0460-0x046F)
    CameraStart = 0x0460,
    CameraFrame = 0x0461,
    CameraStop = 0x0462,

    // Phase 9.6: Net Movie sync playback (0x0470-0x047F)
    MoviePlay = 0x0470,
    MoviePause = 0x0471,
    MovieSeek = 0x0472,
    MovieStop = 0x0473,

    // Phase 6.5: Remote Control (0x0480-0x048F)
    RemoteControlStart = 0x0480,
    RemoteControlEnd = 0x0481,
    RemoteMouseMove = 0x0482,
    RemoteMouseClick = 0x0483,
    RemoteMouseScroll = 0x0484,
    RemoteKey = 0x0485,
    // Phase 12-C step 2: composed Unicode text (Thai/IME).  Sent in addition
    // to RemoteKey VK events for non-control, non-shortcut printable input —
    // see StudentScreenWindow.OnRemoteTextInput for the de-dupe rule.
    RemoteText = 0x0486,

    // Phase 4.6: Live Mic Monitor (0x0490-0x049F)
    MicMonitorStart = 0x0490,
    MicMonitorStop = 0x0491,

    // Phase 9.7 placeholders RoomVoiceJoin (0x032F) / RoomVoiceLeave (0x0330)
    // removed in Phase 13-B step 1: never reached production wiring; Tier 3
    // group voice uses fresh codepoints in 0x064x.

    // Phase 13: Exam System (0x0700-0x07FF)
    QuizStart = 0x0700,         // T→S: includes Exam payload + locks student in
    QuizAnswerSubmit = 0x0701,  // S→T: student answers
    QuizEnd = 0x0702,           // T→S broadcast: exam ended, unlock everyone
    QuizUnlockEarly = 0x0703,   // T→S targeted: unlock one early

    // Phase 13-B (Tier 1): Breakout Rooms — group lifecycle + teacher join/leave +
    // group-targeted teacher screen share.  Routes through the existing 4 per-peer
    // channels (no new transport channel for Tier 1).  Group-state and signaling
    // ride _reliableOutbox; frame fan-out rides _outbox (same DropOldest cap-16 as
    // whole-class screen share).  See docs/breakout-rooms-architecture.md §4.
    GroupSnapshot          = 0x0620,   // T→all, atomic state refresh
    GroupTeacherJoined     = 0x0621,   // T→all
    GroupTeacherLeft       = 0x0622,   // T→all
    GroupScreenStreamStart = 0x0623,   // T→group
    GroupScreenStreamFrame = 0x0624,   // T→group (reuses ScreenStreamFrameMessage payload)
    GroupScreenStreamStop  = 0x0625,   // T→group

    // Phase 13-C (Tier 2): student↔student in-group screen share — presenter
    // mode (the host of a group broadcasts to other in-group members).  Star
    // topology: host → teacher (relay) → group peers != host.  Frames reuse
    // ScreenStreamFrameMessage payload, just a different MessageType + the
    // TargetGroupId envelope field set for the receiver-side IsForMe filter.
    StudentGroupScreenStreamStart = 0x0630,   // host→T→group peers
    StudentGroupScreenStreamFrame = 0x0631,   // host→T→group peers (reuses ScreenStreamFrameMessage)
    StudentGroupScreenStreamStop  = 0x0632,   // host→T→group peers

    // Phase 13-D (Tier 3): per-group voice chat.  PTT default, push-to-talk
    // serialization is the primary echo mitigation (Layer 1 of 3-layer AEC
    // strategy); Layer 2 = WASAPI AEC on Communications-role capture; Layer 3
    // = "USB headset recommended" doc.  Star topology (teacher relay) reuses
    // the Tier 2 screen pattern; voice rides a dedicated _voiceOutbox so
    // teacher loopback audio (_audioOutbox) isn't evicted by voice bursts.
    VoiceAudioFrame  = 0x0640,   // student→T→group peers != sender, lossy via _voiceOutbox
    MicMuteRequest   = 0x0641,   // T→S targeted, reliable
    MicStateUpdate   = 0x0642,   // S→T heartbeat / on-change, reliable
    MicPttSet        = 0x0643,   // T→S targeted, reliable — sets PTT vs always-on mode

    // Phase 14-B (Tier 1): Conference Mode — teacher webcam broadcast.  Closes
    // Phase 9.5 UX gaps around the existing CameraStart/Frame/Stop
    // (0x0460-0x0462) by adding bidirectional cam-state visibility so the
    // teacher UI knows which students have a webcam (data plumbing only in
    // Tier 1; Tier 2 lights it up).  Reliable channel; small + must arrive.
    WebcamStateUpdate = 0x0650,   // S→T heartbeat / on-change, reliable

    // Phase 15-B (MVP): Conference Mode — session lifecycle.  Reliable; small;
    // must arrive in order.  Routes via _reliableOutbox.  Teacher's Start
    // CTA emits 0x0670 with a per-session Guid that's reused as
    // TargetGroupId for mic / cam frame routing within the conference
    // (mode-exclusive with 13-D group voice — see
    // docs/conference-mode-ux-architecture.md § 5 risk #10).
    ConferenceStart = 0x0670,   // T→all, ConferenceStartMessage
    ConferenceEnd   = 0x0671,   // T→all, empty payload

    // Phase 15-E (polish): Conference Mode — transient emoji reactions.
    // HandRaise / HandLower intentionally reuse the existing 0x0110 / 0x0111
    // dispatch (Classroom-side foundation already maintains StudentInfo
    // .IsHandRaised + ControlServer.HandRaiseReceived) so Conference hand
    // state is a single source of truth shared with the Classroom bell
    // badge.  Reactions are new because no equivalent classroom feature
    // exists — small reliable broadcast, ~1 KB max payload.
    Reaction = 0x0674,          // S↔T, reliable, ReactionMessage

    // Phase 16-B+ (in-frame Conference share): mode-separated screen-share
    // for Conference Mode.  Classroom 0x0322-0x0324 ScreenStream* stays
    // full-takeover (StudentScreenWindow fullscreen + remote-control wired);
    // these new codes carry a Zoom/Meet-style in-frame share that lives
    // inside ConferenceShareView — large primary tile + tiles filmstrip.
    // SourceEndpointId in payload + fan-out by teacher relay to all
    // in-Conference participants except the source.  Frames lossy via
    // _outbox; Start / Stop reliable via _reliableOutbox.
    ConferenceShareStart = 0x0683,   // src→T→participants, ConferenceShareStartMessage
    ConferenceShareFrame = 0x0684,   // src→T→participants, ConferenceShareFrameMessage
    ConferenceShareStop  = 0x0685,   // src→T→participants, ConferenceShareStopMessage

    // Phase 16-C (Tier 2): Conference Mode — peer cam routing.  Each
    // participant emits ConferenceCameraStart/Frame/Stop with their own
    // EndpointId in Envelope.SenderId; teacher acts as star-topology relay
    // and fans out to all in-Conference peers != sender.  Self-loopback
    // filter mirrors Phase 13-D voice (env.SenderId == _myEndpointId).
    // Classroom 0x0460-0x0462 stays unidirectional Teacher→student for the
    // 9.5 cam-broadcast UX (pop-up cam window, NOT Conference gallery);
    // mixing roles on one wire code would force every dispatch arm to
    // discriminate by source.  Lossy channel: rides _outbox (DropOldest
    // cap-16), same drop semantics as 0x0461.
    ConferenceCameraStart = 0x0680,   // S→T→peers, ConferenceCameraStartMessage
    ConferenceCameraFrame = 0x0681,   // S→T→peers, ConferenceCameraFrameMessage
    ConferenceCameraStop  = 0x0682,   // S→T→peers, ConferenceCameraStopMessage
}
