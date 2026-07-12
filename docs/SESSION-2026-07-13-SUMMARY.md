# Session Summary — 2026-07-13 (macOS track: Phase 27 native APIs)

Continuation of the macOS/Avalonia port. Session 1 (2026-07-12) reached **Milestone 15**
(LIVE cross-platform interop) — see `SESSION-2026-07-12-SUMMARY.md`. This day added the
**first native-macOS-API work** (Phase 27) across three focused sessions:
- **Session 3 — Phase 27-A:** ScreenCaptureKit basic capture (**Milestone 16**).
- **Session 4 — Phase 27-C:** wire captured frames to the Windows Teacher (**Milestone 17,
  LIVE-CONFIRMED** — MJPEG).
- **Session 5 — Phase 27-B:** VideoToolbox H.264 encoding (**Milestone 18, LIVE-CONFIRMED** —
  ~10× bandwidth win).

> The other 2026-07-13 track — **v1.2.1 Windows bulk-lock patch** — lives in the shipped repo
> `~/Dev/nty-classroom-macos/docs/SESSION-2026-07-13-SUMMARY.md`. Separate branch, separate
> product; non-interfering.

**Repo:** `~/Dev/nty-classroom-avalonia/` · branch `avalonia-experiment` · remote `origin`.

---

## Milestone 16 — Phase 27-A: ScreenCaptureKit basic capture ✅
Established the **native-interop template for all Phase 27 subsystems**: a tiny Swift dylib
(`@_cdecl` C ABI) P/Invoked from the (unchanged) `net10.0` Avalonia app; frames marshalled to
the UI thread (§18). No `net10.0-macos` workload retarget.
- `native/NtyCapture/` → `libNtyCapture.dylib` (Swift + `build.sh`).
- Permission (TCC) + a minimal `.app` bundle (`scripts/package-app.sh`) so the grant persists.
- `SCStream` main-display capture → BGRA callback → `WriteableBitmap` in a "Screen Capture" tab
  with fps/resolution/dropped stats.
- **Live proof:** `docs/phase-27-a-screencapture.png` — real desktop captured in the Sandbox
  (fps 9.1 · 1470×956). Peculiarities documented (relaunch-latched TCC, stride 5888>5880,
  call-scoped buffer). Cheat sheet **§20**.

## Milestone 17 — Phase 27-C: Mac desktop in the Windows Teacher ✅ LIVE-CONFIRMED
The full cross-platform circle, over **existing** wire envelopes — **no shipped-repo change,
no wire change**.
- Investigation confirmed the Teacher already receives (`ViewStudentScreen` → `StudentStreamStart`
  → `RenderMjpeg`), MJPEG is its default, and `StudentStreamStart/Frame/Stop` +
  `ScreenStreamFrameMessage` are existing (T4 Part 2, part of T1–T26).
- **27-C-1** native JPEG (`nty_capture_start_jpeg`, ImageIO, downscale 1280×720 Q60).
- **27-C-2** `ScreenStreamer`: `StudentStreamStart` → capture JPEG → `StudentStreamFrame` via
  `WireClient.SendAsync`; self-tile "🔴 Streaming … N frames".
- **27-C-3** `MockTeacher --streamtest`: real WireClient + ScreenStreamer → **8/8 valid JPEGs**
  headless (no Windows box).
- **LIVE (2026-07-13):** Windows Teacher v1.2 → *View Screen* → **the Mac's actual desktop
  appears live**. See `docs/PHASE-27-C-LIVE-CONFIRMATION.md` + `docs/live-test-milestone-17/`.

## Milestone 18 — Phase 27-B: H.264 encoding (~10× bandwidth) ✅ LIVE-CONFIRMED
VideoToolbox hardware H.264, byte-matching the shipped OpenH264 Annex-B format — same wire,
zero shipped-repo change.
- Investigation: Teacher decodes via **H264Sharp/OpenH264** (Annex-B, in-band SPS/PPS per IDR);
  `Codec=H264` + `IsKeyframe` already exist in `Shared.Wire`.
- **27-B-2** native `VTCompressionSession` (Baseline, no-reorder, CBR, IDR every fps×2) → output
  handler **converts AVCC→Annex-B + prepends SPS/PPS on keyframes** → `nty_capture_start_h264`.
- **27-B-3/4** service `StartH264Async` + `H264FrameReceived`; `ScreenStreamer` picks codec from
  `StudentStreamStartRequest.Codec` (H264 or MJPEG — backward compatible).
