# Conference Parity — Tier 2 Design (Phase 16-C)

**Status:** investigation (Phase 16-A). Step-by-step plan for Phase 16-C
(peer cam routing — student↔student webcam in Conference Mode).
**Parent:** [`conference-parity-architecture.md`](./conference-parity-architecture.md).
**Siblings:** [`conference-parity-tier1-design.md`](./conference-parity-tier1-design.md),
[`conference-parity-tier3-design.md`](./conference-parity-tier3-design.md).
**Last updated:** 2026-05-29.

---

## Goal

After Phase 16-C, every Conference participant (teacher + N students)
can capture their own webcam and see every other participant's
webcam in the gallery tiles. Star topology relay via the teacher (no
SFU yet); same pattern as 13-C Tier 2 student-screen and 13-D voice.

Estimated effort: ~1-2 hr Claude Code time. Per-step commits.

## Pre-flight

- [ ] Phase 16-B landed (`Shared.Wpf` exists, gallery surface on both
      sides, UI bug fixed).
- [ ] 14-B Tier 1 cam pipeline shipping (`CameraBroadcastService` +
      `CameraSelectorDialog` operational on Teacher).
- [ ] Network: gigabit switched LAN verified for 5-10 PC class size
      (148 KB/s × N×(N-1) bandwidth math below).

## Wire Protocol

### Decision: NEW codepoints `0x0680-0x0682`

The 9.5 codes `0x0460-0x0462` (CameraStart / CameraFrame / CameraStop)
are kept Classroom-mode-only:
- Pre-existing, ships unidirectional (teacher→student).
- Mode-bound to Classroom UX (teacher cam window pops up on student
  side, not Conference gallery).
- Mixing roles (teacher→student AND student→student) on one wire code
  would force every dispatch arm to discriminate by source — fragile.

New Conference cam codes:

```csharp
// MessageType.cs — append under 15-E block (highest index first)

// Phase 16-C (Tier 2): Conference Mode — peer cam routing.  Each
// participant emits ConferenceCameraStart/Frame/Stop with their own
// EndpointId in Envelope.SenderId; teacher acts as star-topology relay
// and fans out to all in-Conference peers != sender.  Self-loopback
// filter mirrors Phase 13-D voice (env.SenderId == _myEndpointId).
// Lossy channel: rides _outbox (DropOldest cap-16), same as 0x0461.
ConferenceCameraStart = 0x0680,   // S→T relay→all, ConferenceCameraStartMessage
ConferenceCameraFrame = 0x0681,   // S→T relay→all, ConferenceCameraFrameMessage
ConferenceCameraStop  = 0x0682,   // S→T relay→all, empty payload
```

### DTOs

```csharp
[MessagePackObject]
public class ConferenceCameraStartMessage
{
    /// <summary>Per-session Guid matching ConferenceStartMessage.SessionId
    /// (15-B).  Receivers verify they're in the same session before
    /// allocating a decoder.</summary>
    [Key(0)] public Guid SessionId { get; set; }

    /// <summary>Source endpoint of the cam stream — redundant with
    /// Envelope.SenderId; embedded for grep-friendly diagnostics
    /// (mirrors VoiceAudioFrameMessage.SourceEndpointId at Key 0).</summary>
    [Key(1)] public Guid SourceEndpointId { get; set; }
    [Key(2)] public int  Width  { get; set; } = 320;
    [Key(3)] public int  Height { get; set; } = 240;
    [Key(4)] public int  Fps    { get; set; } = 10;
}

[MessagePackObject]
public class ConferenceCameraFrameMessage
{
    [Key(0)] public Guid SourceEndpointId { get; set; }
    [Key(1)] public byte[] JpegData { get; set; } = Array.Empty<byte>();
    [Key(2)] public long  TimestampMs   { get; set; }
}
```

`ConferenceCameraStop` carries no payload — sender is implicit
(Envelope.SenderId).

### Wire-compat tests

T17 = `ConferenceCameraStartMessage` round-trip (5 keys).
T18 = `ConferenceCameraFrameMessage` round-trip (3 keys, with JpegData
non-empty).
T19 = `ConferenceCameraStop` envelope (empty payload) + SourceId
preserved.

## Routing Topology

Star (mirrors 13-C Tier 2 student-screen + 13-D Tier 3 voice):

