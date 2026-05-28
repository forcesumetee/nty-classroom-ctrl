# Breakout Rooms — Cross-Tier Architecture

**Status:** design only, no code. Foundation for Phase 13-B/C/D implementation primers.
**Owners:** dev review → implementation per tier.
**Last updated:** 2026-05-29.

This document is the single source of truth for the Feature #3 (Breakout Rooms) plan across all
three tiers. Tier-specific design docs live next to it:

- [`breakout-rooms-tier1-design.md`](./breakout-rooms-tier1-design.md) — implementation-ready
- [`breakout-rooms-tier2-design.md`](./breakout-rooms-tier2-design.md) — sketch
- [`breakout-rooms-tier3-design.md`](./breakout-rooms-tier3-design.md) — sketch + risks

---

## 1. Executive summary

**A surprise from the survey:** a substantial chunk of breakout-room infrastructure is already
in the codebase (Phases 8 / 8.5 / 9.7 / 9.8). This plan **extends rather than rebuilds** that
infra. Tier 1 mostly closes implementation gaps (group-targeted teacher screen-share, true
"join group" expanded view) rather than designing from scratch.

**Topology** is star (teacher as relay) — already implemented and matches the school-LAN
deployment. **State authority** is the teacher — already implemented (`ControlServer._studentRoomMap`
is canonical). **Routing** uses the application `EndpointId` + envelope `TargetEndpointId` +
receiver-side `IsForMe` filter — same pattern as every other targeted-to-one-student operation
in the codebase. No Guid-namespace gap like the 12-B regression.

**Three tiers, ~1.5–2 weeks total**:

| Tier | Scope | Days | New protocol msgs | Touches |
|---|---|---|---|---|
| **1** | Groups + teacher join + group-targeted teacher share | 3–5 | `GroupSnapshot`, `GroupTeacherJoined/Left`, `GroupScreenStreamStart/Frame/Stop` (teacher→group) | Existing `ControlServer`, `ScreenBroadcaster`, new `GroupTeacherView`, status-quo `MultiRoomTeacherView` retired |
| **2** | Student↔student screen + per-group text chat | 3–5 | `StudentGroupScreenStart/Frame/Stop`, optional `GroupTextChat` (vs reuse `ChatRoom`) | `StudentBroadcaster` group-aware fan-out, peer-view window |
| **3** | Group voice chat (PTT default per addendum) | 5–7 | `VoiceAudioFrame`, `MicMuteRequest`, `MicStateUpdate`, `MicPttSet` | New `StudentMicBroadcaster`, multi-source `GroupVoicePlayer`, new `_voiceOutbox` channel |

---

## 2. What's already in the codebase (survey)

One paragraph per file, what's there + what we'll reuse vs replace.

### `src/ClassroomCtrl.Shared/Models/BreakoutRoom.cs`
DTO with `Id / Name / MemberIds / HostId` and an `AssignRandomly(students, roomCount)`
helper that shuffles and partitions. **Reuse.** Authoritative DTO shape; the
`AssignRandomly` helper feeds the teacher's "create rooms" command. No changes needed.

### `src/ClassroomCtrl.Teacher/Services/BreakoutManager.cs`
In-memory list of `BreakoutRoom` with Create/AssignToRoom/SetHost/Dissolve. **PARTIAL
DUPLICATE — recommend retire.** The canonical breakout state actually lives in `ControlServer`
(`_studentRoomMap`, `_roomNames`, `_roomHostMap`), not here; this class is referenced in zero
non-trivial code paths. Tier 1 cleanup: delete or fold its helper functions into
`ControlServer`. Flag separately so we don't conflate it with the design proper.

