namespace ClassroomCtrl.Shared.Protocol;

public enum MessageType : ushort
{
    // System (0x0000-0x00FF)
    Hello = 0x0001,
    HelloAck = 0x0002,
    Heartbeat = 0x0003,
    Goodbye = 0x0004,

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

    // Phase 4.6: Live Mic Monitor (0x0490-0x049F)
    MicMonitorStart = 0x0490,
    MicMonitorStop = 0x0491,

    // Phase 9.7: Voice Chat in Breakout Rooms (reuse 0x032F-0x0330)
    RoomVoiceJoin = 0x032F,
    RoomVoiceLeave = 0x0330,

    // Phase 13: Exam System (0x0700-0x07FF)
    QuizStart = 0x0700,         // T→S: includes Exam payload + locks student in
    QuizAnswerSubmit = 0x0701,  // S→T: student answers
    QuizEnd = 0x0702,           // T→S broadcast: exam ended, unlock everyone
    QuizUnlockEarly = 0x0703,   // T→S targeted: unlock one early
}
