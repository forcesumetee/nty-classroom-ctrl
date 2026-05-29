using MessagePack;

namespace ClassroomCtrl.Shared.Protocol;

[MessagePackObject]
public class HelloMessage
{
    [Key(0)] public string MachineName { get; set; } = "";
    [Key(1)] public string DisplayName { get; set; } = "";
    [Key(2)] public string OsVersion { get; set; } = "";
    [Key(3)] public int ProtocolVersion { get; set; }
    [Key(4)] public Guid EndpointId { get; set; }
}

[MessagePackObject]
public class ChatMessage
{
    [Key(0)] public Guid SenderId { get; set; }
    [Key(1)] public string SenderName { get; set; } = "";
    [Key(2)] public Guid? RecipientId { get; set; } // null = broadcast
    [Key(3)] public Guid? RoomId { get; set; }
    [Key(4)] public string Text { get; set; } = "";
    [Key(5)] public long TimestampUtcMs { get; set; }
}

[MessagePackObject]
public class HandRaiseMessage
{
    [Key(0)] public Guid StudentId { get; set; }
    [Key(1)] public string StudentName { get; set; } = "";
    [Key(2)] public bool IsRaised { get; set; }
}

[MessagePackObject]
public class FileAnnounceMessage
{
    [Key(0)] public Guid TransferId { get; set; }
    [Key(1)] public string FileName { get; set; } = "";
    [Key(2)] public long SizeBytes { get; set; }
    [Key(3)] public string Sha256Hex { get; set; } = "";
    [Key(4)] public int ChunkCount { get; set; }
    [Key(5)] public bool UseMulticast { get; set; }
}

[MessagePackObject]
public class RemoteInputMessage
{
    [Key(0)] public byte InputKind { get; set; } // 0=mouse-move, 1=mouse-button, 2=mouse-wheel, 3=key-down, 4=key-up
    [Key(1)] public int X { get; set; }
    [Key(2)] public int Y { get; set; }
    [Key(3)] public int ButtonOrKey { get; set; }
    [Key(4)] public int WheelDelta { get; set; }
}

[MessagePackObject]
public class PolicyApplyMessage
{
    [Key(0)] public bool BlockUsbStorage { get; set; }
    [Key(1)] public bool BlockOpticalDrive { get; set; }
    [Key(2)] public bool BlockPrinting { get; set; }
    [Key(3)] public List<string> BlockedProcessNames { get; set; } = new();
    [Key(4)] public List<string> BlockedHostnames { get; set; } = new();
    /// <summary>
    /// Unix milliseconds when this policy auto-reverts.
    /// 0 = no expiry (persistent until explicit revert).
    /// </summary>
    [Key(5)] public long ExpiresAtUtcMs { get; set; }
}

[MessagePackObject]
public class BreakoutAssignMessage
{
    [Key(0)] public Guid RoomId { get; set; }
    [Key(1)] public string RoomName { get; set; } = "";
    [Key(2)] public List<Guid> StudentIds { get; set; } = new();
    [Key(3)] public Guid? HostStudentId { get; set; }
}

// Phase 13-B step 1 — wire previously-dead Breakout messages so the protocol
// stays grep-friendly (atomic refresh still rides GroupSnapshot in Tier 1;
// these explicit deltas just make logs/audits useful).

[MessagePackObject]
public class BreakoutCreateMessage
{
    [Key(0)] public Guid GroupId { get; set; }
    [Key(1)] public string Name { get; set; } = "";
}

[MessagePackObject]
public class BreakoutDissolveMessage
{
    /// <summary>Dissolve a specific group; if empty, dissolve all.</summary>
    [Key(0)] public Guid GroupId { get; set; }
}

[MessagePackObject]
public class BreakoutHostSetMessage
{
    [Key(0)] public Guid GroupId { get; set; }
    /// <summary>Null = clear host.</summary>
    [Key(1)] public Guid? HostStudentId { get; set; }
}

// Phase 13-B step 2 — Tier 1 Breakout Rooms DTOs.  See
// docs/breakout-rooms-architecture.md §4 and tier1-design.md §3 for the spec.

