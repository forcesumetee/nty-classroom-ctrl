# Phase 27-C Findings — Wire captured frames to the Windows Teacher (Milestone 17)

**Goal:** the Windows Teacher displays the Mac Sandbox's actual desktop in real time.
**Result:** ✅ Mac-side complete + proven headless — on the Teacher's `StudentStreamStart`,
the Mac captures its screen via ScreenCaptureKit → JPEG → sends `StudentStreamFrame`
(existing envelope). `MockTeacher --streamtest` decodes 8/8 valid JPEGs. **LIVE Windows
confirmation is the one remaining human step** (needs the Windows PC).

**Sub-phases:** 27-C-1 native JPEG · 27-C-2 wire integration · 27-C-3 verify · 27-C-4 docs.

## The big win from investigation
The whole feature ships **without touching the shipped repo and without any wire change**:
- The Teacher already has the receiver — `ViewStudentScreenCommand` → `StudentScreenWindow`
  → `RequestStudentStreamAsync` sends `StudentStreamStart`; incoming `StudentStreamFrame`
  → `RenderMjpeg` decodes plain JPEG (it dispatches on the frame's own `Codec` field).
- The Teacher's default requested codec is **`VideoCodec.Mjpeg`** — no H.264 needed (27-B
  is not a blocker).
- `StudentStreamStart/Frame/Stop` + `ScreenStreamFrameMessage` + `Mjpeg` are existing
  (T4 Part 2, part of T1–T26). We **use** them; T1–T26 stays PASS.
- The shipped `StudentBroadcaster.cs` was the exact template (1280×720, JPEG Q60, low fps).

## 27-C-1 — native JPEG (extends the §20 helper)
`nty_capture_start_jpeg(fps, quality, maxW, maxH, cb, ctx)`: SCStream downscales to an
aspect-preserving fit of maxW×maxH (so `SCStreamConfiguration.width/height` does the resize
— no separate scale pass), then per frame `CVPixelBuffer → CGImage` (shared `CIContext`) →
JPEG via `CGImageDestination` at quality/100. Delivers call-scoped JPEG bytes.
Verified: ~4 fps, **1107×720** (fit of 1470×956 into 1280×720), ~136 KB/frame @ Q60, valid
`FFD8…FFD9`. `ScreenCaptureService` exposes `StartJpegAsync` + `JpegFrameReceived` via a
second `[UnmanagedCallersOnly]` callback (same GCHandle-as-ctx pattern as §20).

## 27-C-2 — wire integration
`ScreenStreamer` bridges capture → wire: on start, `StartJpegAsync(1280×720, Q60, 6 fps)`;
per JPEG frame builds `ScreenStreamFrameMessage { Codec=Mjpeg, FrameSeq++, IsKeyframe=true }`
→ `WireClient.SendAsync(StudentStreamFrame)`. `ConnectionViewModel.Dispatch` now starts the
streamer on `StudentStreamStart` (was "deferred") and stops on `StudentStreamStop` / disconnect.
Self-tile shows "🔴 Streaming screen to teacher · N frames sent" (`docs/phase-27-c-streaming.png`).
**Always sends Mjpeg** regardless of the requested codec — the Teacher decodes by the frame's
`Codec` field, so MJPEG renders even if H.264 was requested (H.264 = 27-B).

## 27-C-3 — verification without a Windows box
`MockTeacher --streamtest`: in-process mock Teacher + the **real** `WireClient` + `ScreenStreamer`.
Server sends `StudentStreamStart(MJPEG)` on Hello → Mac captures + sends → server decodes
`ScreenStreamFrameMessage` + validates. **PASS: 8/8 frames valid JPEG, 1107×720 @ ~137 KB**;
sample saved as a real `.jpg`. Interactive `viewscreen`/`stopscreen` added for local GUI testing.
Same de-risking pattern as Milestones 15/17: proven locally, Windows just confirms.

## Threading / interop notes
- Frames flow on the native delivery thread → `WireClient.SendAsync` (thread-safe: internal
  send lock). Fire-and-forget with a try/catch (a mid-stream disconnect just drops the frame;
  the reconnect loop handles the link). `FrameSent` → UI-thread counter (§18).
- One capture at a time (native singleton session) — the 27-A preview tab and 27-C streaming
  are mutually exclusive; a second start returns the native `-3` (surfaced, not fatal).
- Rate/size chosen to match the shipped student's neighborhood; ~137 KB/frame @ 6 fps ≈ 6.5
  Mbit/s on the LAN — fine for a prototype. H.264 (27-B) would cut this ~10×.

## No new cheat-sheet section
27-C is an **integration**, not a WPF→Avalonia porting pattern — it composes §20 (native
Swift-dylib interop) + §18 (Dispatcher marshalling) + existing wire envelopes. Nothing new
to codify in the porting cheat sheet; the reusable nugget ("native-encode into an existing
wire envelope to interop with the shipped product, zero protocol change") lives here.

## LIVE Windows test — the milestone moment (user-run)
1. Windows: launch Teacher v1.2 (Milestone-15 setup, IP 172.20.10.7).
2. Mac: `./scripts/package-app.sh && open ./ClassroomCtrl.Sandbox.app` → Connection tab → Connect.
3. Windows Teacher: right-click the Mac student tile → **View Screen**.
4. Expect: **the Mac's actual desktop appears in the Teacher's StudentScreenWindow**; the Mac
   self-tile shows "🔴 Streaming … N frames sent". Photo the moment — Milestone 17.

## Constraints honored
Sandbox + `native/` + `tools/MockTeacher` only · `Shared.Wire` unchanged (used, not modified) ·
shipped Windows repo untouched · T1–T26 unaffected · per-sub-phase commits.

## Deferred (as planned)
H.264 encode → 27-B (VideoToolbox) · multi-display · cursor toggle · region capture · bitrate
adaptation · optimization.
