# Phase 32 — LIVE Confirmation (Milestone 23: system integration) · STUDENT TRACK COMPLETE

**Date:** 2026-07-13 · **Result:** ✅ **LIVE-CONFIRMED** — the macOS student runs as a real
**menubar background app in BUNDLE form** (the shippable shape, not `dotnet run`): auto-start via
LaunchAgent, config persistence, permission onboarding, and the full enforced lock — connected to
the shipped Windows Teacher over unchanged wire. **This completes the Student track.**

## Test environment
| Role | Machine | Notes |
|---|---|---|
| **Teacher** (shipped **v1.2**, unmodified) | Windows PC | lock command |
| **Student** (Mac, **packaged `.app`**) | **borrowed Mac, macOS 26.5.2** | menubar-only, LaunchAgent |
| Network | iPhone hotspot | 172.20.10.x subnet |

First LIVE test in **bundle form** — M15–M22 all ran via `dotnet run`; 32-G validated the shippable
`.app` shape end-to-end.

## Results (visual verification by tester) — all groups PASS
| Group | Test | Result |
|---|---|---|
| **Bundle** | No Dock icon (`LSUIElement`), tray status item in the menu bar | ✅ |
| **Connect** | Bundle connects to the local-subnet Teacher after granting the **Local Network** prompt (the LNP fix) | ✅ |
| **Lock** | Teacher locks → **shield appears** (native dylib resolves in-bundle), **Cmd+Tab blocked** | ✅ |
| **Auto-start (enable)** | "Start at Login" → plist created (586 B); the `IsBundled=true` gate works | ✅ |
| **Auto-start (disable)** | Toggle off → plist **removed cleanly** ("No such file" confirmed) — **borrowed-Mac no-trace** | ✅ |
| **Quit** | Clean exit; `ps aux` empty, **no relaunch loop** (`KeepAlive=false` confirmed) | ✅ |

## The bug found + fixed during 32-G — macOS Local Network Privacy
A/B testing surfaced a **bundle-only** networking failure: the `.app` couldn't `connect()` to the
local-subnet Teacher (172.20.10.x) while `dotnet run` could — same code/network/Teacher, and
`ping`/`nc` succeeded from Terminal.
- **Root cause (diagnosed, NOT entitlements):** the bundle is ad-hoc signed (`flags=0x2`), **NOT
  sandboxed** (empty entitlements), **NOT** hardened-runtime — so `com.apple.security.network.client`
  is inert (it only applies under App Sandbox, which we must NOT enable). The real gate is **macOS
  Local Network Privacy** (macOS 15+/26): a distinct bundled app identity needs consent to reach the
  local network, and the bundle was **missing `NSLocalNetworkUsageDescription`** → the OS silently
  denied the TCP connect. `dotnet run` isn't gated (it runs under Terminal's granted context).
- **Fix (an Info.plist key, not a codesign change):** add `NSLocalNetworkUsageDescription`; on first
  connect macOS prompts to allow local-network access → grant → connects; the grant binds to the
  stable bundle id. `package-app.sh` guards the key so it can't regress. **No App Sandbox added** —
  the bundle stays unrestricted, so screen/camera/mic/lock are unaffected. Saved as a durable memory
  (it recurs in the Teacher track: a Mac Teacher **accepting** local connections hits the same gate).

## LaunchAgent — borrowed-Mac safety verified
Enable created the plist (`~/Library/LaunchAgents/com.nty.classroomctrl.student.plist`, 586 B) with
`RunAtLoad=true` + `KeepAlive=false`; disable **removed it with no trace** (confirmed absent).
Combined with Quit leaving no relaunch loop, **nothing was left running on the borrowed Mac.**

## Evidence
- **Tester visual verification** (LIVE, bundle form) — all six groups above.
- **Structural (headless):** `--configtest` 14/14 · `--traytest` 10/10 · `--permtest` 13/13 ·
  `--launchagenttest` 16/16 (incl. disable→no-trace + dev-path-refused) · bundle build self-verify
  (`plutil`, stable id, `LSUIElement`, dylib `@rpath`-free) · bundle runtime smoke (dylib resolves,
  no `DllNotFound`).
- **Screenshots:** **not captured** — borrowed Mac (consistent with the M19–M22 precedent).

## What this confirms
- **Scenario 2 (shipped Windows Teacher + macOS Student) is now shippable** — the student runs as a
  real background menubar app (auto-start, config-persistent, onboarded) in bundle form, streaming
  screen (H.264) / camera / mic, playing teacher audio, and obeying an enforced kiosk lock with
  keystroke suppression — all over unchanged wire.
- **Pending only P35** (Developer ID signing + notarization) for wide deployment: an ad-hoc rebuild
  re-prompts TCC (a stable signature fixes it); a relaunch of the *same* built bundle keeps grants.
- **Shipped Windows repo untouched** across all 10 sessions.

## Pattern consistency
| | M17 MJPEG | M18 H.264 | M19 cam | M20 audio | M21 lock | M22 input | **M23 sysint** |
|---|---|---|---|---|---|---|---|
| Native API | ScreenCaptureKit | +VideoToolbox | AVCaptureSession | AVAudioEngine | AppKit shield | CGEventTap | **LaunchAgent / TCC / bundle** |
| Structural | `--streamtest` | `--streamtest-h264` | `--cameratest` | `--audiotest` | `--locktest` | `--inputtest` | **`--configtest`/`--traytest`/`--permtest`/`--launchagenttest`** |
| LIVE | Win visual | Win visual | Win visual | Win audible (3/4) | Win visual | Win visual | **Win visual, BUNDLE form** |

## Constraints honored
Sandbox + `native/` + `scripts/` + `tools/MockTeacher` only · `Shared.Wire` unchanged · shipped repo
untouched · screen/camera/audio/lock/input intact · T1–T27 PASS · per-sub-phase commits.
