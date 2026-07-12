# Phase 28 Findings — Camera streaming (Milestone 19, Mac-side)

**Goal:** stream the Mac's camera to the shipped Windows Teacher as a Conference
Mode peer cam, over existing wire envelopes — no shipped-repo change, no wire change.
**Result:** ✅ **LIVE-CONFIRMED (2026-07-13)** — the shipped Windows Teacher v1.2 displays
the Mac's camera live in its Conference gallery. Mac-side verified headless
(`MockTeacher --cameratest`: 12/12 well-formed `ConferenceCameraFrame`s, correct routing,
clean start/stop) and confirmed LIVE on Windows by tester visual verification. See
`docs/PHASE-28-LIVE-CONFIRMATION.md`.

**Sub-phases:** 28-A investigation · 28-B native AVCaptureSession · 28-C .NET service +
Camera tab · 28-D CameraStreamer wire · 28-E Connection wire-in · 28-F MockTeacher
`--cameratest` + downscale fix · 28-H docs.

## Investigation (28-A) — camera is NOT a mirror of screen streaming
The written plan assumed "reuse the H.264 encoder + the wire path exists
(`CameraStart/Frame/Stop`)." Investigation of the shipped repo proved both halves
wrong, saving a wrong-direction implementation (the third such catch in Sessions 3–6):

1. **No "Teacher views one student's camera in a window" feature.** Screen has
   `RequestStudentStreamAsync` → a dedicated `StudentScreenWindow` (`RenderMjpeg`/
   `RenderH264`). Camera has **no** request envelope, **no** receiver window, **no**
   context-menu command. Building one would require modifying the shipped Teacher.
2. **A student's cam reaches the Teacher only inside Conference Mode.**
   `OnConferenceCameraFrameReceived` routes an inbound `ConferenceCameraFrame` (0x0681)
   to a **gallery tile matched by `EndpointId`** and flips `IsCamLive`.
   `RebuildConferenceGallery` builds one tile per connected student the moment the
   Teacher starts a Conference — so a connected Mac gets a tile for free.
3. **Camera is JPEG-only on the wire** — `ConferenceCameraFrameMessage` =
   `{ SourceEndpointId, byte[] JpegData, long TimestampMs }`. No `Codec`, no
   `IsKeyframe` (unlike `ScreenStreamFrameMessage`). So the 27-B VideoToolbox H.264
   encoder **does not apply**; camera reuses the 27-C ImageIO JPEG path.
4. **`CameraStart/Frame` (0x0460) is the wrong direction** — teacher→student (drives a
   pop-up of the *teacher's* cam on the student). A student emitting it has no receiver.

**Locked decisions:** Conference-camera path (0x0680), **JPEG only**, **320×240 @ 10 fps
Q70** (matches the shipped Windows student exactly: AForge 320×240 Q70 ~10 fps).

## The AVCaptureSession camera path (28-B) — deltas from ScreenCaptureKit
Native `Camera.swift` parallels `NtyCapture.swift` but is **independent**: its own
session state (`CamState`), so a camera stream can coexist with a screen stream.
- **AVFoundation** (added `-framework AVFoundation`), not ScreenCaptureKit.
- `AVCaptureSession` + `AVCaptureVideoDataOutput` (BGRA `kCVPixelFormatType_32BGRA`) →
  same buffer shape as ScreenCaptureKit → **reuses the shared ImageIO encoder**
  (`encodeJpeg`/`encodeJpegFitted`, made internal; shared `CIContext`). Zero encoder dup.
- **Device enumeration works pre-authorization** — `localizedName` resolves before the
  grant, so the tab's device dropdown populates immediately (unlike screen, which needs
  the grant even to list displays cleanly). This Mac enumerates one: "MacBook Air Camera".
- **fps throttle via monotonic PTS** — cameras deliver at their native rate (~30 fps);
  drop frames by presentation timestamp to hit ~10 fps (device-agnostic; no wall clock).

## Camera permission model — different bucket, friendlier than screen
| | Screen Recording (27-A) | **Camera (28-B)** |
|---|---|---|
| TCC key | `NSScreenCaptureUsageDescription` (text NOT shown) | **`NSCameraUsageDescription` (text IS shown)** |
| Effective after grant | **on RELAUNCH only** (entitlement cached at launch) | **immediately** (no relaunch) |
| Preflight prompts? | no | no |
| Enumerate pre-grant | needs grant | **works pre-grant** |

Custom dialog text (visible to the user): *"NTY ClassroomCtrl uses your camera for
classroom video conferencing sessions with the teacher and other students."*
Tri-state on the .NET side (`CameraPermission { NotDetermined, Granted, Denied }`);
deny path logs + leaves the status pill red (graceful).

## Preset unreliability — the bug `--cameratest` caught before LIVE (28-F)
The first `--cameratest` run PASSED structurally but delivered **1920×1080** JPEGs at
**~122 KB/frame (≈9.6 Mbit/s)**. Cause: the MacBook camera **ignores the
`.qvga320x240` preset** and falls back to full-res — **AVCaptureSession presets are
not honored uniformly across devices.**

**Fix — `encodeJpegFitted`:** downscale every frame at encode time (aspect-preserving
`CIImage` transform → `CGImage` at the fitted size, shared `CIContext`), independent of
whatever capture resolution the device delivers. The native preset is now just a
cheap-to-scale hint (`.medium`, broadly supported), not the delivered size.