```
Student A's cam → S→T frame envelope
                  ↓
              Teacher (relay)
                  ↓
          fan out to all in-Conference peers != Student A
                  ↓
       Student B, Student C, ... receive
```

Self-loopback handled by the receive arm:
```csharp
if (env.SenderId == App.Server.TeacherEndpointId) return; // teacher's own frame, already shown via local self-tile
if (env.SenderId == _myEndpointId) return; // student's own frame, already shown via local self-tile
```

Teacher also emits its own cam via 0x0681 (NOT the 9.5 path) so
students consistently route by `Envelope.SenderId` and see the teacher
tile populate.

## Capture Pipeline

### Student-side capture: NEW `StudentCameraBroadcaster`

Lives in `Student.Agent/Services/StudentCameraBroadcaster.cs`. Modelled
on `Teacher/Services/CameraBroadcastService.cs` (14-B / 9.5):

- AForge.Video.DirectShow (existing — used by Teacher; Student.Agent
  gains `AForge.Video` + `AForge.Video.DirectShow` package refs +
  `System.Drawing.Common` for JPEG encode).
- 320×240 @ 10 FPS, JPEG quality 70 (matches Teacher defaults).
- Mode-aware: only emits 0x0681 frames when `IsInConference == true`.
- `Start(deviceMoniker, ct)` / `Stop()` / `LastError` / `IsActive`.

### Teacher-side capture: REUSE `CameraBroadcastService`

Already exists; emits 9.5 0x0460-0x0462 today. Add a mode flag:

```csharp
// Teacher/Services/CameraBroadcastService.cs — modification

public enum CamRouting
{
    Classroom = 0,   // emits 0x0460-0x0462 (existing)
    Conference = 1,  // emits 0x0680-0x0682 (NEW)
}

public CamRouting Routing { get; set; } = CamRouting.Classroom;

private void OnNewFrame(...)
{
    ...
    if (Routing == CamRouting.Conference)
        _ = _server.BroadcastConferenceCameraFrameAsync(...);
    else
        _ = _server.BroadcastCameraFrameAsync(...);
}
```

`MainViewModel.OnIsInConferenceChanged` flips
`App.Camera.Routing = Conference` (or back to Classroom). Stop+Start
cycle isn't required since 0x0680 vs 0x0460 are different envelope
types; existing receivers ignore the unfamiliar code.

### Mode-exclusivity guard

Per architecture-doc § 5 risk #4: defensive assert in the broadcast
path:
```csharp
private void OnNewFrame(...)
{
    if (Routing == CamRouting.Conference && !_modeProvider.IsInConference)
    {
        _logger.LogWarning("Frame routed as Conference but mode is Classroom; dropping");
        return;
    }
    ...
}
```

## ControlServer Relay (Teacher)

```csharp
// Teacher/Services/ControlServer.cs — add

public Task BroadcastConferenceCameraStartAsync(ConferenceCameraStartMessage msg, CancellationToken ct)
{
    var bytes = MessagePackSerializer.Serialize(msg);
    var env = Envelope.Create(MessageType.ConferenceCameraStart, bytes, _teacherId);
    return _tcp.BroadcastAsync(env, ct);
}

public Task BroadcastConferenceCameraFrameAsync(ConferenceCameraFrameMessage msg, CancellationToken ct)
{
    var bytes = MessagePackSerializer.Serialize(msg);
    var env = Envelope.Create(MessageType.ConferenceCameraFrame, bytes, _teacherId);
    return _tcp.BroadcastAsync(env, ct);
    // Note: _tcp's BroadcastAsync uses _outbox (DropOldest cap-16) for
    // 0x0681 frames per the channel routing table.
}

// Receive arm — fires event for MainViewModel to update gallery, then
// re-broadcasts as the original SenderId so other peers see the source.
case MessageType.ConferenceCameraFrame:
    {
        var frame = MessagePackSerializer.Deserialize<ConferenceCameraFrameMessage>(env.Payload);
        ConferenceCameraFrameReceived?.Invoke(this, (env.SenderId, frame));
        // Re-broadcast to all peers; lossy channel.
        var relay = Envelope.Create(MessageType.ConferenceCameraFrame, env.Payload, env.SenderId);
        _ = _tcp.BroadcastAsync(relay, CancellationToken.None);
    }
    break;
```