[MessagePackObject]
public class GroupDescriptor
{
    [Key(0)] public Guid Id { get; set; }
    [Key(1)] public string Name { get; set; } = "";
    [Key(2)] public List<Guid> MemberIds { get; set; } = new();
    [Key(3)] public Guid? HostId { get; set; }
}

/// <summary>Atomic group-state refresh.  Sent on every mutation and on
/// Hello-ack to a joining peer (recovery path after teacher restart or late
/// joiner).  Clients replace their local group view wholesale on receipt;
/// per-student BreakoutAssign deltas remain for grep-friendly logs but the
/// snapshot is authoritative.</summary>
[MessagePackObject]
public class GroupSnapshotMessage
{
    [Key(0)] public List<GroupDescriptor> Groups { get; set; } = new();
    /// <summary>Which group (if any) the teacher is currently joined to.
    /// Null = teacher is viewing the whole class.</summary>
    [Key(1)] public Guid? TeacherJoinedGroupId { get; set; }
}

[MessagePackObject]
public class GroupTeacherJoinedMessage
{
    [Key(0)] public Guid GroupId { get; set; }
    [Key(1)] public string GroupName { get; set; } = "";
}

[MessagePackObject]
public class GroupTeacherLeftMessage
{
    [Key(0)] public Guid GroupId { get; set; }
}

/// <summary>Carries the start/stop signal for group-targeted teacher screen
/// share.  Codec is included so the receiver can configure its decoder before
/// frames arrive (matches the existing whole-class ScreenStreamStart path).
/// GroupScreenStreamFrame envelopes carry an unchanged ScreenStreamFrameMessage
/// payload — only the MessageType + Envelope.TargetGroupId differ.</summary>
[MessagePackObject]
public class GroupScreenStreamControlMessage
{
    [Key(0)] public Guid GroupId { get; set; }
    [Key(1)] public bool Start { get; set; }
    [Key(2)] public VideoCodec Codec { get; set; }
}

/// <summary>
/// Phase 13-C (Tier 2) — start/stop signal for the host-presenter's group
/// screen broadcast.  PresenterId + PresenterName let the GroupPeerView label
/// the stream ("Presenting: {name} · {group}").  Frames carry the unchanged
/// ScreenStreamFrameMessage payload; only the MessageType
/// (StudentGroupScreenStream{Start,Frame,Stop}) and the Envelope.TargetGroupId
/// differ.  Sent by the host's StudentBroadcaster (S→T) and relayed by the
/// teacher to in-group peers != PresenterId.
/// </summary>
[MessagePackObject]
public class StudentGroupScreenStreamControlMessage
{
    [Key(0)] public Guid GroupId { get; set; }
    [Key(1)] public Guid PresenterId { get; set; }
    [Key(2)] public string PresenterName { get; set; } = "";
    [Key(3)] public bool Start { get; set; }
    [Key(4)] public VideoCodec Codec { get; set; }
}

// ───────────── Phase 13-D (Tier 3): per-group voice chat DTOs ─────────────

/// <summary>
/// Phase 13-D (Tier 3) — one mic-captured audio frame from a student.  PCM is
/// 16 kHz mono 16-bit little-endian, 100ms frames (≈ 3200 bytes).
/// SourceEndpointId + GroupId are also in Envelope.SenderId / TargetGroupId;
/// embedding them in the payload keeps diagnostics grep-friendly and lets the
/// VoiceMixer keep per-source state even if envelope SenderId is rewritten.
/// </summary>
[MessagePackObject]
public class VoiceAudioFrameMessage
{
    [Key(0)] public Guid SourceEndpointId { get; set; }
    [Key(1)] public Guid GroupId { get; set; }
    [Key(2)] public byte[] Pcm { get; set; } = Array.Empty<byte>();
    [Key(3)] public long Ts { get; set; }
}

/// <summary>
/// Phase 13-D (Tier 3) — teacher → student force-mute (or unmute).  Reason is
/// shown in a balloon notification on the student so the mute isn't silent.
/// </summary>
[MessagePackObject]
public class MicMuteRequestMessage
{
    [Key(0)] public Guid TargetEndpointId { get; set; }
    [Key(1)] public bool Muted { get; set; }
    [Key(2)] public string Reason { get; set; } = "";
}

