# Breakout Rooms — Tier 1 Design (Implementation-Ready)

**Status:** design only, no code.
**Prereq read:** [`breakout-rooms-architecture.md`](./breakout-rooms-architecture.md).
**Estimated effort:** 3–5 days (1 day cleanup + 2–3 days new features + 1 day 2-PC validation).

---

## 1. Scope

What Tier 1 delivers — and not.

**In scope:**

1. **Group manager (teacher UI)** — create, rename, dissolve groups; manual + random/auto-
   balance assignment; designate host per group; persistent templates.
2. **Group state propagation** — atomic `GroupSnapshot` on every change; per-student
   `BreakoutAssign` retained as today (no breaking-change for student-agent state machine).
3. **Group-targeted teacher screen share** — teacher selects "share to group X"; only group X's
   students receive frames; main classroom continues unaffected (or share is paused for them,
   teacher choice).
4. **Teacher "Join Group" expanded view** — teacher opens a per-group expanded view that
   contains a grid of group members' screens + per-member remote control button; "Leave"
   returns to whole-class view.
5. **Cleanup** — retire `BreakoutManager` duplicate state; wire previously-defined-but-dead
   message types or remove; replace minimal `MultiRoomTeacherView` with the new
   `GroupManagerView`.

**Out of scope (deferred to Tier 2/3):**

- Student↔student screen sharing within a group (Tier 2).
- Per-group voice chat (Tier 3).
- Per-group text chat is NOT new work — already shipped in Phase 9.8 (`ChatRoom` message +
  `RouteRoomChatAsync`). Tier 2 may extend with attachments, but reuse is fine.

---

## 2. UX

Three new surfaces + edits to the main teacher dashboard.

### 2.1 Group manager (replaces `MultiRoomTeacherView`)

`GroupManagerView` — modeless window, opened from the existing "Multi-room" / "Breakout"
button on the main toolbar.

```
┌───────────────────────────── Group Manager ──────────────────────────┐
│  [Create Random ▼] [+ New Empty Group]  [Auto-Balance]  [Dissolve All]│
│                                                                       │
│  ┌─ Group 1 (4 members) ────  Host: Som   [⋮]  ────────────────┐     │
│  │  ✕ Som | ✕ Pim | ✕ Kao | ✕ Mai             [Join] [Share→]  │     │
│  └──────────────────────────────────────────────────────────────┘     │
│  ┌─ Group 2 (3 members) ────  Host: —     [⋮]  ────────────────┐     │
│  │  ✕ Aor | ✕ Nat | ✕ Pat                    [Join] [Share→]  │     │
│  └──────────────────────────────────────────────────────────────┘     │
│  ┌─ Unassigned (6 students) ────────────────────────────────────┐     │
│  │  □ Eve | □ Joy | □ Ton | □ Ben | □ Sai | □ Win                │     │
│  └──────────────────────────────────────────────────────────────┘     │
│                                                                       │
│  [Save as Template…]   [Load Template…]                               │
└───────────────────────────────────────────────────────────────────────┘
```

**Interactions:**
- **`Create Random ▼`** — dropdown asks "create N groups" (default 4); calls
  `BreakoutRoom.AssignRandomly` over currently-online students; broadcasts the resulting
  `GroupSnapshot`.
- **`+ New Empty Group`** — adds an empty group with auto-name "Group N+1".
- **`Auto-Balance`** — re-distributes currently-online students across existing groups
  (preserves group count, evens membership). Per-student `BreakoutAssign` follows.
- **`Dissolve All`** — confirmation prompt → all students return to main classroom.
- **`✕ Name`** click on member — remove from group (back to Unassigned).
- **`☐ Name` click in Unassigned** — pops a small "Move to: Group 1 / Group 2 / …" menu.
- **`[⋮]` per group** — context menu: Rename, Set Host (sub-menu), Reseed from current
  online, Delete this group.