- **27-B-5** `MockTeacher --streamtest-h264`: NAL-structure check → **12/12 well-formed** (first
  keyframe, all Annex-B, keyframes SPS+PPS+IDR, deltas slice).
- **LIVE (2026-07-13):** Teacher → *View Screen* → **Mac desktop via `RenderH264`** at **~1.16
  Mbit/s @ 1080p** vs M17 MJPEG ~6.5 Mbit/s @ 720p → **~10× per-pixel**. See
  `docs/PHASE-27-B-LIVE-CONFIRMATION.md`. Cheat sheet **§20** VideoToolbox addendum.

### Bandwidth: M17 MJPEG vs M18 H.264
| | M17 MJPEG | **M18 H.264** |
|---|---|---|
| Resolution | 1107×720 | **1920×1080** |
| Per-frame | ~124–137 KB | **~16 KB** |
| Sustained | ~6.5 Mbit/s | **~1.16 Mbit/s** |
| Net | baseline | **~10× per-pixel** |

## Milestone 19 — Phase 28: Camera streaming (AVCaptureSession) ⏳ Mac-side ✅ · LIVE pending
Camera (webcam) peer-cam to the shipped Teacher's **Conference gallery**, over existing
envelopes — no shipped-repo change, no wire change. **Session 6.**
- **Investigation (28-A) reshaped the plan** (third wrong-direction catch): camera is NOT a
  mirror of screen — **no** Teacher "view one student's cam" window/request exists; a student's
  cam reaches the Teacher **only inside Conference Mode** (`ConferenceCameraFrame` 0x0681 →
  gallery tile by `EndpointId`); camera is **JPEG-only** (no `Codec`/`IsKeyframe`) so the H.264
  encoder does NOT apply; `CameraStart/Frame` 0x0460 is the wrong direction. Locked: Conference
  path + JPEG + 320×240 @ 10 fps (matches the shipped Windows student).
- **28-B** native `AVCaptureSession` (`Camera.swift`, +AVFoundation) → BGRA → shared ImageIO
  encoder; own session state (`CamState`); camera permission (`NSCameraUsageDescription`, shown,
  no relaunch, enumerate pre-grant). **28-C** `.NET CameraCaptureService` + Camera Capture tab
  (device dropdown + live self-preview) — **interactively confirmed** (permission dialog with
  custom text, immediate live preview).
- **28-D/E** `CameraStreamer` (`ConferenceCameraStart/Frame/Stop`) + `ConnectionViewModel` wire-in
  on the Conference lifecycle; self-tile "🔴 Camera live". Multi-trigger default = respect the
  manual preview; 5 edge cases handled.
- **28-F** `MockTeacher --cameratest`: **12/12** valid JPEG + correct `SourceEndpointId` + clean
  start/stop. **Caught a real bug headless:** the cam ignores the `qvga320x240` preset (delivered
  1080p, ~122 KB/frame) → fixed with **`encodeJpegFitted`** (encode-time aspect downscale) →
  **320×180 @ ~7.4 KB/frame (~16× reduction, ~0.6 Mbit/s)**.
- **LIVE (28-G):** Teacher → **Start Conference Mode** → Mac's gallery tile shows its camera.
  See `docs/PHASE-28-FINDINGS.md`. Cheat sheet **§20** AVCaptureSession addendum. *(awaiting LIVE
  confirmation.)*

### Bandwidth: camera preset-bug vs fitted
| | Preset-only (bug) | **encodeJpegFitted (fix)** |
|---|---|---|
| Delivered dims | 1920×1080 | **320×180** (16:9 fit, no distortion) |
| Per-frame | ~122 KB | **~7.4 KB** |
| Sustained @ 10 fps | ~9.6 Mbit/s | **~0.6 Mbit/s** (~16× reduction) |

