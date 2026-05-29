# Conference Mode — Tier 3 Design (Mode 3: Full Gallery)

**Status:** sketch + extensive risk register. **Last updated:** 2026-05-29.
**Parent:** [`conference-mode-architecture.md`](./conference-mode-architecture.md).

⚠ **Tier 3 is optional.** Mode 1 (Tier 1) delivers ~50% of customer
value; Mode 2 (Tier 2) delivers ~80%. **Only commit to Tier 3 if the
customer explicitly asks for whole-class gallery-style video** AND the
bandwidth analysis below holds on the customer's switched LAN.

## 1. Scope

Mode 3 = whole-class video gallery, Zoom-style. Every student in the
class can see every other student's webcam (or thumbnail) simultaneously.
A single "active speaker" gets the high-resolution focus tile; everyone
else is rendered as thumbnails.

What's in scope:
- Active-speaker detection (auto via Phase 13-D `MicStateUpdate.IsSpeaking`,
  with teacher pin override).
- Source-side dual-encoding (every active student emits BOTH high-res
  AND thumbnail streams) **OR** teacher-side downscale per recipient
  (CPU on teacher).
- Paginated gallery UI (16 tiles/page, with prev/next).
- Selective forwarding rules: only send high-res to clients viewing
  that student.
- `WebcamPresenterSet` (0x0656) wire message for teacher pin.

What's NOT in scope (deliberately deferred):
- Recording of the gallery view (Tier 4 if customer asks).
- Per-tile mute / volume (Tier 4).
- Picture-in-picture / spotlight overlays (Tier 4).
- Cross-class breakouts (multi-classroom federation).

## 2. Pre-flight + hard gate

Tier 3 must pass these gates before any feature code lands:

- [ ] Tier 1 + Tier 2 acceptance both passed on dev box.
- [ ] **Customer commits to gigabit switched LAN** (NOT Wi-Fi, NOT a
      hub) on the conference-mode test site. Verify with `iperf3` round-
      trip ≥ 500 Mbps between two student PCs.
- [ ] **Customer-site dry-run with 5+ PCs:** enable Tier 2 cam on all 5
      PCs simultaneously, capture Wireshark on teacher. Confirm teacher
      egress fits the bandwidth math below.
- [ ] **Decision lock:** Source-dual-encode VS Teacher-relay-downscale.
      The `WebcamPresenterSet.SourceDualEncode` flag exists to A/B; pick
      one as default before merge. Recommended: source-dual-encode
      (offloads CPU from teacher, easier debugging — each student's
      stream is independent).
- [ ] **`_outbox` capacity bump from 16 → 32 considered.** With 1 high-
      res + 39 thumbnails active simultaneously, cap 16 ≈ ~0.4 s of
      headroom on a 40-stream peer. May not be enough; raise to 32
      or split out a 6th `_webcamOutbox` lane.

## 3. Architecture

### 3.1 Active-speaker selection

**Source signal:** Phase 13-D `MicStateUpdate.IsSpeaking` (RMS-driven,
~100 ms window). The teacher already tracks per-student speaking state
in `ControlServer._micStates`.

**Selection logic** (teacher side):

```csharp
// Pseudocode in MainViewModel or new ActiveSpeakerService:
//
//   currentSpeaker = null
//   on MicStateUpdated(endpointId, state):
//     if state.IsSpeaking AND currentSpeaker == null:
//       currentSpeaker = endpointId
//       broadcast WebcamPresenterSet { ActiveSpeakerEndpointId = endpointId }
//       (start 1.5s hysteresis timer to confirm)
//     if state.IsSpeaking AND currentSpeaker != endpointId:
//       (different speaker started talking; start "transition" timer)
//       if transition timer fires AND no one else has spoken in 1.5s:
//         currentSpeaker = endpointId
//         broadcast WebcamPresenterSet { ActiveSpeakerEndpointId = endpointId }
//     if state.IsSpeaking == false AND endpointId == currentSpeaker:
//       (start 1.5s "presenter lingers" timer; keep presenter if they
//        resume; otherwise on timer fire return to neutral)
```

