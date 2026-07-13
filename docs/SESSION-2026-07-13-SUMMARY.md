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

## Milestone 19 — Phase 28: Camera streaming (AVCaptureSession) ✅ LIVE-CONFIRMED
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
- **LIVE (28-G, 2026-07-13):** Teacher → **Start Conference Mode** → **Mac's gallery tile shows
  its camera live** (320×180 JPEG) → clean stop on End Conference. Tester visual verification on
  borrowed Mac (screenshots not captured; structural proof = `--cameratest` 12/12). See
  `docs/PHASE-28-LIVE-CONFIRMATION.md` + `docs/PHASE-28-FINDINGS.md`. Cheat sheet **§20**
  AVCaptureSession addendum.

### Bandwidth: camera preset-bug vs fitted
| | Preset-only (bug) | **encodeJpegFitted (fix)** |
|---|---|---|
| Delivered dims | 1920×1080 | **320×180** (16:9 fit, no distortion) |
| Per-frame | ~122 KB | **~7.4 KB** |
| Sustained @ 10 fps | ~9.6 Mbit/s | **~0.6 Mbit/s** (~16× reduction) |

## Milestone 20 — Phase 29: Bidirectional audio (AVAudioEngine) ✅ LIVE-CONFIRMED (as scoped)
Raw-PCM audio both ways between the Mac and the shipped Teacher, over existing envelopes —
no shipped-repo change, no wire change. **Session 7.**
- **Investigation (29-A) pivot** (4th wrong-direction catch): audio is **raw PCM** (16 k/mono/
  16-bit, 100 ms/3200 B) — no codec (29-C dropped); the clean trigger is **Mic Monitor
  (0x0490), not Conference** (audio's "listen to one student" is first-class, so it's *easier*
  to LIVE-test than camera); "bidirectional" = two independent paths.
- **Path B (mic → Teacher):** `MicMonitorStart` → `AudioStreamer` → `StudentAudioStreamStart/
  Frame/Stop` (0x032B–D) + `MicStateUpdate`. **Path A (Teacher audio → Mac):** `AudioStreamStart/
  Frame/Stop` (0x0328–A) → native playback.
- **29-B** native AVAudioEngine mic tap + `AVAudioConverter` (48 k→16 k), own state, mic TCC
  bucket. **29-D** `AudioCaptureService` + Audio tab (RMS meter, no loopback) — **interactively
  confirmed** (mic dialog + meter moves on speech). **29-E** mic wire-in (Mic Monitor). **29-F**
  playback on a **separate** AVAudioEngine + `AVAudioPlayerNode` with a jitter buffer (3-frame
  ~300 ms prebuffer, overrun-drop-on-ingress at ~1 s, underrun = silent gap).
- **29-G** `MockTeacher --audiotest`: **both directions PASS** (B: 15/15 valid PCM, seq/endpoint/
  non-silent/lifecycle; A: consumed 12/12) + **T27** locks `AudioStreamFrameMessage` byte-compat
  (T1–T27). Bandwidth **measured** ~288 kbit/s wire.
- **LIVE (29-H, 2026-07-13): 3/4 paths confirmed.** ✅ Path B (Mic Monitor → Mac mic streamed,
  teacher has a real `WaveOut` sink) · ✅ Path A system audio → Mac plays, **no delay** (proves the
  whole playback chain) · ✅ manual Audio-tab doesn't stream (correct-by-design multi-trigger) · ❌
  Teacher-**mic** → Mac silent — **NOT a Mac defect:** the Teacher's Mic + Share-Computer-Audio emit
  the *identical* 0x0329 envelope, but the mic source's brittle fixed-format `WaveInEvent` emits zero
  frames if the Windows mic can't open at 16 k/16/16 → **Windows-track follow-up**, not Mac-port work.
  M20 complete as scoped. See `docs/PHASE-29-LIVE-CONFIRMATION.md`. Cheat sheet **§20** addendum.

### Bandwidth: all four media streams
| Stream | Sustained | |
|---|---|---|
| Screen H.264 (M18) | ~1.16 Mbit/s | 1080p |
| Camera JPEG (M19) | ~0.6 Mbit/s | 320×180 |
| **Audio PCM (M20)** | **~288 kbit/s wire** (~277 raw) | 16 k/mono/16-bit; ~4% MessagePack overhead |
| **3 concurrent / student** | **≈ ~2 Mbit/s** | Mic Monitor is 1-at-a-time — record for network sizing |

