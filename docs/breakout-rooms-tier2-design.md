# Breakout Rooms — Tier 2 Design (Sketch)

**Status:** sketch, not implementation-ready.
**Prereq read:** [`breakout-rooms-architecture.md`](./breakout-rooms-architecture.md),
[`breakout-rooms-tier1-design.md`](./breakout-rooms-tier1-design.md).
**Estimated effort:** 3–5 days.
**Triggers Tier 2 readiness:** Tier 1 shipped + validated + customer confirms collaboration
(not just monitoring) is needed.

---

## 1. Scope

Tier 2 turns breakout rooms into **collaborative spaces** — group members can see each other
and exchange text. Voice stays out (Tier 3).

**In:**
1. **Student → group screen sharing** — a designated presenter (host or self-selected) shares
   their screen with the group's other members; routed through the teacher (star topology
   per Decision 1).
2. **Per-group text chat** — full extension of the existing room-chat path.

**Out:**
- Voice (Tier 3).
- Multiple simultaneous presenters per group (Tier 2 allows one presenter at a time per
  group; switching is explicit).
- P2P mesh (rejected per Decision 1).

---

## 2. UX

### 2.1 Student-side: presenter toggle

In the existing student `HostFloatingToolbar` (host role) and a new per-student "Group" panel
(non-host members), add a **"Present screen to group"** button.

- **Host:** can always start/stop presenting; can also designate another member as presenter.
- **Non-host member:** can request to present (sends a `PresentRequest` to host or teacher);
  in v2 first cut, allow direct start without approval to keep it simple; if politeness is a
  problem, add the request flow later.

On start: a small overlay banner on the presenter's screen reads "Presenting to {GroupName}";
matches the `RemoteControlBanner` visual language for consistency.

### 2.2 Student-side: peer screen view

Each group member sees the active presenter's screen in a new **`GroupPeerScreenWindow`**
(modeless). Opens automatically on `StudentGroupScreenStart`. Closes on stop.

```
┌──── Presenting: Som  · Group 1 ────────────────[X]┐
│  [Live screen frame, 1280×720 fit]                │
│                                                    │
│  Status: 18 fps · 720p                            │
└────────────────────────────────────────────────────┘
```

Plain viewer only — no remote control of peer screens (that's host privilege via teacher
relay if at all; Tier 2 doesn't add it).

### 2.3 Teacher-side

- `GroupExpandedView` (from Tier 1) gets a small "Presenter: {Name}" indicator in the group
  header.
- `GroupManagerView`: per-group row shows the current presenter (if any) and a "Force stop"
  button — teacher override.

### 2.4 Text chat

Already wired in Phase 9.8 (`ChatRoom` + `RouteRoomChatAsync`). Tier 2 only adds:
- A per-group chat panel in the student's main UI (toggle to switch between "Class chat" and
  "Group chat"). The group chat is empty unless the student is currently in a group.
- Optional: chat history per group while the session is alive (in-memory).

---

## 3. Wire messages

From architecture.md §4 (Tier 2 codepoints):

| Code | Type | Direction | Channel |
|---|---|---|---|
| `0x0630` | `StudentGroupScreenStart` | S → T → group peers | `_reliableOutbox` |
| `0x0631` | `StudentGroupScreenFrame` | S → T → group peers | `_outbox` (DropOldest) |
| `0x0632` | `StudentGroupScreenStop` | S → T → group peers | `_reliableOutbox` |
| `0x0633` | `GroupTextChat` (optional; **prefer reusing `ChatRoom` 0x0102** if no extra payload needed) | S → T → group | `_reliableOutbox` |

The control messages carry `GroupId + PresenterEndpointId + Codec`; frame payload reuses
`StudentStreamFrameMessage`. The teacher relays: on receipt of a Frame from presenter P, fan
out to all group members != P.

---

## 4. Major components

### 4.1 Student-side

**Extend `StudentBroadcaster`** (`src/ClassroomCtrl.Student.Agent/StudentBroadcaster.cs`):
- New mode flag: `bool TargetGroup { get; set; }` (default false = upstream to teacher only,
  as today).
- When true: each captured + encoded frame goes as `StudentGroupScreenFrame` with
  `TargetGroupId = _myRoomId`. Otherwise the existing `StudentStreamFrame` upstream path.

**New `GroupPeerScreenWindow`** — modeless WPF window, mirrors the simplest path of
`StudentScreenWindow` (frame render only, no remote control).

