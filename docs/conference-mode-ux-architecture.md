# Conference Mode (UX positioning) — Cross-Tier Architecture

**Status:** investigation only, no code. Foundation for Phase 15-B/C/D/E/F
implementation primers. **Last updated:** 2026-05-29.

Sibling docs:
- [`conference-mode-ui-mockups.md`](./conference-mode-ui-mockups.md) — ASCII layouts
- [`conference-mode-implementation-plan.md`](./conference-mode-implementation-plan.md) — per-phase steps

This doc reframes Feature #4 (Conference Mode). The prior Phase 14 plan
(see [`conference-mode-architecture.md`](./conference-mode-architecture.md))
treated Conference as a *capture-side* problem to be solved bottom-up by
adding webcam-per-tier. Phase 15 treats it as a **UX-positioning** problem
solved top-down: ship a **mode toggle** that swaps the entire shell
between two product personalities.

```
📚 Classroom Control   = teacher-led, lock screens, breakouts, recording  (EXISTING — preserve)
📹 Conference          = Google-Meet-style gallery, equal participants    (NEW)
```

Strategic value: product positioning shifts from "classroom control with
webcam" to "classroom + Meet, integrated". Customer no longer needs
Google Workspace alongside our software. Classroom-mode controls
(lock, remote, breakouts, screen share) remain available; Conference-mode
delivers a familiar Meet shell on top of the backends we already ship.

---

## 1. Strategic positioning

**The lateral move, not the feature.** Phase 14 shipped a privacy banner
+ student-side WMI cam detection + reliable Start/Stop. That work
*stands* — Tier 1 of conf-mode-architecture.md is the underlying
teacher-broadcast plumbing. Phase 15 adds the UX shell on top so the
same backend serves two different product experiences.

### What we own that Meet doesn't
- Lock screen / remote control / classroom power management
- Breakout rooms with teacher-as-relay (Phase 13)
- Screen share with adaptive bitrate (Phase 11-B inc4)
- File transfer reliable channel (Phase 10.21)
- PDPA-aware recording (Phase 5b)

### What Meet has that we'd add (Conference shell)
- Equal-tile gallery with auto-layout
- Active-speaker focus tile
- Pin / spotlight
- Meet-style bottom toolbar (mic/cam/share/chat/hand/more/end)
- Sidebar (chat / participants)
- Raise hand + reactions (delight; not load-bearing)

### What stays deferred (v2 or later)
- Recording (gallery composition is hard; defer)
- Background blur (Media Foundation segmentation; heavy)
- Live captions (STT pipeline; heavy)
- Live transcription

---

## 2. Code survey (Task 1)

Where the mode toggle goes + what becomes mode-aware.

### Teacher shell

| File | Role | What changes in Phase 15 |
|---|---|---|
| `Teacher/MainWindow.xaml` (962 lines) | The whole shell. 3-row × 3-col Grid: header (56px), conditional Demo banner, main content. Cols: 240 sidebar / `*` content / 320 chat rail. | Add Conference toggle button in the header right-cluster (next to bell + branding cog). Visibility-bind the sidebar + chat rail to `CurrentMainView != Conference` so Conference takes the full content+rail width. |
| `Teacher/ViewModels/MainViewModel.cs` (2391 lines) | All state. **Already has `MainViewKind { StudentGrid, QuizManager }`** at line 50 + `[ObservableProperty] currentMainView` at line 52. Existing `OpenQuizManager`/`OpenStudentGridView` flip between them. | Extend enum with `Conference`. Add `ToggleConferenceCommand`, `IsInConference` derived prop, `OpenConferenceCommand`, `EndConferenceCommand`. New `OnConferenceStateChanged` to broadcast `ConferenceStart`/`ConferenceEnd` envelopes. |
| `Teacher/Converters/ViewKindToVisibilityConverter.cs` | Maps `MainViewKind` → Visibility for the mutually-exclusive content cells. | No change. The new `Conference` enum value just slots in. |
| `Teacher/Views/QuizManagerView.xaml` | Existing embedded view that proves the `MainViewKind` swap pattern. Lives at `Grid.Row="2" Grid.Column="1"` with `Visibility="{Binding CurrentMainView, Converter=..., ConverterParameter=QuizManager}"`. | Precedent for `ConferenceGalleryView`. New view sits in the same cell with `ConverterParameter=Conference`, BUT also extends to `Grid.ColumnSpan="2"` to overlay the chat rail when Conference is active (so the gallery uses full width). |
| `Teacher/MainWindow.xaml` lines 654–962 (chat rail) | Right-rail tabs (Chat / Activity), DM tab strip, message list. Reuses `MainViewModel.Conversations`. | Conference mode hides the rail (sidebar + rail both collapse). The Meet-style chat re-uses the existing `Conversation` infrastructure surfaced through a **sidebar overlay panel** instead of the rail. |
| `Teacher/MainWindow.xaml.cs` (136 lines) | Wire of CopyIP_Click, NotificationsBell_Click, popup toggle. Small. | One new handler for the mode-toggle click if not pure command-bound. |