- **`[Join]`** — teacher opens the `GroupExpandedView` for this group.
- **`[Share →]`** — toggle: teacher's screen now broadcasts to this group only (button reads
  "Stop Sharing" while active). Mutually exclusive with normal "Share to All" — the main toolbar
  reflects state.
- **`[Save as Template…]` / `[Load Template…]`** — Decision 8 persistence (groups.json).

### 2.2 Group expanded view (`GroupExpandedView`)

Replaces or sits next to the main classroom grid while the teacher is joined to a group.
Implementation choice (per open-unknown #3): **replace main view + breadcrumb "← All Groups"
button to leave**. Less screen real-estate fights; clear mental model.

```
┌────────── Group: Group 1 (4 members)     [← Leave Group]  [Share→] ───┐
│  ┌─ Som [Host] ─────┐  ┌─ Pim ────────────┐                          │
│  │  [Screen 720p]   │  │  [Screen 720p]    │                          │
│  │  [Remote] [Lock] │  │  [Remote] [Lock]  │                          │
│  └──────────────────┘  └───────────────────┘                          │
│  ┌─ Kao ────────────┐  ┌─ Mai ────────────┐                          │
│  │  [Screen 720p]   │  │  [Screen 720p]   │                          │
│  │  [Remote] [Lock] │  │  [Remote] [Lock] │                          │
│  └──────────────────┘  └───────────────────┘                          │
└───────────────────────────────────────────────────────────────────────┘
```

- **Grid** = N × M responsive (1, 2×1, 2×2, 2×3, 3×3 depending on member count). Each cell
  hosts a `StudentScreenView` (extracted from the body of `StudentScreenWindow`'s render path).
- **Per-cell `[Remote]` / `[Lock]`** route to the existing teacher operations; no protocol
  change.
- **`[← Leave Group]`** — broadcasts `GroupTeacherLeft`; closes view; teacher returns to
  whole-class.
- **`[Share →]`** — same toggle as in the manager; group-targeted teacher screen-share.
- Implicit: `StudentStreamStart` is issued to all group members on view open; `StudentStreamStop`
  on close. Same lifecycle as today's `StudentScreenWindow`, but for N students at once.

### 2.3 Main dashboard edits

- Existing "Multi-room" toolbar button label changes → "Group Manager" (the localized
  `Btn_MultiRoom` resource gets a new value, no code-name change).
- New status indicator on the main toolbar when teacher is joined to a group: chip
  "Joined: Group N · [Leave]" — clicking returns to whole-class view.
- Per-tile (the small student-card in the existing grid): show "G1" / "G2" / "—" badge so the
  teacher can see grouping at a glance without opening the manager.

### 2.4 Student-side UX

- Existing `BreakoutAssign` toast remains. No new student UI required for Tier 1 (host toolbar
  unchanged; group expanded view is teacher-only).
- New: when teacher joins the student's group, a one-line system message in the student's chat
  ("Teacher joined this group"); reverse when teacher leaves. Drives off
  `GroupTeacherJoined/Left` messages.
- New: when teacher starts group-targeted share, the existing `ScreenStreamStart` handler
  fires as today (student doesn't need to know it's group-scoped — the routing handled it
  upstream).

---

## 3. Wire messages (from architecture.md §4)

Tier 1 introduces these codepoints; full DTOs in architecture.md:

| Code | Type | Direction | Channel | Notes |
|---|---|---|---|---|
| `0x0620` | `GroupSnapshot` | T → all | `_reliableOutbox` | Authoritative state refresh. |
| `0x0621` | `GroupTeacherJoined` | T → all | `_reliableOutbox` | Same payload to group + others; UI behaves differently per recipient. |
| `0x0622` | `GroupTeacherLeft` | T → all | `_reliableOutbox` | |
| `0x0623` | `GroupScreenStreamStart` | T → group | `_reliableOutbox` | Single envelope with `TargetGroupId` set. |
| `0x0624` | `GroupScreenStreamFrame` | T → group | `_outbox` | Reuses `ScreenStreamFrameMessage` payload. |
| `0x0625` | `GroupScreenStreamStop` | T → group | `_reliableOutbox` | |

Plus: **`Envelope.TargetGroupId: Guid?`** field added at the next available `[Key(n)]` index;
`IsForMe` extended to match `TargetGroupId == _myRoomId`.

Plus: **wire** `0x0600 BreakoutCreate`, `0x0602 BreakoutDissolve`, `0x0603 BreakoutHostSet`
(they're emitted from the teacher in their respective UI paths; students receive them but the
authoritative refresh is still via `GroupSnapshot`).

---

## 4. File-by-file changes

Implementation-ready: every file that's touched, what changes, in what order.

### 4.1 Protocol & shared

#### `src/ClassroomCtrl.Shared/Protocol/MessageType.cs`
Add the six Tier-1 codepoints (`0x0620–0x0625`).

#### `src/ClassroomCtrl.Shared/Protocol/Messages.cs`
Add: `GroupDescriptor`, `GroupSnapshotMessage`, `GroupTeacherJoinedMessage`,
`GroupTeacherLeftMessage`, `GroupScreenStreamControlMessage`.

#### `src/ClassroomCtrl.Shared/Protocol/Envelope.cs` *(verify exact file path)*
Add `public Guid? TargetGroupId { get; set; }` at the next `[Key(n)]` (HIGHEST existing key + 1).

**WIRE-COMPAT VERIFICATION REQUIRED** before merging: read the existing `Envelope`
[Key] numbering and pick the next index. Confirm MessagePack default behavior on the field
when receiving from an older client (should default to null). If the existing layout uses
positional fields without explicit keys, this CANNOT be added safely without a full audit
— flag for the implementation primer.

### 4.2 Teacher

#### `src/ClassroomCtrl.Teacher/Services/ControlServer.cs`

State (already in place from Phase 8):
- `_studentRoomMap`, `_roomNames`, `_roomHostMap` ✓ keep
- New: `Guid? _teacherJoinedGroupId` (null = whole-class)
- New: `Guid? _activeGroupShareGroupId` (null = no group-targeted screen share)

New methods:
```csharp
// Snapshot fan-out — broadcast to everyone (each receiver filters / displays).
public async Task BroadcastGroupSnapshotAsync(CancellationToken ct);

// Atomic group-create (wires BreakoutCreate per cleanup, plus snapshot).
public async Task CreateGroupAsync(string name, IList<Guid> members, CancellationToken ct);
public async Task DeleteGroupAsync(Guid groupId, CancellationToken ct);
public async Task RenameGroupAsync(Guid groupId, string newName, CancellationToken ct);
public async Task SetGroupHostAsync(Guid groupId, Guid? hostId, CancellationToken ct);
public async Task DissolveAllAsync(CancellationToken ct);  // existing DissolveAllRoomsAsync, renamed for symmetry

// Teacher join/leave — purely a notification + state stamp.  Does NOT change group membership.
public async Task TeacherJoinGroupAsync(Guid groupId, CancellationToken ct);
public async Task TeacherLeaveGroupAsync(CancellationToken ct);

// Group-targeted teacher screen-share (start/stop signaling; frame fan-out is in ScreenBroadcaster).
public async Task StartGroupScreenShareAsync(Guid groupId, VideoCodec codec, CancellationToken ct);
public async Task StopGroupScreenShareAsync(CancellationToken ct);
```

Existing methods to extend:
- `AssignToRoomAsync` — keep, also call `BroadcastGroupSnapshotAsync` after the per-student
  assign so all clients have the atomic refresh.
- `DissolveAllRoomsAsync` — rename to `DissolveAllAsync`; same behavior + snapshot broadcast.

#### `src/ClassroomCtrl.Teacher/Services/ScreenBroadcaster.cs`

Add group-aware routing:
- New property `Guid? TargetGroupId { get; set; }` — when set, every emitted frame goes out
  via `MessageType.GroupScreenStreamFrame` with `Envelope.TargetGroupId = TargetGroupId.Value`
  instead of `MessageType.ScreenStreamFrame` broadcast.
- The frame-build path (capture → encode) is unchanged; the only difference is the envelope
  type + routing field.
- On `StartGroupScreenShareAsync(groupId, codec)` from `ControlServer`: set
  `TargetGroupId = groupId`, swap codec if needed, and emit a `GroupScreenStreamStart`
  signaling envelope.
- On `StopGroupScreenShareAsync`: clear `TargetGroupId` and emit `GroupScreenStreamStop`.

**Risk:** if the teacher is already sharing to whole class AND tries to share to a group, the
two paths conflict. Tier 1 decision: **group share preempts whole-class share**; the toolbar
state shows which is active; switching toggles cleanly. Whole-class share resumes only when
teacher explicitly clicks it again.

#### `src/ClassroomCtrl.Teacher/Services/BreakoutManager.cs`

**Action:** delete (or, less invasive, mark `[Obsolete]` and route call sites to
`ControlServer`). The single helper used externally is `BreakoutRoom.AssignRandomly`, which
lives in `BreakoutRoom.cs` and remains.

#### `src/ClassroomCtrl.Teacher/ViewModels/MainViewModel.cs`

- Existing `Rooms` ObservableCollection + `RoomViewModel` stays.
- `OpenBreakoutCommand` opens new `GroupManagerView` instead of `MultiRoomTeacherView`.
- New `JoinGroupCommand: RelayCommand<Guid>` — opens `GroupExpandedView`, calls
  `App.Server.TeacherJoinGroupAsync(groupId)`.
- New `LeaveGroupCommand` — closes `GroupExpandedView`, calls `TeacherLeaveGroupAsync`.
- New `ShareToGroupCommand: RelayCommand<Guid>` — toggles `StartGroupScreenShareAsync` /
  `StopGroupScreenShareAsync`.
- Observe `App.Server.HostChanged`, `App.Server.RoomsChanged` (NEW event the controller fires
  when state mutates) — refresh `Rooms`.
- Per-tile group badge: extend `StudentViewModel` with `string GroupLabel` ("G1"/"G2"/"—")
  bound to a small badge in the existing classroom-grid template.

#### `src/ClassroomCtrl.Teacher/GroupManagerView.xaml(.cs)` *(NEW)*

XAML for the §2.1 layout. Code-behind handles drag-drop (or simple click-menu for
assignment), template save/load (groups.json), and command bindings.

#### `src/ClassroomCtrl.Teacher/GroupExpandedView.xaml(.cs)` *(NEW)*

XAML for the §2.2 layout. Code-behind:
- On open: subscribe to `App.Server.StudentStreamFrameReceived`, filter by group member set.
- For each member: issue `RequestStudentStreamAsync(memberId, codec)` (existing).
- On close: issue `StopStudentStreamAsync(memberId)` for each; unsubscribe.
- Per-cell `[Remote]` opens the existing `StudentScreenWindow` (or inlines its capture-+-
  Remote-button machinery; recommend inline for less window-spawn noise).

#### `src/ClassroomCtrl.Teacher/MultiRoomTeacherView.xaml(.cs)`

**Action:** delete (or keep dormant until next release if conservative).
Localization key `Btn_MultiRoom` retained but the resource string updates to "Group Manager".

#### `src/ClassroomCtrl.Teacher/Services/GroupTemplateStore.cs` *(NEW, optional)*

Reads/writes `%ProgramData%\NTY\ClassroomCtrl\groups.json`:
```json
{
  "templates": [
    {
      "name": "Math Project Q4",
      "groups": [
        { "name": "Team A", "members": [ "som@school.local", "pim@school.local" ] },
        { "name": "Team B", "members": [ "kao@school.local", "mai@school.local" ] }
      ]
    }
  ]
}
```
Members stored by **display-name/identifier**, not `Guid` — because Guids regenerate per
session, but display names persist. On load, match by display-name + best-effort drop
unmatched.

### 4.3 Student-side

#### `src/ClassroomCtrl.Student.Service/ClassroomWorker.cs`

Extend `IsForMe(env)` with the `TargetGroupId` clause (architecture.md §4).
Dispatch new message types — forward to Agent via existing IPC. New handlers needed at
service level: none beyond the IsForMe extension; everything else routes to Agent like today.

#### `src/ClassroomCtrl.Student.Agent/MainWindow.xaml.cs`

Add handlers for:
- `GroupSnapshot` → update local `_groups` map; refresh tile badges where rendered.
- `GroupTeacherJoined` → if it's my group, log "Teacher is here"; otherwise notify "Teacher
  joined Group X" in chat.
- `GroupTeacherLeft` → reverse.
- `GroupScreenStreamStart/Frame/Stop` → render in the existing ScreenStreamFrame viewer (it's
  already there for whole-class teacher screen share; the student doesn't actually care
  whether it's whole-class or group-targeted — it's just incoming frames).
- `BreakoutAssign` handling stays as today; nothing new for student in Tier 1.

#### `src/ClassroomCtrl.Student.Agent/RemoteControlReceiver.cs`

**No changes.** Tier 1 doesn't extend remote control. Multi-monitor/IME is Tier-2 of Feature
#2 (Phase 12-C), separate from breakout work.

---

## 5. State transitions

### 5.1 Group lifecycle (teacher-authoritative)

```
[empty] ──CreateGroupAsync──> {Group G, members M, host=null}
   │                              │
   │                              ├──RenameGroupAsync──>
   │                              ├──SetGroupHostAsync──>
   │                              ├──AssignToRoomAsync(s, G)──> M ∪ {s}
   │                              ├──AssignToRoomAsync(s, null)──> M \ {s}
   │                              ├──DeleteGroupAsync──> [empty]
   │                              └──DissolveAllAsync──> [empty] for all
   └─ on each mutation: BroadcastGroupSnapshotAsync + targeted BreakoutAssign
```

### 5.2 Teacher join / leave

```
whole-class ──TeacherJoinGroupAsync(G)──> joined(G)
                                              │
                                              └──TeacherLeaveGroupAsync──> whole-class
```

Constraint (Decision 6): only one `joined(G)` state at a time; `TeacherJoinGroupAsync(G2)`
while in `joined(G1)` first emits `TeacherLeaveGroupAsync` internally.

### 5.3 Group-targeted screen share

```
not-sharing ──StartGroupScreenShareAsync(G)──> sharing(G)
                                                  │
                                                  ├──Stop──> not-sharing
                                                  └──(implicit) on TeacherLeaveGroup──> not-sharing
```

Group share + whole-class share are mutually exclusive (engineering simplicity; UI hides
"Share to All" while group share is active and vice versa).

---

## 6. Persistence (`groups.json`)

- Path: `%ProgramData%\NTY\ClassroomCtrl\groups.json`.
- Format: JSON template store from §4.2.
- Read on `GroupManagerView` open; not auto-applied — teacher loads explicitly.
- Write on "Save as Template…" — overwrites existing template by name (confirm prompt).
- Survives teacher restart. Active membership state is NOT persisted.

---

## 7. 2-PC validation plan

Teacher `sirin`, student `force` + ideally one more student PC if available. If only 2 PCs,
single-student tests cover most paths; group-of-2 is the minimum exercise of the protocol.

### Must-pass (Tier 1 acceptance)

1. **Create / dissolve** — teacher creates 2 groups (random or manual). Student receives
   `BreakoutAssign`; toast appears. Student log shows correct `_myRoomId`. Teacher dissolves;
   student returns to main, toast "Returned to main".
2. **GroupSnapshot propagation** — verify that every state change emits a `GroupSnapshot`
   (check teacher log). New student joining mid-session receives the snapshot on Hello-ack
   and ends up in the correct state immediately.
3. **Host assignment** — teacher sets host = student A in Group 1. Student A sees
   `HostFloatingToolbar` open. Teacher clears host. Toolbar closes.
4. **Group-targeted screen share** — teacher in main, no share. Click `Share→` on Group 1.
   Group 1 member sees teacher's screen render; non-group student does NOT receive frames
   (verify with student-log absence of `ScreenStreamFrame` arrivals). Stop share — frames
   stop for group 1.
5. **Teacher join group** — click `Join` on Group 1. `GroupExpandedView` opens. Streams for
   Group 1 members start. Group 1 student sees "Teacher is here" chat. Click `Leave` — view
   closes, streams stop, "Teacher left" chat fires.
6. **Per-tile group badge** — main classroom grid shows G1/G2/— badges; updates live on
   reassignment.
7. **Template save/load** — save template with 2 groups + members; restart teacher; load —
   groups recreate with matched members.

### Guards / regressions

8. **Remote control still works** — open `StudentScreenWindow` for any student (in or out of
   a group); Phase 12-B remote-control paths unaffected.
9. **Whole-class share still works** — disable group share; "Share to All" still works as
   today.
10. **Audio still in-sync** — Phase 11-C audio path untouched; verify with mic off (Tier 1
    doesn't add voice).
11. **File transfer** — `_reliableOutbox` path untouched; send a file, verify intact.
12. **Net Movie** — `BroadcastReliableAsync` path untouched.

### Logs to grep
- Teacher: `[GroupManager]`, `[Breakout]`, existing `[Remote]`, `[StudentScreenWindow]`.
- Student: `[BreakoutAssign]`, new `[Group]`, existing `[Remote]`.

---

## 8. Effort estimate

| Sub-task | Days |
|---|---|
| Wire protocol (codepoints, DTOs, envelope field, IsForMe extension) | 0.5 |
| Cleanup (BreakoutManager retire, MultiRoomTeacherView delete) | 0.25 |
| `ControlServer` new methods + state | 0.5 |
| `ScreenBroadcaster` group routing | 0.5 |
| `GroupManagerView` (XAML + code-behind + template store) | 1.0 |
| `GroupExpandedView` (XAML + multi-stream wiring) | 1.0 |
| Main toolbar status chip + per-tile badge | 0.25 |
| Student-agent handler additions | 0.25 |
| 2-PC validation + fixes | 0.75 |
| **Total** | **5.0** |

Tighter: 3 days if `GroupExpandedView` reuses `StudentScreenWindow` mostly intact (open N
windows tiled rather than a custom grid). Recommended for the first cut; build custom grid in
a follow-up if needed.

---

## 9. Implementation order for the primer

When Phase 13-B implementation primer drops, suggested order:

1. **Cleanup first** (BreakoutManager + MultiRoomTeacherView retired, dead messages wired).
2. **Wire protocol additions** (codepoints + DTOs + envelope field). Build clean before going on.
3. **ControlServer state + new methods + GroupSnapshot broadcast**. 2-PC: state propagates.
4. **GroupManagerView** (replaces multi-room view). 2-PC: create/dissolve/assign/host/template.
5. **Group-targeted screen share** (broadcaster + UI toggle). 2-PC: in-group sees, out-of-
   group doesn't.
6. **GroupExpandedView** (join button → expanded multi-screen). 2-PC: join/leave + remote
   from grid.
7. **Per-tile group badge + status chip**.
8. **Polish + run full 2-PC checklist**.

Each step in its own commit; the dev can stop and validate after any step.