**Teacher override:** explicit pin via `WebcamPresenterSet` in the
teacher UI (right-click on a student tile → "Pin as Presenter").
Override neutralizes auto-selection until cleared.

### 3.2 Source-side dual-encoding

When a student becomes the active speaker (receives
`WebcamPresenterSet { ActiveSpeakerEndpointId = self }`), their
`StudentCameraBroadcaster`:
- Emits **two streams** per captured frame:
  - **High-res:** 640×480 @ 10 FPS (`StudentGroupCameraFrame` semantics
    but in Mode 3 the envelope is broadcast: no `TargetGroupId`).
  - **Thumbnail:** 160×120 @ 5 FPS, JPEG Q60 (smaller bitrate).
- Frames are emitted on different cadences (high-res every 100 ms,
  thumbnail every 200 ms) — independent timers.

**New broadcaster state:**
- `IsActiveSpeaker { get; set; }` (default false)
- Thumbnail encoding pipeline = downscale `Bitmap` via
  `System.Drawing.Bitmap` constructor with the smaller dimensions,
  then JPEG Q60.

**New wire message:** none. Reuses `StudentGroupCameraFrame` (0x0652)
with `TargetGroupId = null` (broadcast). Includes a new `IsThumbnail`
flag in the message:

```csharp
[MessagePackObject(true)]
public class StudentGroupCameraFrameMessage   // existing Tier 2 DTO, NEW field
{
    public Guid GroupId { get; set; }
    public Guid SourceEndpointId { get; set; }
    public byte[] JpegData { get; set; } = Array.Empty<byte>();
    public long TimestampMs { get; set; }
    public bool IsThumbnail { get; set; }   // NEW Tier 3 field
}
```

**Wire-compat:** `IsThumbnail` is a new key appended at the highest
index. Old clients deserialize as `false` (the default), which is
correct — pre-Tier-3 cam frames are always full-res from the source's
perspective.

### 3.3 Selective forwarding (teacher relay)

Naive Tier 2 fan-out is "broadcast every frame to every student". For
Mode 3 at scale that's 40 students × 40 frames × 30 KB = ~36 MB/s
relay — unacceptable. The relay must select per-recipient:

- **Active speaker frame (high-res):** broadcast to all students (every
  student needs it for the focus tile).
- **Active speaker frame (thumbnail):** drop on relay (thumbnail of the
  active speaker is redundant — receiver knows who the speaker is and
  uses the high-res).
- **Other student high-res frame:** drop on relay (no one is viewing
  their high-res).
- **Other student thumbnail frame:** broadcast to all students (every
  student's gallery needs all the thumbnails).

This drops total per-recipient inbound to:
- 1 × high-res @ 10 FPS × 30 KB = 300 KB/s
- 39 × thumbnail @ 5 FPS × 5 KB = 975 KB/s
- Total: ~1.3 MB/s per recipient = ~10 Mbps. ✅

**Teacher egress:** 40 recipients × 1.3 MB/s = **52 MB/s = ~420 Mbps.**
Fits a 1 Gbps switched LAN.

### 3.4 Channel sizing

At Tier 3 peak (40 students, all cams active, 1 active speaker), the
`_outbox` carries:
- 1 high-res stream @ 10 FPS = 10 envelopes/s
- 39 thumbnail streams @ 5 FPS = 195 envelopes/s
- Plus screen-share (not all suspended in conference mode) = up to
  20 envelopes/s

= ~225 envelopes/s on the teacher's egress. Cap 16 = ~70 ms of
headroom. **Marginal.** Two options:
- **Option A (recommended):** bump `_outbox` capacity from 16 to 32
  in Tier 3. Doubles headroom; minimal code change.
- **Option B:** add a 6th `_webcamOutbox` lane (DropOldest cap 16),
  separate from screen `_outbox`. More work but cleaner — screen
  share and cam don't evict each other under load.

**Default decision: Option A.** Pre-merge, verify with a 5-PC dry run.

### 3.5 Gallery UI

Student-side: new `ConferenceGalleryView.xaml` window.
- One large focus tile (~640×480 visual area, ~50% of window).
- N-1 thumbnail tiles in a paginated grid (e.g., 4×4 = 16 per page).
- "Next page" button when N > 16.
- Each tile has source name overlay.
- Active speaker auto-highlights with a border.

Teacher-side: same `ConferenceGalleryView` window, plus per-tile
right-click menu ("Pin as Presenter", "Force Camera Off this student").

Tier 3 also adds a teacher main-toolbar entry "Conference Mode →
Gallery" that opens the gallery on all students (broadcasts a
`ConferenceModeStart` envelope) and dismisses on Stop.

