# Phase 30 Findings — Screen-lock enforcement (Milestone 21, Mac-side)

**Goal:** enforce the teacher screen-lock on macOS (the `LockScreen` envelope has arrived
LIVE since M15 but was only *reflected*, not enforced), over existing envelopes — no
shipped-repo change, no wire change.
**Result:** ✅ Mac-side complete + verified headless — the native shield + kiosk options
render (30-B harness, `presentationOptions` raw = 506), and `MockTeacher --locktest`
proves all three dead-man grace cases. **LIVE Windows test = the remaining human step (30-F).**

**Sub-phases:** 30-A investigation · 30-B native shield + kiosk options · 30-C LockService
+ wire-in + four-layer dead-man · 30-D shield visual · 30-E `--locktest` · 30-G docs.

## Investigation (30-A) — the reframing discovery
The shipped Windows teacher-lock is a **soft, cooperative overlay**, not a kiosk. This
reframed the whole phase (traced to `MainWindow.xaml.cs:518–540` + `LockOverlayWindow`):
- A **single** Topmost/Maximized/borderless WPF window, **primary monitor only** (no
  `Screen.AllScreens` → second monitor fully usable).
- **No keyboard hook, no `BlockInput`, no taskbar hide, not elevated.** Alt+Tab/Win/Ctrl+Esc
  all work.
- **Deliberate escape hatches:** `Ctrl+Shift+Alt+U` (**printed on the lock screen**) and
  `Alt+F4` (no `Closing` guard).
- **NO auto-unlock** — no heartbeat/timeout/disconnect-dismiss; stays up until an explicit
  `UnlockScreen`. *(The one genuinely dangerous property — a port must fix it.)*
- Fixed visual (payload empty; `SetCustomMessage` is dead code → no teacher-custom message).
- The keyboard-hook kiosk (`WH_KEYBOARD_LL`, taskbar hide, 100 ms topmost watchdog) exists
  **only** for the separate *exam* feature (`QuizStart 0x0700`) — out of scope.

**Product decisions (locked):** HARD lock (exceed Windows) · NO student-facing escape hatch ·
45 s silent disconnect-grace. All with **zero Accessibility permission**.

## macOS enforcement (30-B) — shield + kiosk options, NO Accessibility
- **Shield:** a borderless `NSWindow` at `CGShieldingWindowLevel()` on **every `NSScreen`**
  (above Dock/menu-bar/screensaver), `collectionBehavior = [.canJoinAllSpaces, .stationary,
  .fullScreenAuxiliary]`, `ShieldWindow.canBecomeKey = true` (keystrokes land on it), rebuilt
  on `didChangeScreenParameters` (hotplug).
- **Kiosk levers:** `NSApplicationPresentationOptions` — the harness confirmed the raw value
  **506** applied on show, cleared to **0** on hide:

  | Flag | raw | blocks |
  |---|---|---|
  | `.hideDock` | 2 | Dock |
  | `.hideMenuBar` | 8 | menu bar |
  | `.disableAppleMenu` | 16 | Apple menu |
  | `.disableProcessSwitching` | 32 | **Cmd+Tab** |
  | `.disableForceQuit` | 64 | **Cmd+Opt+Esc** |
  | `.disableSessionTermination` | 128 | logout/shutdown menu |
  | `.disableHideApplication` | 256 | Cmd+H |
  | **sum** | **506** | |

- **No Accessibility permission** for either the shield or the options. `CGEventTap` (which
  *does* need Accessibility) is **Phase 31**, only to close the two residuals below.
- **Caveat:** presentation options hold only while the app is **frontmost** → `NSApp.activate`
  on show + **re-assert on `didResignActive`** + `NSWorkspace.didWake`.

## Escape-route comparison (Windows teacher-lock vs our macOS hard lock)
| Escape route | Windows student | macOS (this design) |
|---|---|---|
| Alt+Tab / Cmd+Tab | ❌ works | ✅ blocked (`.disableProcessSwitching`) |
| Ctrl+Alt+Del | unblockable → Task Mgr → kill | n/a (no SAS on macOS) |
| Cmd+Opt+Esc (Force Quit) | n/a | ✅ blocked (`.disableForceQuit`) |
| Spotlight (Cmd+Space) | n/a (Win/Start works) | ⚠ **residual** → Phase 31 (CGEventTap) |
| Mission Control (F3) | n/a | ⚠ **residual** → Phase 31 |
| Kill process (Activity Monitor) | ✅ (Task Mgr) → overlay dies | ⚠ harder (app-switch blocked); if killed → shield dies + options auto-released = **safe unlock** |
| Disconnect network | ❌ **stays locked forever** | ✅ auto-unlock after **45 s grace** (silent) |
| Second monitor | ❌ **fully usable** | ✅ **covered** (per-`NSScreen`) |
| Dock / menu bar / taskbar | ❌ taskbar not hidden | ✅ hidden |
| Force power off | ✅ tolerated | ✅ tolerated — same |
| Close lid / sleep | no re-assert (may fall behind) | ✅ **re-assert on wake** |
| Boot to recovery | ✅ tolerated | ✅ tolerated (Cmd+R) — same |

