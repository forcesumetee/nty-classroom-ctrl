# Phase 31 Findings — Input-hook keystroke guard (Milestone 22, Mac-side)

**Goal:** close the two macOS-specific escape routes the Phase 30 kiosk shield can't reach on its
own — **Spotlight-launch (Cmd+Space)** and **Mission Control** — by intercepting a small, explicit
set of system keyboard shortcuts while the teacher lock is up, using a `CGEventTap`. Additive to the
lock; no wire change, no shipped-repo change.
**Result:** ✅ **LIVE-CONFIRMED (2026-07-13)** — under a lock from the shipped Windows Teacher, a
macOS student suppresses Spotlight + Mission Control when Accessibility is granted, degrades
gracefully (lock unaffected) when it is denied, and releases the tap on every safety path. See
`docs/PHASE-31-LIVE-CONFIRMATION.md`.

**Sub-phases:** 31-A investigation · 31-B native `CGEventTap` + fail-open safety gate · 31-C
`LockService` injectable input-guard backend + wire-in · 31-D `--locktest` guard-lifecycle
extension · 31-E LIVE · 31-F findings + docs.

## Investigation (31-A) — the safety framing
A `CGEventTap` has a **bigger blast radius than the Phase 30 shield**: a wedged tap could, in
principle, freeze the keyboard machine-wide. The investigation established that macOS makes the
wedged state **unreachable**, so the guard is safe to build:
- **Fails OPEN.** A session tap that doesn't return from its callback in time is **auto-disabled by
  the OS**, which posts `kCGEventTapDisabledByTimeout` — and at that point keystrokes **already flow
  untouched**. We re-enable on that event to resume guarding, but a stuck tap cannot strand keys.
- **Process-kill releases it.** The tap is a `CFMachPort` owned by the process; process death tears
  it down (OS-enforced) — the same ownership guarantee as the M21 shield's presentation options.
- **Smaller blast radius than the Windows `WH_KEYBOARD_LL` hook** the shipped exam-kiosk uses.

