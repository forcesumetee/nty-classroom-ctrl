# Phase 31 — LIVE Confirmation (Milestone 22: input-hook keystroke guard)

**Date:** 2026-07-13 · **Result:** ✅ **LIVE-CONFIRMED** — a macOS student under a teacher lock
now **suppresses the two escape routes the Phase 30 shield couldn't reach** (Spotlight-launch,
Mission Control) via a `CGEventTap` keystroke guard, while every safety and degradation path holds.
Additive to the M21 lock, over unchanged wire; no shipped-repo change.

## Test environment
| Role | Machine | Notes |
|---|---|---|
| **Teacher** (shipped **v1.2**, unmodified) | Windows PC | lock / unlock commands |
| **Student** (Mac Sandbox, Avalonia + AppKit shield + CGEventTap) | **borrowed Mac** | packaged `.app`, Accessibility toggled per test |
| Network | iPhone hotspot | 172.20.10.x subnet |

## Results (visual verification by tester) — all four groups PASS
| # | Group | Test | Result |
|---|---|---|---|
| 1 | **Suppress** | Grant Accessibility → teacher locks → **Cmd+Space (Spotlight) + Mission Control suppressed** | ✅ |
| 1 | **Suppress** | Unlock → **Spotlight / Mission Control work again** (guard removed with the shield) | ✅ |
| 2 | **Graceful degrade** | **Deny** Accessibility → lock **still fully works** (shield + Cmd+Tab / Force-Quit blocked); only the two residuals stay open | ✅ |
| 3 | **Kill-release** | Kill the student process → **shield + tap both released**, keyboard fully recovered | ✅ |
| 4 | **Grace-release** | 45 s disconnect (teacher-death) → grace fires → **shield + tap both released** | ✅ |

## Evidence
- **Tester visual verification** (LIVE) — suppression engaged under lock, residuals reopened on
  unlock, lock intact when Accessibility denied, keyboard recovered on kill and on grace fire.
- **Structural (Mac-side, headless):**
  - `MockTeacher --inputtest` — **fail-open demonstrated via the real OS watchdog**: a deliberately
    stalled callback is auto-disabled by macOS (`kCGEventTapDisabledByTimeout`) so keystrokes flow
    untouched, then the callback re-enables. **4/4 runs** (3 back-to-back + 1 post-kill), one OS
    auto-disable each; plus suppression, recovery, and guaranteed-uninstall. Process-kill release
    proven separately (`--inputhold` → `kill -9` → pid gone = OS tore down the tap).
  - `MockTeacher --locktest` — **21/21 checks** (CASE A–E): guard installed-on-lock (trusted), HELD
    through a Wi-Fi blip, and **RELEASED on all three dead-man paths** (explicit unlock / 45 s grace
    fire / 30 min cap fire), plus graceful-degrade (denied → shield up, guard absent, prompted once).
    Flag backend → **zero real taps** in automated testing.
- **Screenshots:** **not captured** — borrowed Mac hardware (consistent with the M19–M21 precedent).
  The `--inputtest` / `--locktest` artifacts + tester visual verification stand in for the pixel proof.

## What this confirms — the two Phase 30 residuals are CLOSED
Phase 30's escape-route table left two macOS-specific residuals the kiosk shield couldn't reach
(Spotlight-launch via Cmd+Space, Mission Control). Phase 31 closes both with a `CGEventTap` guard
that is **additive** to the lock:
- **Enforcement** — under a lock with Accessibility granted, Spotlight and Mission Control are
  suppressed; the guard is installed only while the shield is up and removed on every unlock.
- **Never weakens the lock** — the shield + `NSApplicationPresentationOptions` still enforce the
  lock with **zero Accessibility**; the tap only adds keystroke suppression on top. If Accessibility
  is denied the lock is unaffected (graceful degrade) — the two residuals simply stay open.
- **Fails safe** — a slow/wedged tap is auto-disabled by the OS (keys flow), process-kill releases
  it, and it comes down on every dead-man path. **A frozen keyboard is not a reachable state.**

## Safety recap (verified live + structurally)
- **Fail-open** (OS auto-disable of a slow tap → keys flow → re-enable) — `--inputtest` 4/4.
- **Process-kill release** (OS-enforced, dead-man layer 1) — LIVE + `--inputhold`/`kill -9`.
- **Grace / cap release** — LIVE (grace) + `--locktest` CASE B/C (grace) + D (cap).
- **Graceful degrade** when Accessibility denied — LIVE + `--locktest` CASE E.
- **Zero real taps in automated testing** — `--locktest` flag backend.

## Pattern consistency
| | M17 MJPEG | M18 H.264 | M19 camera | M20 audio | M21 lock | **M22 input guard** |
|---|---|---|---|---|---|---|
| Native API | ScreenCaptureKit | +VideoToolbox | AVCaptureSession | AVAudioEngine | AppKit shield/kiosk | **CGEventTap + Accessibility** |
| Structural proof | `--streamtest` | `--streamtest-h264` | `--cameratest` | `--audiotest` | `--locktest` (3 grace) | **`--inputtest` (fail-open) + `--locktest` (guard lifecycle, 21/21)** |
| LIVE proof | Windows visual | Windows visual | Windows visual (borrowed) | Windows audible (3/4) | Windows visual (borrowed, single-display) | **Windows visual (borrowed): suppress / degrade / kill / grace** |

## Constraints honored
Sandbox + `native/` + `tools/MockTeacher` only · `Shared.Wire` unchanged · shipped repo untouched ·
screen/camera/audio/lock intact · T1–T27 PASS · per-sub-phase commits.