## Commits (Sessions 3–5) — 16
```
3a350c7  27-B-7: findings + cheat sheet §20 addendum (VideoToolbox) + screenshot
7cfc1ed  27-B-5: MockTeacher --streamtest-h264 (NAL-structure verification)
8ce9201  27-B-4: codec-aware wire integration (H.264 or MJPEG per request)
8dd2df6  27-B-3: ScreenCaptureService H.264 API
8f95051  27-B-2: native VideoToolbox H.264 encoder (Annex-B, matches shipped)
7e71739  Milestone 17 LIVE-CONFIRMED (close-out)
0c9156a  27-C-4: findings + streaming screenshot (Milestone 17 Mac-side)
9eeb1cc  27-C-3: MockTeacher --streamtest + viewscreen
9ca1a18  27-C-2: wire integration — StudentStreamStart → JPEG → StudentStreamFrame
f5ab855  27-C-1: native JPEG capture mode (ImageIO) + service API
96bf88a  27-A-4: findings + cheat sheet §20 (native macOS interop)
865d5b6  27-A-3 verify: headless screencapture scenario + live proof screenshot
f852d69  27-A-3-ui: Screen Capture tab — live preview + fps/res/dropped stats
eb384c1  27-A-3-net: ScreenCaptureService — start/stop + GC-rooted frame callback
cbfa6cd  27-A-3-swift: SCStream main-display capture → BGRA C callback
a195338  27-A-2: native ScreenCaptureKit helper + permission + .app bundle
```

## Verification (all green, on this Mac)
| Check | Result |
|---|---|
| Solution build | ✅ 0 errors |
| T1–T26 wire compat | ✅ PASS (no wire change) |
| ScreenCaptureService (headless) | ✅ real frames, stride stripped |
| `MockTeacher` — `--selftest` / `--streamtest` / `--streamtest-h264` | ✅ all PASS |
| Shipped Windows repo | ✅ untouched |
| LIVE Windows *View Screen* (MJPEG + **H.264**) | ✅ Mac desktop displayed |

## Combined day (2026-07-13) — both tracks
| Track | Repo / branch | Outcome |
|---|---|---|
| **Windows patch** | `nty-classroom-macos` / `v1.2-multiselect` | **v1.2.1** bulk-lock fix SHIP-READY (tag `v1.2.1`) |
| **macOS native APIs** | `nty-classroom-avalonia` / `avalonia-experiment` | **Milestones 16–18** — screen capture + **LIVE MJPEG & H.264 streaming to the Windows Teacher (~10× bandwidth)** |

## Tooling / docs state (macOS track)
- Cheat sheet: **21 sections** (§20 native interop + VideoToolbox addendum).
- HeadlessCapture: scenarios incl. `screencapture`, `streaming`.
- MockTeacher: `--selftest` + `--streamtest` + **`--streamtest-h264`** + interactive
  `viewscreen` / `viewscreen-h264`.
- Native: `native/NtyCapture/` (Swift dylib — ScreenCaptureKit + ImageIO + **VideoToolbox**),
  `scripts/package-app.sh` (.app bundle).
- **Codec support: MJPEG + H.264** (chosen per `StudentStreamStartRequest.Codec`).

## Milestones (macOS track)
| # | Phase | Outcome |
|---|---|---|
| 15 | 26.0 | LIVE wire interop (lock/policy/chat/hand) |
| 16 | 27-A | ScreenCaptureKit capture (native foundation) |
| 17 | 27-C | **LIVE** MJPEG screen streaming → Windows Teacher |
| 18 | 27-B | **LIVE** H.264 streaming (~10× bandwidth), **MJPEG + H.264** |
| **19** | **28** | Camera peer-cam → Conference gallery (JPEG 320×180, ~16× vs preset bug) — Mac-side ✅, **LIVE pending** |

## Next-session priority queue
1. **Audio (AVFoundation)** — mic capture + the Conference voice-audio wire frames; reuses the
   §20 template (independent session, own C callback). **Recommended next.**
3. **Screen-lock enforcement** (overlay + Accessibility) · **Input hooks** (CGEventTap).
4. **System integration** — permissions bundle, auto-start, packaging/signing for distribution.
5. **Ship v1.2.1 installer** (Windows track, ~1 h) — customer commitment.
6. Progressive UI ports + runtime light/dark theme swap.

## Team handoff
- **Cross-platform demo circle COMPLETE + optimized:** M15 wire compat · M17 MJPEG LIVE ·
  **M18 H.264 LIVE (~10× bandwidth)**. A macOS student streams its screen, production-grade,
  into the shipped Windows Teacher over unchanged wire.
- **Native-APIs progression:** ✓ 27-A capture · ✓ 27-C JPEG · ✓ 27-B H.264 · ✓ 28 camera
  (AVCaptureSession, Mac-side) · ⏳ audio/lock/input · ⏳ system integration.
- Native-interop template (**§20**, incl. VideoToolbox) proven three times; `MockTeacher`
  `--streamtest*` is the reuse + de-risk pattern for every remaining subsystem.
- Both tracks green, synced with origin, independently documented.
