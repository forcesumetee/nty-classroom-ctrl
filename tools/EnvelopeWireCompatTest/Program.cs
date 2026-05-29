// Phase 13-B step 2 — Envelope wire-compat regression guard.
//
// Goal: prove that the Envelope.TargetGroupId field added at [Key(6)] in
// Phase 13-B step 2 does NOT break the wire when:
//   (a) a pre-Tier-1 ("vintage") sender emits a payload without Key 6, and
//   (b) a Tier-1 receiver decodes it.
// Plus the symmetric case (Tier-1 sender → vintage receiver) by checking the
// invariant that an envelope with TargetGroupId=null serializes identically to
// what a vintage sender would produce.
//
// Run from solution root:
//   dotnet run --project tools/EnvelopeWireCompatTest -c Release
//
// Exit code 0 on PASS, non-zero on FAIL.  Leave this tool in the tree as a
// regression guard — any future Envelope schema change should still keep it
// passing (or, if breaking changes are deliberate, the test should fail loudly
// so the dev makes the protocol-version decision consciously).

using ClassroomCtrl.Shared.Protocol;
using MessagePack;

static int Fail(string msg) { Console.Error.WriteLine($"FAIL: {msg}"); return 1; }
static void Pass(string msg) => Console.WriteLine($"PASS: {msg}");

int errors = 0;

// ──────── Test 1: round-trip an envelope with TargetGroupId = null ────────
{
    var env = Envelope.Create(MessageType.Hello, new byte[] { 1, 2, 3, 4 }, Guid.NewGuid());
    var bytes = env.Serialize();
    var back = Envelope.Deserialize(bytes);
    if (back.TargetGroupId != null) { errors += Fail($"T1: TargetGroupId should be null, got {back.TargetGroupId}"); }
    else if (back.Type != MessageType.Hello) { errors += Fail("T1: Type round-trip mismatch"); }
    else if (back.Payload.Length != 4) { errors += Fail("T1: Payload round-trip mismatch"); }
    else { Pass("T1: round-trip with TargetGroupId=null preserved"); }
}

// ──────── Test 2: round-trip an envelope with TargetGroupId = Guid ────────
{
    var groupId = Guid.NewGuid();
    var env = Envelope.CreateGroupTargeted(MessageType.GroupScreenStreamFrame, new byte[] { 9, 9 }, Guid.NewGuid(), groupId);
    var bytes = env.Serialize();
    var back = Envelope.Deserialize(bytes);
    if (back.TargetGroupId != groupId) { errors += Fail($"T2: TargetGroupId mismatch {back.TargetGroupId} != {groupId}"); }
    else if (back.TargetEndpointId != Guid.Empty) { errors += Fail("T2: TargetEndpointId should be empty"); }
    else { Pass("T2: round-trip with TargetGroupId=set preserved"); }
}

// ──────── Test 3: VINTAGE → NEW.  A sender from before Tier 1 only knows
//          Keys 0..5.  Simulate by serializing a mirror type that has only
//          those keys, then deserializing as the real Envelope and asserting
//          TargetGroupId defaults to null. ────────
{
    var vintage = new VintageEnvelope
    {
        MessageId = 12345UL,
        Type = MessageType.Hello,
        TimestampUtcMs = 1000_000,
        SenderId = Guid.NewGuid(),
        Payload = new byte[] { 0xAA, 0xBB },
        TargetEndpointId = Guid.NewGuid(),
    };
    var bytes = MessagePackSerializer.Serialize(vintage);
    Envelope back;
    try { back = Envelope.Deserialize(bytes); }
    catch (Exception ex)
    {
        errors += Fail($"T3: vintage→new deserialize threw: {ex.Message}");
        goto T3End;
    }
    if (back.TargetGroupId != null) { errors += Fail($"T3: expected null TargetGroupId, got {back.TargetGroupId}"); }
    else if (back.MessageId != vintage.MessageId) { errors += Fail("T3: MessageId mismatch"); }
    else if (back.Type != vintage.Type) { errors += Fail("T3: Type mismatch"); }
    else if (back.TargetEndpointId != vintage.TargetEndpointId) { errors += Fail("T3: TargetEndpointId mismatch"); }
    else { Pass("T3: vintage→new deserialize: TargetGroupId defaults to null, existing fields preserved"); }
    T3End: ;
}

// ──────── Test 4: NEW (with null group) → VINTAGE.  A Tier-1 sender that does
//          not group-target an envelope must produce bytes that a vintage
//          receiver can still parse.  Simulate by deserializing as VintageEnvelope. ────────
{
    var env = Envelope.Create(MessageType.ChatBroadcast, new byte[] { 7, 7 }, Guid.NewGuid());
    var bytes = env.Serialize();
    try
    {
        var asVintage = MessagePackSerializer.Deserialize<VintageEnvelope>(bytes);
        if (asVintage.Type != MessageType.ChatBroadcast) { errors += Fail("T4: vintage decode lost Type"); }
        else if (asVintage.Payload.Length != 2) { errors += Fail("T4: vintage decode lost Payload"); }
        else { Pass("T4: new (TargetGroupId=null) → vintage decode: existing fields preserved"); }
    }
    catch (Exception ex) { errors += Fail($"T4: new→vintage threw: {ex.Message}"); }
}

