# Phase 26.0 Findings — Wire Real Data (Mac Sandbox → Teacher LIVE)

**Goal:** promote the Phase 24.2 interop *tool* into a functional student inside the
Sandbox app — connect over the real wire protocol, appear as a tile, receive Teacher
commands, maintain a heartbeat. **Result:** ✅ the whole loop is **proven locally**
(deterministic self-test) and staged for one-shot live confirmation against the Windows
Teacher. `docs/phase-26.0-connection.png`.

**Sub-phases:** 26.0-A WireClient + connection UI · 26.0-B MockTeacher + self-test ·
26.0-C reception + debug display · 26.0-D docs.

## Key insight — this was packaging, not protocol discovery
The Phase 24.2 interop tool already byte-verified the transport (framing, Hello,
Ping/Pong) against the shipped Windows Teacher. So 26.0 lifted that transport into a
proper service and added only what a *persistent* student needs. The shipped Student's
`ClassroomWorker.cs` gave the exact cadence (no guessing):
- Framing `[4B big-endian len][MessagePack(Envelope)]`; 5 s connect timeout.
- **Hello on connect** = appear as a tile.
- **Ping every 5 s**, fire-and-forget = stay alive; Teacher's Pong is silent.
- Reconnect backoff **2 s → ×2 → 30 s**.

## 26.0-A — WireClient (service) + ConnectionViewModel (UI)
`Services/WireClient.cs` is **UI-agnostic** (no Avalonia): connect + Hello + heartbeat +
read loop + reconnect, raising plain events. Stable `EndpointId` across reconnects → one
persistent tile. `ConnectionViewModel` marshals those background-thread events to the UI
thread (§19). IP validation extracted to `Services/IpValidation.cs` and **reused** by the
25.7-C TeacherIPDialog (single source of truth).

## 26.0-B — MockTeacher + self-test (the de-risker)
`tools/MockTeacher` is a local TCP Teacher stand-in (accepts Hello, Ping→Pong, pushes
commands). Its `--selftest` spins up the server **and the real `WireClient`** in-process
and asserts the full loop:
```
✅ Hello received by server
✅ Pong sent by server in reply to Ping
✅ Client received LockScreen / PolicyApply / ChatBroadcast
=== SELFTEST PASS ✅ ===   (exit 0)
```
This turned "hope it works when we plug in Windows" into "already proven locally" — and is
reusable as a regression harness. Interactive mode runs alongside the Sandbox GUI for a
manual local demo.

## 26.0-C — reception + debug display
`ConnectionViewModel.Dispatch` decodes each inbound envelope into **(a)** a decoded summary
row in the wire-traffic log and **(b)** a state mutation on `StudentSelfTileViewModel`
("how the Teacher sees me"): lock banner, policy chips, chat line, hand-raise. Capture-class
commands (screenshot / screen-stream / camera) are **logged + noted as deferred** (need
native macOS APIs, Phase 27+) — no enforcement, per scope. Bonus S→T: a "Raise hand" button
emits HandRaise/HandLower.

## New pattern → cheat sheet §19
**Background services + `Dispatcher` marshalling.** All prior phases were static views; this
was the first long-lived async service. Rule: service = pure async + events (Avalonia-free,
so it's headless-testable); VM = `Dispatcher.UIThread.CheckAccess()/Post()` +
`CancellationTokenSource` lifetime; never `await` the run loop inside a command
(fire-and-forget, cancel to stop). 1:1 with WPF `Dispatcher` habits. Cheat sheet 18 → **20
sections** (§19 + Reference links → §20).

## Deferred (unchanged scope)
Screen capture (ScreenCaptureKit), video/camera/audio (AVFoundation), actual Lock/Policy
enforcement (NSWorkspace/overlay + native policy), real chat send/receive UI — all Phase 27+.
26.0 reflects/logs these; it does not perform them.

## Tallies
- HeadlessCapture: 10 → **11 scenarios** (`connection`, seeded connected session).
- MainWindow tabs: 11 → **12** (tab 11 = Connection).
- New tool: `tools/MockTeacher` (interactive + `--selftest`).
- Cheat sheet: **20 sections**.

## Live Windows test — staged, see `docs/LIVE-TEST-26.0.md`
The remaining step needs the Windows PC. Procedure documented; expected result = the Mac
appears as a tile in Windows Teacher and reflects Lock/Policy sent from it. This is the
**milestone screenshot** to capture.
