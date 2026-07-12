# Session Summary — 2026-07-13 (macOS track: Phase 27 native APIs)

Continuation of the macOS/Avalonia port. Session 1 (2026-07-12) reached **Milestone 15**
(LIVE cross-platform interop) — see `SESSION-2026-07-12-SUMMARY.md`. This day added the
**first native-macOS-API work** (Phase 27) across two focused sessions:
- **Session 3 — Phase 27-A:** ScreenCaptureKit basic capture (**Milestone 16**).
- **Session 4 — Phase 27-C:** wire captured frames to the Windows Teacher (**Milestone 17,
  LIVE-CONFIRMED**).

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

## Commits (Sessions 3–4) — 10
```
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
| `MockTeacher --streamtest` | ✅ 8/8 valid JPEG frames |
| `--selftest` (regression) | ✅ PASS |
| Shipped Windows repo | ✅ untouched |
| LIVE Windows *View Screen* | ✅ Mac desktop displayed |

## Combined day (2026-07-13) — both tracks
| Track | Repo / branch | Outcome |
|---|---|---|
| **Windows patch** | `nty-classroom-macos` / `v1.2-multiselect` | **v1.2.1** bulk-lock fix SHIP-READY (tag `v1.2.1`) |
| **macOS native APIs** | `nty-classroom-avalonia` / `avalonia-experiment` | **Milestones 16–17** — screen capture + **LIVE screen streaming to the Windows Teacher** |

## Tooling / docs state (macOS track)
- Cheat sheet: **21 sections** (§20 native interop added).
- HeadlessCapture: **13 scenarios** (+ `screencapture`, `streaming`).
- MockTeacher: `--selftest` + **`--streamtest`** + interactive `viewscreen`.
- New: `native/NtyCapture/` (Swift dylib), `scripts/package-app.sh` (.app bundle).
- Sandbox: **13 tabs** (+ Screen Capture).

## Next-session priority queue
1. **Phase 27-B — VideoToolbox H.264 encode** (~3–5 h, Mac): ~10× bandwidth cut vs MJPEG; the
   Teacher already decodes H.264 (`RenderH264`). Extends the §20 template + the existing
   `StudentStreamFrame` path (Codec=H264). Highest-leverage next.
2. **Phase 27-D — remaining native subsystems** (camera / audio / lock-screen enforcement /
   input hooks) on the proven Swift-dylib template.
3. **Ship v1.2.1 installer** (Windows track, ~1 h) — customer commitment.
4. Progressive UI ports + runtime light/dark theme swap.

## Team handoff
- **Cross-platform demo circle is COMPLETE** — a macOS student's screen streams live into the
  shipped Windows Teacher, over unchanged wire.
- Native-interop template proven twice (capture + JPEG stream); §20 + `MockTeacher --streamtest`
  are the reuse + de-risk patterns for every remaining subsystem.
- Both tracks green, synced with origin, independently documented.
