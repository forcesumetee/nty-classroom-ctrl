# Phase 26.0 — LIVE Confirmation (Mac Sandbox ↔ Windows Teacher, real hardware)

**Date:** 2026-07-12 · **Result:** ✅ **4/4 PASS** — byte-perfect, bidirectional
cross-platform wire interop confirmed against the **shipped Windows Teacher v1.2** on
real hardware. This closes **Milestone 15** and validates the full-stack porting method
end-to-end.

## Test environment
| Role | Machine | Address |
|---|---|---|
| **Teacher** (shipped **v1.2**, WPF/.NET 10) | Windows PC | **172.20.10.7:7777** (TCP control) |
| **Student** (Mac Sandbox, Avalonia/.NET 10) | Suwijaks-MacBook-Air | **172.20.10.2** |
| Network | iPhone hotspot | 172.20.10.x subnet · channel `1234` |

The Mac ran `dotnet run --project src/ClassroomCtrl.Avalonia.Sandbox` → **Connection** tab →
entered `172.20.10.7` → **Connect**. The `WireClient` (Phase 26.0-A) drove the real
transport; no MockTeacher involved.

## Results — 4/4 PASS
| # | Direction | Command | Result |
|---|---|---|---|
| 1 | Windows → Mac | **Lock screen** | ✅ Sandbox reflected the 🔒 lock banner + `RX ▼ LockScreen` |
| 2 | Windows → Mac | **Apply Policy** | ✅ Sandbox reflected the policy chips + `RX ▼ PolicyApply` (decoded) |
| 3 | Windows → Mac | **Chat message** | ✅ Sandbox showed `💬 Teacher: test` + `RX ▼ ChatBroadcast` |
| 4 | **Mac → Windows** | **Raise Hand** | ✅ **Bidirectional** — Teacher tile lit the hand indicator (S→T) |

## What the live traffic log proves (screenshot)
`docs/live-test-2026-07-12/Screenshot 2569-07-12 at 23.45.42.png`:
- Status pill **green "Connected"** to the real Teacher.
- `SYS • TCP connected to 172.20.10.7`, then **`TX ▲ Hello` = 213 B** — note the real
  frame is larger than the local demo's 194 B because `RuntimeInformation.OSDescription`
  on this Mac is a longer string; the envelope shape is identical, the size just reflects
  real payload content.
- **`TX ▲ Ping` every 5 s → `RX ▼ Pong` = 102 B "keepalive ack"** — the heartbeat loop
  round-tripping with the shipped Windows `TcpControlServer`, keeping the tile alive.
- Self-tile chat `Teacher: test` and the deferred note (below).

## Screen view — correctly DEFERRED (in scope)
The live log shows **`StudentStreamStart · deferred (needs native APIs)`** on the self-tile:
the Teacher requested a screen view of the Mac, and the Sandbox **logged + noted it as
deferred** rather than streaming — exactly the Phase 26.0 scope boundary. Actual screen
capture (ScreenCaptureKit) is **Phase 27+**. This is a *correct* result, not a gap: it
demonstrates the dispatch layer handles capture-class commands gracefully today.

## What this confirms
- **Byte-perfect wire protocol** between macOS/Avalonia and the **shipped, unmodified**
  Windows/WPF production code — the exact envelopes (T1–T26 shapes), framing
  (`[4B BE len][MessagePack]`), Hello, and 5 s Ping/Pong heartbeat.
- **Bidirectional** interop (T→S commands *and* S→T hand-raise).
- The MockTeacher `--selftest` predicted the live result precisely — "proven locally,
  Windows confirms" held.
- The macOS Sandbox is a **functional classroom student** at the protocol level.

## Boundary (unchanged — Phase 27+)
Screen capture (ScreenCaptureKit), video/camera/audio (VideoToolbox/AVFoundation), and
actual Lock/Policy *enforcement* (NSWorkspace/overlay + native policy) remain deferred.
26.0 connects, appears, heartbeats, receives, and reflects — it does not capture or enforce.

**Evidence:** commit `efb3c5e` (screenshot committed + pushed).