## 4. Wire protocol additions

```
0x0656  WebcamPresenterSet           (T→all, sets active-speaker focus)
```

Also: new `IsThumbnail` field on existing `StudentGroupCameraFrameMessage`
(see §3.2). And new `ConferenceModeStart` / `Stop` if dev wants a
distinct top-level switch:

```csharp
// Optional — may NOT be needed if "open the gallery" is just a teacher-side
// UI action triggered by the first WebcamPresenterSet broadcast.
0x0657  ConferenceModeStart   (T→all, "open the gallery view")
0x0658  ConferenceModeStop    (T→all, "close the gallery view")
```

Tier 3 design recommends NOT adding 0x0657/0x0658 — open-gallery can
piggyback on the first `WebcamPresenterSet` (when `Tier3Active` flag
flips). Saves wire codes.

## 5. Implementation steps

### Step 1 — Channel capacity bump

Bump `_outbox` cap 16 → 32 (or add 6th lane per §3.4). Verify with the
5-PC dry-run BEFORE further Tier 3 code.

**Commit:** `"Phase 14-D step 1: bump _outbox capacity (16→32) for Tier 3 gallery load"`

### Step 2 — Wire protocol (1 new codepoint + 1 new field)

```csharp
WebcamPresenterSet = 0x0656,   // T→all, reliable
```

`StudentGroupCameraFrameMessage.IsThumbnail` field appended.

Tests T19–T20.

**Commit:** `"Phase 14-D step 2: wire protocol (0x0656 WebcamPresenterSet + IsThumbnail field)"`

### Step 3 — Source-side dual-encode in `StudentCameraBroadcaster`

Adds `IsActiveSpeaker` property + thumbnail-encoder timer. When
`IsActiveSpeaker == true`, emit two streams; when false, emit only
thumbnail.

**Commit:** `"Phase 14-D step 3: source-side dual-encode (high-res + thumbnail with IsThumbnail flag)"`

### Step 4 — Teacher selective forwarding (relay rules)

In `ControlServer.OnMessage`, the `StudentGroupCameraFrame` arm gains
filtering logic per §3.3.

**Commit:** `"Phase 14-D step 4: teacher selective forwarding (active-speaker high-res broadcast, thumbnails for others)"`

### Step 5 — Active-speaker selection (`ActiveSpeakerService`)

New file in `Teacher/Services/ActiveSpeakerService.cs`. Subscribes to
`ControlServer.MicStateUpdated`; runs hysteresis + transition logic;
broadcasts `WebcamPresenterSet`.

**Commit:** `"Phase 14-D step 5: ActiveSpeakerService with 1.5s hysteresis + teacher pin override"`

### Step 6 — Conference gallery UI (student)

New `ConferenceGalleryView.xaml(.cs)`. Focus tile + paginated
thumbnail grid. Renders incoming `StudentGroupCameraFrame` by
`IsThumbnail` flag.

**Commit:** `"Phase 14-D step 6: ConferenceGalleryView (focus + paginated thumbnails) on student"`

### Step 7 — Teacher gallery + Pin override

Same view + per-tile right-click "Pin as Presenter".

**Commit:** `"Phase 14-D step 7: teacher-side gallery + per-tile Pin as Presenter override"`

### Step 8 — Bandwidth telemetry