// ──────── Test 5: NEW (with TargetGroupId set) → VINTAGE.  Even when the new
//          field is populated, the receiver must still be able to parse the
//          other keys.  MessagePack's behavior is to skip unknown keys. ────────
{
    var env = Envelope.CreateGroupTargeted(MessageType.GroupScreenStreamStart, new byte[] { 5 }, Guid.NewGuid(), Guid.NewGuid());
    var bytes = env.Serialize();
    try
    {
        var asVintage = MessagePackSerializer.Deserialize<VintageEnvelope>(bytes);
        if (asVintage.Type != MessageType.GroupScreenStreamStart) { errors += Fail("T5: vintage decode lost Type"); }
        else { Pass("T5: new (TargetGroupId=set) → vintage decode: existing fields preserved, Key 6 silently skipped"); }
    }
    catch (Exception ex) { errors += Fail($"T5: new(group-targeted)→vintage threw: {ex.Message}"); }
}

// ──────── Test 6 (Phase 13-C step 1): StudentGroupScreenStreamControlMessage
//          round-trip.  New DTO added for Tier 2 host-presenter signaling.
//          Verifies explicit Key(0..4) serialization shape stays stable. ────────
{
    var control = new StudentGroupScreenStreamControlMessage
    {
        GroupId = Guid.NewGuid(),
        PresenterId = Guid.NewGuid(),
        PresenterName = "Som",
        Start = true,
        Codec = VideoCodec.H264,
    };
    var bytes = MessagePackSerializer.Serialize(control);
    var back = MessagePackSerializer.Deserialize<StudentGroupScreenStreamControlMessage>(bytes);
    if (back.GroupId != control.GroupId) { errors += Fail("T6: GroupId round-trip mismatch"); }
    else if (back.PresenterId != control.PresenterId) { errors += Fail("T6: PresenterId round-trip mismatch"); }
    else if (back.PresenterName != control.PresenterName) { errors += Fail("T6: PresenterName round-trip mismatch"); }
    else if (back.Start != control.Start) { errors += Fail("T6: Start round-trip mismatch"); }
    else if (back.Codec != control.Codec) { errors += Fail("T6: Codec round-trip mismatch"); }
    else { Pass("T6: StudentGroupScreenStreamControlMessage round-trip preserved (5 keys)"); }
}

// ──────── Test 7 (Phase 13-C step 1): StudentGroupScreenStream{Start,Frame,
//          Stop} envelopes round-trip with TargetGroupId set, mirroring the
//          host-presenter wire-up.  Frame envelope wraps a real
//          ScreenStreamFrameMessage payload to confirm the existing frame
//          DTO is unchanged (just a new MessageType wrapping). ────────
{
    var groupId = Guid.NewGuid();
    var hostId = Guid.NewGuid();
    var frame = new ScreenStreamFrameMessage
    {
        FrameData = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67, 0x42 },
        Width = 1920,
        Height = 1080,
        FrameSeq = 42,
        Codec = VideoCodec.H264,
        IsKeyframe = true,
    };
    var framePayload = MessagePackSerializer.Serialize(frame);
    var env = Envelope.CreateGroupTargeted(MessageType.StudentGroupScreenStreamFrame, framePayload, hostId, groupId);
    var bytes = env.Serialize();
    var back = Envelope.Deserialize(bytes);
    var backFrame = MessagePackSerializer.Deserialize<ScreenStreamFrameMessage>(back.Payload);
    if (back.Type != MessageType.StudentGroupScreenStreamFrame) { errors += Fail("T7: Type lost"); }
    else if (back.TargetGroupId != groupId) { errors += Fail("T7: TargetGroupId lost"); }
    else if (back.SenderId != hostId) { errors += Fail("T7: SenderId lost"); }
    else if (backFrame.FrameSeq != frame.FrameSeq) { errors += Fail("T7: nested frame.FrameSeq mismatch"); }
    else if (backFrame.IsKeyframe != frame.IsKeyframe) { errors += Fail("T7: nested frame.IsKeyframe mismatch"); }
    else { Pass("T7: StudentGroupScreenStreamFrame envelope + nested ScreenStreamFrameMessage round-trip preserved"); }
}

if (errors > 0)
{
    Console.Error.WriteLine($"\n{errors} test(s) FAILED — wire-compat broken.");
    return 1;
}
Console.WriteLine("\nAll envelope wire-compat tests PASSED.");
return 0;


// Vintage shape: mirror of Envelope as it existed BEFORE Phase 13-B step 2
// (Keys 0..5 only, no TargetGroupId).  Used to simulate cross-version
// serialization without depending on git history.
[MessagePackObject]
public class VintageEnvelope
{
    [Key(0)] public ulong MessageId { get; set; }
    [Key(1)] public MessageType Type { get; set; }
    [Key(2)] public long TimestampUtcMs { get; set; }
    [Key(3)] public Guid SenderId { get; set; }
    [Key(4)] public byte[] Payload { get; set; } = Array.Empty<byte>();
    [Key(5)] public Guid TargetEndpointId { get; set; } = Guid.Empty;
}