Channel routing: 0x0681 rides `_outbox` (DropOldest cap-16, same as
0x0461). 0x0680/0x0682 ride `_reliableOutbox` (Wait cap-4) — small
control envelopes.

## Receive Routing

### Teacher

`MainViewModel.OnConferenceCameraFrameReceived(senderId, frame)`:
- Find `ConferenceGallery.Tiles.First(t => t.EndpointId == senderId)`.
- Decode JPEG via existing pattern (mirror
  [`MainViewModel.cs:OnTeacherCameraFrameSent`](../src/ClassroomCtrl.Teacher/ViewModels/MainViewModel.cs)).
- Assign to `tile.JpegFrame`.
- Set `tile.IsCamLive = true`.

### Student

`Student.Agent/MainWindow.xaml.cs` dispatch arm:
```csharp
case MessageType.ConferenceCameraFrame:
    if (env.SenderId == _myEndpointId) break; // self-loopback
    try
    {
        var frame = MessagePackSerializer.Deserialize<ConferenceCameraFrameMessage>(env.Payload);
        Dispatcher.Invoke(() => _confWindow?.UpdatePeerFrame(env.SenderId, frame.JpegData));
    }
    catch (Exception ex) { IpcClient.LogToFile($"[MainWindow] ConferenceCameraFrame: {ex.Message}"); }
    break;
```

`ConferenceGalleryWindow.UpdatePeerFrame(Guid endpointId, byte[] jpeg)`
finds the matching tile in its `_shellVm.ConferenceGallery.Tiles`
collection and updates `JpegFrame`. Tile created on `ConferenceCameraStart`
if absent.

## Bandwidth Math

Per CamSpike (Phase 14-A measured): **148 KB/s per stream**
(actual @ 9.2 FPS, ~50% of architecture-doc-budget allocation).

Relay model: N participants, each sees N-1 peers' streams →
N × (N-1) total relays through the teacher's egress.

| Participants (N) | Streams in (S→T) | Streams out (T→all) | Teacher egress |
|---|---|---|---|
| 2 | 1 | 1 | ~150 KB/s |
| 3 | 2 | 2×2 = 4 | ~590 KB/s = ~5 Mbps |
| 5 | 4 | 4×4 = 16 | ~2.4 MB/s = ~19 Mbps |
| 10 | 9 | 9×9 = 81 | ~12 MB/s = ~96 Mbps |
| 15 | 14 | 14×14 = 196 | ~29 MB/s = ~230 Mbps |
| 20 | 19 | 19×19 = 361 | ~53 MB/s = ~425 Mbps |
| 25 | 24 | 24×24 = 576 | ~85 MB/s = ~680 Mbps |
| 30 | 29 | 29×29 = 841 | ~125 MB/s = ~1000 Mbps |

**Conclusion:** comfortable through N=15 on gigabit. N=20 fits with
~50% headroom. N=25 fills 70% of egress; N=30 saturates gigabit —
needs SFU (15-F).

### Customer-site validation gates

| Class size | Validation step |
|---|---|
| ≤ 10 | 2-PC bandwidth confidence; ship |
| 10-20 | Customer-site 5-PC dry-run with Wireshark egress baseline; ship if < 70% gigabit |
| 20-30 | Defer ship to customer; gate on iperf3-verified switched-gigabit + adoption-rate measurement (typical school: 50% cam-on) |
| 30+ | 15-F SFU required; defer until customer trigger |

Typical classroom adoption (architecture doc § "Bandwidth math"):
~50% cam-on at any moment, so effective N is halved → 15-F trigger
shifts to ~40-50 students.

## Step-by-step (5 steps + 1 acceptance)

### Step 1 — Wire protocol + T17-T19

**Files:** `Shared/Protocol/MessageType.cs`, `Shared/Protocol/Messages.cs`,
`tools/EnvelopeWireCompatTest/Program.cs`.

Append 0x0680-0x0682 codepoints + 2 DTOs (Start, Frame) + 3
wire-compat tests.

**Commit:** `Phase 16-C step 1: wire protocol (0x0680-0x0682) + T17-T19`

### Step 2 — Teacher relay + `BroadcastConferenceCamera*Async`

**Files:** `Teacher/Services/ControlServer.cs`.

3 new public broadcast methods + dispatch switch arms for the 3 new
types. Frame arm re-broadcasts to all peers as `env.SenderId`.

