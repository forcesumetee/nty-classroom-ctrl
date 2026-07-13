# Phase 30 — LIVE Confirmation (Milestone 21: screen-lock enforcement)

**Date:** 2026-07-13 · **Result:** ✅ **LIVE-CONFIRMED** — the shipped Windows Teacher v1.2
locks and unlocks a macOS student via a **HARD kiosk shield** (exceeds the soft Windows
teacher-lock), with a proven **dead-man switch** so no student is ever stranded. Over the
existing `LockScreen`/`UnlockScreen` envelopes; no shipped-repo change, no protocol change.

## Test environment
| Role | Machine | Notes |
|---|---|---|
| **Teacher** (shipped **v1.2**, unmodified) | Windows PC | lock/unlock commands |
| **Student** (Mac Sandbox, Avalonia + AppKit shield) | **borrowed Mac, single-display** | packaged `.app` |
| Network | iPhone hotspot | 172.20.10.x subnet |

## Results (visual verification by tester)
| Test | Result |
|---|---|
| Teacher locks → **shield appears** on the Mac | ✅ |
| **Kiosk enforcement** — Cmd+Tab / Cmd+Opt+Esc (Force Quit) blocked | ✅ |
| Explicit **UnlockScreen** → shield gone | ✅ |
| Lock/unlock cycle reliable (repeatable) | ✅ |
| Machine recoverable — **no stranding** | ✅ |

## Evidence
- **Tester visual verification** (LIVE) — shield shown, kiosk active, clean unlock, recoverable.
- **Structural (Mac-side, headless):** `MockTeacher --locktest` proved all three dead-man grace
  cases — **A** Wi-Fi blip HELD (past the original window), **B** teacher-death UNLOCKED, **C**
  no-reset continuous window (fired from first departure, not from Disconnected; the log shows
  exactly one `grace start`). Plus the 30-B auto-hide harness confirmed `presentationOptions` raw
  **506** (the 7 kiosk flags) applied on show, cleared to 0 on hide.
- **Screenshots:** **not captured** — borrowed Mac hardware (consistent with the M19/M20 precedent).
  The `--locktest` artifact + tester visual verification stand in for the pixel proof.

## Known gap — multi-display untested on hardware
The Mac used was **single-display**. The shield's per-`NSScreen` construction + display-hotplug
rebuild (`didChangeScreenParameters`) are **code-correct** but **were NOT validated on real
multi-monitor hardware.** This is an honest gap to close on an **owned multi-display Mac** in a
future run (attach an external monitor while locked → verify a shield covers it too). Single-display
coverage is what was confirmed live. *(We do not claim multi-display works on hardware — only that
the code path exists and is correct by construction.)*

## What this confirms
- **Enforced** screen-lock (not just reflected) from the shipped, unmodified Windows Teacher to a
  macOS student — a HARD kiosk (shield + `NSApplicationPresentationOptions`) with **zero
  Accessibility permission**, over unchanged wire.
- **Safer than the Windows teacher-lock:** Windows never auto-unlocks (permanent-stranding risk);
  ours has the four-layer dead-man (process-kill / 45 s disconnect grace / 30 min cap / wake
  re-assert). "Machine recoverable — no stranding" was confirmed live.
- **Stronger than the Windows teacher-lock:** blocks Cmd+Tab / Force-Quit / Dock / menu (Windows
  blocks none of these for its teacher-lock), and covers all displays by construction (Windows:
  primary only).
- Zero shipped-repo change, T1–T27 intact.

## Residuals → Phase 31
Two macOS-specific escape routes remain (Spotlight-launch, Mission Control) — closed by a
`CGEventTap` (which needs Accessibility) in **Phase 31 (input hooks)**, out of M21 scope.

## Pattern consistency
| | M17 MJPEG | M18 H.264 | M19 camera | M20 audio | **M21 lock** |
|---|---|---|---|---|---|
| Native API | ScreenCaptureKit | +VideoToolbox | AVCaptureSession | AVAudioEngine | **AppKit shield/kiosk** |
| Structural proof | `--streamtest` | `--streamtest-h264` | `--cameratest` | `--audiotest` | **`--locktest`** (3 grace cases) |
| LIVE proof | Windows visual | Windows visual | Windows visual (borrowed) | Windows audible (3/4) | **Windows visual (borrowed, single-display)** |