/// <summary>
/// Phase 13-D (Tier 3) — student → teacher mic-state heartbeat.  Sent every
/// 1-2 seconds and on any state change.  Drives the teacher-side per-student
/// mic indicator.
/// </summary>
[MessagePackObject]
public class MicStateUpdateMessage
{
    [Key(0)] public Guid EndpointId { get; set; }
    [Key(1)] public bool MicLive { get; set; }   // true while capturing + emitting
    [Key(2)] public bool PttMode { get; set; }   // true = hold-key, false = always-on
    [Key(3)] public bool IsSpeaking { get; set; } // RMS > threshold over last frame
}

/// <summary>
/// Phase 13-D (Tier 3) — teacher → student set PTT vs always-on mode.
/// HotkeyVk is the Win32 virtual-key code for the PTT key (default 0x20 = VK_SPACE).
/// </summary>
[MessagePackObject]
public class MicPttSetMessage
{
    [Key(0)] public Guid TargetEndpointId { get; set; }
    [Key(1)] public bool PttMode { get; set; }
    [Key(2)] public ushort HotkeyVk { get; set; }
}

// ───────────── Phase 14-B (Tier 1): Conference Mode ─────────────

/// <summary>
/// Phase 14-B (Tier 1) — student → teacher webcam-state heartbeat.  Sent at
/// agent startup, on WMI device-arrival/removal events (cam plug/unplug),
/// and on any local toggle.  Drives the teacher-side per-student cam
/// indicator + lets the teacher UI know which students will receive
/// teacher-cam broadcasts.
///
/// In Tier 1 the student never *captures* a cam — DeviceAvailable reflects
/// presence (via Win32_PnPEntity), CamLive is always false, Mode is always
/// Off.  Tier 2 lights CamLive / Mode up when StudentCameraBroadcaster lands.
/// </summary>
[MessagePackObject]
public class WebcamStateUpdateMessage
{
    [Key(0)] public bool DeviceAvailable { get; set; }
    [Key(1)] public bool CamLive { get; set; }
    [Key(2)] public WebcamMode Mode { get; set; }
    [Key(3)] public string LastError { get; set; } = "";
}

/// <summary>
/// Phase 14-B (Tier 1) — student-side webcam capture mode.  Mirrors
/// Phase 13-D's mic-mode shape (Off / PTT / AlwaysOn).  Tier 1 only ever
/// reports Off; Tier 2 wires PTT + AlwaysOn for actual student cam.
/// </summary>
public enum WebcamMode : byte { Off = 0, Ptt = 1, AlwaysOn = 2 }

// ───────────── Phase 15-B (MVP): Conference Mode ─────────────

/// <summary>
/// Phase 15-B (MVP) — teacher → all students conference-session start.  Carries
/// a per-session <see cref="SessionId"/> Guid that students cache and reuse as
/// the TargetGroupId for any conference-scoped mic / cam frame routing in
/// Phase 15-C/D/E.  Reliable; small; must arrive in order.
///
/// HostName is the teacher's display name pulled from the active branding /
/// telemetry settings — surfaced verbatim in the student-side
/// ConferenceGalleryWindow header (e.g. "Hosted by {HostName}").
/// StartedAtMs is the UTC wall-clock anchor for the elapsed-time chip;
/// students use Math.Max(0, now - StartedAtMs) to compute their own elapsed
/// independent of clock drift between machines.
/// </summary>
[MessagePackObject]
public class ConferenceStartMessage
{
    [Key(0)] public Guid SessionId { get; set; }
    [Key(1)] public string HostName { get; set; } = "";
    [Key(2)] public long StartedAtMs { get; set; }
}

// ───────────── File transfer DTOs (Spec §6.4) ─────────────

[MessagePackObject]
public class FileChunkMessage
{
    [Key(0)] public Guid TransferId { get; set; }
    [Key(1)] public int ChunkIndex { get; set; }
    [Key(2)] public byte[] Data { get; set; } = Array.Empty<byte>();
}

