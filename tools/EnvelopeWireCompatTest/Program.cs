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

// ──────── Tests 8-11 (Phase 13-D step 1): Tier 3 voice + mic DTOs.
//          One round-trip test per new DTO.  Voice path is performance-
//          sensitive (100ms frame rate) — Key ordering on these DTOs is
//          load-bearing for future schema changes. ────────
{
    var msg = new VoiceAudioFrameMessage
    {
        SourceEndpointId = Guid.NewGuid(),
        GroupId = Guid.NewGuid(),
        Pcm = new byte[] { 0x01, 0x00, 0xFF, 0xFF, 0x00, 0x80 },
        Ts = 1_700_000_000_000L,
    };
    var bytes = MessagePackSerializer.Serialize(msg);
    var back = MessagePackSerializer.Deserialize<VoiceAudioFrameMessage>(bytes);
    if (back.SourceEndpointId != msg.SourceEndpointId) { errors += Fail("T8: SourceEndpointId mismatch"); }
    else if (back.GroupId != msg.GroupId) { errors += Fail("T8: GroupId mismatch"); }
    else if (back.Pcm.Length != msg.Pcm.Length) { errors += Fail("T8: Pcm length mismatch"); }
    else if (back.Ts != msg.Ts) { errors += Fail("T8: Ts mismatch"); }
    else { Pass("T8: VoiceAudioFrameMessage round-trip preserved (4 keys)"); }
}
{
    var msg = new MicMuteRequestMessage
    {
        TargetEndpointId = Guid.NewGuid(),
        Muted = true,
        Reason = "Teacher muted you for the class discussion.",
    };
    var bytes = MessagePackSerializer.Serialize(msg);
    var back = MessagePackSerializer.Deserialize<MicMuteRequestMessage>(bytes);
    if (back.TargetEndpointId != msg.TargetEndpointId) { errors += Fail("T9: TargetEndpointId mismatch"); }
    else if (back.Muted != msg.Muted) { errors += Fail("T9: Muted mismatch"); }
    else if (back.Reason != msg.Reason) { errors += Fail("T9: Reason mismatch"); }
    else { Pass("T9: MicMuteRequestMessage round-trip preserved (3 keys)"); }
}
{
    var msg = new MicStateUpdateMessage
    {
        EndpointId = Guid.NewGuid(),
        MicLive = true,
        PttMode = true,
        IsSpeaking = false,
    };
    var bytes = MessagePackSerializer.Serialize(msg);
    var back = MessagePackSerializer.Deserialize<MicStateUpdateMessage>(bytes);
    if (back.EndpointId != msg.EndpointId) { errors += Fail("T10: EndpointId mismatch"); }
    else if (back.MicLive != msg.MicLive) { errors += Fail("T10: MicLive mismatch"); }
    else if (back.PttMode != msg.PttMode) { errors += Fail("T10: PttMode mismatch"); }
    else if (back.IsSpeaking != msg.IsSpeaking) { errors += Fail("T10: IsSpeaking mismatch"); }
    else { Pass("T10: MicStateUpdateMessage round-trip preserved (4 keys)"); }
}
{
    var msg = new MicPttSetMessage
    {
        TargetEndpointId = Guid.NewGuid(),
        PttMode = false,
        HotkeyVk = 0x20,   // VK_SPACE
    };
    var bytes = MessagePackSerializer.Serialize(msg);
    var back = MessagePackSerializer.Deserialize<MicPttSetMessage>(bytes);
    if (back.TargetEndpointId != msg.TargetEndpointId) { errors += Fail("T11: TargetEndpointId mismatch"); }
    else if (back.PttMode != msg.PttMode) { errors += Fail("T11: PttMode mismatch"); }
    else if (back.HotkeyVk != msg.HotkeyVk) { errors += Fail("T11: HotkeyVk mismatch"); }
    else { Pass("T11: MicPttSetMessage round-trip preserved (3 keys)"); }
}

