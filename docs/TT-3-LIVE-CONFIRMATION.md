# TT-3 LIVE Confirmation — a real Windows Student's screen renders live in the Mac Teacher

**Date:** 2026-07-14 · **Milestone:** TT-3 (per-student live screen view — MJPEG) · **Gate:** *double-click
a real student's tile → their screen appears live in a Mac Teacher window; close → the stream stops.*

## Result: PASS
Double-clicked **BELL**'s tile in the Mac Teacher grid → a `ScreenViewWindow` opened → **BELL's live
screen appeared (MJPEG)** → everything worked; closing the window stopped the stream.

- ✅ Double-tap the tile → the screen-view window opens (title = the student's name).
- ✅ Within ~1 s → **BELL's actual desktop renders live** (MJPEG), the status strip reads "Live — W×H".
- ✅ Close the window → `StopStudentStreamAsync` → BELL stops encoding/sending (its "👁 Teacher is
  viewing your screen" notice clears).

## Why this is the strong proof — it closes caveat 1 from TT-3-B
The TT-3-B headless gate proved the marshal by raising a frame from a `Task.Run` **fake**. This LIVE
run proves the **real** path end to end:
- frames arrive on the **real transport background read loop** (not a fake thread),
- are **decoded** (MJPEG → `new Bitmap`) off the UI thread,
- and the `Image.Source` swap is **marshaled to the UI thread** (`Dispatcher.UIThread.Post`) — the
  render loop reads `CurrentFrame` concurrently, so an off-thread mutation would have raced it here.

An un-marshaled build would race the render loop **right here**, at frame rate, exactly while a frame
is being drawn. It didn't. The render path is proven with a **shipped, unmodified Windows Student**.

## Scenario 3 now spans the whole chain
Mac Teacher + Windows Student, against the shipped Windows product with **zero changes to it**:
**roster (TT-1) → live grid (TT-2) → live screen view (TT-3).**

## Codec — confirmed by the LIVE run and by the shipped code
The Teacher requests **MJPEG**; the shipped `Student.Agent` **honors the requested codec** (defaults
to MJPEG, uses `req.Codec` from `StudentStreamStartRequest`), so BELL streamed MJPEG — renderable with
no H.264 decoder. The **H.264 path is TT-4's** LIVE gate, not this one.

## Environment
- **Teacher:** MacBook Air (Apple Silicon), `Avalonia.Teacher` via
  `dotnet run --project src/ClassroomCtrl.Avalonia.Teacher` → `TeacherSession` hosting the real
  `Teacher.Core` on `0.0.0.0:7777`.
- **Student:** a real Windows PC ("BELL") running the shipped v1.2 product, unmodified, pointed at the
  Mac's LAN IP.
- **LNP:** passed — `dotnet run` inherits Terminal's local-network grant (no prompt). The bundled
  Teacher (TT-13) will still need `NSLocalNetworkUsageDescription`.

## Headless companion (committed-pattern gate)
- Skia-enabled headless suite **36/36** (`TT3BSmoke`): lifecycle, studentId filter + real 64×48/100×80
  decode, background→UI marshal thread-assertion, window Opened/Closed, controller
  dedupe/disconnect-close/CloseAll, and the codec dispatch (MJPEG + H.264 stub).
- `MockStudent --teacherselftest` **20/20** (roster/liveness) + **T1–T27** wire compat, all still PASS.

## Pattern consistency
Every milestone M15–M23 and TT-1/TT-2 had a LIVE gate that caught what headless tests couldn't. TT-3
holds it: the headless suite proves the wiring + the marshal + the codec seam; **this LIVE run proves
the render reflects a real shipped client over the real transport read loop.** The Mac Teacher can now
*see* a student's screen.

## Known gaps (not blockers for TT-3)
- **H.264 decode** — TT-4 (now a small additive phase; the scale gate is closed — see
  `docs/TT-3-FINDINGS.md`).
- Disconnect closes the window (rather than a "student disconnected" freeze) — no-leak over
  last-frame; revisitable.
- Remote control / per-student recording / screenshot / quality reporting — the deferred remainder of
  the shipped `StudentScreenWindow`.