**Verdict (blunt):** the macOS hard lock is **comparable-to-stronger** than what Windows
tolerates — it blocks the app-switch/force-quit/dock routes Windows leaves open and covers
all displays, leaving only two macOS-specific residuals (Spotlight-launch, Mission Control)
that Phase 31 closes; the unblockable routes (power-off, recovery) are identical on both.

## The four-layer dead-man switch (30-C) — safer than Windows
Windows never auto-unlocks (permanent-stranding risk). Ours fails safe on four layers:
1. **Process-kill** — the shield + options are owned by THIS process, so killing it releases
   the presentation options (OS-enforced) + drops the windows = **auto-unlock**. By construction.
2. **Disconnect 45 s grace** — see the state table below.
3. **Max-duration cap** — an independent **30 min** timer from lock-show → unlock regardless
   of connection (backstop against a wedged-but-alive teacher). Counts *since-lock-shown*
   (vs grace's *since-disconnect*) — no collision.
4. **Sleep/wake re-assert** — native `didWake` re-asserts, and native's `active` flag tracks
   the .NET `_locked` (single source of truth: .NET decides locked/unlocked, native executes).

### Grace-timer state-transition table (the blip-vs-death-vs-no-reset logic)
| Transition (while locked) | Action | Outcome |
|---|---|---|
| `Connected → Reconnecting` | **start** one 45 s timer | grace begins |
| `Reconnecting → Connected` | **cancel** | lock **HOLDS** ← Wi-Fi blip |
| `Reconnecting → Disconnected` | timer **keeps running** (`StartGraceIfIdle` no-ops if a timer exists) | **not restarted** — continuous window |
| `Disconnected → Connected` | **cancel** | lock **HOLDS** |
| 45 s, never `Connected` | **fire** → unlock | **auto-unlock** ← teacher-death |

`--locktest` proved all three (grace=3 s): **A** blip HELD (past the original window), **B**
teacher-death UNLOCKED, **C** no-reset (shown at t=2.6 s, unlocked by t=3.5 s → fired from
first departure not from Disconnected; the log shows exactly **one** `grace start`).

### Why 45 s
It must exceed the WireClient reconnect/heartbeat window (Ping every 5 s; a transient blip
drops TCP then auto-reconnects within ~10–20 s). 45 s clears that, so **a brief blip during a
genuine lock does NOT unlock** (the reconnect cancels the pending timer); yet real teacher-death
frees students **within a minute**. The disconnect exploit is self-limiting: yanking Wi-Fi buys
45 s of black screen → then you're merely **offline** (no reward), and reconnecting → teacher
re-locks.

### Silent grace
No countdown, no hint on the shield — a visible timer would *teach* the exploit. The only trace
is the dev wire-log (`grace start/cancel/fire`), never the student.

## Injectable shield backend (30-E) — test vs native
The native full-screen shield **hangs a console main thread** (no AppKit run loop) and would
be a screen-takeover risk in automated tests. So `LockService` takes a **shield backend**
(production ctor = native `nty_lock_*`; test ctor = a flag-tracking backend). `--locktest` drives
the **real** LockService (real timers, real transitions) with **zero AppKit** → the dead-man
logic is proven headlessly and safely; the native **rendering** is proven separately (30-B
auto-hide harness + 30-F LIVE). Clean separation of "logic under test" from "OS window."

## Feasibility / scope
- **Zero wire changes** ✅ · **zero shipped-repo changes** ✅ · **zero Accessibility** ✅.
- **Out of scope → Phase 31:** `CGEventTap` keystroke interception to close the Spotlight-launch
  + Mission-Control residuals (needs Accessibility). The exam kiosk (`QuizStart 0x0700`) is a
  separate feature.
- **Multi-display:** code-correct (per-`NSScreen` + hotplug); **single-display validated** here
  — the multi-monitor path is exercised LIVE only if an external display is attached (30-F).

## Verification (Mac, no Windows box)
- **30-B harness:** shield shows (`presentationOptions` = 506, live clock) + auto-hides
  (options → 0), clean exit. Guaranteed-auto-hide (never strands the machine).
- **`--locktest`:** all 3 grace cases PASS (blip/death/no-reset) + explicit unlock.
- T1–T27 PASS · `--selftest`/`--streamtest`/`--streamtest-h264`/`--cameratest`/`--audiotest`
  still PASS (native screen/camera/audio unaffected by the AppKit+Lock additions).

## LIVE Windows test (30-F — user-run) — awaiting result
Teacher locks Mac → shield on all displays; Cmd+Tab + Cmd+Opt+Esc blocked; steal-focus →
`didResignActive` re-assert; **disconnect → 45 s → auto-unlock** (real wall-clock); explicit
`UnlockScreen` → gone; **kill Sandbox process while locked → shield gone** (safety backstop);
sleep/wake → re-assert; multi-display if an external monitor is attached.

## Constraints honored
Sandbox + `native/` + `tools/MockTeacher` only · `Shared.Wire` unchanged · shipped repo
untouched · screen/camera/audio intact · per-sub-phase commits · T1–T27 PASS.

## Deferred
Phase 31 CGEventTap (Spotlight/Mission-Control residuals, power-key) · exam-kiosk lockdown
(`QuizStart 0x0700`) · teacher-custom lock message (Windows payload is empty).