## Milestone 21 — Phase 30: Screen-lock enforcement (kiosk + dead-man) ✅ LIVE-CONFIRMED
Enforce the teacher lock (the envelope has arrived since M15 but was only *reflected*) — a HARD
lock that **exceeds** the soft Windows teacher-lock, with **zero Accessibility**. **Session 8.**
- **Investigation (30-A) reframe:** the shipped Windows teacher-lock is a **soft** topmost overlay
  (no hook, no `BlockInput`, primary-monitor-only, **printed** escape hotkey, **NO auto-unlock** —
  the dangerous gap). The hardened keyboard-hook kiosk is the *exam* feature only. → We build
  stronger AND safer. Product decisions: **HARD** lock · **no** student escape hatch · **45 s** silent
  disconnect-grace.
- **30-B** native shield — borderless `NSWindow` at `CGShieldingWindowLevel()` **per `NSScreen`** +
  hotplug + 7 kiosk `NSApplicationPresentationOptions` (raw **506** = Cmd+Tab/Force-Quit/logout/Dock/
  menu/Apple-menu/Cmd+H) + `didResignActive`/`didWake` re-assert. **No Accessibility.** **30-D** visual
  (glyph + live clock/date + brand; no printed hotkey).
- **30-C** `LockService` + wire-in + **four-layer dead-man:** ①process-kill (in-process → OS releases
  options = auto-unlock) ②45 s disconnect grace (continuous window; blip holds, teacher-death unlocks)
  ③30 min cap ④wake re-assert. Sole owner of `SelfTile.IsLocked`.
- **30-E** `MockTeacher --locktest`: **all 3 grace cases PASS** (blip HELD / teacher-death UNLOCKED /
  no-reset continuous window — the log shows exactly one `grace start`). Injectable shield backend →
  proves real dead-man logic with zero AppKit (no screen-takeover in tests).
- **LIVE (30-F, 2026-07-13):** teacher locks → **shield appears**, **Cmd+Tab/Force-Quit blocked**,
  explicit unlock → gone, cycle reliable, **machine recoverable (no stranding)**. Tester visual
  verification on borrowed **single-display** Mac; **multi-display untested on hardware** (code-correct,
  flagged). See `docs/PHASE-30-LIVE-CONFIRMATION.md`. Cheat sheet **§20** screen-lock addendum.
- **Residuals → Phase 31:** Spotlight-launch + Mission-Control (need `CGEventTap` + Accessibility).

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
| **macOS native APIs** | `nty-classroom-avalonia` / `avalonia-experiment` | **Milestones 16–20** — screen capture + **LIVE MJPEG & H.264 screen streaming (~10×)** + **LIVE camera peer-cam** + **bidirectional audio** (Mac-side; LIVE pending) to the shipped Windows Teacher |

### Combined day totals (Sessions 1–8, 2026-07-13)
- **21 milestones** (M1–M21; M20 LIVE 3/4, M21 LIVE-confirmed single-display); today added the first
  native-macOS-API work + camera + audio + screen-lock.
- **Native APIs: 6 subsystems** — ScreenCaptureKit capture · ImageIO JPEG · VideoToolbox H.264 ·
  **AVCaptureSession camera** · **AVAudioEngine audio** · **AppKit shield/kiosk lock** (no Accessibility).
- **Cheat sheet: 21 sections**, §20 with **four addenda** (VideoToolbox + AVCaptureSession +
  AVAudioEngine + screen-lock/kiosk).
- **MockTeacher: 6 automated modes** — `--selftest` / `--streamtest` / `--streamtest-h264` /
  `--cameratest` / `--audiotest` / **`--locktest`** (+ interactive `viewscreen`/`viewscreen-h264`).
  **Wire compat: T1–T27.**
- **Cross-platform demo: complete + expanded** — screen (MJPEG/H.264), camera, bidirectional audio,
  **and enforced screen-lock**, a macOS student into the shipped, unmodified Windows Teacher over unchanged wire.

## Tooling / docs state (macOS track)
- Cheat sheet: **21 sections** (§20 native interop + **VideoToolbox + AVCaptureSession +
  AVAudioEngine** addenda).