| | Preset-only (bug) | **encodeJpegFitted (fix)** |
|---|---|---|
| Delivered dims | 1920×1080 | **320×180** (16:9 fit of the 320×240 box, no distortion) |
| Per-frame | ~122 KB | **~7.4 KB** |
| Sustained @ 10 fps | ~9.6 Mbit/s | **~0.6 Mbit/s** — **~16× reduction** |

This restores the production peer-cam baseline (the shipped 320×240 Q70 cam is in the
same neighborhood). **MockTeacher-first discipline caught it headless, before LIVE.**

## Wire integration (28-D/E) — Conference lifecycle, zero wire change
`CameraStreamer` emits only existing envelopes:
- **`ConferenceCameraStart` (0x0680)** on start — `SessionId` + `SourceEndpointId(=self)`
  + name; sent AFTER a successful native start (no dangling Start on failure).
- **`ConferenceCameraFrame` (0x0681)** per frame — raw JPEG + `TimestampMs`.
- **`ConferenceCameraStop` (0x0682)** on stop (best-effort; dead socket → the Teacher's
  implicit PeerDisconnected stop covers it).

`SourceEndpointId` = `WireClient.EndpointId` (== `Envelope.SenderId`) — the exact key the
Teacher's `OnConferenceCameraFrameReceived` matches to a gallery tile.

`ConnectionViewModel` drives it from the Conference lifecycle: `ConferenceStart` (0x0670)
→ decode `SessionId` → start peer cam; `ConferenceEnd` (0x0671) → stop + clear;
disconnect → stop both streamers + reset. Self-tile shows
**"🔴 Camera live to teacher · N frames sent"**.

### Multi-trigger conflict — chosen default: respect the manual preview
The native camera is a **single session**, so if the Camera Capture tab (28-C) is already
previewing when a `ConferenceStart` arrives, `CameraStreamer.StartAsync` gets `-3`
(already running). We **respect the manual preview**: log a clear note and emit **no**
Start (Start is only sent after `rc==0`, so nothing spurious hits the wire). In normal
use the two aren't run simultaneously.

### Edge cases handled (28-E)
1. **Reconnect while in conference** → Teacher re-sends `ConferenceStart` on reconnect →
   fresh handling (session cleared on disconnect).
2. **Sequential Conference sessions** → each `Start`/`End` pair carries its own `SessionId`.
3. **Camera permission revoked mid-stream** → native simply stops delivering frames; no crash.
4. **Native start failure** → no spurious `Start` (ordering guarantee).
5. **Dead socket on disconnect** → `StopAsync` send is best-effort (try/catch); implicit stop covers it.

## Camera vs Screen — the shared/independent split
- **Independent:** native session state (`CamState` vs `NtyState`), .NET service
  (`CameraCaptureService` vs `ScreenCaptureService`), streamer (`CameraStreamer` vs
  `ScreenStreamer`), permission bucket. → the two can run concurrently.
- **Shared:** the ImageIO JPEG encoder + `CIContext`; the `nty_jpeg_cb` callback
  signature; the GC-rooted `[UnmanagedCallersOnly]` interop template (§20).

## Verification (Mac, no Windows box)
`MockTeacher --cameratest` — real `WireClient` + `CameraStreamer` vs an in-process mock
Conference host. **PASS:** 12/12 valid JPEG (`FFD8..FFD9`), 12/12 correct
`SourceEndpointId` (== Hello EndpointId == `Envelope.SenderId`), `ConferenceCameraStart`
seen (src match), `ConferenceCameraStop` seen after `ConferenceEnd`, 320×180 @ ~7.4 KB.
- T1–T26 PASS · `--selftest`/`--streamtest`/`--streamtest-h264` still PASS (screen paths
  unaffected — screen `encodeJpeg` untouched).

**Verification boundary:** as with M17/M18, headless proof is structural (well-formed
JPEG + correct routing + clean lifecycle). **Definitive pixel display = the LIVE Windows
test (28-G):** Teacher → Start Conference Mode → the Mac's gallery tile shows its camera.

## LIVE Windows test (28-G) — ✅ CONFIRMED 2026-07-13
Teacher v1.2 (unmodified) on Windows, Mac Sandbox on **borrowed hardware**, iPhone hotspot.
Tester visual verification: Mac connected → tile in student grid → Teacher **Start Conference
Mode** → **Mac camera appeared live in the Conference gallery** (320×180 JPEG) → self-tile
"🔴 Camera live to teacher" → **End Conference → clean stop**. Screenshots not captured
(borrowed Mac); structural proof = `--cameratest` (12/12 valid JPEG). Full writeup:
`docs/PHASE-28-LIVE-CONFIRMATION.md`.

## Constraints honored
Sandbox + `native/` + `tools/MockTeacher` only · `Shared.Wire` unchanged · shipped repo
untouched · screen streaming (M17/M18) intact · Camera Capture tab (28-C) intact ·
per-sub-phase commits · T1–T26 PASS.

## Deferred
Multi-camera device switching UI polish · true 320×240 (4:3) center-crop option ·
camera + screen concurrent conference share · adaptive quality · audio (next: AVFoundation mic).
