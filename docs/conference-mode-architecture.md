# Conference Mode — Cross-Tier Architecture

**Status:** design only, no code beyond [`tools/CamSpike`](../tools/CamSpike/).
Foundation for Phase 14-B/C/D implementation primers.
**Owners:** dev review → implementation per tier.
**Last updated:** 2026-05-29.

This document is the single source of truth for the Feature #4 (Conference
Mode — Zoom-style multi-webcam video) plan across all three tiers. Tier-
specific design docs live next to it:

- [`conference-tier1-design.md`](./conference-tier1-design.md) — implementation-ready
- [`conference-tier2-design.md`](./conference-tier2-design.md) — sketch
- [`conference-tier3-design.md`](./conference-tier3-design.md) — sketch + risks

---

## 1. Executive summary

**A surprise from the survey:** Phase 9.5 Camera Broadcast is already shipped.
`CameraBroadcastService` captures the teacher's webcam via
`AForge.Video.DirectShow`, JPEG-encodes via `System.Drawing`, broadcasts as
`CameraStart/Frame/Stop` (0x0460–0x0462) on the lossy `_outbox`, and
`CameraViewWindow` on the student renders MJPEG. **Mode 1 (teacher
broadcast) is ~70% built.** Tier 1 closes UX gaps (start/stop UI, no-cam
graceful handling, privacy banner) rather than building from scratch — the
same surprise that shaped Phase 13-A's "extend rather than rebuild" plan for
breakout rooms.

**Topology** is star (teacher as relay) — same as every other media path
in the codebase. **Routing** uses application `EndpointId` +
`Envelope.TargetGroupId` (Phase 13-B addition) + receiver-side `IsForMe` —
no new namespace gap. **State authority** is the teacher.

**Three tiers, ~1.5–2 weeks total**:

| Tier | Scope | Days | New protocol msgs | Touches |
|---|---|---|---|---|
| **1** | Mode 1 — teacher broadcast (closes Phase 9.5 gaps) | 2–3 | `WebcamStateUpdate` (S→T device-presence) | Existing `CameraBroadcastService`, new teacher Start/Stop UI, no-cam graceful disable, privacy banner on teacher |
| **2** | Mode 2 — group video (student-side cam → group peers) | 3–5 | `StudentGroupCameraStart/Frame/Stop`, `WebcamForceEnable`, `WebcamModeSet` | New `StudentCameraBroadcaster` (add AForge to Student.Agent), extends `GroupPeerView` with video tile, teacher cam-control UI |
| **3** | Mode 3 — full gallery (whole-class) | 5–7 + risk | `WebcamPresenterSet`, source-side dual-encode signaling | Active-speaker selection (reuses 13-D VAD), thumbnail+high-res dual-encode OR teacher-side downscale, paginated gallery UI, selective forwarding |

---

## 2. What's already in the codebase (survey)

One paragraph per file, what's there + what we reuse vs replace.

