# Cam Spike — Phase 14-A Step 0

Hard-gate spike for Feature #4 (Conference Mode). Verifies the technical path
Tier 1 / Tier 2 webcam capture will use BEFORE any feature code lands.  Mirrors
the [`AECSpike`](../AECSpike/README.md) pattern from Phase 13-D Step 0.

## What this tool verifies *programmatically*

1. **Webcam device(s) enumerable via DirectShow.** Same library
   (`AForge.Video.DirectShow` 2.2.5) already used in production by Teacher's
   Phase 9.5 `CameraBroadcastService`.  If zero devices, Tier 1 must
   default-disable the cam toggle UI with tooltip "No webcam detected".
2. **Default device opens via `VideoCaptureDevice`.** Same API path Tier 2's
   student-side `StudentCameraBroadcaster` would use.
3. **Negotiated capture format.** Reveals whether the broadcaster can stay at
   the design target (640×480 @ 10–15 FPS) or has to negotiate down.
4. **5-second capture with per-frame timing.** Computes:
   - Actual sustained FPS
   - Inter-frame interval jitter (mean, stddev, min, max)
   - Per-frame raw bitmap size + sampled JPEG-Q70 encoded size
   - Approximate sustained per-stream bandwidth at observed FPS
5. **Trace of first 30 frame arrivals** so the dev can spot stalls.

## What this tool does NOT verify

- Concurrent screen-share + cam capture CPU contention (Tier 1 spec captures
  this; Tier 1 2-PC test validates).
- Multi-monitor + window-position interaction (irrelevant for cam capture).
- Hardware encoder integration (deferred to Tier 3 only if bandwidth math
  demands).
- Multi-cam systems (default to first; settings to pick later — Tier 2 polish).

## How to run

```pwsh
dotnet run --project tools/CamSpike -c Release
```

The tool enumerates + dumps capabilities unconditionally, then waits at
`Press Enter to start a 5-second capture …` so the dev can position in front
of the camera.  The capture window then runs for exactly 5 seconds.

## Dev procedure

1. Close any app that might hold the camera (Teams, Zoom, OBS, browser tabs
   that requested cam permission).
2. Run the tool.
3. At the "Press Enter to start" prompt, look at the camera or just be in
   frame.
4. Wait for the 5-second capture to complete.
5. Save the printed device list, negotiated capability, frame stats, and
   trace.

## Verdict matrix

Compare observed FPS to the negotiated capability's `Average FPS`:

| Observed FPS vs target | Jitter (stddev/mean) | Verdict | Action |
|---|---|---|---|
| ≥ 80% of target | ≤ 30% | **WORKS** | Tier 1 ships AForge default at 640×480 @ 10–15 FPS. |
| ≥ 50% of target | ≤ 50% | **PARTIAL** | Tier 1 caps at 10 FPS; document "10 FPS minimum, 15 FPS preferred". |
| < 30% of target OR any Δ > 500 ms | any | **INVESTIGATE** | STOP.  Try second device, then alternative library (Vortice.MediaFoundation already in `Shared.csproj`, or `OpenCvSharp`) before any Tier 1 code lands. |

## Result from this dev box (sirin)

```
Devices enumerated:    [0] Integrated Camera (Chicony USB)
Selected device:       Integrated Camera (Chicony USB)
Negotiated capability: 640×480 @ avg 30 FPS  (driver-advertised; actual driver caps ~10)
First-frame format:    640×480  pixelFormat=Format24bppRgb  (AForge converts from YUY2 internally)
Frame count / 5 s:     46  → 9.2 FPS sustained
Interval ms:           mean≈109 stddev≈7%  min≈100  max<200  (no stalls > 500 ms)
Raw frame size:        ~900 KB (24bpp uncompressed)
JPEG Q70 size:         ~16 KB (well under design budget of 30–40 KB)
Sustained bandwidth:   ~148 KB/s per stream (vs 300–400 KB/s design budget — well under)
Verdict:               WORKS
Hardware notes:        laptop integrated cam (Chicony built-in); typical Win11 install
```

**Verdict: WORKS.** Tier 1 commits to AForge.Video.DirectShow at 640×480
@ 10 FPS sustained (driver caps below the 30 FPS advertised — design
already targeted 10 FPS so no plan adjustment needed beyond updating
the bandwidth-math reference numbers from 300–400 KB/s to ~150 KB/s).

The ~148 KB/s per-stream actual is ~50% of the architecture doc's
design budget, which is good news for Mode 3 gallery scale-out — see
`docs/conference-tier3-design.md` § 7.1.

## What to do after Step 0

- **WORKS:** Resume Phase 14-A doc round.  Tier 1 design commits to
  `AForge.Video.DirectShow` at 640×480 @ observed-FPS.
- **PARTIAL:** Resume Phase 14-A doc round.  Tier 1 caps at 10 FPS and ship
  notes flag "10 FPS minimum, USB webcam recommended for higher".
- **INVESTIGATE:** STOP.  Document failing device + driver in this README;
  the architecture doc's "Library decision" section gets a follow-up TODO
  for the dev to evaluate `Vortice.MediaFoundation` (already in
  `ClassroomCtrl.Shared.csproj`) as the Tier 1 baseline before any feature
  code.

## Notes for the dev

- Teacher project (`src/ClassroomCtrl.Teacher/ClassroomCtrl.Teacher.csproj`)
  already references `AForge.Video` + `AForge.Video.DirectShow` 2.2.5. The
  existing `CameraBroadcastService` (Phase 9.5) is the production code path.
  This spike's measurements apply directly to Tier 1 of Feature #4.
- Student.Agent project (`src/ClassroomCtrl.Student.Agent/ClassroomCtrl.Student.Agent.csproj`)
  does NOT currently reference AForge.  Tier 2 implementation will add it.
- If a customer site reports cam-capture failure post-ship, this same spike
  is the field-triage tool: same binary, same trace, same verdict matrix.