Add a Tier 3 specific log line: every 2 seconds, the teacher logs
`Tier3 egress: N MB/s, M streams active, current presenter: {name}`.
Customer-site validation reads these.

**Commit:** `"Phase 14-D step 8: Tier 3 bandwidth telemetry (2s log: egress + stream count + presenter)"`

### Step 9 — Cleanup + acceptance

If everything works, fold any small lints into the prior commits.

## 6. Acceptance checklist (5-PC validation REQUIRED)

| # | Check | How to verify |
|---|---|---|
| 1 | **Teacher enables Conference Mode:** all 5 student PCs open ConferenceGalleryView. | UI on each PC. |
| 2 | **All 5 students enable cam:** gallery shows 1 focus tile + 4 thumbnails (own tile may or may not be shown — dev choice; default not). | Visual. |
| 3 | **Active-speaker detection:** student speaks → their tile becomes focus within 2 s. | Wait for hysteresis. |
| 4 | **Speaker swap:** student B speaks while A is presenter → swap within 2-3 s after A stops. | Hysteresis confirms. |
| 5 | **Teacher Pin override:** right-click on tile → "Pin as Presenter" → tile locks as focus regardless of voice. | Teacher UI test. |
| 6 | **Teacher Unpin:** "Unpin" → returns to auto-selection. | UI test. |
| 7 | **Gallery pagination:** with 16+ cams, prev/next pages work. | Use VMs to reach 17+ if needed. |
| 8 | **Bandwidth:** Wireshark on teacher shows ≤ 500 Mbps egress with 5 active cams + 1 active speaker. | Capture. |
| 9 | **Teacher bandwidth telemetry log:** new "Tier3 egress" lines appear every 2 s. | grep teacher log. |
| 10 | **Concurrent screen share:** teacher shares screen while gallery is active; screen + gallery both work. | Visual. |
| 11 | **Concurrent voice:** Tier 3 voice from 13-D works alongside cam gallery. | Hear audio. |
| 12 | **Cam unplugged during gallery:** student tile shows "📷 No camera" placeholder. | Unplug. |
| 13 | **Cross-tier privacy:** student banner still shows during Tier 3 capture. | Visual. |
| 14 | **Localization:** all Tier 3 UI in EN+TH. | Switch lang. |

**Customer-site validation criteria** (run separately, on customer's
LAN with their PC fleet):

| # | Customer check | Target |
|---|---|---|
| C1 | Tier 3 gallery with N=customer-class-size students all cams on. | UI loads + frames flow. |
| C2 | Teacher egress measured. | ≤ 60% of LAN capacity (e.g., ≤ 600 Mbps on 1 Gbps). |
| C3 | Student CPU on the spec-min PC. | ≤ 25% sustained during gallery. |
| C4 | Active-speaker swap latency. | ≤ 3 s reaction. |
| C5 | One full lecture hour without UI/network regression. | Stable. |

## 7. Risks expanded

### 7.1 Bandwidth

Already in `conference-mode-architecture.md` §6. **The single largest
Tier 3 risk.** Mitigations:
- Selective forwarding (§3.3) — load-bearing.
- Thumbnail size + FPS cap (160×120 @ 5 FPS) — tunable.
- Customer pre-validation (gate § 2).
- Fall-back: "fewer-cams mode" — limit active gallery cams to N
  configurable (e.g., 16) and rotate via active-speaker.

### 7.2 Teacher relay CPU

Per-recipient frame filtering at 225 envelopes/s × MessagePack
deserialize+route is ~5–10% CPU on a mid-spec teacher PC. Should be
fine but **measure during 5-PC dry run.**

If load is too high, the alternative is teacher-side downscale per
recipient (worse — adds JPEG-decode + bitmap-resize + JPEG-encode per
frame per recipient). Avoid unless forced.

### 7.3 Active-speaker churn

1.5 s hysteresis (per design) chosen to balance responsiveness vs.
flicker. Tune during dry-run if too sticky or too flippy.

### 7.4 PDPA + customer ethics