### `src/ClassroomCtrl.Teacher/Services/CameraBroadcastService.cs`
**Phase 9.5 production code.** Enumerates DirectShow video input devices,
opens via `AForge.Video.DirectShow.VideoCaptureDevice`, picks closest-match
capability, JPEG-encodes (Q70) each frame via `System.Drawing.Bitmap.Save`,
fires `ControlServer.BroadcastCameraFrameAsync`. Has an `Interlocked`-based
busy lock that drops overlapping encodes. **Reuse for Tier 1.** Gaps to
close: (1) no UI hook in `MainViewModel` exposing `Start(moniker, w, h, fps)`,
(2) no graceful no-cam disable — `EnumerateDevices` is called only on
explicit Start, (3) no privacy banner on teacher screen while broadcasting,
(4) no clean error path when another app holds the camera (the catch-all
`_logger.LogError` swallows + returns false but UI doesn't surface it).

### `src/ClassroomCtrl.Student.Agent/CameraViewWindow.xaml(.cs)`
Receives `CameraFrame` envelopes via `MainWindow.xaml.cs:662`, decodes JPEG
into `BitmapImage` with `OnLoad` caching + `Freeze`, sets `CamImage.Source`.
Static window position (top-right offset 500). **Reuse for Tier 1.** Tier 2
extends with multi-source mode (one cam tile per group peer in
`GroupPeerView` rather than a separate window).

### `src/ClassroomCtrl.Shared/Protocol/MessageType.cs` — Phase 9.5 codes
`CameraStart = 0x0460`, `CameraFrame = 0x0461`, `CameraStop = 0x0462` —
already wired teacher → all students. **Reuse for Tier 1**; Tier 2 adds
fresh codepoints in `0x065x` for student-originated streams + teacher
cam-control signaling.

### `src/ClassroomCtrl.Shared/Protocol/Messages.cs` — Phase 9.5 DTOs
`CameraStartMessage { Width, Height, Fps }` (3 keys, `[MessagePackObject(true)]`)
and `CameraFrameMessage { JpegData, TimestampMs }` (2 keys). **Reuse for
Tier 1.** Tier 2 adds new DTOs that mirror these but with
`SourceEndpointId` + `GroupId` for in-group fan-out.

### `src/ClassroomCtrl.Teacher/Services/ControlServer.cs:312-327`
`BroadcastCameraStartAsync`, `BroadcastCameraFrameAsync`,
`BroadcastCameraStopAsync` use plain `Envelope.Create` (broadcast — no
targeting). **Reuse for Tier 1.** Tier 2 adds three new methods that
mirror this shape but use `CreateGroupTargeted` (Phase 13-B helper) so
the receiver `IsForMe` filter routes by `_myRoomId`.

### `src/ClassroomCtrl.Networking/TcpControlServer.cs`
**Five per-peer channels** post-13-D: `_outbox` (DropOldest cap 16),
`_reliableOutbox` (Wait cap 4), `_audioOutbox` (DropOldest cap 3),
`_inputOutbox` (Wait cap 32), `_voiceOutbox` (Wait+TrySkip cap 8). Writer-
loop priority: reliable → audio → voice → input → one lossy. **Verdict:
ride the existing `_outbox` for Tier 1.** Phase 9.5's
`BroadcastCameraFrameAsync` already uses `_outbox` and shipped fine; a
single teacher cam stream is one steady ~80–200 kbps producer, well within
cap 16 ≈ 0.8 s of headroom. Tier 2 also stays on `_outbox` (each in-group
peer sees at most one host cam stream); Tier 3 reconsiders (see Decision 4).

### `src/ClassroomCtrl.Shared/Codec/*`
Full codec abstraction: `IVideoEncoder`, `VideoEncoderFactory`,
`MJpegEncoder`, `MediaFoundationH264Encoder`, `MediaFoundationH264AsyncEncoder`,
`OpenH264Encoder`. **Tier 1 stays on plain `System.Drawing` JPEG** (matches
9.5 path, ~30 KB at Q70 at 640×480). Tier 3 may opt-in to `IVideoEncoder` +
H.264 if dual-encode bandwidth math demands.

### `src/ClassroomCtrl.Student.Agent/StudentBroadcaster.cs`
**Reference shape for Tier 2's `StudentCameraBroadcaster`.** The screen
broadcaster pattern (Phase 4 Part 2 → Phase 11-B inc4 → Phase 13-C
dual-emit) is the template. Key bits to mirror: explicit codec field,
target FPS, IPC to Service for transport, dual-emit semantics (Tier 2
cam can emit AS `CameraFrame` AND `StudentGroupCameraFrame` if the teacher
is also viewing the same student's cam in a per-student dialog, though
the simpler model is "one stream at a time" — see Decision 7).

### `src/ClassroomCtrl.Student.Agent/MainWindow.xaml.cs:650-677`
Dispatch arms for `CameraStart` → opens `CameraViewWindow`, `CameraFrame`
→ `UpdateFrame(JpegData)`, `CameraStop` → closes window. **Reuse for
Tier 1.** Tier 2 adds dispatch arms for the new `0x065x` group-cam
messages routed via `IsForMe(env.TargetGroupId)`.

### `src/ClassroomCtrl.Student.Agent/GroupPeerView.xaml(.cs)`
Phase 13-C Tier 2 in-group screen view. **Extend for Tier 2 of conf.**
Adds a small "Camera" tile under the screen image showing the host's
webcam if it's live. Bound to `_groupCameraJpeg` field, refreshed on
each `StudentGroupCameraFrame` arrival.

### `src/ClassroomCtrl.Student.Agent/MicBroadcaster.cs`
Phase 13-D VAD output (`StateUpdateReady` → `IsSpeaking` flag in
`MicStateUpdateMessage`). **Reused for Tier 3 active-speaker
selection.** No change to MicBroadcaster itself; the active-speaker UI
logic on the teacher side consumes `ControlServer.MicStateUpdated`.

### `src/ClassroomCtrl.Teacher/ClassroomCtrl.Teacher.csproj`
References `AForge.Video 2.2.5` + `AForge.Video.DirectShow 2.2.5` (Phase 9.5
addition). **No change for Tier 1.** Tier 2 adds the same two packages to
`Student.Agent.csproj`.

### `src/ClassroomCtrl.Student.Agent/ClassroomCtrl.Student.Agent.csproj`
Does NOT currently reference AForge. **Tier 2 adds the packages.**

### `src/ClassroomCtrl.Shared/ClassroomCtrl.Shared.csproj`
References `H264Sharp 1.6.0` + `Vortice.MediaFoundation 3.6.2`. **Available
fallback** if AForge.Video.DirectShow proves unsuitable on student
hardware (CamSpike validates first).

### `tools/CamSpike/`
Phase 14-A step 0 — webcam capture feasibility spike using the same
`AForge.Video.DirectShow` library. Enumerates devices, opens default,
captures 5 s, reports negotiated capability + observed FPS + jitter + JPEG
size. Dev runs once on each test-rig type before Tier 1 commits.

---

## 3. Architectural decisions

For each: chosen option, why, alternatives considered, risk, validation
requirement.

### Decision 1 — Capture library
- **Chosen:** `AForge.Video.DirectShow 2.2.5` (already in Teacher.csproj,
  Phase 9.5 production code).
- **Why:** Shipped + battle-tested on this codebase; the library version is
  net4-targeted but warns-and-runs under net10.0-windows (Teacher project
  proves this in production); zero new package surface area for Tier 1.
- **Alternative considered — `Vortice.MediaFoundation` 3.6.2:** already in
  `Shared.csproj` (used for HW H.264 encode). Modern Win10+ API; higher
  ceiling. Con: significantly more code to wire (`IMFSourceReader` +
  `IMFMediaType` negotiation); no upside for Tier 1's "1 stream, ≤15 FPS,
  ≤640×480" needs. Hold in reserve if CamSpike rejects AForge on the dev
  hardware or a customer site.
- **Alternative considered — `OpenCvSharp4`:** ~200 MB native binaries +
  large dependency surface; installer impact rejected for Tier 1.
- **Alternative considered — `MediaCaptureAdapter` (WinRT):** modern but
  WinRT-in-WPF requires interop scaffolding; ergonomics worse than
  AForge for our needs.
- **Risk:** AForge 2.2.5 dates to 2013; NU1701 warning shipped in
  production. Hot-path: `VideoCaptureDevice.NewFrame` event delivers a
  `Bitmap`. If the bitmap surface format negotiated by DirectShow varies
  across student PCs, the JPEG path must validate `PixelFormat` —
  CamSpike's "first-frame format" line covers this.
- **Validation:** CamSpike on dev box (sirin + force) + 2-PC Tier 1
  acceptance test.

### Decision 2 — Topology
- **Chosen:** Star (teacher relays everything, same as #1/#2/#3).
- **Why:** Already implemented across every existing media path;
  school-LAN deployment doesn't need NAT traversal; reusing the 5-channel
  per-peer transport.
- **Alternative:** Per-group P2P mesh (Mode 2 only) for in-group cam.
  Rejected for the same reason as the 13-A breakout-rooms topology
  decision: ~3× code, breaks the "all state through teacher" invariant,
  complicates recording.
- **Risk:** Teacher NIC saturation at customer scale (see bandwidth math,
  §6, and Tier 3 risks).

### Decision 3 — Routing key
- **Chosen:** Application `EndpointId` + existing `Envelope.TargetGroupId`
  (Phase 13-B addition) + receiver-side `IsForMe`.
- **Why:** Zero new infrastructure. Tier 2 group-cam reuses the same
  field path Phase 13-C/13-D Tier 2/3 already uses; the
  `IsForMe(env.TargetGroupId)` extension is one line.
- **Alternative:** Per-cam-stream `TargetEndpointId` enumeration (loop
  over members at the teacher). Rejected — matches the 12-B regression
  pattern. Group fan-out via TargetGroupId is the canonical path now.
- **Risk:** None — every Tier 1+ message in the codebase uses this
  pattern.

### Decision 4 — Cam channel (transport)
- **Chosen Tier 1+2:** Reuse the existing per-peer `_outbox` lane
  (DropOldest cap 16).
- **Why:** Phase 9.5 already does this in production; one cam stream is
  one ~32 KB × 10 FPS = 320 KB/s producer per peer; cap 16 = ~800 ms of
  headroom; DropOldest matches "stale cam frame is musically worthless,
  show the latest".
- **Tier 3 reconsider:** if the gallery's active-speaker pattern means
  every student receives 1 high-res + 15 thumbnails, the cap might need
  to lift OR Tier 3 adds a 6th `_webcamOutbox` lane mirroring the 13-D
  `_voiceOutbox` decision. See Tier 3 § "Channel sizing".
- **Alternative — new 6th `_webcamOutbox` for Tier 1:** rejected. The
  isolation rationale that motivated `_voiceOutbox` (teacher loopback
  audio must NOT be evicted by voice bursts) doesn't apply here — cam
  frames and screen frames have the same lossy DropOldest semantics, so
  sharing the lane is correct.

### Decision 5 — Codec
- **Chosen Tier 1+2:** **MJPEG** via `System.Drawing` (matches Phase 9.5
  production path).
- **Why:** Simple encode + simple decode; CPU-bound but well within
  budget at 640×480 @ 10 FPS; no codec library dependency on student
  side beyond `System.Drawing.Common` (already transitive).
- **Tier 3:** **H.264** opt-in flag, default off, gated by HW MFT
  availability. Reuses existing `IVideoEncoder` factory + Phase 11-B
  inc2 Quick Sync wiring. Only enabled when Mode 3 bandwidth math demands
  it. See Tier 3 doc.
- **Alternative — H.264 from Tier 1:** rejected. Quick Sync paths add
  inc2-grade lifecycle risk (encoder stalls) that Mode 1's single
  teacher cam doesn't justify.

### Decision 6 — Default mic mode (privacy)
- **Chosen (addendum):** Cam default OFF; explicit student-toggle opt-in;
  teacher force-enable override; persistent privacy banner during capture.
- **Why:** Mirrors the Phase 13-D Decision 5 PTT-default rationale —
  privacy-by-default in a school context is non-negotiable. The
  `RemoteControlBanner` (Phase 6.5) + `VoiceLiveBanner` (Phase 13-D)
  visual language already establishes the "something on your machine is
  broadcasting" pattern. Cam joins as a third banner kind.
- **Cam-PTT (hold-to-show):** explicit opt-in per-student toggle in
  cam UI. Default hotkey: `C` (Space is taken by mic-PTT). Pass-through
  hook same as `PttKeyboardHook`.
- **Teacher force-enable:** overt — `WebcamForceEnable` arrives at
  student with a `Reason` field; balloon notification (mirrors Phase 13-D
  `MicMuteRequest` reason field).
- **Risk:** "Force enable" UX is the single highest customer-facing
  ethical risk — see §6.

### Decision 7 — Concurrent streams per student
- **Chosen Tier 1+2:** **One cam stream per student at a time.** A student
  whose cam is in-group-broadcast cannot simultaneously be
  cam-broadcast to teacher's "view this student" dialog.
- **Why:** Simplifies the broadcaster state machine, eliminates the
  dual-emit Tier 2 13-C had to do for screen share. Customer use case
  doesn't actually need both simultaneously (teacher monitors via the
  group-cam tile or by joining the group).
- **Alternative:** Dual-emit cam frames (same as 13-C screen). Adds
  complexity for marginal benefit.
- **Risk:** None for v1.

### Decision 8 — Active-speaker selection (Tier 3 only)
- **Chosen:** Auto via Phase 13-D `MicStateUpdate.IsSpeaking` signal, with
  teacher-override (`WebcamPresenterSet`).
- **Why:** Free signal — Tier 3 voice already broadcasts speaking state;
  Tier 3 conf reuses it. Teacher-override handles the "active speaker
  isn't actually who the class should focus on" case (e.g. presenting
  student isn't the loudest).
- **Alternative — Cam-only active-speaker (no voice):** would need a new
  source (Tier 3 cam VAD on frame deltas) — wasteful.
- **Risk:** If Mode 3 ships before Mode 3 voice activity is universal
  (e.g. PTT-mode student isn't transmitting voice while presenting),
  auto-selection misfires. Teacher override is the escape valve.

### Decision 9 — Cam resolution + FPS
- **Chosen:** **640×480 @ 10 FPS default**, ceiling 1280×720 @ 15 FPS
  (Mode 1 only, teacher hardware better).
- **Why:** Customer optical scale: students view in a ~320×240 tile in
  gallery mode → 640×480 capture downscales cleanly; bandwidth math
  (§6) closes at this size; CamSpike confirms achievable FPS.
- **Configurable:** teacher-side setting (Phase 14-D Tier 3 polish);
  Tier 1 ships fixed at default.

### Decision 10 — Hardware variability
- **Chosen:** Graceful no-cam handling AT EVERY ENTRY POINT.
  - Startup enumerate; if zero devices → cam-toggle UI disabled with
    tooltip "No webcam detected".
  - Runtime cam-unplugged → broadcaster catches, emits `WebcamStateUpdate
    { Live = false, Error = "Camera disconnected" }` to teacher, closes
    own banner.
  - Multiple cams → default to first device; settings dialog to pick
    explicitly (Tier 2 polish).
- **Why:** Phase 9.5's current code surfaces failures only via log; this
  is a Tier 1 UX gap.
- **Risk:** Customer site reports "cam toggle does nothing" — Tier 1
  acceptance criteria must include "toggle disabled with tooltip when
  no devices".

---

## 4. Wire protocol additions (full spec)

Code-point allocation (extending the existing layout):

```
0x0460  CameraStart                  (existing — Phase 9.5, T→all, in production)
0x0461  CameraFrame                  (existing — Phase 9.5, T→all)
0x0462  CameraStop                   (existing — Phase 9.5, T→all)

  ── Tier 1 additions (NEW) ──
0x0650  WebcamStateUpdate            (S→T, "I have/don't have a camera",
                                       "I just started/stopped my cam",
                                       used by teacher UI to enable/disable
                                       per-student cam controls)

  ── Tier 2 additions (NEW) ──
0x0651  StudentGroupCameraStart      (S→T→group peers, "I'm broadcasting my cam to group")
0x0652  StudentGroupCameraFrame      (S→T→group peers, single JPEG frame)
0x0653  StudentGroupCameraStop       (S→T→group peers, end stream)
0x0654  WebcamForceEnable            (T→S targeted, overt force, includes Reason)
0x0655  WebcamModeSet                (T→S targeted, Off / PTT / AlwaysOn + hotkey VK)

  ── Tier 3 addition (NEW) ──
0x0656  WebcamPresenterSet           (T→all, picks active-speaker for gallery
                                       focus; nullable EndpointId = clear)
```

### `Envelope` — no change for conference mode

The Phase 13-B `TargetGroupId` field at `[Key(6)]` is sufficient. Tier 2
fan-out uses `Envelope.CreateGroupTargeted`; Tier 1 stays on `Create`
(broadcast) since the cam target is "all students".

### Tier 1 DTO (NEW)

```csharp
[MessagePackObject(true)]
public class WebcamStateUpdateMessage
{
    /// <summary>True if at least one DirectShow video input device is
    /// enumerable on this student.  False = "no webcam detected".  Set
    /// at agent startup + on device-arrival/removal events (WM_DEVICECHANGE).</summary>
    public bool DeviceAvailable { get; set; }
    /// <summary>True if the cam is currently capturing + transmitting.
    /// Reflects student-toggle / PTT-down / teacher-force-enable state.</summary>
    public bool CamLive { get; set; }
    /// <summary>Current mode.  Mirrors MicMode shape from Phase 13-D.</summary>
    public WebcamMode Mode { get; set; }
    /// <summary>Optional per-frame error string; empty if healthy.  Surfaces
    /// "Camera held by another app" / "Driver error" / etc. to teacher UI.</summary>
    public string LastError { get; set; } = "";
}

public enum WebcamMode : byte { Off = 0, Ptt = 1, AlwaysOn = 2 }
```

### Tier 2 DTOs (NEW)

```csharp
[MessagePackObject(true)]
public class StudentGroupCameraControlMessage
{
    public Guid GroupId { get; set; }
    public Guid SourceEndpointId { get; set; }
    public string SourceDisplayName { get; set; } = "";
    public bool Start { get; set; }   // true = Start, false = Stop
    public int Width { get; set; } = 640;
    public int Height { get; set; } = 480;
    public int Fps { get; set; } = 10;
}

[MessagePackObject(true)]
public class StudentGroupCameraFrameMessage
{
    public Guid GroupId { get; set; }
    public Guid SourceEndpointId { get; set; }
    public byte[] JpegData { get; set; } = Array.Empty<byte>();
    public long TimestampMs { get; set; }
}

[MessagePackObject(true)]
public class WebcamForceEnableMessage
{
    /// <summary>True = teacher overrides student-off; false = teacher
    /// re-allows student to turn off.</summary>
    public bool Enable { get; set; }
    /// <summary>Shown verbatim in the student's balloon notification.</summary>
    public string Reason { get; set; } = "";
}

[MessagePackObject(true)]
public class WebcamModeSetMessage
{
    public WebcamMode Mode { get; set; }
    public string HotkeyVk { get; set; } = "C";   // mirrors MicPttSet shape
}
```

### Tier 3 DTO (NEW)

```csharp
[MessagePackObject(true)]
public class WebcamPresenterSetMessage
{
    /// <summary>Null clears the active-speaker focus → gallery returns to
    /// equal-tile mode.  Non-null = focus this student's cam at high-res.</summary>
    public Guid? ActiveSpeakerEndpointId { get; set; }
    /// <summary>True = source-side dual-encode (active speaker emits BOTH
    /// high-res frames AND a thumbnail stream); false = teacher-side downscale
    /// (active-speaker source emits only high-res; teacher relay decimates per
    /// recipient).  Tier 3 design picks one; this flag exists for A/B at
    /// validation time without a wire rev.</summary>
    public bool SourceDualEncode { get; set; }
}
```

### Channel routing summary

| MessageType | Channel | Why |
|---|---|---|
| `CameraStart` / `CameraStop` (existing) | `_outbox` | Phase 9.5 default; control + frames share the lossy lane fine. Could be promoted to `_reliableOutbox` in Tier 1 cleanup so start/stop don't compete with frames. |
| `CameraFrame` (existing) | `_outbox` | Lossy DropOldest is correct. |
| `WebcamStateUpdate` (Tier 1) | `_reliableOutbox` | Small + must arrive in order for the teacher cam-control UI to reflect state. |
| `StudentGroupCameraStart/Stop` (Tier 2) | `_reliableOutbox` | Must arrive in order. |
| `StudentGroupCameraFrame` (Tier 2) | `_outbox` | Same shape as Phase 13-C `StudentGroupScreenStreamFrame`. |
| `WebcamForceEnable` / `WebcamModeSet` (Tier 2) | `_reliableOutbox` | Targeted; must not be dropped. |
| `WebcamPresenterSet` (Tier 3) | `_reliableOutbox` | Broadcast; must not be dropped. |

**Cleanup note for Tier 1:** Phase 9.5's `BroadcastCameraStartAsync` /
`BroadcastCameraStopAsync` currently ride `_outbox`. Promoting Start/Stop
(but not Frame) to `_reliableOutbox` is a one-line change and a real
robustness win (a dropped Start = a black `CameraViewWindow` until the
teacher gives up + restarts). Flag for the Tier 1 PR.

---

## 5. Open unknowns

Locked-in answers (from the primer) apply throughout. Remaining unknowns
the dev should clarify before Tier 1 *kickoff* (not before this design
lands):

1. **Cam-PTT default hotkey.** Primer recommends `C`; alternative is `Tab`.
   Default-`C` chosen here; flag for dev confirmation before Tier 2 commit.
2. **Force-enable confirmation UX.** Teacher clicks "Force Enable Cam" →
   immediate force, or modal "Are you sure?" confirm? PDPA + customer
   ethics lean toward modal; Tier 1 design assumes immediate (with overt
   student-side balloon); revisit at Tier 2 if customer asks.
3. **Default mode at student startup.** Off, PTT, or AlwaysOn? **Locked
   to Off.** Privacy-by-default.
4. **Multiple cams on one PC.** Tier 1 = default to device[0]; Tier 2 =
   settings dialog to pick. Confirm Tier 2 priority vs other Tier 2 work.
5. **Class-wide "Conference Start" workflow** (Mode 1+2 simultaneously).
   Tier 2 design assumes "teacher cam = always available toggle;
   per-group cam = teacher enables per group". Confirm dev wants a
   single "Conference Mode ON" entrypoint that enables all of the above
   at once, or per-feature toggles.

---

## 6. Risks across tiers

### Bandwidth at customer scale (Tier 3 — the real concern)

Customer addendum: class 40 PCs, up to 10 groups, group size 4–6.

Per-stream bandwidth at the design defaults:
- 640×480 @ 10 FPS, JPEG Q70 ≈ **~30–40 KB/frame** → **~300–400 KB/s = ~2.4–3.2 Mbps per stream**.
- (CamSpike confirms; the README's "JPEG Q70 size mean" line is the
  load-bearing measurement.)

Mode 1 (teacher broadcast, 1 stream × N receivers):
- 1 × 40 students × 400 KB/s = **16 MB/s = ~128 Mbps total teacher egress.**
- Within gigabit headroom + competes with screen share but doesn't
  saturate. ✅

Mode 2 (group video, presenter-mode per group, ≤1 active cam per group
× ≤6 in-group receivers):
- 10 groups × 1 × 6 = 60 streams × 400 KB/s = **24 MB/s = ~192 Mbps relay**.
- Still under gigabit. ✅
- Teacher relay CPU load is real (60 active relay paths); profile during
  Tier 2 validation.

Mode 2 (all-members mode, every in-group student broadcasts to every
other in-group member — NOT the Tier 2 default):
- 10 groups × 6 members × 5 receivers = **300 streams × 400 KB/s =
  120 MB/s ≈ 960 Mbps**.
- **At the gigabit edge.** Tier 2 ships presenter-mode default; all-
  members is a flag for customer-validated upgrade. ⚠️

Mode 3 (full gallery, whole-class everyone-to-everyone, naive):
- 40 students × 40 receivers × 400 KB/s = **1600 streams × 400 KB/s ≈
  640 MB/s ≈ 5 Gbps**.
- **EXCEEDS gigabit LAN by 5×.** ❌
- **Solution required:** Selective forwarding (active-speaker SFU
  pattern). Math:
  - 1 active-speaker high-res (400 KB/s) +
  - 39 other students thumbnail (160×120 @ 5 FPS, ~5–8 KB ≈ 30–40 KB/s) =
  - per-recipient inbound: 400 + 39 × 35 = ~1.8 MB/s = ~14 Mbps.
  - Teacher egress: 40 recipients × 1.8 MB/s = **72 MB/s ≈ 580 Mbps.**
  - Still tight but fits a 1 Gbps switched LAN, AND drops to under
    300 Mbps if half the students disable cam (likely in practice).
- **Caveat:** requires source-side dual-encode (high + thumbnail per
  webcam) OR teacher-side downscale per recipient (CPU-heavy on teacher).
  Tier 3 design picks one; the `WebcamPresenterSet.SourceDualEncode`
  flag lets us A/B at validation time.
- **Complexity:** significant. Tier 3 is high-risk.

### CPU load — concurrent cam capture + screen share + voice on student low-spec

Phase 11-B inc1 already established the screen-capture+H.264 budget on
i5 student boxes. Cam adds:
- AForge `NewFrame` callback + JPEG encode at Q70 ≈ **~2–4% additional CPU
  on 7th-gen i5** at 640×480 @ 10 FPS (per CamSpike on dev box).
- Risk: Celeron / Atom fleets approach 20% CPU on cam alone + screen
  share + voice.
- **Mitigation:** Tier 1 ships 10 FPS default; Tier 2 student-side cap at
  10 FPS with no "high quality" toggle until customer-fleet identified;
  add "Min spec for Conference Mode" line to ship doc.

### Active-speaker detection (Tier 3) latency + churn

Phase 13-D `MicStateUpdate.IsSpeaking` is RMS-driven with ~100 ms windows.
Naively using it as the gallery-focus trigger means rapid speaker swaps
on classroom interjections.
- **Mitigation:** Tier 3 design includes a 1.5 s hysteresis filter
  (current speaker must remain silent 1.5 s AND new speaker must speak
  1.5 s before the swap fires).
- **Teacher override:** explicit pin via `WebcamPresenterSet` neutralizes
  auto-selection until cleared.

### Privacy — overt indicators + force-enable ethics

Strong customer-facing risk in a school context.
- **Persistent on-screen banner** when cam is live (mirrors
  `VoiceLiveBanner` + `RemoteControlBanner`). Red capsule with text
  "🎥 Camera LIVE — visible to {target}". Banner flashes during cam-PTT
  hold-down.
- **Teacher force-enable is overt**: balloon notification with the
  teacher-supplied `Reason` string + audible chime. Mirrors Phase 13-D
  `MicMuteRequest` UX. Customer must be able to defend "the student knew
  their cam was just turned on" in any dispute.
- **PDPA notify:** Tier 1 acceptance includes verifying
  `StudentRecordingNotify` (0x0403) fires when cam goes live during a
  per-student recording.
- **Ship doc clause:** "Conference Mode discloses webcam state to the
  classroom; force-enable is logged + visible to the student. Confirm
  use is consistent with your school's PDPA policy."

### Camera unplugged mid-session

AForge `VideoCaptureDevice.VideoSourceError` fires; broadcaster catches,
emits `WebcamStateUpdate { CamLive = false, LastError = ... }` + closes
its banner. Teacher UI greys out the per-student cam toggle.
- **Validation:** Tier 1 acceptance includes unplugging the cam during
  active broadcast on the dev box.

### Wire-format ordering

Same constraint as 13-A: new `[Key(n)]` fields go at the highest index;
new MessageType codes appended (0x0650+). Tier 1 wire-compat tests T12+
extend `tools/EnvelopeWireCompatTest` with cam DTOs.

---

## 7. Implementation order

1. **Phase 14-A (this round)** — design docs + CamSpike. ~1 hour.
2. **Dev runs CamSpike** on the test rig + fills the README "Dev box
   results" section. Verdict: WORKS / PARTIAL / INVESTIGATE.
3. **Phase 14-B (Tier 1)** — see `conference-tier1-design.md`. ~2–3 hours
   Claude Code (small because Phase 9.5 ate the hard part). 2-PC
   validation.
4. Dev runs Tier 1 acceptance + decides whether to proceed.
5. **Phase 14-C (Tier 2)** — see `conference-tier2-design.md`. ~3–5
   hours. 3-PC validation ideal (teacher + 2 students in a group).
6. Dev runs Tier 2 acceptance + decides whether to commit to Tier 3.
7. **Phase 14-D (Tier 3)** — see `conference-tier3-design.md`. ~4–6
   hours + risk. 5+ PCs to actually exercise gallery at scale.
8. Customer-site validation pre-ship for Tier 3 bandwidth + UI scaling.

**Recommended phasing**: Tier 1 ships "teacher cam" as ~50% of customer
value. Tier 2 ships "group video" as ~80%. **Tier 3 is optional** if
Tier 1+2 satisfy the customer; only commit if the customer explicitly
asks for full gallery.

---

## 8. What this design deliberately does NOT change

- All Feature #1 (basic remote control / chat / file transfer) + #2
  (screen share) + #3 (breakout rooms Tier 1+2+3) untouched.
- The 5-channel transport (`_outbox`, `_reliableOutbox`, `_audioOutbox`,
  `_inputOutbox`, `_voiceOutbox`) untouched. Tier 1+2 cam rides
  `_outbox`. Tier 3 may add a 6th lane — design only, no commit until
  Tier 3 validates the need.
- Phase 9.5 `CameraBroadcastService` capture/encode loop — Tier 1
  reuses unchanged; only adds UI hooks + privacy banner + state-update
  emission around it.
- Wire format ordering of `Envelope` + existing DTOs. Only new
  `MessageType` codepoints appended at 0x0650+.
- Phase 11-B inc4 screen-share capture/encode/lossy `_outbox`. Cam
  shares the lane but is a different message type; routing is the only
  intersection.
- Phase 13-D voice path. Mode 3 active-speaker selection consumes
  `MicStateUpdated` events as a READ-only signal.

These are all already validated and we don't want to re-litigate any of
them.

---

## 9. Cleanup that should ride this feature

While we're here:

1. **`MainViewModel` cam-start path** — Phase 9.5's
   `CameraBroadcastService.Start` is called from one menu item but with
   no error surfacing. Tier 1 wires the call through `MainViewModel`
   with proper `IsActive` binding + an error toast.
2. **`CameraStart` / `CameraStop` on `_reliableOutbox`** — promote (one
   line in `ControlServer`). Frames stay on `_outbox`.
3. **`CameraViewWindow` window position** — currently hardcoded
   `Left = ScreenWidth - 500`. Should remember last position. Defer to
   Tier 2 polish — not load-bearing for ship.
4. **Privacy banner on teacher** — Phase 9.5 has no banner; Tier 1
   adds one. Mirrors `VoiceLiveBanner` from Phase 13-D.

---

## 10. Cross-tier diagrams

### Mode 1 — Teacher broadcast (Tier 1)

```
                          ┌─────────────────────┐
                          │  Teacher MainWindow │
                          │  ┌───────────────┐  │
                          │  │ "Start Cam"   │──┼──> CameraBroadcastService.Start()
                          │  └───────────────┘  │       │
                          │  (privacy banner    │       │ AForge.VideoCaptureDevice
                          │   shows while live) │       │   ↓ NewFrame
                          └─────────────────────┘       │   ↓ JPEG Q70
                                                        │   ↓
                                            ControlServer.BroadcastCameraFrameAsync
                                                        │
                                          ┌─────────────┴─────────────┐
                                          │                           │
                                       _outbox                     _outbox
                                          │                           │
                                          ▼                           ▼
                                  Student.Service  (×N)       Student.Service
                                          │                           │
                                          │ IsForMe = true (broadcast)│
                                          │                           │
                                          ▼                           ▼
                                  Student.Agent                 Student.Agent
                                          │                           │
                                          ▼                           ▼
                                  CameraViewWindow             CameraViewWindow
                                  UpdateFrame(jpeg)            UpdateFrame(jpeg)
```

### Mode 2 — Group video (Tier 2)

```
   Student (group host, cam ON)        Teacher (relay)         Other in-group students
   ─────────────────────────────       ────────────────         ──────────────────────
   AForge VideoCaptureDevice       ──> BroadcastReliable        ←── IsForMyGroup = true
   ↓ NewFrame                          (Start/Stop)
   ↓ JPEG Q70                          BroadcastAsync
   ↓                                   on _outbox to
   StudentCameraBroadcaster            in-group peers
   ↓ IPC                                              ──>      ──>  CameraViewWindow
   StudentService.SendAsync                                         (or tile in
   ↓                                                                 GroupPeerView)
   StudentGroupCameraFrame
   (TargetGroupId = mine,
    SourceEndpointId = mine)

   Non-group students: IsForMe = false → drop at Service-side dispatcher.
   Self (the host): SenderId == _myEndpointId → self-filter in Agent dispatch.
```

### Mode 3 — Full gallery active-speaker (Tier 3)

```
   Active speaker (auto via MicState.IsSpeaking OR teacher-pinned via
   WebcamPresenterSet)
      │
      │ emits 640×480 @ 10 FPS  (high-res stream)
      │ AND 160×120 @ 5 FPS     (thumbnail stream — source dual-encode)
      ▼
   ControlServer relay
      │
      ├──> _outbox to ALL students: high-res frame
      │
      └──> _outbox to ALL students: thumbnail frame (also serves
           OTHER students' thumbnails when they are not active speaker)

   Each student's gallery UI: 1 high-res tile (current speaker) + N-1
   thumbnail tiles paginated 16/page.
```

---

## 11. Reference — Phase 13-A precedent

This doc deliberately mirrors [`breakout-rooms-architecture.md`](./breakout-rooms-architecture.md)
in structure + decision rigor. The "surprise: significant infra is already
shipped" pattern is identical (Phase 8/8.5/9.7 for breakout rooms, Phase
9.5 for conference mode). Tier-1-closes-gaps, Tier-2-extends-with-new-DTOs,
Tier-3-takes-the-architectural-risk is the same shape.

Where Phase 14 differs from Phase 13:
- **Bandwidth math is harder.** Voice is ~32 KB/s; cam is ~400 KB/s.
  Mode 3 gallery is the first wire-protocol design in this codebase
  that requires SFU-style selective forwarding to fit gigabit.
- **Library decision is locked.** AForge is shipped; no spike-or-rewrite
  question like 13-D's AEC engagement check.
- **CamSpike is verification, not gating.** AECSpike could have rejected
  the entire Tier 3 voice plan if AEC didn't engage; CamSpike merely
  confirms the FPS budget on dev hardware.