**Product decisions (locked via AskUserQuestion):**
1. **Thorough suppression list** — Spotlight (Cmd+Space, Cmd+Opt+Space) + Mission Control / Exposé
   (F3, Ctrl+Up/Down) + Spaces (Ctrl+Left/Right) + **Cmd+Tab / Cmd+`** belt-and-suspenders.
2. **Cmd+Q stays UNBLOCKED** — quit = process death = dead-man auto-unlock (safe), and the tester's
   escape hatch. Volume/brightness/media and screenshots also pass through.
3. **Graceful degrade if Accessibility denied** — the lock (shield + presentation options) works
   with **zero** Accessibility; the tap is additive and never blocks enforcement.

## macOS mechanics (31-B) — CGEventTap on a dedicated run-loop thread
- **Tap:** `CGEvent.tapCreate(tap: .cgSessionEventTap, place: .headInsertEventTap,
  options: .defaultTap, …)` for `keyDown | flagsChanged`. `.defaultTap` (not listen-only) is what
  lets the callback **suppress** by returning `nil`.
- **Dedicated thread.** The tap's `CFRunLoopSource` runs on its **own `CFRunLoop` thread**, not the
  AppKit main loop — so a busy main thread can never stall guarding. `stop()` is **bounded**
  (`CFRunLoopStop` + wake, then joins the thread with a **3 s** ceiling) so uninstall is guaranteed
  and never hangs, even mid-stall.
- **Exact-modifier suppression rules.** Each rule is `(keycode, exact major-modifier set)` so we
  don't over-suppress (e.g. Cmd+Ctrl+Space = emoji picker is a different combo than Cmd+Space and is
  left alone). Callback is **allocation-light + lock-free** so the OS never disables us spuriously.
- **Note:** the dedicated hardware Mission-Control key can arrive as an `NSSystemDefined` event, not
  a `keyDown`, so a keyDown tap can't catch that specific key — the Ctrl+Arrow shortcuts cover the
  same actions and ARE keyDown. Documented residual; not user-visible in practice.

## The fail-open keystone (31-B) — proven, not asserted
The safety gate for the whole phase was **demonstrating** fail-open, not just designing for it:
- `--inputtest` arms a deliberate stall in the callback (a TEST-ONLY lever, off in production),
  drives events through the now-slow tap, and observes the **real OS watchdog** auto-disable it
  (`disabled_count ≥ 1`) — a window where keystrokes flowed untouched — after which the callback
  re-enables. **4/4 runs** (3 back-to-back + 1 post-kill), reliably one auto-disable each.
- **Diagnosis honesty (the 29-G discipline):** the first demo attempt failed two checks. Traced to a
  **cross-thread test-hook bug** — `CGEventTapEnable` / a mutable stall flag set from the P/Invoke
  thread don't apply against the tap's idle run loop. Fix: arm the stall **before** `start()` (thread
  creation is the memory barrier) so the real watchdog fires; and the flaky deterministic hook was
  **removed** rather than shipped as unreliable test surface in a security dylib. The mechanism
  (production fail-open) was correct; only the instrumentation was finicky.

## Injectable input-guard backend (31-C) — the 30-E pattern, again
`LockService` gets a **5-delegate input-guard backend** (`trusted?` / `prompt` / `install` /
`remove` / `isActive`), exactly parallel to the 30-E shield backend: production wires the native
`nty_accessibility_*` / `nty_input_guard_*` symbols; tests inject **flags** (so automated tests
install **zero real taps**). Lifecycle is tied to lock state at the show/hide choke points:
- **Shield-up → `InstallGuardIfPossible` AFTER the shield is up:** if Accessibility-trusted, arm the
  tap; else log + **prompt once** and proceed (graceful degrade). Arms on the **next** lock — no
  retro-arm of an already-active lock.
- **Every unlock path → `RemoveGuard` FIRST** (never leave the guard without the shield),
  unconditional / safe-when-idle: explicit unlock, 45 s grace fire, 30 min cap fire. Process-kill is
  OS-enforced (no code).

## Structural proof (31-B/D) — headless, safe, ZERO real taps in `--locktest`
- **`--inputtest` (real tap, needs Accessibility on the test binary):** suppression (Ctrl+Left ×3
  swallowed), **live fail-open** (OS auto-disable + re-enable), **recovery** (guards again after the
  cycles), **guaranteed uninstall** (try/finally + bounded stop), and the **graceful-degrade** path
  when untrusted (`start()` → -1, no tap). `--inputhold` holds a real tap on a bounded timer as a
  `kill -9` target for the process-kill release demo.
- **`--locktest` (flag backend, ZERO real taps):** the guard lifecycle proven with the same CASE
  rigor as the 30-E dead-man cases — **21/21**:
  - **A** blip → guard installed (trusted), HELD through reconnect, RELEASED on explicit unlock;
  - **B** teacher-death → guard RELEASED on grace fire;
  - **C** no-reset continuous window → guard RELEASED on grace fire;
  - **D** (new) → guard RELEASED on max-duration cap fire (no disconnect);
  - **E** denied → shield UP, guard NOT installed, prompted once, clean unlock (graceful degrade).
  Every dead-man path (explicit / grace / cap) releases the guard, not just the happy path.

## LIVE (31-E, 2026-07-13) — ✅ CONFIRMED
Shipped Windows Teacher v1.2 (unmodified), Mac Sandbox on a **borrowed Mac**, iPhone hotspot. Tester
visual verification, all four groups PASS: **suppress** (grant → Spotlight + Mission Control blocked;
unlock → they work again), **graceful degrade** (deny → lock still fully works, residuals stay open),
**kill-release** (kill → shield + tap released, keyboard recovered), **grace-release** (45 s
disconnect → shield + tap released). Screenshots not captured (borrowed Mac). Full writeup:
`docs/PHASE-31-LIVE-CONFIRMATION.md`.

## Feasibility / scope
- **Zero wire changes** ✅ · **zero shipped-repo changes** ✅ · Accessibility **optional** (graceful
  degrade) ✅. The **two Phase 30 residuals (Spotlight, Mission Control) are now CLOSED.**
- Native surface is **purely additive** (`Sources/Input.swift`; existing screen/camera/audio/lock
  Swift sources byte-unchanged). Interop template (§20) proven a **seventh** time.

## Constraints honored
Sandbox + `native/` + `tools/MockTeacher` only · `Shared.Wire` unchanged · shipped repo untouched ·
screen (M17/M18) + camera (M19) + audio (M20) + lock (M21) intact · per-sub-phase commits · T1–T27 PASS.

## Deferred
Multi-display shield validation on owned hardware (M21 gap, unchanged) · exam-kiosk lockdown
(`QuizStart 0x0700`, a separate feature) · teacher-custom lock message (Windows payload is empty) ·
the hardware Mission-Control key's `NSSystemDefined` variant (covered functionally by Ctrl+Arrows).
