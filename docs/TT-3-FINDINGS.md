# TT-3 Findings — per-student live screen view (MJPEG) · COMPLETE (LIVE-confirmed; targeting corrected TT-6-D)

> ⚠️ **CORRECTION (2026-07-14, found TT-6-D):** the per-student **screen-stream request was
> latently mis-targeted** during this phase. `RequestStudentStream(A)` is `CreateTargeted` and
> broadcast to all peers, but the Mac Student's receive-side `IsForMe` filter was missing, so
> **every** connected Mac student would have started streaming, not just the requested one (a
> silent screen-capture privacy issue). This phase's LIVE pass was **valid for what it tested** —
> MJPEG capture/encode/wire/decode/render of the requested screen, with **one** Mac student
> connected — but "only the requested student streams" was **unverified**. Fixed by the TT-6-D
> receive-side filter (`StudentEnvelopeFilter.IsForMe`). See `docs/TT-6-FINDINGS.md`.

**Goal:** the Mac Teacher opens a **live screen view of one student**, driven by the already-ported
frame flow (TT-1-C). **LIVE gate met: a real, shipped, unmodified Windows Student's screen rendered
live in the Mac Teacher (MJPEG) — double-click the tile → open → live frames → close → stream stops.**
Constraints held: `src/ClassroomCtrl.Avalonia.Teacher` only, **Sandbox untouched**, Shared.Wire
unchanged, Teacher.Core unchanged, **shipped Windows repo untouched**.

Split: **TT-3-B** = the screen-view window + VM + controller (MJPEG); **TT-3-C** = the codec-dispatch
seam (MJPEG decode + H.264 stub for TT-4); **TT-3-D** = LIVE; **TT-3-E** = this close-out.

---

## 🔴 THE SCALE FINDING — the most consequential result of the Teacher track so far

**Student screen streams are ON-DEMAND, not always-on.** Traced from the shipped Windows code, not
inferred:
- `MainViewModel.ViewStudentScreen` opens a `StudentScreenWindow` for **one** student and calls
  `RequestStudentStreamAsync` for **that** student; the window calls `StopStudentStreamAsync` on close.
- `StudentStreamStart` is **targeted** (`Envelope.CreateTargeted(…, studentId)`), not a broadcast.
- **No loop** anywhere starts streams for all students.
- **Tiles never stream.** `ThumbnailImage` is set only from a **manual** `ScreenshotResponseMessage`
  (JPEG) — there is no periodic all-student screenshot timer.

So the Teacher decodes **1–4 concurrent streams** (one per open screen-view window), **never 40**.
**`VTDecompressionSession ×40 concurrent` was never the real shape** — it was a phantom.

**RESOLVED PRODUCT QUESTION (2026-07-14, confirmed with sales):** customer B expects the **shipped
behavior** — the teacher clicks to view **one student at a time** — **not** a live thumbnail wall. So:
- The scale gate is **CLOSED, not deferred**.
- The roadmap's **downscale / decode-on-demand mitigation is unnecessary — dropped** (§7 of the roadmap).
- **TT-4 collapses to "add one decoder to a working pipeline."**
- `--classroom 40` stays as a **stress test, not a gate**.

**For the record:** a live thumbnail wall would be a **NEW FEATURE** (not in the shipped product) and
**would reintroduce the scale gate**. If it ever comes up, it is a scoped feature request with a known
cost, not a bug.

This is the **4th phantom** investigation-first has killed on this project (M21 "strong Windows lock"
→ soft; M23 "Service/Agent split needed" → Session-0 artifact; 32-G "entitlement problem" → Local
Network Privacy; now TT-3 "×40 decode gate" → on-demand).

## THE RENDER SEAM — why TT-4 is additive
`OnFrame` → **codec-dispatch `RenderFrame(frame)` → `switch (frame.Codec)`**, mirroring the shipped
`OnStudentFrame → RenderMjpeg / RenderH264`:
- `Mjpeg` → `new Bitmap(jpegStream)` (the shipped `RenderMjpeg` equivalent).
- `H264` → **stub** (returns null) until TT-4.