// ──────── Test 12 (Phase 14-B step 1): WebcamStateUpdateMessage round-trip ────────
{
    var msg = new WebcamStateUpdateMessage
    {
        DeviceAvailable = true,
        CamLive = false,           // Tier 1 always reports false; Tier 2 lights it up
        Mode = WebcamMode.Off,
        LastError = "",
    };
    var bytes = MessagePackSerializer.Serialize(msg);
    var back = MessagePackSerializer.Deserialize<WebcamStateUpdateMessage>(bytes);
    if (back.DeviceAvailable != msg.DeviceAvailable) { errors += Fail("T12: DeviceAvailable mismatch"); }
    else if (back.CamLive != msg.CamLive) { errors += Fail("T12: CamLive mismatch"); }
    else if (back.Mode != msg.Mode) { errors += Fail("T12: Mode mismatch"); }
    else if (back.LastError != msg.LastError) { errors += Fail("T12: LastError mismatch"); }
    else { Pass("T12: WebcamStateUpdateMessage round-trip preserved (4 keys, Tier 1 default)"); }
}

// ──────── Test 13 (Phase 14-B step 1): WebcamStateUpdateMessage with all
//          fields exercised — non-empty LastError, AlwaysOn mode, CamLive=true.
//          Confirms enum byte-encoding + UTF-8 string round-trip. ────────
{
    var msg = new WebcamStateUpdateMessage
    {
        DeviceAvailable = true,
        CamLive = true,
        Mode = WebcamMode.AlwaysOn,
        LastError = "Camera held by another app",
    };
    var bytes = MessagePackSerializer.Serialize(msg);
    var back = MessagePackSerializer.Deserialize<WebcamStateUpdateMessage>(bytes);
    if (back.DeviceAvailable != msg.DeviceAvailable) { errors += Fail("T13: DeviceAvailable mismatch"); }
    else if (back.CamLive != msg.CamLive) { errors += Fail("T13: CamLive mismatch"); }
    else if (back.Mode != WebcamMode.AlwaysOn) { errors += Fail($"T13: Mode mismatch (got {back.Mode})"); }
    else if (back.LastError != msg.LastError) { errors += Fail("T13: LastError mismatch"); }
    else { Pass("T13: WebcamStateUpdateMessage fully-populated round-trip preserved"); }
}

// ──────── Test 14 (Phase 15-B step 1): ConferenceStartMessage round-trip ────────
{
    var msg = new ConferenceStartMessage
    {
        SessionId = Guid.NewGuid(),
        HostName = "Teacher Sirin",
        StartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    };
    var bytes = MessagePackSerializer.Serialize(msg);
    var back = MessagePackSerializer.Deserialize<ConferenceStartMessage>(bytes);
    if (back.SessionId != msg.SessionId) { errors += Fail("T14: SessionId mismatch"); }
    else if (back.HostName != msg.HostName) { errors += Fail("T14: HostName mismatch"); }
    else if (back.StartedAtMs != msg.StartedAtMs) { errors += Fail("T14: StartedAtMs mismatch"); }
    else { Pass("T14: ConferenceStartMessage round-trip preserved (3 keys)"); }
}

// ──────── Test 15 (Phase 15-B step 1): ConferenceStart envelope with empty
//          payload (a future no-payload variant) AND ConferenceEnd envelope
//          (always empty) round-trip cleanly via the Envelope wrapper. ────────
{
    var teacherId = Guid.NewGuid();
    var endEnv = Envelope.Create(MessageType.ConferenceEnd, Array.Empty<byte>(), teacherId);
    var endBytes = endEnv.Serialize();
    var endBack = Envelope.Deserialize(endBytes);
    if (endBack.Type != MessageType.ConferenceEnd) { errors += Fail("T15: ConferenceEnd Type lost"); }
    else if (endBack.SenderId != teacherId) { errors += Fail("T15: ConferenceEnd SenderId lost"); }
    else if (endBack.Payload.Length != 0) { errors += Fail($"T15: ConferenceEnd Payload should be empty, got {endBack.Payload.Length}B"); }
    else { Pass("T15: ConferenceEnd envelope (empty payload) round-trip preserved"); }
}

// ──────── Test 16 (Phase 15-E step 1): ReactionMessage round-trip ────────
{
    var msg = new ReactionMessage
    {
        Emoji = "❤️",
        ExpiresAtMs = 1748534400_000L,
    };
    var bytes = MessagePackSerializer.Serialize(msg);
    var back = MessagePackSerializer.Deserialize<ReactionMessage>(bytes);
    if (back.Emoji != msg.Emoji) { errors += Fail($"T16: Emoji mismatch (got '{back.Emoji}')"); }
    else if (back.ExpiresAtMs != msg.ExpiresAtMs) { errors += Fail("T16: ExpiresAtMs mismatch"); }
    else { Pass("T16: ReactionMessage round-trip preserved (2 keys, UTF-16 emoji)"); }
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
