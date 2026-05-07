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