**The type question was VERIFIED by reflection, not assumed** (the probe):
`typeof(WriteableBitmap).BaseType == typeof(Bitmap)` (WriteableBitmap **derives from** Bitmap), and
`Image.Source` is declared `IImage?`. So `CurrentFrame` typed **`Bitmap?` already accommodates TT-4's
`WriteableBitmap`** with no rewrite. (The Student-track `ScreenCaptureViewModel` types its field
`WriteableBitmap?` only because it *only* ever produces those; the Teacher's view needs the common
base, `Bitmap`.)

**TT-4 fills ONLY the `H264` case** — VTDecompressionSession → CVPixelBuffer (BGRA) →
`WriteableBitmap.Lock()` + `Marshal.Copy` → return a fresh `WriteableBitmap` per frame (the proven
ScreenCaptureViewModel seam). Untouched by TT-4: the subscription, the studentId filter, the
request/stop lifecycle, the UI-thread marshal, and the dispose-previous-frame logic. **If TT-4 has to
reach into any of those, the seam was wrong — it doesn't.** An H.264 frame today is a clean no-op:
no crash, no garbage, the last good frame is kept, and the status strip says *"H264 stream — decoder
arrives in TT-4"* once (not per frame).

## THE SKIA HEADLESS GOTCHA — record this for every future render test
**Default `Avalonia.Headless` decodes `new Bitmap(jpeg)` to a 1×1 STUB** (its no-op drawing backend).
So **`assert bitmap != null` passes even when decode is completely broken** — a hollow test. A probe
(`BitmapProbe`, run before writing the gate) proved it: default → `1x1`; **Skia-enabled headless**
(`UseSkia()` + `AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }`) → real `64x48`.

**Rule:** run every render gate under **Skia-enabled headless** (it matches production — `Avalonia.Desktop`
uses Skia) and **assert EXACT dimensions**, never just non-null. This is the same class of trap as the
count-only marshal test (TT-2-C): a naive assertion passes with the bug present. That's now **2-for-2**
on catching "the obvious assertion is hollow" before it poisons a suite.

## LIFECYCLE DISCIPLINE — `ScreenViewController` (the thing most likely to leak)
- **One window per student.** A second double-tap **focuses** the existing window — no duplicate
  `RequestStudentStreamAsync`.
- **Every close path funnels through the window's `Closed` handler** → `Stop()` → `StopStudentStreamAsync`.
  User-close, `CloseAll` on quit, and disconnect-close all stop the stream exactly once.
- **`CloseAll` on quit runs BEFORE `session.Dispose`** — so the stop envelope can still be sent while
  the server is alive. Wrong order = a student left encoding to nothing.
- **Disconnect-close:** roster `StudentRemoved` (a background thread) closes that student's window,
  **marshaled to the UI thread**.

A student left streaming to a dead window would be a **real leak at 50 seats** (encode + send CPU/network
burned for a window nobody is watching). This is the same discipline as the shipped
`StudentScreenWindow.xaml.cs:730` (stop-on-close).

## THE MARSHAL (carried from TT-2, proven again here)
Frames arrive on the transport's **background read loop**; the `Image.Source` swap
(`CurrentFrame`) is marshaled to the **UI thread** via `Dispatcher.UIThread.Post` — the render loop
reads `CurrentFrame` concurrently, so an off-thread mutation would race it. The headless gate asserts
the thread at **both** ends (`CheckAccess() == false` at the raise site, `== true` at the update site)
— the TT-2-C pattern; a naive "a frame arrived" test would pass even off-thread. TT-3-D then proved
it on the **real** transport read loop (not a `Task.Run` fake) — closing caveat 1.