### Existing camera / mic / share / chat / breakout — what becomes mode-aware

| Feature | Service | Tier 1 wire | Phase 15 mode-awareness |
|---|---|---|---|
| **Webcam capture (teacher)** | `CameraBroadcastService` (Phase 9.5 + 14-B) | `CameraStart/Frame/Stop` 0x0460–0x0462 | **Reused as-is.** Conference's bottom-toolbar `📷` button calls the same `ToggleCameraCommand` (existing). Banner stays. |
| **Webcam capture (student)** | None yet | Tier 2 = `StudentCameraBroadcaster` | Out of scope for 15-B/C/D MVP. Conference launches with teacher cam only; student cam = Phase 14-C Tier 2 OR Phase 15-F bundle. |
| **Mic capture (student)** | `MicBroadcaster` + `PttKeyboardHook` (Phase 13-D) | `VoiceAudioFrame` 0x0640 | Reused. In Conference mode the per-group routing collapses to "everyone in the conference" — use `TargetGroupId = ConferenceId` (a per-session Guid). |
| **Mic capture (teacher)** | `AudioBroadcaster` mic path | `AudioStreamFrame` 0x0329 | Reused; teacher mic broadcasts to all. |
| **Voice playback** | `VoiceMixer` (Phase 13-D) | Per-source mixing already in place | Reused. No code change. |
| **Screen share (teacher)** | `ScreenBroadcaster` (Phase 11-B) | `ScreenStreamFrame` 0x0323 | Reused. Conference's `🖥 Share` button calls `ShareScreenCommand`. |
| **Chat** | `ChatService` + `ChatRoom` (Phase 9.8) | `ChatRoom` 0x0102, `ChatBroadcast` 0x0100 | **Reused with a new ChatRoom.** Conference creates a virtual ChatRoom for the session; messages route through existing infra. UI shell is Meet-sidebar instead of right-rail. |
| **Breakout rooms** | `ControlServer._studentRoomMap` etc. (Phase 13-B) | `BreakoutAssign` 0x0601 + 0x062x | Mode-incompatible by default. See § 5 Q1 ("Mode transition while breakout active"). |
| **Privacy banners** | `CamLiveBanner` (14-B), `VoiceLiveBanner` (13-D), `RemoteControlBanner` (12-C) | n/a | Reused — same triggers fire in both modes. |

### Student shell

| File | Role | What changes in Phase 15 |
|---|---|---|
| `Student.Agent/MainWindow.xaml` | Chat-centric utility window, usually hidden in tray. 5-row Grid (header, search, message body, input, status). | Not modified. Conference mode launches a **new full-screen window** (analogous to `CameraViewWindow` for Phase 9.5) — not a swap of MainWindow. |
| `Student.Agent/MainWindow.xaml.cs` | Dispatch arms for every MessageType. | Adds 5 new arms: `ConferenceStart`, `ConferenceEnd`, `HandRaise`, `HandLower`, `Reaction`. `ConferenceStart` instantiates the new full-screen window; `ConferenceEnd` closes it. |
| (NEW) `Student.Agent/ConferenceGalleryWindow.xaml(.cs)` | Mirror of the teacher's `ConferenceGalleryView` but as a Window (not embedded view). | Created in Phase 15-C. |

### What survived 14-B that 15-B leverages
- `CamLiveBanner` (teacher) — used as-is when cam toggle fires in Conference.
- `WebcamDeviceWatcher` + `WebcamStateUpdate` (0x0650) — teacher uses to pre-disable cam toggle for students with no webcam (Tier 2 polish).
- `Conf_*` localization keys (6 strings, EN+TH) — reused; add `Conf_StartConference`, `Conf_EndConference`, `Conf_HandRaise`, `Conf_Reaction*` in 15-B/E.

---

## 3. State machine (Task 2)

