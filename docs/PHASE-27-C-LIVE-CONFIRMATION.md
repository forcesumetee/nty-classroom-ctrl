# Phase 27-C — LIVE Confirmation (Milestone 17: Mac desktop in the Windows Teacher)

**Date:** 2026-07-13 · **Result:** ✅ **LIVE-CONFIRMED** — the shipped Windows Teacher v1.2
displays the Mac Sandbox's **actual desktop in real time** via *View Screen*. This closes the
full cross-platform demo circle and **Milestone 17**.

## Test environment
| Role | Machine | Address |
|---|---|---|
| **Teacher** (shipped **v1.2**, WPF/.NET 10) | Windows PC | **172.20.10.7:7777** (TCP control) |
| **Student** (Mac Sandbox, Avalonia/.NET 10 + ScreenCaptureKit) | Suwijaks-MacBook-Air | 172.20.10.x |
| Network | iPhone hotspot | 172.20.10.x subnet |

Mac launched from the packaged bundle (`./scripts/package-app.sh` → `open
./ClassroomCtrl.Sandbox.app`) so the Screen Recording (TCC) grant persists.

## Procedure + result
1. Mac: Connection tab → **Connect** to 172.20.10.7 (Milestone-15 flow).
2. Windows Teacher: right-click the Mac student tile → **View Screen** → sends `StudentStreamStart`.
3. Mac: `ConnectionViewModel.Dispatch` starts `ScreenStreamer` → ScreenCaptureKit → JPEG →
   `StudentStreamFrame` (existing envelope) upstream.
4. **✅ The Mac's actual desktop appears live in the Teacher's `StudentScreenWindow`.** The Mac
   self-tile shows "🔴 Streaming screen to teacher · N frames sent".

## The full cross-platform circle (all proven)
```
Mac capture (27-A, ScreenCaptureKit)  →  JPEG encode (27-C-1, ImageIO)
   →  wire send (27-C-2, StudentStreamFrame, EXISTING envelope)
   →  Windows Teacher v1.2 receives + RenderMjpeg  (SHIPPED, UNCHANGED)
   →  Mac desktop on the teacher's screen
```

## Baseline parameters
| Param | Value | Note |
|---|---|---|
| Codec | **MJPEG** (`VideoCodec.Mjpeg`) | Teacher decodes by the frame's Codec field; H.264 = Phase 27-B |
| Frame rate | **~6 fps** | matches the shipped student's neighborhood (default 4) |
| Capture size | **fit 1280×720** | aspect-preserving; measured 1107×720 on this display |
| JPEG quality | **60** | ~137 KB/frame (~6.5 Mbit/s at 6 fps) |
| Envelopes | `StudentStreamStart/Frame/Stop` + `ScreenStreamFrameMessage` | **existing** (T4 Part 2, part of T1–T26) |

## What this confirms
- **Byte-compatible screen streaming** from a macOS/Avalonia student to the **shipped,
  unmodified** Windows/WPF Teacher — over **existing** wire envelopes (no protocol change).
- The native-interop template (§20) + the `MockTeacher --streamtest` de-risking predicted the
  live result exactly (8/8 valid JPEGs headless → real display live).
- Zero shipped-repo changes, T1–T26 intact, v1.2 clients compatible.

## Evidence
- Mac-side UI: `docs/phase-27-c-streaming.png` (self-tile streaming badge + StudentStreamFrame TX log).
- Live captured frame (headless proof): `MockTeacher --streamtest` → real `.jpg` (8/8 valid).
- **LIVE Windows screenshots:** `docs/live-test-milestone-17/` (Mac desktop inside the Teacher window).

## Boundary (unchanged — later phases)
H.264 encode (VideoToolbox) → 27-B · multi-display · cursor toggle · region capture · adaptive
bitrate · lock/policy *enforcement*. Milestone 17 streams the screen; it does not encode H.264
or enforce policy.