## BELL's codec — traced (why TT-3 could LIVE-test MJPEG with no TT-4 dependency)
The shipped `Student.Agent` (`MainWindow.xaml.cs:1191-1216`) **defaults to `VideoCodec.Mjpeg`** and
then **honors `req.Codec`** from the `StudentStreamStartRequest` (falls back to MJPEG on a parse
failure), starting `new StudentBroadcaster { Codec = codec }`. **The Teacher requests the codec; the
student encodes what it's told.** The Teacher's `ScreenViewModel` requests **MJPEG explicitly**, so a
shipped Windows Student streams MJPEG — renderable today, no H.264 decoder required.

## Project structure
- All TT-3 code is in `src/ClassroomCtrl.Avalonia.Teacher`: `Services/IStudentStreamSource.cs` (the
  seam), `ViewModels/ScreenViewModel.cs`, `Views/ScreenViewWindow.axaml`(+`.cs`),
  `Services/ScreenViewController.cs`; plus small edits to `TeacherSession` (implements the seam by
  forwarding to the in-app `ControlServer`), `MainWindow` (double-tap → `StudentActivated`), and `App`
  (wires the controller + roster removal + teardown).
- **`IStudentStreamSource` is the seam** that lets `ScreenViewModel` be unit-tested against a fake
  source (no real transport). Teacher.Core's `ControlServer` already had the whole frame flow
  (TT-1-C: `StudentStreamFrameReceived` / `Request` / `StopStudentStreamAsync`), so TT-3 added **zero**
  wire/routing code — pure UI + lifecycle.
- **Trigger:** **double-tap a tile** (the context menu affordance is deferred). Minor known side
  effect: a double-tap toggles the tile's visual selection twice (net unchanged) — cosmetic.

## Deferred (honest, with reasons)
- **H.264 decode** — TT-4 (the one big new native piece; now additive, see the scale finding).
- **The other ~600 LOC of the shipped `StudentScreenWindow`** — remote control, per-student recording,
  screenshot, quality reporting. Out of TT-3 scope (a working screen view is request → render → stop).
- **Disconnect shows window-close, not a "student disconnected" freeze** — chose no-leak over
  last-frame-freeze; easily revisitable (set a status + keep the window).
- **Tile screen thumbnails** — the shipped product updates these only from a manual screenshot; not a
  live stream. Out of scope; would piggyback on the same decode path if wanted.

## Verification (all green)
- **Skia-headless gate — 36/36** (`TT3BSmoke`, run under `UseSkia()` + `UseHeadlessDrawing=false`):
  lifecycle (Start→request MJPEG / Stop→stop, idempotent) · studentId **filter** (matching renders,
  non-matching dropped) · **real 64×48 decode** (not the 1×1 stub) · the **background→UI marshal**
  thread-assertion · window Opened/Closed · controller **dedupe / disconnect-close / CloseAll** ·
  codec dispatch (MJPEG **exact 100×80**; H.264 stub: no crash / no render / keeps last frame /
  surfaces "TT-4" / doesn't wedge the view).
- **Non-regression:** full solution build **0 errors**; **T1–T27 PASS**; `MockStudent --selftest`
  PASS; **`--teacherselftest` PASS**. Sandbox / Shared.Wire / Teacher.Core unchanged; **shipped
  Windows repo untouched**.
- **LIVE (TT-3-D, 2026-07-14):** a real Windows Student's screen rendered live in the Mac Teacher
  (MJPEG). See `docs/TT-3-LIVE-CONFIRMATION.md`.

## What TT-3 unlocks · next
Scenario 3 (Mac Teacher + Windows Student) now spans **roster (TT-1) → live grid (TT-2) → live screen
view (TT-3)** against the shipped, unmodified Windows product. **The Mac Teacher is now: server +
roster + live grid + live screen view (MJPEG).** **Next: TT-4** — H.264 **decode**
(`VTDecompressionSession`), now a **small additive phase** into a proven pipeline (scale gate closed),
filling only the `RenderFrame` H.264 branch + the native decoder.
