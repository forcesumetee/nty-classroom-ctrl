# TT-4 LIVE Confirmation — H.264 screen decode, both sources render live on the Mac Teacher

**Date:** 2026-07-14 · **Milestone:** TT-4 (H.264 student-screen decode, VTDecompressionSession) ·
**Gate:** *double-click a student streaming H.264 → their screen renders live on the Mac Teacher.*

## Result: PASS — both sub-gates

### ✅ Sub-gate 1 — Mac Student → Mac Teacher (scenario 4, customer B's path)
A real **Mac Student** (the Sandbox app) streaming **our M18 VideoToolbox H.264** rendered live in
the Mac Teacher. Double-click the tile → the window opens → the student's screen appears in H.264 →
close → the stream stops. This is customer B's exact artifact end to end: Mac student app → real
M18 encode → real wire → Mac Teacher **decode** → render.

> **Run detail (worth recording):** sub-gate 1 could **not** run the Sandbox under `dotnet run` —
> the Screen Recording grant is bound to Terminal and TCC caches at process launch, so the
> restart-caveat kept firing even after restarting Terminal. Running the Sandbox **as the bundle**
> ("NTY ClassroomCtrl.app") worked immediately — its TCC grant persists from 32-G. Same
> ad-hoc-signature friction M22/32-F recorded; **P35 Developer ID signing eliminates it.** Not a
> TT-4 defect — a distribution gap. The Teacher ran fine under `dotnet run` (it needs no Screen
> Recording — it decodes, it doesn't capture).

### ✅ Sub-gate 2 — BELL (shipped Windows Student) → Mac Teacher (scenario 3, OpenH264→VT interop)
A real, shipped, **unmodified Windows Student** ("BELL", v1.2, OpenH264) streaming H.264 rendered
live in the Mac Teacher. No issues. **This closes the interop that was the phase's stated risk:
OpenH264 (Windows) → VideoToolbox (Mac).**

## The interop is now proven TWICE
- **Headless + regression-protected:** the committed BELL fixture (`TT4CGate` sub-gate B) — real
  OpenH264 Annex-B → VideoToolbox → exact 1920×1080 WriteableBitmap.
- **LIVE:** this run, a real Windows Student over the LAN.

So VideoToolbox decodes **both** sources: our M18 VideoToolbox encoder (Mac students) **and** the
shipped OpenH264/MediaFoundation encoder (Windows students) — one decode path, verified both ways.

## Environment
- **Teacher:** MacBook Air (Apple Silicon), `Avalonia.Teacher` via `dotnet run` → `TeacherSession`
  hosting `Teacher.Core` on `0.0.0.0:7777`; H.264 decode via `libNtyCapture.dylib`
  (VTDecompressionSession). No Screen Recording needed (decode-only).
- **Sub-gate 1 student:** the Mac Student **bundle** ("NTY ClassroomCtrl.app"), pointed at
  `127.0.0.1`, streaming M18 VideoToolbox H.264.
- **Sub-gate 2 student:** a real Windows PC ("BELL") running shipped v1.2, unmodified, pointed at
  the Mac's LAN IP, streaming OpenH264 H.264.
- **LNP:** `dotnet run` inherited Terminal's local-network grant (no prompt). The bundled Teacher
  (TT-13) will still need `NSLocalNetworkUsageDescription`.

## Headless companions (committed / committed-fixture gates)
- `TT4CGate` **18/18** (Skia-headless): VT + OpenH264 (BELL fixture) decode to exact dims; delta
  waits; malformed keeps last frame; MJPEG fallback; C-ABI ×20 no leak; disposed on Stop.
- TT-4-B native round-trip **13/13**; TT3BSmoke (MJPEG path) green; `--teacherselftest` 20/20;
  **T1-T27** PASS.

## Pattern consistency
Every milestone M15–M23 and TT-1…TT-3 had a LIVE gate that caught what headless tests couldn't.
TT-4 holds it: the headless gate proves the decode pipeline + the C-ABI glue + the interop (via the
committed fixture); **this LIVE run proves both real encoders render over the real transport** — and
surfaced the `dotnet run` TCC friction that only shows up on real hardware. The Mac Teacher can now
*see a student's screen in H.264*, from Windows and Mac students alike.

## Known gaps (not blockers for TT-4)
- The 3-byte start-code parse is proven synthetically only (BELL's build emits 4-byte) — see
  `docs/TT-4-FINDINGS.md`.
- Screen-Recording-under-`dotnet run` friction for the Mac *Student* → P35 (Developer ID signing).
- Remote control / per-student recording / screenshot / quality reporting — the deferred remainder
  of the shipped StudentScreenWindow.
