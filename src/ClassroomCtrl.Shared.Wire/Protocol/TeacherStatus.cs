using MessagePack;

namespace ClassroomCtrl.Shared.Protocol;

/// <summary>
/// IPC-only (Service→Agent, <see cref="MessageType.TeacherStatusNotify"/>) snapshot of the daemon's
/// TCP link to the Teacher. Sent on every connect/disconnect transition, and once when a fresh Agent
/// connects (so the tray converges immediately). Drives the tray "Status" + "Teacher" lines.
///
/// Lives in Shared.Wire so both the daemon (serializer) and the tray Agent (deserializer) share it.
/// </summary>
[MessagePackObject]
public class TeacherStatusMessage
{
    /// <summary>True while the daemon's TCP connection to the Teacher is established.</summary>
    [Key(0)] public bool Connected { get; set; }

    /// <summary>
    /// Human label for the teacher, shown as "Teacher: {name}". The wire protocol carries no teacher
    /// display name (the Teacher never announces one), so this is the teacher's IP — the same identity
    /// the student UI uses. Empty when disconnected.
    /// </summary>
    [Key(1)] public string TeacherName { get; set; } = "";
}