### Q1 — Mode toggle persistence: **(b) Soft start**

Conference is a **session**, not a view. The toggle in the header is a
*UI mode preference* (teacher's choice to expose Conference controls in
their own shell) but the actual "everyone is in Conference now"
transition happens via an explicit "Start Conference" action that emits
`ConferenceStart` to all connected students.

**Rationale:**
- Matches Meet's mental model ("start meeting" → broadcast invitation).
- Lets the teacher stage controls (cam on, mic test, choose participants)
  before everyone transitions.
- Clean rollback — "End Conference" returns everyone to Classroom.
- Survives teacher-side accidental toggle clicks without disrupting
  students mid-class.

**Rejected:**
- *(a) Hard switch.* A click that immediately repositions all 40
  students is hostile UX and breaks active screen-share / breakout
  state without warning.
- *(c) Separate sessions.* Two product binaries is a worse install
  story; loses the "all-in-one" positioning.

### Q2 — Authority in Conference: **(b) Host pattern, teacher = host**

Teacher gets admin tools: mute-all, mute-individual, remove-from-conf,
spotlight, end-for-all.

**Rationale:**
- School context requires a responsible adult.
- Meet's host-pattern is universally understood.
- Reuses Phase 13-D `MicMuteRequest` (0x0641) verbatim — no new wire.

**Rejected:**
- *(a) Egalitarian.* Unsupervised classroom chaos.
- *(c) Co-host (hybrid).* v2 polish; defer until customer asks for
  TA workflows.

### Q3 — Tier ordering: **B → C → D → E → F (recommended)**

| Phase | Scope | Effort | Status |
|---|---|---|---|
| **15-B** | Mode toggle + skeleton view + Start/End wire | ~1.5 hr | MVP gate |
| **15-C** | Gallery view + active-speaker + pin | ~2 hr | MVP |
| **15-D** | Meet bottom toolbar + chat sidebar + participants panel | ~1.5 hr | MVP |
| **15-E** | Raise hand + reactions | ~1 hr | Polish |
| **15-F** | Tier 3 SFU backend (active-speaker high-res + thumbnails) | ~3–4 hr | Scale gate |

B+C+D = minimum viable Meet experience. E = delightful polish, low risk.
**F is deferred** to when customer hits >20 simultaneous Conference
participants (per Phase 14-A Tier 3 bandwidth math); under that
threshold the existing 5-channel transport handles cam+voice+screen
fan-out without selective forwarding.

### State diagram (text)

```
                ┌──────────────────────────┐
                │   Classroom Control      │  ←── default at app start
                │   CurrentMainView =      │
                │   StudentGrid            │
                │   (or QuizManager)       │
                └──────────────────────────┘
                  │              ▲
   teacher clicks │              │ teacher clicks
   "Start         │              │ "End Conference"
    Conference"   │              │   (or teacher
                  │              │    disconnects)
                  ▼              │
       ─── emit ConferenceStart envelope (broadcast, reliable) ───
                  │              │
                  │              │   ─── emit ConferenceEnd ───
                  ▼              │
                ┌──────────────────────────┐
                │   Conference (host)      │
                │   CurrentMainView =      │
                │   Conference             │
                │   IsInConference = true  │
                │   ConferenceSessionId    │
                │     = Guid.NewGuid()     │
                └──────────────────────────┘

  Student side, in parallel:

                ┌──────────────────────────┐
                │   Tray + chat utility    │  ←── default state
                └──────────────────────────┘
                  │              ▲
       ConferenceStart           │ ConferenceEnd
                  │              │
                  ▼              │
                ┌──────────────────────────┐
                │   ConferenceGalleryWindow│
                │     full-screen          │
                │     (modal-feeling)      │
                └──────────────────────────┘
```

### Per-feature mode-awareness rules

| If teacher does X | And mode is Classroom | And mode is Conference |
|---|---|---|
| Camera toggle | Phase 9.5 broadcast as today | Same — same banner, same `CameraStart` envelope |
| Mic toggle | Reuses existing | Same |
| Screen share | Reuses existing | Same envelope, but Conference UI shows it as a tile + a thumbnail |
| Lock screen all | Allowed | **Blocked** with toast "Locking screens disabled while Conference is active" |
| Apply policy | Allowed | Allowed (admin function persists across modes) |
| Send file | Allowed | Allowed (sidebar entry in Conference) |
| Breakout open | Allowed | **Conference auto-ends, breakout opens** (warning dialog) |
| Quiz manager | Allowed | **Blocked** with toast "Quiz mode requires Classroom mode" |
| Recording | Allowed | **Disabled** with tooltip (deferred to v2 per § 1) |