40 students all visible to each other is a significantly higher privacy
exposure than Mode 1 (teacher → students) or Mode 2 (within breakout
group). **Customer MUST review against their school's PDPA policy
before deploying.** Ship doc note:

> Mode 3 (Full Gallery) discloses every student's webcam to every other
> student. Verify your school's PDPA policy permits this prior to enabling.

### 7.5 Gallery UI scaling at 30+ tiles

Pagination + 16/page works but is unusual for video conferencing UX
(Zoom shows up to 49). If customer wants more visible, the design
allows a single per-page-size constant — bump and re-test FPS.

### 7.6 Cam-unplugged-mid-presenter

If active speaker unplugs cam: `WebcamStateUpdate { CamLive = false }`
arrives at teacher; `ActiveSpeakerService` detects, picks next eligible
speaker (or returns to neutral). Tier 3 implementation must handle this
state transition.

### 7.7 Phase 9.5 path not yet refactored

The old `CameraStart/Frame/Stop` (0x0460-0x0462) path used by Mode 1
is teacher-broadcast. Tier 3 reuses the new student-originated
`StudentGroupCameraFrame` (0x0652) with `IsThumbnail` flag, **not**
the 0x0460 path. If a customer simultaneously runs Mode 1 (teacher
cam) AND Mode 3 (gallery), both can coexist — different wire codes,
different broadcasters.

## 8. Defer-or-commit decision criteria

Commit to Tier 3 IF AND ONLY IF:
- Customer explicitly asks for whole-class gallery (not just "more
  video").
- Customer's LAN is gigabit + switched + verified (gate § 2).
- 5-PC dry-run shows ≤ 60% teacher egress and ≤ 25% student CPU.
- Customer signs off on the PDPA disclosure language.

If ANY of those fails: DEFER. Document the failing gate in this doc
under § 9 (Decision log), recommend customer use Mode 2 (group video).

## 9. Decision log

(Empty for Tier 3 design phase — populated as customer pre-validation
results come in.)

## 10. Effort estimate

- Step 1 (channel) — 5 min
- Step 2 (wire) — 15 min
- Step 3 (dual-encode) — 45 min
- Step 4 (selective forward) — 30 min
- Step 5 (active-speaker service + hysteresis) — 60 min
- Step 6 (student gallery UI) — 90 min
- Step 7 (teacher gallery + pin) — 45 min
- Step 8 (telemetry) — 15 min
- Step 9 (cleanup) — 30 min
- **Total: ~5.5 hours Claude Code + multi-hour 5-PC dry-run + customer
  site validation.**

## 11. What can go wrong (Tier 3 catastrophe scenarios)

1. **5-PC dry-run reveals teacher egress > 600 Mbps.** Selective
   forwarding math was wrong. Falls back to: reduce N visible cams
   per gallery (e.g., 12 instead of 16), or drop thumbnail FPS to 3,
   or both.
2. **Student CPU > 40% on customer's spec-min PCs.** AForge capture +
   JPEG-decode of 40 streams overwhelms low-end. Falls back: switch
   to H.264 (Tier 3 codec opt-in flag) which is faster to decode in
   software; OR reduce thumbnail FPS.
3. **Network with high jitter** (Wi-Fi, daisy-chained switches). Lossy
   `_outbox` drops too many frames; gallery becomes a stuttering mess.
   This is the "customer's network isn't actually gigabit switched"
   case from the pre-flight gate.
4. **Active-speaker swaps too fast (every word).** Hysteresis tuning
   off; increase from 1.5 s to 3 s.
5. **Privacy controversy.** Teacher's Pin override gets misused; PDPA
   complaint. Defensive logging: every `WebcamPresenterSet` logged with
   `Reason` field on teacher side + sent to student in balloon (same
   pattern as 13-D `MicMuteRequest`).

## 12. Tier 3 is optional

Restating from the top: **only commit to Tier 3 if customer explicitly
asks for full gallery.** Tier 1 + Tier 2 give them most of the
conference experience. If you decide to defer, mark this doc with a
DEFERRED note at the top + add to the architecture doc's Decision Log.