[MessagePackObject]
public class FileCompleteMessage
{
    [Key(0)] public Guid TransferId { get; set; }
    [Key(1)] public string FileName { get; set; } = "";
}

// ───────────── Screen thumbnail DTOs (Spec §6.6) ─────────────

[MessagePackObject]
public class ScreenshotResponseMessage
{
    [Key(0)] public Guid StudentId { get; set; }
    [Key(1)] public byte[] JpegData { get; set; } = Array.Empty<byte>();
    [Key(2)] public int Width { get; set; }
    [Key(3)] public int Height { get; set; }
    [Key(4)] public long CapturedAtUtcMs { get; set; }
}


/// <summary>Phase 4 Part 4: Video codec selector.</summary>
public enum VideoCodec : int
{
    Mjpeg = 0,
    H264 = 1,
}

[MessagePackObject]
public class ScreenStreamFrameMessage
{
    /// <summary>Encoded frame bytes — JPEG payload for Mjpeg, H.264 NAL units (Annex B) for H264.</summary>
    [Key(0)] public byte[] FrameData { get; set; } = Array.Empty<byte>();
    [Key(1)] public int Width { get; set; }
    [Key(2)] public int Height { get; set; }
    [Key(3)] public long TimestampUtcMs { get; set; }
    [Key(4)] public int FrameSeq { get; set; }
    [Key(5)] public VideoCodec Codec { get; set; } = VideoCodec.Mjpeg;
    /// <summary>True if frame can be decoded standalone (always true for MJPEG; IDR for H.264).</summary>
    [Key(6)] public bool IsKeyframe { get; set; } = true;
}

/// <summary>Phase 4 Part 4: Payload of StudentStreamStart — tells student which codec to encode in.</summary>
[MessagePackObject]
public class StudentStreamStartRequest
{
    [Key(0)] public VideoCodec Codec { get; set; } = VideoCodec.Mjpeg;
}

/// <summary>
/// Phase 4 Part 5: Per-interval reception quality report from a student to the teacher.
/// Sent every ~2 seconds while the screen viewer is open. The teacher's adaptive
/// bitrate controller aggregates reports across students and chooses a single
/// broadcast bitrate driven by the slowest receiver.
/// </summary>
[MessagePackObject]
public class ScreenStreamQualityReportMessage
{
    [Key(0)] public int FramesExpected { get; set; }
    [Key(1)] public int FramesReceived { get; set; }
    [Key(2)] public int FramesDropped { get; set; }
    [Key(3)] public int BufferDepthMs { get; set; }
    /// <summary>Computed by student (0–100). Teacher uses this as the primary signal.</summary>
    [Key(4)] public int QualityScore { get; set; }
    [Key(5)] public long IntervalStartUtcMs { get; set; }
    [Key(6)] public long IntervalEndUtcMs { get; set; }
}

// ───────────── Phase 8.5: Host actions inside breakout rooms ─────────────

/// <summary>
/// Host → Teacher relay. Teacher validates that SenderId is the current host of
/// some breakout room before relaying the implied action.
/// </summary>
[MessagePackObject(true)]
public class HostActionMessage
{
    /// <summary>Audit field; teacher cross-checks against env.SenderId.</summary>
    public Guid SenderHostId { get; set; }
    /// <summary>For HostActionMute — target peer in same room. Null otherwise.</summary>
    public Guid? TargetStudentId { get; set; }
    /// <summary>For HostActionMessageToMain — chat text. Empty for other actions.</summary>
    public string TextOrPayload { get; set; } = "";
}

// ───────────── Phase 9.5: Camera Broadcast ─────────────

[MessagePackObject(true)]
public class CameraStartMessage
{
    public int Width { get; set; } = 320;
    public int Height { get; set; } = 240;
    public int Fps { get; set; } = 10;
}

[MessagePackObject(true)]
public class CameraFrameMessage
{
    public byte[] JpegData { get; set; } = Array.Empty<byte>();
    public long TimestampMs { get; set; }
}

// ───────────── Phase 9.6: Net Movie ─────────────