### `src/ClassroomCtrl.Teacher/Services/ControlServer.cs`
**The de-facto canonical breakout state**. Contains:
- `_studentRoomMap: Dictionary<Guid, Guid?>` — endpointId → roomId (null = main).
- `_roomNames: Dictionary<Guid, string>` — display names.
- `_roomHostMap: Dictionary<Guid, Guid>` — roomId → host endpointId.
- `_roomVoiceMembers: HashSet<Guid>` — Phase 9.7 placeholder (per code comment, "remain in
  MessageType.cs but are no longer used").
- Methods: `AssignToRoomAsync`, `SetRoomHostAsync`, `BroadcastChatToRoomAsync`,
  `RouteRoomChatAsync`, `HostMutePeerAsync`, `BroadcastHostMessageToMainAsync`,
  `DissolveAllRoomsAsync`, `GetStudentRoom`, `Rooms` (read-only view).
- Uses the existing targeted-via-broadcast + `IsForMe` pattern.

**Reuse and extend.** Tier 1 adds: `GroupSnapshot` broadcast on changes, group-targeted teacher
screen-share routing, `TeacherJoinedGroupAsync` / `TeacherLeftGroupAsync` notifications.

### `src/ClassroomCtrl.Networking/TcpControlServer.cs`
Four per-peer channels (`_outbox`, `_reliableOutbox`, `_audioOutbox`, `_inputOutbox`); writer-
loop priority `reliable → audio → input → one lossy`. **Reuse with one Tier-3 addition.** Tier 3
adds a fifth channel `_voiceOutbox` so group-voice (multi-source, DropOldest) doesn't contend
with teacher-loopback audio on `_audioOutbox`. Tier 1 and Tier 2 use existing channels
(reliable for group-state, lossy `_outbox` for screen).

**Transport vs application Guid distinction** (from the 12-B regression): `_peers` is keyed by
the server-generated transport peerId, NOT by the student's `EndpointId`. Every targeted
operation in the codebase uses fan-out + `IsForMe` instead of dict lookup. Our additions follow
the same pattern.

### `src/ClassroomCtrl.Teacher/Services/ScreenBroadcaster.cs`
Captures teacher's screen, encodes (H.264 or MJPEG), broadcasts via
`_server.BroadcastScreenFrameAsync(...)` which routes through `_tcp.BroadcastAsync` (`_outbox`).
**Extend for Tier 1.** Add `GroupId? TargetGroupId` to `ScreenStreamFrameMessage` (or new
`GroupScreenStreamFrameMessage`); broadcaster decides at runtime whether to send "to all"
(current) or "to group" (new). Per-group framing is the same shape; routing is the only diff.

### `src/ClassroomCtrl.Student.Agent/StudentBroadcaster.cs`
Mirror of the teacher broadcaster. **Extend for Tier 2** with "additional recipients in same
group" so a student's screen can fan out to their group peers + teacher simultaneously.

### `src/ClassroomCtrl.Teacher/Services/AudioBroadcaster.cs`
Teacher loopback + mic capture, mixed to 16 kHz mono PCM, routed via `BroadcastAudioAsync` →
`_audioOutbox`. **Reference shape for Tier 3.** Student-side `StudentMicBroadcaster` is a near-
copy of this class but reading from default capture device (mic) instead of WASAPI loopback,
and routing to a per-group `VoiceAudioFrame` message on a new `_voiceOutbox`.

### `src/ClassroomCtrl.Student.Agent/AudioPlayer.cs`
Single-source bounded jitter buffer (200 ms pre-buffer, 400 ms soft cap, 100 ms WaveOut).
**Reference shape for Tier 3.** New `GroupVoicePlayer` maintains one `BufferedWaveProvider`
per active source endpoint, feeds them into a `MixingSampleProvider`, plays the mix via
`WaveOutEvent`. The jitter-buffer policy carries over per source.

### `src/ClassroomCtrl.Shared/Protocol/Messages.cs` + `MessageType.cs`
- `MessageType.Breakout*` (0x0600–0x0603): `BreakoutCreate`, `BreakoutAssign`,
  `BreakoutDissolve`, `BreakoutHostSet`. **Only `BreakoutAssign` is wired**; the other three
  are dead-letter codes that the current implementation works around by re-broadcasting
  `BreakoutAssign` per member.
- `MessageType.HostAction*` (0x0610–0x0612): `HostActionMute`, `HostActionShare`,
  `HostActionMessageToMain`. Wired through `HostFloatingToolbar`.
- `MessageType.RoomVoice*` (0x032F, 0x0330): defined but unused (Phase 9.7 placeholder).
- `BreakoutAssignMessage` (`RoomId / RoomName / StudentIds / HostStudentId`) — current per-
  student targeted broadcast carries this; the `StudentIds` list isn't actually populated by
  the sender (assignment is one-by-one via `AssignToRoomAsync`).
- `ChatMessage.RoomId` is already part of the chat DTO; room-chat reuses `MessageType.ChatRoom`.

**Plan:**
1. **Wire the previously-defined message types** (BreakoutCreate, BreakoutDissolve, BreakoutHostSet)
   so the protocol is honest about what happens. **Or**, simpler, **add a new `GroupSnapshot`**
   that carries the entire state atomically + use `BreakoutAssign` for per-student deltas. The
   second option is what every "Zoom-like" peer-state design uses; recommended.
2. **Add the Tier-1/2/3 new types** in §4 below.

### `src/ClassroomCtrl.Teacher/StudentScreenWindow.xaml.cs`
Single-student fullscreen viewer with frame render + remote control. **Reference for Tier 1**
"join group" expanded view: open a similar window that hosts a grid of N `StudentScreenView`
controls + per-cell remote-control buttons, instead of one big image. Reuses the same
underlying StudentStreamFrame events.

### `src/ClassroomCtrl.Student.Service/ClassroomWorker.cs`
TCP message dispatch with `IsForMe(env)` filter (matches `_endpointId` or `TargetEndpointId == Guid.Empty`).
**Extend** the filter: also match if the envelope's `TargetGroupId` corresponds to our current
room. New code path: `IsForMyGroup(env) => env.TargetGroupId.HasValue && env.TargetGroupId == _myRoomId`.
This is the foundational route hook every Tier-1+ message uses.

### `src/ClassroomCtrl.Teacher/MultiRoomTeacherView.xaml(.cs)`
Existing minimal multi-room view: list of rooms, "Send chat" button per room. **Replace in
Tier 1** with a richer `GroupManagerView` that handles creation, assignment (drag-drop or
explicit), random/auto-balance, rename, host-set, dissolve, and a "Join Group N" button per
row. The existing minimal view stays in place until the new one ships.

### `src/ClassroomCtrl.Student.Agent/HostFloatingToolbar.xaml(.cs)`
Student-side host UI: 3 buttons (mute peer, share screen [denied in v1], send message to main).
**Unchanged in Tier 1**; Tier 2 adds a 4th button (start group screen-share to peers).

---

## 3. Architectural decisions (with addendum overrides applied)

For each: chosen option, why, alternatives considered, risk, validation requirement.

### Decision 1 — Topology
- **Chosen:** Star (teacher relays everything).
- **Why:** Already implemented; reuses every existing channel; school LAN doesn't need NAT
  traversal; mesh's NAT/firewall code is moot here.
- **Alternative:** P2P mesh per group (Tier 2 screen, Tier 3 voice). Pro: teacher bandwidth not a
  bottleneck. Con: ~3× the code, breaks the "all state through teacher" invariant, complicates
  recording, harder to mute centrally.
- **Risk:** Teacher NIC saturation at extreme class sizes (addressed in bandwidth math, §6).
- **Validation:** none additional — already in use.

### Decision 2 — Group state authority
- **Chosen:** Teacher (single source of truth).
- **Why:** Already implemented; students never mutate; eliminates split-brain.
- **Alternative:** Distributed (host-managed within group). Worse for the "teacher can dissolve
  rooms at any time" requirement.
- **Risk:** Teacher restart loses in-memory state (mitigated by Decision 8 persistence).

### Decision 3 — Routing key
- **Chosen:** Application `EndpointId` + new optional `Envelope.TargetGroupId` field.
- **Why:** Matches every existing targeted operation; sidesteps the 12-B Guid-namespace gap
  (transport peerId is server-generated, never sees the app); IsForMe extension is one line.
- **Alternative:** A real `endpointId → connectionPeerId` map in `TcpControlServer` (the 12-B
  "Option 2"). Better long-term but not justified by Tier 1/2/3 needs alone.
- **Risk:** None — every message in the codebase already targets via this pattern.

### Decision 4 — Audio mixing strategy (Tier 3)
- **Chosen:** Student-side mixing (SFU-style: teacher forwards each peer's mic to other group
  members; each student mixes locally).
- **Why:** Bounded teacher CPU regardless of group voice activity; standard industry shape;
  matches the per-source jitter-buffer policy from 11-C.
- **Alternative:** Server-side mixing (teacher pre-mixes per recipient). Pro: lower student CPU.
  Con: quadratic teacher CPU with group size; mixing latency adds to perceived latency; one
  bad source poisons the mix for all listeners (no per-source mute on listener side).
- **Risk:** Low-spec student CPU mixing 4–5 streams (covered in Tier 3 risks).

### Decision 5 — Mic default (OVERRIDDEN by addendum)
- **Chosen (override):** **PTT (push-to-talk, hold-key-to-speak) is the default; always-on is
  explicit opt-in.**
- **Why (addendum):** Worst-case hardware = laptop built-in mic + speaker = guaranteed feedback
  without serialization; PTT serializes naturally to one speaker at a time; matches
  Discord/TeamSpeak classroom convention; reduces bandwidth (only active speakers transmit).
- **PTT hotkey:** configurable, default `Space-bar` (must capture into the agent's mic-broadcast
  state, not into the focused text editor — see Tier 3 risk).
- **Always-on mode:** explicit per-student toggle in mic UI; the teacher's mic-monitor +
  group-host can override-mute.
- **Alternative:** Mute-by-default + click-to-unmute (original Decision 5). Rejected for the
  hardware-class reason above.
- **Risk:** Space-bar capture interferes with normal typing while a peer's voice is wanted;
  hotkey should be configurable + visible (Tier 3 design).

### Decision 6 — Teacher in N groups simultaneously
- **Chosen:** No, one at a time.
- **Why:** Simpler audio routing (teacher's outbound mic targets exactly one group while
  joined); simpler "leave to return to whole class" semantics; UI clarity.
- **Alternative:** Teacher monitors multiple groups simultaneously (e.g., overhear N groups at
  once). Possible later but adds mixing complexity + cognitive load on teacher.
- **Risk:** None for v1.

### Decision 7 — Voice channel transport (Tier 3)
- **Chosen:** **New per-peer `_voiceOutbox`** (cap 6, DropOldest), separate from `_audioOutbox`.
- **Why:** `_audioOutbox` is sized for a single 100 ms × 3-deep buffer of one teacher audio
  stream; voice in a 5-person group means up to 4 inbound streams per student × bursty PTT
  patterns. Sharing the channel could cause teacher's loopback audio to get evicted by voice
  bursts (and vice versa). Per-peer per-source ring buffer in `GroupVoicePlayer` is the right
  shape.
- **Alternative:** Reuse `_audioOutbox` with a `Kind` discriminator in the message. Smaller
  channel-count delta, but the eviction-contention risk above is real.
- **Risk:** One more channel to keep tuned; needs Tier-3 2-PC validation under load.

### Decision 8 — Group persistence
- **Chosen:** Optional `%ProgramData%\NTY\ClassroomCtrl\groups.json` with TEMPLATES (room names
  + intended membership). Active membership state is session-only.
- **Why:** Teachers reuse the same student grouping across many sessions (project teams that
  meet weekly); but only the students currently online get auto-assigned at session-load.
- **Alternative:** Pure session-only. Forces teacher to re-create groups every class.
- **Alternative:** Persist active membership too. Confusing when a student is offline.
- **Risk:** Roster drift (a student leaves the class long-term but stays in a saved group);
  Tier 1 design includes "Clear template" + per-group "Reseed from current online students".

### Decision 9 — Group size limit
- **Chosen (addendum):** 4–6 typical, hard cap 8.
- **Why:** UI grid (2×3 / 2×4) fits comfortably; bandwidth math (§6) holds with margin; pedagogy
  research generally favors 4–6 for breakout discussion.
- **Risk:** Hard cap enforcement at the assignment UI (not just visual): assignment to a
  full group is rejected with a toast.

### Decision 10 — Max groups
- **Chosen (addendum):** 8 typical, hard cap 12. (Adjusted up from "8 suggested" because the
  addendum's class-size 40 / group-4 = 10 groups can exceed 8.)
- **Why:** Class 40 / group 4 = 10 groups; class 40 / group 5 = 8 groups. Need headroom.
- **Risk:** UI tab/grid for 10–12 groups starts to crowd — Tier 1 design includes scrollable list.

---

## 4. Wire protocol additions (full spec)

Code-point allocation (extending the existing layout):

```
0x0600  BreakoutCreate              (existing, currently UNUSED — wire it or remove)
0x0601  BreakoutAssign              (existing, in use)
0x0602  BreakoutDissolve            (existing, currently UNUSED — wire it or remove)
0x0603  BreakoutHostSet             (existing, currently UNUSED — wire it or remove)
0x0610  HostActionMute              (existing, in use)
0x0611  HostActionShare             (existing, denied stub)
0x0612  HostActionMessageToMain     (existing, in use)

  ── Tier 1 additions ──
0x0620  GroupSnapshot               (T→all, current state — atomic refresh)
0x0621  GroupTeacherJoined          (T→group + non-group, "teacher is now in this group")
0x0622  GroupTeacherLeft            (T→group + non-group, "teacher returned to main")
0x0623  GroupScreenStreamStart      (T→group, teacher's screen now targets THIS group only)
0x0624  GroupScreenStreamFrame      (T→group, framed screen content for group)
0x0625  GroupScreenStreamStop       (T→group, end group-targeted share)

  ── Tier 2 additions ──
0x0630  StudentGroupScreenStart     (S→T→group, student presenting screen to group)
0x0631  StudentGroupScreenFrame     (S→T→group)
0x0632  StudentGroupScreenStop      (S→T→group)
0x0633  GroupTextChat               (S↔T↔group, optional — reuses ChatRoom 0x0102 if not needed)

  ── Tier 3 additions ──
0x0640  VoiceAudioFrame             (S→T→group peers, mic PCM 100 ms)
0x0641  MicMuteRequest              (T→S, teacher forces mic mute)
0x0642  MicStateUpdate              (S→T, mic on/off/PTT/active-talking)
0x0643  MicPttSet                   (T→S, configure PTT mode + hotkey)
```

### `Envelope` extension (minimal new field)

Add **`Guid? TargetGroupId`** (nullable) to the existing `Envelope`. Receivers extend `IsForMe`:

```csharp
private bool IsForMe(Envelope env)
{
    if (env.TargetEndpointId == Guid.Empty) return true;
    if (env.TargetEndpointId == _endpointId) return true;
    if (env.TargetGroupId.HasValue && env.TargetGroupId == _myRoomId) return true;
    return false;
}
```

**Wire compat:** if `Envelope` is MessagePack-serialized with positional `[Key(n)]`, the new
field gets the next available key. Old clients deserializing miss it gracefully (MessagePack
defaults nullable fields to null). Forward-only compat: bump no version. New clients receiving
an old envelope work fine. **Verify** the existing serializer config in `Envelope.cs` before
committing the field — if it's a fixed-positional layout, the field MUST go at the end.

### Tier 1 DTOs

```csharp
[MessagePackObject(true)]
public class GroupDescriptor
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public List<Guid> MemberIds { get; set; } = new();
    public Guid? HostId { get; set; }
}

[MessagePackObject(true)]
public class GroupSnapshotMessage
{
    /// <summary>All current groups + their membership.  Sent on group changes
    /// (atomically) so clients don't need to track per-student deltas.</summary>
    public List<GroupDescriptor> Groups { get; set; } = new();
    /// <summary>Optional: which group (if any) the teacher is currently joined to.</summary>
    public Guid? TeacherJoinedGroupId { get; set; }
}

[MessagePackObject(true)]
public class GroupTeacherJoinedMessage
{
    public Guid GroupId { get; set; }
    public string GroupName { get; set; } = "";
}

[MessagePackObject(true)]
public class GroupTeacherLeftMessage
{
    public Guid GroupId { get; set; }
}

[MessagePackObject(true)]
public class GroupScreenStreamControlMessage
{
    public Guid GroupId { get; set; }
    public bool Start { get; set; }            // true = Start, false = Stop
    public VideoCodec Codec { get; set; }
}

// Reuses existing ScreenStreamFrameMessage payload shape for 0x0624 — only the
// envelope's MessageType + TargetGroupId differs.  No new DTO required.
```

### Tier 2 DTOs

Same shape as Tier 1's group screen but originating from a student:

```csharp
[MessagePackObject(true)]
public class StudentGroupScreenControlMessage
{
    public Guid GroupId { get; set; }
    public Guid PresenterEndpointId { get; set; }
    public bool Start { get; set; }
    public VideoCodec Codec { get; set; }
}
// StudentGroupScreenFrame reuses StudentStreamFrameMessage shape.
```

For text chat: **prefer reusing `ChatRoom` + existing `ChatMessage.RoomId`**; no new DTO needed
unless we need attachments/reactions per group. Decision deferred to Tier 2 design.

### Tier 3 DTOs

```csharp
[MessagePackObject(true)]
public class VoiceAudioFrameMessage
{
    public Guid GroupId { get; set; }
    public Guid SourceEndpointId { get; set; }
    public byte[] PcmData { get; set; } = Array.Empty<byte>();   // 16 kHz mono 16-bit
    public int SampleRate { get; set; }                          // = 16000
    public int Channels { get; set; }                            // = 1
    public int BitsPerSample { get; set; }                       // = 16
    public long TimestampUtcMs { get; set; }
    public int FrameSeq { get; set; }                            // per-source monotonic
}

[MessagePackObject(true)]
public class MicMuteRequestMessage
{
    public bool Muted { get; set; }
    public string Reason { get; set; } = "";  // shown in student UI
}

[MessagePackObject(true)]
public class MicStateUpdateMessage
{
    public Guid SourceEndpointId { get; set; }
    public bool MicOn { get; set; }
    public bool IsActiveTalking { get; set; }     // RMS-driven; rate-limited
    public MicMode Mode { get; set; }             // Off / PTT / AlwaysOn
}

public enum MicMode : byte { Off = 0, Ptt = 1, AlwaysOn = 2 }

[MessagePackObject(true)]
public class MicPttSetMessage
{
    public MicMode Mode { get; set; }
    public string HotkeyVk { get; set; } = "Space";  // VK-name string
}
```

### Channel routing summary

| MessageType | Channel | Why |
|---|---|---|
| `BreakoutAssign`, `GroupSnapshot`, `GroupTeacher*`, `MicPttSet`, `MicMuteRequest` | `_reliableOutbox` | Must arrive in order; small. |
| `GroupScreenStreamFrame`, `StudentGroupScreenFrame` | `_outbox` | Same as existing screen-share; DropOldest is correct semantics. |
| `RemoteControl*` (existing) | `_inputOutbox` | (Phase 12-B fix already in place.) |
| `VoiceAudioFrame` | **NEW** `_voiceOutbox` (Tier 3) | Multi-source group voice; isolating from teacher loopback. |
| `MicStateUpdate`, group-state acks | `_reliableOutbox` | Small + must arrive. |
| `GroupTextChat` / `ChatRoom` | `_reliableOutbox` | Existing reliable path. |

---

## 5. Open unknowns

Locked-in answers (addendum) apply throughout. Remaining unknowns the dev should clarify
before Tier 1 *kickoff* (not before this design lands):

1. **UX expectations for group creation.** Tier 1 design proposes (a) random-N + (b) drag-
   drop. The addendum doesn't lock this. Pick the one matching the customer's workflow.
2. **Persistence scope of templates.** Tier 1 implements *templates* (room names + intended
   membership lists), session-only active state. Confirm.
3. **"Join group" while still seeing whole class.** Tier 1 design picks *replace main view
   with expanded group view + "Leave" returns to whole class*. Other option: split-screen
   (group panel + class panel). Pick.
4. **Recording per-group.** Out of scope for v1; flag.
5. **Teacher voice routing while joined to a group.** Confirm: teacher mic → group only (not
   broadcast back to main class). Default in Tier 3 design.

---

## 6. Risks across tiers

### Bandwidth (Tier 3, the only real concern at the addendum's class-size 40)

Per addendum: worst case = 40 students in 8 groups of 5 + teacher.
- Mic PCM at 16 kHz × 16-bit × 1 ch ≈ 32 KB/s = **~256 kbps per active mic stream**.
- Per-student incoming: 4 peers × 256 kbps = **~1.0 Mbps inbound**.
- Teacher relay aggregate: if every group has 1 active speaker (PTT serialization), that's
  8 simultaneous mic streams × 256 kbps × ≤4 recipients each = **~8 Mbps egress max**.
- With screen-share running concurrently (e.g. teacher H.264 at 500 kbps × 40 students =
  20 Mbps), total teacher egress ≈ 28 Mbps. **Comfortably under gigabit; even fits 100 Mbps**.

PTT-default (addendum override) drastically helps because typical PTT load is one speaker per
group at a time → bandwidth halves vs always-on.

**Opus encoding (deferred Tier 3.x)** would cut mic bandwidth ~10× (256 kbps → 24-32 kbps),
relevant only if class scales beyond 60–80 students or LAN drops to Wi-Fi. Flagged for later.

### Acoustic echo cancellation (Tier 3, the main quality concern)

Per addendum, 3-layer strategy:

1. **PTT default** — serializes speakers; biggest single mitigation.
2. **WASAPI AEC mode** — Windows native; enable via NAudio + WASAPI capture with
   `AudioClientStreamFlags.None | _Loopback | _RawProcessingMode` flags and the system's AEC
   effects pipeline. **Risk:** AEC quality varies by driver; some IT-managed Windows installs
   disable system effects. Investigate at Tier 3 design + spike on real hardware.
3. **Documentation** — "USB headset recommended" in customer setup. Built-in mic+speaker
   admits residual echo.

### Multi-stream mixing CPU on low-spec student boxes

NAudio's `MixingSampleProvider` + per-source `BufferedWaveProvider` + WASAPI output is
well-optimized; mixing 4-5 streams at 16 kHz mono adds ~1–2% CPU on a 7th-gen i5. Risk is real
on Atom / Celeron laptop fleets — flag for customer-hardware identification before Tier 3
implementation.

### Privacy indicator placement (Tier 3)

Strong customer-facing risk in a school context. Tier 3 design specifies:
- Persistent **on-screen banner** when mic is live ("🎤 Mic ON in {GroupName}") — same pattern
  as `RemoteControlBanner`.
- **Always-visible** for AlwaysOn; **flashes during PTT down** for PTT.
- Teacher's force-mute is overt: a balloon notification on the student.

### Voice activity detection (VAD) threshold tuning (Tier 3)

Simple RMS threshold (e.g. > -40 dBFS for > 100 ms) drives the "speaking" UI indicator. PTT
makes this mostly cosmetic. Risk: noisy classrooms trigger false positives — tier-3 design
suggests adaptive threshold or a simple energy-history filter, but keep it minimal.

### Group state divergence after teacher restart

If teacher restarts mid-session, in-memory `_studentRoomMap` is lost. Students will keep
their current `_myRoomId` until they get a new `BreakoutAssign`. **Mitigation:** Tier 1 spec
includes `GroupSnapshot` on every student's reconnect (Hello-ack path) so teacher restart
recovers state at the cost of "all students temporarily back to main until snapshot replays".
Acceptable. Persistent template (Decision 8) is the other half of the recovery.

### Hotkey capture (Tier 3 PTT)

Space-bar default captured by WPF in the agent — but the agent isn't usually focused while a
student is interacting with another app. The Tier 3 PTT design needs a **low-level keyboard
hook** (`SetWindowsHookEx(WH_KEYBOARD_LL, ...)`) to capture the hotkey regardless of focus.
Mirror style of `RemoteControlReceiver` but in reverse direction (capture not inject). The
hotkey itself stays pass-through (Space still types a space).

---

## 7. Implementation order

1. **Phase 13-B (Tier 1)** — see `breakout-rooms-tier1-design.md`. ~3–5 days.
2. Dev runs 2-PC validation per the Tier 1 acceptance checklist.
3. **Phase 13-C (Tier 2)** — see `breakout-rooms-tier2-design.md`. ~3–5 days.
4. Dev runs 2-PC validation per the Tier 2 acceptance checklist.
5. **Phase 13-D (Tier 3)** — see `breakout-rooms-tier3-design.md`. ~5–7 days, with explicit
   AEC + low-spec-CPU spikes in the first 1–2 days.
6. Multi-PC validation (3–4 PCs needed to actually exercise group voice).

---

## 8. What this design deliberately does NOT change

- The Phase 12-B remote-control fan-out + `_inputOutbox` path. Untouched.
- Phase 11-C audio pump + `_audioOutbox`. Untouched (Tier 3 adds parallel `_voiceOutbox`).
- Phase 11-B screen-share (capture / encode / lossy `_outbox`). Tier 1 group-share is a
  routing extension, not a pipeline change.
- The 10.21 reliable channel for file transfers. Untouched.
- Wire format ordering of existing `Envelope` fields (the new `TargetGroupId` MUST be
  appended at the highest `Key` index).

These are all already validated and we don't want to re-litigate any of them.

---

## 9. Cleanup that should ride this feature

While we're here, two pieces of dead/half-built code should be cleaned up:

1. **`BreakoutManager`** is a half-duplicate of state that lives in `ControlServer`. Tier 1
   design retires it or folds its single useful helper (`AssignRandomly`) into the
   `MainViewModel.CreateRooms` call site.
2. **`MultiRoomTeacherView`** is the minimal Phase 9.8 demo (just per-room chat button); Tier 1
   replaces it with `GroupManagerView`. Existing window stays until the new one is ready; flag
   for deletion in Tier 1 PR.
3. **Unused `MessageType`s** (`BreakoutCreate`, `BreakoutDissolve`, `BreakoutHostSet`): Tier 1
   either wires them (preferred — cleaner protocol) or removes them. Decision: **wire them**
   so the protocol is honest, even if `BreakoutAssign` could carry the deltas — atomic
   `GroupSnapshot` is the primary refresh, but explicit Create/Dissolve/HostSet messages make
   logs grep-friendly.
4. **`RoomVoiceJoin / RoomVoiceLeave`** placeholder messages (0x032F / 0x0330) — keep
   reserved for Tier 3 mic-mode signaling, or remove and reuse codepoints. The Tier-3 spec
   uses fresh codepoints in 0x064x; recommend removing the placeholders.
