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

    /// <summary>
    /// Phase 13-B (Tier 1) — Optional: when set, the message targets every peer
    /// whose current breakout-room id matches this value.  Receiver-side
    /// <c>IsForMe</c> extends to also match this field against the student's
    /// <c>_myRoomId</c>.  Null (default) = not group-targeted; standard
    /// TargetEndpointId / broadcast routing applies.
    ///
    /// WIRE-COMPAT: this field appended at [Key(6)] (next available index).
    /// Older clients without this field decode as null on the receiver,
    /// older receivers reading newer payloads simply ignore the extra key.
    /// </summary>
    [Key(6)] public Guid? TargetGroupId { get; set; } = null;

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

    /// <summary>Phase 13-B (Tier 1) — convenience for group-targeted envelopes.
    /// Receivers route via the new TargetGroupId field + IsForMe extension.</summary>
    public static Envelope CreateGroupTargeted(MessageType type, byte[] payload, Guid senderId, Guid targetGroupId) => new()
    {
        MessageId = (ulong)Random.Shared.NextInt64(),
        Type = type,
        TimestampUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        SenderId = senderId,
        Payload = payload,
        TargetEndpointId = Guid.Empty,
        TargetGroupId = targetGroupId,
    };

    public byte[] Serialize() => MessagePackSerializer.Serialize(this);
    public static Envelope Deserialize(byte[] data) => MessagePackSerializer.Deserialize<Envelope>(data);
}