[MessagePackObject(true)]
public class MoviePlayMessage
{
    public string FileName { get; set; } = "";
    public double SeekTime { get; set; }
    public long PlayAtTimestampMs { get; set; }
    /// <summary>
    /// Phase 10.21 — total file size in bytes, carried so the student's player
    /// can gate playback on size match (defence-in-depth against a partial file
    /// somehow being exposed despite the atomic .part/rename in FileReceiver).
    /// Default 0 keeps the field backward-compatible: an older student build
    /// that doesn't read this field still plays whatever's on disk, same as
    /// today.  A newer student build treats 0 as "no check, fall back to
    /// existence-only" so a teacher that hasn't been upgraded still works.
    /// </summary>
    public long ExpectedFileSizeBytes { get; set; }
}

[MessagePackObject(true)]
public class MovieSeekMessage
{
    public double SeekTime { get; set; }
}

// ───────────── Phase 6.5: Remote Control ─────────────

[MessagePackObject(true)]
public class RemoteMouseMoveMessage
{
    public double NormalizedX { get; set; }
    public double NormalizedY { get; set; }
}

[MessagePackObject(true)]
public class RemoteMouseClickMessage
{
    public int Button { get; set; } // 1=L, 2=R, 3=M
    public bool IsDown { get; set; }
}

[MessagePackObject(true)]
public class RemoteMouseScrollMessage
{
    public int Delta { get; set; }
}

[MessagePackObject(true)]
public class RemoteKeyMessage
{
    public int VirtualKeyCode { get; set; }
    public bool IsDown { get; set; }
    public bool ShiftDown { get; set; }
    public bool CtrlDown { get; set; }
    public bool AltDown { get; set; }
}

/// <summary>
/// Phase 12-C step 2 — composed Unicode text from teacher's WPF TextInput
/// event.  Survives layout mismatch (TH/EN) and multi-key IME compositions
/// that the VK channel (RemoteKeyMessage) cannot carry.  Student replays via
/// SendInput with KEYEVENTF_UNICODE so the wScan field is interpreted as a
/// UTF-16 code unit instead of a scan code.
/// </summary>
[MessagePackObject]
public class RemoteTextMessage
{
    [Key(0)] public string Text { get; set; } = string.Empty;
}

// ───────────── Phase 9.2: Screen Pen annotation ─────────────

[MessagePackObject(true)]
public class DrawingPointDto
{
    public double X { get; set; }  // normalized 0..1 of overlay width
    public double Y { get; set; }  // normalized 0..1 of overlay height
}

[MessagePackObject(true)]
public class DrawingStrokeMessage
{
    public List<DrawingPointDto> Points { get; set; } = new();
    public string ColorHex { get; set; } = "#FFFF0000";  // ARGB hex
    public double Thickness { get; set; } = 4.0;
    public string Tool { get; set; } = "pen";  // "pen" | "highlighter"
}

// ───────────── Phase 9.1: Student Demonstration ─────────────

/// <summary>
/// Sent on DemoStart so receiving students know whose peer screen window to open
/// and what to label it. Source student (matching SourceStudentId) ignores this — the
/// teacher sends them StudentStreamStart separately to begin broadcasting.
/// </summary>
[MessagePackObject(true)]
public class DemoStartMessage
{
    public Guid SourceStudentId { get; set; }
    public string SourceName { get; set; } = "";
}

// ───────────── Phase 5b: Per-student recording PDPA notification ─────────────

[MessagePackObject(true)]
public class StudentRecordingNotifyMessage
{
    public bool IsRecording { get; set; }
}

// ───────────── Audio stream DTO (Phase 4 Part 3a) ─────────────

[MessagePackObject]
public class AudioStreamFrameMessage
{
    /// <summary>Raw PCM 16-bit little-endian samples.</summary>
    [Key(0)] public byte[] PcmData { get; set; } = Array.Empty<byte>();
    [Key(1)] public int SampleRate { get; set; }
    [Key(2)] public int Channels { get; set; }
    [Key(3)] public int BitsPerSample { get; set; }
    [Key(4)] public long TimestampUtcMs { get; set; }
    [Key(5)] public int FrameSeq { get; set; }
}