**Commit:** `Phase 16-C step 2: teacher relay broadcast methods + receive arms`

### Step 3 — Student `StudentCameraBroadcaster`

**Files:** new `Student.Agent/Services/StudentCameraBroadcaster.cs`,
`Student.Agent/ClassroomCtrl.Student.Agent.csproj` (AForge package refs +
System.Drawing.Common), `Student.Agent/App.xaml.cs` (init the service).

Mirror `Teacher/Services/CameraBroadcastService.cs` (14-B): AForge
device enumeration, capture, JPEG encode, emit
`ConferenceCameraFrameMessage` via IPC client.

**Commit:** `Phase 16-C step 3: Student.Agent webcam capture + emit (Conference mode only)`

### Step 4 — Teacher cam mode-aware emission

**Files:** `Teacher/Services/CameraBroadcastService.cs`,
`Teacher/ViewModels/MainViewModel.cs`.

Add `Routing` property + frame-emit branch. Flip on
`OnIsInConferenceChanged`. Update `BroadcastCameraStartAsync` to also
choose 0x0680 vs 0x0460 based on `Routing`.

**Commit:** `Phase 16-C step 4: teacher cam mode-aware routing (Classroom vs Conference)`

### Step 5 — Receive routing + self-loopback filter

**Files:** `Teacher/ViewModels/MainViewModel.cs`,
`Student.Agent/MainWindow.xaml.cs`,
`Student.Agent/ConferenceGalleryWindow.xaml.cs`.

- Teacher: subscribe to `ControlServer.ConferenceCameraFrameReceived`
  event; decode + route to `ConferenceGallery.Tiles` by EndpointId.
- Student: dispatch arms for 0x0680 / 0x0681 / 0x0682; per-tile routing
  in the window; self-loopback filter.
- `Tiles` collection lifecycle: tile auto-added on
  `ConferenceCameraStart` if absent, removed on `ConferenceCameraStop`.

**Commit:** `Phase 16-C step 5: receive routing + self-loopback filter + per-tile dispatch`

### Step 6 — Build + 3-PC ideal smoke (2-PC fallback)

- Build all 3 projects clean.
- T1-T19 wire-compat PASS.
- 3-PC manual: teacher + 2 students; each toggles cam; verify each sees
  the other two cams in gallery.
- 2-PC fallback (if 3-PC unavailable): teacher + 1 student; verify
  teacher cam visible on student gallery (already worked in 15-C) AND
  student cam visible on teacher gallery (NEW).

**Commit:** `Phase 16-C step 6: build + acceptance` (or fold into step 5).

## Acceptance checklist (8 items)

| # | Check | 2-PC | 3-PC |
|---|---|---|---|
| 1 | T1-T19 wire-compat PASS | ✅ | ✅ |
| 2 | Builds clean across 3 projects | ✅ | ✅ |
| 3 | Student.Agent has WebcamSelector / device-pick UX | ✅ | ✅ |
| 4 | Student cam starts → frames arrive at teacher | ✅ | ✅ |
| 5 | Student cam frame visible in teacher gallery tile | ✅ | ✅ |
| 6 | Teacher cam frame visible in student gallery tile (0x0681 path) | ✅ | ✅ |
| 7 | Two student cams visible in 3-PC: each sees the other's tile | ❌ | ✅ |
| 8 | No regression in 9.5 Classroom cam broadcast | ✅ | ✅ |

## Risk-specific notes

- **Risk #4 (mode-exclusivity collision):** defensive assert in step 4.
- **Risk #6 (force-cam by host):** NOT in 16-C scope — deferred to
  16-E. Host can still see when student cam is off (existing
  `IsCamLive` indicator).
- **Risk #7 (peer cam dropped on leave):** add 5-frame grace window
  in step 5 receive arm before removing tile JPEG (prevents flicker on
  brief packet loss).
- **Risk #9 (bandwidth at 30+):** documented in bandwidth math
  table; customer-site validation gates above.
- **Risk #10 (self-loopback):** same one-line pattern as 13-D voice
  filter; documented in step 5.

## After 16-C

Conference Mode functionally complete for parity (everyone sees
everyone). UI roles still uniform — students can click "End for All"
and other admin buttons (no-op or surprising). **16-D** layers the
role-gated UI on top to hide admin controls on student side.