---

## 4. Meet feature mapping (Task 4)

✅ = have it, reuse · 🟡 = partial, wire up · ❌ = build

| Feature | Status | Implementation | Effort |
|---|---|---|---|
| Gallery auto-layout | ❌ | New `ConferenceGalleryView` (Teacher), `ConferenceGalleryWindow` (Student.Agent) | 15-C ~1.5 hr |
| Active speaker focus | 🟡 13-D VAD exists (`MicStateUpdate.IsSpeaking`) | Subscribe in gallery; accent border on speaker's tile; 1.5 s hysteresis | 15-C ~30 min |
| Pin / Spotlight | ❌ | New `PinnedParticipantId` ObservableProperty + tile click handler | 15-C ~30 min |
| Mic toggle | ✅ Phase 13-D | Reuse existing toggle command in toolbar | 0 |
| Cam toggle | ✅ Phase 14-B | Reuse existing `ToggleCameraCommand` | 0 |
| Screen share | ✅ Phase 11-B | Reuse `ShareScreenCommand` | 15-D ~30 min for inline UI |
| Chat sidebar | ✅ Phase 9.8 ChatRoom infra | Adapt UI to slide-in sidebar; reuse `ChatRoom` 0x0102 with conference RoomId | 15-D ~30 min |
| Breakout rooms in Conference | ✅ Phase 13-B | Mode-incompatible: opening breakouts ends Conference (see § 5 risk #2) | 15-D ~20 min |
| Participants panel | 🟡 partial (`MainViewModel.Students` exists) | Adapt list with Conference badges (mic/cam state) | 15-D ~20 min |
| Raise hand | ❌ | New `0x0660` HandRaise / `0x0661` HandLower; tile badge + queue order on teacher | 15-E ~30 min |
| Reactions (👍❤️😂😮😢) | ❌ | New `0x0662` Reaction (with emoji + senderId); floats on tile briefly | 15-E ~30 min |
| Recording | ❌ | Defer to v2 — gallery composition is non-trivial | — |
| Background blur | ❌ | Defer — Media Foundation segmentation is heavy | — |
| Live captions | ❌ | Defer — STT pipeline is heavy | — |
| Live transcription | ❌ | Defer | — |
| Privacy banners | ✅ | Reused — `CamLiveBanner`, `VoiceLiveBanner` fire on the same triggers | 0 |
| Mute-all / individual mute | ✅ Phase 13-D | Reuse `MicMuteRequest` (0x0641) — teacher's Conference admin menu | 0 |

**MVP (15-B+C+D) = 5 hr Claude Code + ~1 hr dev validation.**

---

## 5. Risks + open issues (Task 6)

### 1. Mode transition while screen share is active
**Decision:** screen share **persists** across the transition. The
share envelope stream doesn't care which UI shell is showing it. The
Conference UI surfaces the share as a 16:9 tile in the gallery
(special-case rendering) + a chip "Sharing: Teacher".

**Why:** killing a share mid-presentation is hostile; pausing requires
encoder-state buffering we don't have.

### 2. Mode transition while a breakout group is active
**Decision:** opening Conference while breakouts exist shows a modal:
> "Starting Conference will dissolve all breakout groups. Continue?"

Click Continue → fire existing `BreakoutDissolve` (0x0602) for each
group, THEN broadcast `ConferenceStart`.

**Why:** breakout groups are stateful (host, members, screen-share
target) — running Conference *inside* a breakout would multiply
relay paths quadratically and confuse routing.

**Rejected:** "Conference-within-breakout" — too much engineering for
ambiguous customer value.

### 3. Student declines Conference (camera off, mic off)
**Decision:** allowed. ConferenceGalleryWindow opens with a
placeholder tile ("🚫 Camera off") and mic muted-by-default. Student
can opt-in via the bottom toolbar (mic/cam buttons). Teacher's force-
enable (Phase 14-C wire 0x0654 — NOT yet implemented but design-locked)
overrides.

### 4. Teacher disconnects mid-Conference
**Decision:** auto-end. On teacher disconnect, ControlServer fires
`ConferenceEnd` to all students (treats as if teacher clicked End).
All students return to default state.

**Why:** no co-host (Q2 decision); without a host the session has no
authority.

**Rejected:** hand off host to a student — adds permission-elevation
UX that we don't need for v1.

### 5. Conference with N students on gigabit LAN
**Decision threshold:** N ≤ 20 → Phase 15-B/C/D MVP backend (existing
5-channel transport) is sufficient. N > 20 → commit to Phase 15-F SFU
(active-speaker high-res + thumbnails per Phase 14-A Tier 3 design).

**Math** (revised from CamSpike actual ~148 KB/s vs Phase 14 doc's
300–400 KB/s budget):
- 20 students × 20 receivers × 150 KB/s naive = 60 MB/s = ~480 Mbps
  egress. Tight on gigabit but works.
- 30 × 30 × 150 = 135 MB/s = ~1.08 Gbps. **Exceeds gigabit**; SFU
  required.

### 6. Recording deferred but customer may ask
**Decision:** ship 15-B–E without recording. Add a disabled "Record"
button in the toolbar with tooltip "Recording will arrive in a future
update". Sets expectation honestly.

### 7. Mobile / tablet rendering
**Out of scope.** Windows desktop product; no responsive layout work.

### 8. Mode preference persistence on app restart
**Decision:** **no.** App always launches in Classroom mode. Conference
is a transient session, not a persistent view choice. Surviving a
restart would resurface stale session state (e.g. a Conference window
that the server no longer knows about).

### 9. Teacher in two places (host + participant)
**Decision:** teacher sees self-view in gallery (small PIP toggleable).
Their own cam tile appears alongside students; their own mic VAD
fires their tile's active-speaker border. No special-casing.

### 10. Group voice (Phase 13-D Tier 3) and Conference voice
**Decision:** mode-exclusive. Conference uses a single virtual
ConferenceId (Guid) for `TargetGroupId` routing. While in Conference,
13-D group voice paths are gated off (the `_studentRoomMap` lookup
still points to the conference Guid). No new wire needed — re-use of
the 13-D code paths.

---

## 6. Recommendations

1. **Proceed to Phase 15-B.** The architectural surface area is small
   (one new enum value, one new view, one new wire pair). The
   existing `MainViewKind` state-machine precedent (line 50 of
   MainViewModel) makes the toggle a 1-line change.

2. **Keep Phase 14 Tier 1 in production.** The cam-state plumbing
   (0x0650 `WebcamStateUpdate`) is reused as-is. Tier 2 design from
   Phase 14-C (student-side cam) folds naturally into Phase 15-F as
   a co-dependent commit.

3. **Skip Phase 14-C/D as standalone work.** Phase 15-B/C/D delivers
   the customer-visible UX they actually asked for ("ระบบ conference").
   Phase 14-C's group-cam work becomes part of Phase 15-F (full
   gallery + student cam + SFU) bundled.

