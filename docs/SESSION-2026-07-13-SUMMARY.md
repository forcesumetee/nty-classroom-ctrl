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

## Milestone 22 — Phase 31: Input-hook keystroke guard (CGEventTap) ✅ LIVE-CONFIRMED
Close the two M21 residuals (Spotlight-launch, Mission Control) with a `CGEventTap` keystroke guard —
**additive to the lock, needs Accessibility, must FAIL OPEN.** **Session 9.**
- **Investigation (31-A) safety framing:** a tap's blast radius exceeds the shield (a wedged tap
  could freeze the keyboard machine-wide), but macOS makes that **unreachable** — a slow callback is
  **auto-disabled by the OS** (`kCGEventTapDisabledByTimeout`) with keys already flowing; process-kill
  releases it. Smaller blast radius than the Windows `WH_KEYBOARD_LL` exam-hook. Product decisions:
  **thorough** suppression list · **Cmd+Q unblocked** (quit = dead-man unlock + escape hatch) ·
  **graceful degrade** if Accessibility denied.
- **31-B** native `Sources/Input.swift` — `CGEventTap` (`.cgSessionEventTap`/`.headInsertEventTap`/
  `.defaultTap`, keyDown+flagsChanged) on a **dedicated `CFRunLoop` thread**; exact-modifier
  suppression (Spotlight/Mission-Control/Spaces/Cmd+Tab/Cmd+`), passes Cmd+Q/media/screenshots;
  **FAILS OPEN** (re-enable on OS auto-disable); `AXIsProcessTrusted` check/request; bounded
  guaranteed-uninstall. `--inputtest` **DEMONSTRATED fail-open via the real OS watchdog, 4/4 runs**;
  process-kill release proven (`--inputhold` + `kill -9`).
- **31-C** `LockService` **injectable input-guard backend** (same seam as the 30-E shield backend):
  install on shield-up **iff Accessibility-trusted** (else prompt once + degrade), remove on **every**
  unlock path (explicit / grace / cap). Guard tied to lock state; never guard without shield.
- **31-D** `--locktest` extension — guard lifecycle **21/21** (CASE A–E): installed-on-lock, HELD
  through a blip, **RELEASED on all three dead-man paths** + graceful-degrade. **Flag backend → ZERO
  real taps** in automated testing.
- **LIVE (31-E, 2026-07-13):** borrowed Mac, all four groups PASS — **suppress** (Spotlight + Mission
  Control blocked under lock; reopen on unlock) · **graceful degrade** (deny → lock still works) ·
  **kill-release** · **grace-release**. See `docs/PHASE-31-LIVE-CONFIRMATION.md`. Cheat sheet **§20**
  CGEventTap addendum. **The two M21 residuals are now CLOSED.**

## Milestone 23 — Phase 32: System integration (shippable Student) ✅ LIVE-CONFIRMED · STUDENT TRACK COMPLETE
Turn the M15–M22 subsystems into a **shippable background app**. **Session 10.** LIVE-confirmed in
**bundle form** on macOS 26.5.2.
- **Investigation (32-A) reframe:** the Windows Service/Agent/Watchdog split is a Session-0 artifact
  (LocalSystem crash-looped WPF; the Watchdog respawns the Service only). Product calls: **single
  user-session LaunchAgent** (macOS forces it — the window server is needed for every job; no
  privileged ops yet); **`KeepAlive=false` + `RunAtLoad=true`** (matches the Windows Agent + preserves
  the M22 Cmd+Q escape); **config = a JSON file** under `~/Library/Application Support`
  (admin-pre-seedable, mirrors the Windows file model).
- **32-B** `StudentConfig` (config.json; default display name = host name) · **32-C** menubar
  `TrayIcon`→`NSStatusItem` (drawn icons; Show Debug Window + Start at Login + Quit; menubar-app
  lifecycle, Quit = dead-man unlock) · **32-D** 4-permission onboarding (Screen = Required+relaunch;
  Camera/Mic/Accessibility = Optional; reuses M16/M19/M20/M22 native checks) · **32-E** LaunchAgent
  (self-targets `ProcessPath`, gated on `IsBundled`; `bootstrap`/`bootout`; disable = no-trace) ·
  **32-F** `.app` bundle (stable id `com.nty.classroomctrl.student`, `LSUIElement`; dylib in
  `Contents/MacOS/` resolves with no rpath surgery — runtime-verified).
- **Structural:** `--configtest` 14/14 · `--traytest` 10/10 · `--permtest` 13/13 ·
  `--launchagenttest` 16/16 (installs ZERO real agents; disable→no-trace; dev-path refused).
- **LIVE (32-G):** bundle form — no Dock icon; connects; locks (dylib resolves in-bundle);
  LaunchAgent enable (586 B plist) + disable (**no trace**); clean Quit (no relaunch loop). **Bug
  fixed: macOS Local Network Privacy** blocked the bundle's local-subnet connect — root cause was the
  missing **`NSLocalNetworkUsageDescription`** (NOT entitlements; the bundle isn't sandboxed), fixed
  with the Info.plist key + a package-app.sh guard. Saved as a memory (recurs in the Teacher track).
  See `docs/PHASE-32-LIVE-CONFIRMATION.md`.
- **STUDENT TRACK COMPLETE:** scenario 2 (shipped Windows Teacher + Mac Student) is shippable,
  pending only **P35** Developer ID signing for wide deployment.

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

### Combined day totals (Sessions 1–10, 2026-07-13)
- **23 milestones** (M1–M23; M20 LIVE 3/4, M21 LIVE single-display, M22 LIVE, **M23 LIVE — bundle
  form**). **STUDENT TRACK COMPLETE.**
- **Native APIs: 8 subsystems** — ScreenCaptureKit capture · ImageIO JPEG · VideoToolbox H.264 ·
  **AVCaptureSession camera** · **AVAudioEngine audio** · **AppKit shield/kiosk lock** (no Accessibility) ·
  **CGEventTap input guard** (fails open) · **system integration** (LaunchAgent / TCC onboarding /
  config.json / `.app` bundle).
- **Cheat sheet: 21 sections**, §20 with **five addenda** (VideoToolbox + AVCaptureSession +
  AVAudioEngine + screen-lock/kiosk + CGEventTap).
- **MockTeacher: 11 automated modes** — `--selftest` / `--streamtest` / `--streamtest-h264` /
  `--cameratest` / `--audiotest` / `--locktest` / `--inputtest` (+ `--inputhold`) / **`--configtest`**
  / **`--traytest`** / **`--permtest`** / **`--launchagenttest`**. **Wire compat: T1–T27.**
- **Cross-platform demo: COMPLETE** — a macOS student, **as a shippable menubar background app in
  bundle form** (auto-start, config, onboarding), streams screen (MJPEG/H.264) + camera + bidirectional
  audio, plays teacher audio, and obeys an enforced kiosk lock with keystroke suppression — into the
  shipped, unmodified Windows Teacher over unchanged wire.

## Tooling / docs state (macOS track)
- Cheat sheet: **21 sections** (§20 native interop + **VideoToolbox + AVCaptureSession +
  AVAudioEngine + screen-lock/kiosk + CGEventTap** addenda).
- HeadlessCapture: scenarios incl. `screencapture`, `streaming`.
- MockTeacher: `--selftest` + `--streamtest` + `--streamtest-h264` + `--cameratest` +
  `--audiotest` + `--locktest` + **`--inputtest`** (+ `--inputhold`) + interactive `viewscreen` /
  `viewscreen-h264`. **Wire compat: T1–T27.**
- Native: `native/NtyCapture/` (Swift dylib — ScreenCaptureKit + ImageIO + VideoToolbox +
  AVFoundation/AVCaptureSession + AVAudioEngine + **AppKit shield + CGEventTap/ApplicationServices**),
  `scripts/package-app.sh` (.app bundle, +`NSCameraUsageDescription` +`NSMicrophoneUsageDescription`).
- **Media: MJPEG + H.264** screen · **JPEG** camera (Conference peer-cam) · **raw PCM** audio
  (16 k/mono/16-bit, bidirectional). **Enforcement: kiosk shield + CGEventTap keystroke guard.**

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
| **22** | **31** | **LIVE** input-hook keystroke guard — `CGEventTap` closes the two M21 residuals (Spotlight, Mission Control); fails open (OS watchdog), Accessibility graceful-degrade, guard released on all dead-man paths |
| **23** | **32** | **LIVE (bundle form)** system integration — shippable menubar Student: config.json · TrayIcon/NSStatusItem · 4-perm onboarding · LaunchAgent (KeepAlive=false, no-trace uninstall) · `.app` bundle (stable id, LSUIElement, in-bundle dylib). Fixed macOS Local Network Privacy. **STUDENT TRACK COMPLETE** |

## Next-session priority queue
1. **Teacher track (macOS Teacher, Avalonia)** — roadmap **TT-0…TT-13** in
   `docs/TEACHER-TRACK-ROADMAP.md`. **TT-0 (MockStudent) + TT-1 (Teacher.Core: transport + router +
   roster) + TT-2 (windowed Teacher — live student grid) + TT-3 (per-student live screen view, MJPEG)
   + TT-4 (H.264 screen DECODE, VTDecompressionSession) COMPLETE + LIVE-confirmed 2026-07-14.**
   Scenario 3 (Mac T + Win S) spans the whole chain: a shipped, **unmodified Windows Student joins the
   roster (TT-1), appears as a live TILE (TT-2), and its screen renders live — MJPEG (TT-3) AND H.264
   (TT-4, OpenH264→VideoToolbox interop)** — plus the **network-cut → 15 s stale-sweep → tile-gone**
   path, zero changes to the shipped product. Scenario 4 (Mac T + **Mac** S) also proven: a Mac Student
   streams **our M18 VideoToolbox H.264** → decoded on the Mac Teacher (customer B's path). See
   `docs/TT-1-*` … `docs/TT-4-*`. Headless gates: `MockStudent --teacherselftest` **20/20** + the
   Avalonia **Skia**-headless UI suites (TT-3-B/C 36/36; **TT4CGate 18/18** — VT + a committed real
   OpenH264 (BELL) fixture both decode to exact dims; C-ABI ×20 no leak). The roster namespace-gap bug
   (both customers exposed) is fixed in the port + logged for the Windows team.

   **🔴 THE SCALE GATE IS CLOSED (not deferred).** Traced from shipped code + confirmed with sales:
   student screen streams are **ON-DEMAND** (targeted `StudentStreamStart`, one per open screen-view
   window; tiles never stream — thumbnails only from a manual screenshot), so the Teacher decodes **1–4
   concurrent** streams, never 40 — `VTDecompressionSession ×40` was never the real shape. Customer B
   expects the shipped one-at-a-time behavior, **not** a live thumbnail wall. So the roadmap's
   downscale/decode-on-demand mitigation is **dropped**, and **TT-4 collapses to "add one decoder to a
   working pipeline."** The render seam is already in place (codec-dispatch `RenderFrame`; H.264 is a
   clean stub; type verified — `WriteableBitmap : Bitmap`). A live thumbnail wall, if ever wanted, is a
   NEW feature that reintroduces scale — a scoped request, not a bug. See `docs/TT-3-FINDINGS.md`.

   **🟢 THE BITSTREAM RISK IS RETIRED (TT-4).** Every student — Windows (OpenH264/MediaFoundation) and
   Mac (our M18 VideoToolbox) — puts the SAME shape on the wire: Annex-B, Baseline (profile_idc=66,
   confirmed in BELL's SPS), in-band SPS/PPS per IDR, fixed 1920×1080. One decode path handles all
   sources — proven headless (the BELL fixture) AND LIVE (both encoders). Our M18 encoder was built to
   byte-match shipped OpenH264, so its AVCC→Annex-B is the exact inverse reference for the decoder.
   H.264 decode is the **first multi-instance (handle-based) native subsystem** (1–4 concurrent views);
   the §20 background→UI marshal is now proven **9×**. An undecodable H.264 keyframe → a visible MJPEG
   fallback (Stop + Request(Mjpeg) + status), not a dead window.

   **The Mac Teacher now:** server + roster + live grid + **live screen view (MJPEG + H.264, from
   Windows & Mac students)**. Both biggest Teacher-track risks — scale (TT-3) and the bitstream
   (TT-4) — are retired. **Next: TT-5** (core commands — lock/unlock, policy, power: the full-circle
   interop where a macOS Teacher sends the exact messages the macOS Student already receives). **The
   critical path for customer B (Mac teacher + Mac students, 50 seats).**
2. **Phase 35 — Distribution** — Developer ID codesign + notarization (stops the ad-hoc-rebuild TCC
   re-prompt; a *relaunch* of the same built bundle already keeps grants) + `.pkg`/`.dmg` installer +
   self-contained runtime bundling (for .NET-less lab Macs). Makes the Student track deployable at scale.
3. **Phase 33 — Progressive UI ports** + runtime light/dark theme swap.
4. **Ship v1.2.1 installer** (Windows track, ~1 h) — customer commitment.
5. Windows-track follow-up: Teacher mic `WaveInEvent` robustness (M20 gap).
6. **Windows-track follow-up: roster namespace-gap bug** (peerId vs EndpointId) — found TT-1-A, fixed in the macOS port (TT-1-D); candidate v1.2.x patch. Details below.
7. Path C breakout peer voice (TargetGroupId + PTT + AEC) — deferred audio scope.

## Team handoff
- **Cross-platform demo circle COMPLETE:** M15 wire · M17 MJPEG · **M18 H.264 (~10×)** · M19 camera ·
  M20 audio (3/4) · M21 lock · M22 input-guard · **M23 system integration** — all LIVE. A macOS
  student, **as a shippable menubar background app in bundle form** (auto-start, config, onboarding),
  streams screen (H.264) + camera + bidirectional audio into the shipped, unmodified Windows Teacher
  and is enforced-locked with keystroke suppression — all over unchanged wire.
- **Native-APIs progression (8/9 subsystems):** ✓ 27-A ScreenCaptureKit · ✓ 27-C JPEG ·
  ✓ 27-B H.264 · ✓ 28 AVCaptureSession camera · ✓ 29 AVAudioEngine audio (LIVE 3/4; teacher-mic =
  Windows follow-up) · ✓ 30 AppKit shield/kiosk lock (**LIVE**; single-display) · ✓ 31 CGEventTap
  input guard (**LIVE**; fails open) · ✓ **32 system integration** (**LIVE**; bundle form) · ⏳ 33 UI ports.
- **Student track COMPLETE:** scenario 2 (shipped Windows Teacher + macOS Student) is shippable — a
  real menubar background app (auto-start, config, onboarding) in bundle form, pending only **P35**
  Developer ID signing for wide deployment. The **Teacher track** is next — roadmap
  `docs/TEACHER-TRACK-ROADMAP.md` (TT-0…TT-13, MockStudent-first); it's the critical path for a
  Mac-teacher + Mac-students deployment (customer B).
- **Windows-track follow-up (logged, not Mac-port work):** the shipped Teacher's own-mic broadcast
  uses a brittle fixed-format `WaveInEvent` (16 k/16/1) that emits no 0x0329 frames if the mic can't
  open at that exact format — verify the test-box mic; a v1.2.x patch could make it format-robust
  like the loopback path. The Mac plays any 0x0329 that arrives (proven via system audio).
- **Windows-track follow-up (logged, not Mac-port work) — ROSTER NAMESPACE-GAP** (found TT-1-A, fixed in the macOS port TT-1-D):
  - Transport keys peers by **peerId** (fresh Guid at TCP-accept); the app keys students by **EndpointId** (= `Envelope.SenderId`). Never bridged.
  - `MainViewModel.OnStudentLeft`: `FirstOrDefault(x => x.EndpointId == peerId)` never matches → falls back to `RemoveAt(Count-1)` → removes the **LAST** student, not the departed one.
  - `TcpControlServer.cs:187` calls the proper map a "Tier-3 follow-up" — known, unfixed.
  - Also silently no-ops the EndpointId-keyed cleanups (Conference cam senders, share permissions, tile selection).
  - **Invisible at 1–2 seats. At 50 (both customers), a mid-class disconnect greys out the WRONG student.**
  - **Fix:** bridge peerId ↔ EndpointId at the transport→app boundary (adopt the Hello's EndpointId per peer, report EndpointId on disconnect, guard so a stale socket doesn't evict a reconnected owner). Teacher-internal C#, zero wire change.
  - Candidate for a **v1.2.x Windows patch**. We do NOT touch the shipped repo — this is a report to the team.
- Native-interop template (**§20**, incl. VideoToolbox + AVCaptureSession + AVAudioEngine +
  shield/kiosk + **CGEventTap**) proven **seven times** (M23 added no native dylib — it's byte-unchanged);
  `MockTeacher --*test` is the reuse + de-risk pattern — it caught the camera preset bug (M19), two
  audio-harness bugs (M20), the console-main-thread shield hang (M21), and the cross-thread tap-enable /
  fail-open bug (M22) headless before LIVE. M23 added `--configtest`/`--traytest`/`--permtest`/
  `--launchagenttest` (the last proving ZERO-real-agent LaunchAgent safety); the macOS **Local Network
  Privacy** bug (M23) was found LIVE via A/B and saved as a memory (recurs in the Teacher track).
- Both tracks green, synced with origin, independently documented.
