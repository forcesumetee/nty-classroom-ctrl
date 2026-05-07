using MessagePack;

namespace ClassroomCtrl.Shared.Protocol;

[MessagePackObject]
public class Envelope
{
    [Key(0)] public ulong MessageId { get; set; }
    [Key(1)] public MessageType Type { get; set; }
    [Key(2)] public long TimestampUtcMs { get; set; }
    [Key(3)] public Guid SenderId { get; set; }
    [Key(4)] public byte[] Payload { get; set; } = Array.Empty<byte>();

    /// <summary>
    /// Optional: when set, the message targets this specific endpoint only.
    /// Empty Guid = broadcast (default; preserves backward compat).
    /// </summary>
    [Key(5)] public Guid TargetEndpointId { get; set; } = Guid.Empty;

    public static Envelope Create(MessageType type, byte[] payload, Guid senderId) => new()
    {
        MessageId = (ulong)Random.Shared.NextInt64(),
        Type = type,
        TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        SenderId = senderId,
        Payload = payload,
        TargetEndpointId = Guid.Empty,
    };

    public static Envelope CreateTargeted(MessageType type, byte[] payload, Guid senderId, Guid targetEndpointId) => new()
    {
        MessageId = (ulong)Random.Shared.NextInt64(),
        Type = type,
        TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        SenderId = senderId,
        Payload = payload,
        TargetEndpointId = targetEndpointId,
    };

    public byte[] Serialize() => MessagePackSerializer.Serialize(this);
    public static Envelope Deserialize(byte[] data) => MessagePackSerializer.Deserialize<Envelope>(data);
}