4. **Defer recording, blur, captions.** Document the deferral in the
   ship-doc + a disabled toolbar button.

5. **No new wire-format changes in 15-A.** The five new MessageTypes
   sketched in `conference-mode-implementation-plan.md` (0x0670–0x0674)
   are *proposed*; codepoints are not added to `Messages.cs` until the
   relevant tier primer is approved.

---

## 7. What this design deliberately does NOT change

- The `MainViewKind` enum mechanism. Just adds an enum value.
- Phase 11-B screen share. Routes the same; the Conference UI
  consumes the same envelopes.
- Phase 13-D mic + voice. Routing uses `TargetGroupId = ConferenceId`
  (a single Guid) — same wire, different routing key value.
- Phase 14-B camera broadcast. Reused as-is.
- Phase 13-B breakout rooms. Mode-incompatible — § 5 risk #2 details
  the dissolve-on-Conference policy.
- All existing services (CameraBroadcastService, MicBroadcaster,
  VoiceMixer, ChatService, ControlServer, TcpControlServer) — called
  but not modified.
- The 5-channel transport. Conference rides existing lanes; no
  6th `_webcamOutbox`.

---

## 8. Reference — prior precedent

Phase 13-A → 13-B/C/D and Phase 14-A → 14-B follow this same shape:
investigation doc + tier sketches → implementation primer. Phase 15-A
mirrors that. The key precedent finding here, paralleling
Phase 13-A's "breakout infra already exists" and Phase 14-A's "cam
infra already exists", is:

**The `MainViewKind` state machine + `ViewKindToVisibilityConverter` +
the QuizManagerView precedent prove the mode toggle is a 1-line
enum addition + one new embedded view.** The complexity all lives in
the new view's content, not in the shell's routing.
