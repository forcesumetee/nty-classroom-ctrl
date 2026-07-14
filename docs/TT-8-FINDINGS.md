# TT-8 Findings — Teacher "Share My Screen" + Mac-Student display · COMPLETE (LIVE-confirmed 2026-07-14)

**Goal:** the Mac Teacher captures its own screen and broadcasts it to all students; the Mac Student
receives + displays it (the shipped takeover-viewer UX). Built as **BATCH 1** with TT-7. **LIVE gate
met** with **2 Mac Sandbox students on separate physical Macs** — both saw the teacher's screen live,
and Stop closed both viewers cleanly. See `docs/BATCH-1-LIVE-CONFIRMATION.md`. Constraints held:
Shared.Wire unchanged (T1–T27 green), shipped repo untouched.

Sub-phases: **TT-8-B** shared Media lib (extract) · **TT-8-C** teacher capture→broadcast ·
**TT-8-D** student receive+display · **TT-8-E** committed gate + regression.

---

## 🔴 THE EXTRACT DECISION — copy diverge-able UI, SHARE must-not-diverge native interop
The delegated design fork: (a) copy the H.264 decoder into the Sandbox (the TT-2 StudentCard
precedent) or (b) extract it to a shared lib. **Chose (b)** — new `ClassroomCtrl.Avalonia.Media`
holding `H264DecoderWrapper` + a new `ScreenFrameDecoder` (the pure codec→Bitmap core), referenced
by BOTH Teacher and Sandbox. This **deliberately inverts** the StudentCard precedent, and the reason
is the principle: **copy UI that is SUPPOSED to diverge; SHARE code that must NOT diverge.** The
decoder is native interop — `GCHandle` rooting, stride-aware `Marshal.Copy`, `WriteableBitmap.Lock`
(the exact TT-4 gotchas). Two drifting copies of that is the worst bug class we could create (a
stride fix applied to one copy, silently broken in the other). Extraction was clean (the wrapper had
zero Teacher-specific deps). **Reused again by TT-15 (demonstration display) + TT-17 (breakout group
frames)** — the shared lib pays off three times. The app-specific viewing lifecycle (the H.264→MJPEG
fallback re-request, the per-student windows) stayed in each app; only the decode core moved.

## Teacher capture → broadcast — reuses M17/M18
`TeacherScreenBroadcaster` captures the main display via the native ScreenCaptureKit path
(`nty_capture_start_jpeg`, 1280×720 / Q60 / 2 fps — matches the shipped `ScreenBroadcaster`) and
pushes each frame to `ControlServer.BroadcastScreenFrameAsync`. The native capture is a process
SINGLETON, but there's **no conflict**: the Teacher captures ONE screen while DECODING student
screens through the separate handle-based decoder subsystem. (Kept a small Teacher-side capture
driver rather than sharing `ScreenCaptureService` — the capture ABI is a trivial 3-function surface;
fold into Media later if it grows.)

## 🔴 BUG #5 — ScreenStreamStop promoted to reliable; Start stays lossy (asymmetry deliberate)
`BroadcastScreenStreamControlAsync`: the **STOP** now routes reliable, the **START** stays lossy.
A dropped Start just means a student misses this share (the next frame or a re-share self-heals — no
stuck state). A dropped **Stop under a frame flood** (exactly when the lossy `DropOldest` queue is
full) **strands the student in the takeover viewer**, unable to see their own screen — a stuck state.
Camera Start/Stop were already reliable; screen Stop was not. Same *"dropped control message leaves a
stuck state"* class as bug #6 (hand-lower). **Frames route lossy** (`BroadcastAsync`, DropOldest cap
16) — correct for video, confirmed no head-of-line stall behind a slow peer.

## Screen Recording TCC — NEW on the Teacher → a Teacher bundle (TT-13 pulled forward)
Until TT-8 the Teacher only DECODED (operates on buffers — no permission). Capturing its own screen
needs **Screen Recording TCC**, which binds to the launching binary + bundle id — an unbundled
`dotnet run` attributes it to the `dotnet`/Terminal host and won't persist (the TT-4-D friction). No
Teacher bundle existed (`package-app.sh` is Sandbox-only), so the TT-8 LIVE gate could not run. Built
**`scripts/package-teacher.sh` + `Info.Teacher.plist`** (distinct id `com.nty.classroomctrl.teacher`,
windowed — no LSUIElement, `NSScreenCaptureUsageDescription` + LNP, dylib next to the apphost for
capture + decode). A minimal, ad-hoc-signed, framework-dependent **slice of TT-13 pulled forward**
because the LIVE gate depended on it (flagged before the gate, not discovered at it). P35 does the
real Developer ID signing + notarization.

## Student-track item #10 — Mac Student receives + displays the teacher screen (net-new)
Was misclassified as a deferred student-capture command; now three real Dispatch cases: **Start**
(open the takeover viewer), **Frame** (decode via the shared `ScreenFrameDecoder`, handled before the
filter/log like audio), **Stop** (close it — reliable). A `TeacherScreenWindow` + `TeacherScreenController`
(the shipped `ScreenViewWindow` analog); disconnect also closes it. The teacher-share is a
**broadcast** (`IsForMe` passes for the whole class — unlike a targeted command).

## Verification (all green)
- **`--teacherselftest`** — PASS, incl. **(0f)** (ScreenStreamStart/Stop broadcasts pass IsForMe;
  `ScreenFrameDecoder` dispatch + the H.264 keyframe-fail fallback SIGNAL + empty-MJPEG guard) and
  **(1c)** (real transport: teacher Share START/FRAME/STOP reach a real WireClient student; frame
  round-trips intact — 1280×720/Mjpeg/seq/bytes).
- **T1–T27** PASS (Shared.Wire unchanged); **`--selftest`** PASS; solution build 0 errors; the
  Teacher decode path (ScreenViewModel on the extracted decoder) unregressed.
- **Bundle** built + verified + ad-hoc codesigned clean.
- **LIVE (2026-07-14)** — 2 Mac students on separate Macs: teacher screen reached BOTH live; Stop
  closed BOTH viewers cleanly (no stuck viewer — the reliable-Stop fix); disconnect-close. See
  `docs/BATCH-1-LIVE-CONFIRMATION.md`.

## What TT-8 unlocks · next
The Mac Teacher can now **present** to the whole class, and the Mac Student displays it (item #10
done). The shared `ClassroomCtrl.Avalonia.Media` decode core is now in place for TT-15/TT-17.
**Next: the multi-peer cluster (TT-9 → TT-10 → TT-11)** — new-native audio + the flagship relay.