- HeadlessCapture: scenarios incl. `screencapture`, `streaming`.
- MockTeacher: `--selftest` + `--streamtest` + `--streamtest-h264` + `--cameratest` +
  **`--audiotest`** + interactive `viewscreen` / `viewscreen-h264`. **Wire compat: T1–T27.**
- Native: `native/NtyCapture/` (Swift dylib — ScreenCaptureKit + ImageIO + VideoToolbox +
  **AVFoundation/AVCaptureSession + AVAudioEngine**), `scripts/package-app.sh` (.app bundle,
  +`NSCameraUsageDescription` +`NSMicrophoneUsageDescription`).
- **Media: MJPEG + H.264** screen · **JPEG** camera (Conference peer-cam) · **raw PCM** audio
  (16 k/mono/16-bit, bidirectional).

## Milestones (macOS track)
| # | Phase | Outcome |
|---|---|---|
| 15 | 26.0 | LIVE wire interop (lock/policy/chat/hand) |
| 16 | 27-A | ScreenCaptureKit capture (native foundation) |
| 17 | 27-C | **LIVE** MJPEG screen streaming → Windows Teacher |
| 18 | 27-B | **LIVE** H.264 streaming (~10× bandwidth), **MJPEG + H.264** |
| 19 | 28 | **LIVE** camera peer-cam → Conference gallery (JPEG 320×180, ~16× vs preset bug) |
| 20 | 29 | **LIVE (3/4)** bidirectional audio (raw PCM 16 k) — mic talkback + system-audio playback; teacher-mic = Windows-side issue |
| **21** | **30** | **LIVE** screen-lock enforcement — HARD kiosk (shield + presentationOptions 506, no Accessibility) + four-layer dead-man (single-display validated; multi-display code-correct/untested) |

## Next-session priority queue
1. **Phase 31 — Input hooks** (`CGEventTap`, needs Accessibility) — closes the two macOS lock
   residuals (Spotlight-launch, Mission Control). **Recommended next.**
2. **Phase 32 — System integration** — permissions bundle, auto-start, packaging/signing.
3. **Phase 33 — Progressive UI ports** + runtime light/dark theme swap.
4. **Ship v1.2.1 installer** (Windows track, ~1 h) — customer commitment.
5. Windows-track follow-up: Teacher mic `WaveInEvent` robustness (M20 gap).
6. Path C breakout peer voice (TargetGroupId + PTT + AEC) — deferred audio scope.

## Team handoff
- **Cross-platform demo circle COMPLETE + optimized + EXPANDED:** M15 wire compat · M17 MJPEG
  LIVE · **M18 H.264 LIVE (~10×)** · **M19 camera LIVE**. A macOS student streams its **screen
  (production-grade H.264) AND its camera (Conference peer-cam)** into the shipped, unmodified
  Windows Teacher over unchanged wire.
- **Native-APIs progression (6/9 subsystems):** ✓ 27-A ScreenCaptureKit · ✓ 27-C JPEG ·
  ✓ 27-B H.264 · ✓ 28 AVCaptureSession camera · ✓ 29 AVAudioEngine audio (LIVE 3/4; teacher-mic =
  Windows follow-up) · ✓ 30 AppKit shield/kiosk lock (**LIVE**; single-display) · ⏳ 31 input hooks
  (CGEventTap) · ⏳ 32 system integration · ⏳ 33 UI ports.
- **Windows-track follow-up (logged, not Mac-port work):** the shipped Teacher's own-mic broadcast
  uses a brittle fixed-format `WaveInEvent` (16 k/16/1) that emits no 0x0329 frames if the mic can't
  open at that exact format — verify the test-box mic; a v1.2.x patch could make it format-robust
  like the loopback path. The Mac plays any 0x0329 that arrives (proven via system audio).
- Native-interop template (**§20**, incl. VideoToolbox + AVCaptureSession + AVAudioEngine +
  shield/kiosk) proven **six times**; `MockTeacher --*test` is the reuse + de-risk pattern for every
  remaining subsystem — it caught the camera preset bug (M19), two audio-harness bugs (M20), and the
  console-main-thread shield hang (M21) headless before LIVE.
- Both tracks green, synced with origin, independently documented.