### 4.2 Teacher-side (relay only)

**Extend `ControlServer`**: handle incoming `StudentGroupScreenStart/Frame/Stop` envelopes.
- Verify sender is a member of the claimed `GroupId` (defensive — drop if not).
- Re-broadcast to other members of the group via fan-out (mirrors how `DemoFrame` (Phase 9.1)
  rebroadcast works — see `_currentDemoSourceId` path).
- Maintain `_groupPresenter: Dictionary<Guid, Guid>` map (groupId → presenterEndpointId) so
  the teacher UI can show "Presenter: Name" and reject conflicts.

**`GroupExpandedView`** — gets the presenter indicator + "Force stop" button.

### 4.3 Routing

Reuses Tier 1's `TargetGroupId` envelope field. No new transport channel needed; screen-share
frames continue to ride `_outbox` (DropOldest, cap 16) as they already do; the student-to-
teacher leg is the same as today's `StudentStreamFrame`.

---

## 5. State transitions

```
no-presenter ──StudentGroupScreenStart(by P)──> presenting(P)
                                                    │
                                                    ├──StudentGroupScreenStop──> no-presenter
                                                    ├──Teacher force-stop──> no-presenter
                                                    └──P leaves group──> no-presenter
```

Conflict resolution: if member Q starts while P is presenting, the teacher detects via
`_groupPresenter` map and **rejects Q's start** (sends Q a `PresenterDenied` notification
chat). Simple first-cut; promotes politeness without negotiation overhead.

---

## 6. Effort estimate

| Sub-task | Days |
|---|---|
| Wire protocol additions (codepoints + DTOs) | 0.5 |
| `StudentBroadcaster.TargetGroup` mode + fan-out | 0.5 |
| Teacher-side relay logic + presenter map | 0.75 |
| `GroupPeerScreenWindow` (new student-side viewer) | 0.75 |
| Presenter toggle UI on student (host toolbar + non-host panel) | 0.5 |
| `GroupExpandedView` extensions (presenter indicator + force stop) | 0.25 |
| Per-group chat panel toggle on student | 0.5 |
| 2-PC validation (ideally 3-PC) | 0.75 |
| **Total** | **4.5** |

3-PC validation strongly preferred: presenter (PC2) + viewer (PC3) + teacher relay (PC1).

---

## 7. Risks & open questions

### Bandwidth at scale

Per addendum (40 students, 8 groups of 5):
- One presenter per group → 8 simultaneous student-to-teacher uplinks.
- Each at H.264 1080p 4 FPS ≈ 500 kbps → 4 Mbps total uplink across class.
- Teacher fan-out: 8 × 500 kbps × 4 recipients each = 16 Mbps egress.
- With Tier 1 teacher whole-class share running concurrently (20 Mbps) → 36 Mbps total
  teacher NIC. Comfortable on gigabit; tight on 100 Mbps with screen-share.
- **Mitigation:** if MJPEG (Tier 1 default codec): per-group share lifts to 2 Mbps per
  presenter × 8 = 16 Mbps uplink; teacher fan-out 64 Mbps. Hits 100 Mbps LAN ceiling at peak.
  Tier 2 design recommends **forcing H.264 for group-presenter screen-share** to keep within
  budget. Whole-class share can stay MJPEG default per inc4.1.

### Concurrent presenter conflict UX

The simple "first wins, second denied" rule is curt. Alternative: queue with a "request to
present" notification to the current presenter. Defer to v2.x if the simple rule annoys users.

### Recording per-group screen share

Out of scope; flag for future. The teacher's existing `RecordingService` is whole-class only.

### Presenter screen overlap with teacher's group share

If teacher has group-targeted share active AND a student starts group screen share, two
streams fight for the same recipients' viewer. Recommend: **teacher's group share takes
priority** (student's presenter start is rejected with a notification "Teacher is sharing"),
OR show both in a split panel. Pick simple rejection for v1.

### Frame ordering

Each presenter has its own per-source `FrameSeq`; receivers track presenter source and reset
on presenter switch. No new mechanism needed; mirrors existing `StudentStreamFrame` shape.

---

## 8. Out of scope (Tier 3+ / future)

- Multiple simultaneous presenters per group.
- Per-student annotation overlay on the presenter's screen (would need the existing
  `DrawingStroke` Phase 9.2 routes extended to per-group).
- File sharing within a group (current file path is whole-class only).
- Per-group breakout recordings.
