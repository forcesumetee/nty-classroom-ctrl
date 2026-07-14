using MessagePack;

namespace ClassroomCtrl.Shared.Protocol;

/// <summary>
/// IPC-only (Agent→Service, <see cref="MessageType.ChatSendRequest"/>) — the student's chat reply text.
/// The Agent sends only the text; the daemon owns identity, so it stamps the real <see cref="ChatMessage"/>
/// (SenderId = its persistent endpoint id, SenderName = the configured student name) and forwards it to the
/// Teacher as a standard <see cref="MessageType.ChatBroadcast"/>. Lives in Shared.Wire so both sides share it.
/// </summary>
[MessagePackObject]
public class ChatSendRequestMessage
{
    [Key(0)] public string Text { get; set; } = "";
}
