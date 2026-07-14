# TT-5 LIVE Confirmation — the Mac Teacher commands students (lock/unlock + power)

**Date:** 2026-07-14 · **Milestone:** TT-5 (core commands — lock/unlock + power) ·
**Gate:** *right-click a student tile → lock/unlock (both platforms) and power (Windows) →
the command takes effect on the student.*

> ⚠️ **CORRECTION (2026-07-14, found TT-6-D):** this run was a **false pass** for per-student
> *targeting*. Each sub-gate below happened with only ONE Mac student (or BELL) connected, so a
> missing Student-side target filter went unseen: a command targeted at one student was acted on
> by **every** connected Mac student. The Teacher send was correct; the Mac Student's `IsForMe`
> filter was missing in the port (the shipped Windows Student has it — verified). **Fixed in the
> TT-6-D receive-side-filter step**, whose re-LIVE gate is the standing test: **2 Mac students +
> BELL connected at once**, command targeted at one → only that one acts. The commands below do
> take effect on the targeted student; what was unverified here was that they *don't* also hit the
> others.

## Result: PASS — all sub-gates (see correction above re: targeting)

### ✅ Sub-gate 1 — Windows Student (BELL), scenario 3: power + lock, confirmed
A real, shipped, **unmodified Windows Student** ("BELL", v1.2) driven from the Mac Teacher:
- Power items **enabled** on BELL's tile (CanReceivePower from `HelloMessage.OsVersion`
  containing "Windows").
- **Lock screen** → BELL's overlay appears; **Unlock screen** → gone.
- **Log off** → confirm dialog → **Cancel → nothing happens** (verifies the safe default);
  → Log off again → **Confirm → BELL logs off.** The full confirm→execute path, both branches.

### ✅ Sub-gate 2 — Mac Student (Sandbox), scenario 4 (customer B): hard lock + platform gate
A **Mac Student** (the Sandbox app) driven from the Mac Teacher:
- **Lock screen** → the **M21 kiosk shield is enforced** — the strong full-circle deliverable
  for customer B (a macOS Teacher hard-locks a macOS Student over the wire the Student already
  speaks).
- Power items **greyed out with the tooltip** ("Power actions aren't available for macOS
  students yet") — **decision #2 working live**: `CanReceivePower` correctly denies power to a
  Mac student (no macOS power handler yet), while keeping the capability visible. No dead
  action ships.

### ✅ Sub-gate 3 — the reliable channel under load
Commands issued **while the Teacher was actively screen-streaming** landed — the exact
condition the shipped v1.2.1-class per-student lossy bug fails (a command evicted from the
DropOldest video queue). The port's `reliable:true` routing holds.

## LIVE observation recorded (design working as intended, not a gap)
Locking a Mac Student **co-located on the Teacher's own Mac** covered the whole screen —
including the Teacher window — so Unlock was unreachable from that machine. Recovered by
cutting Wi-Fi and waiting the 45 s dead-man; **Cmd+Q would have worked instantly** (the one
deliberately-unblocked escape, M22). This is a **single-machine-testing artifact**, not a
product issue — in a real classroom the teacher is on a different Mac. It also demonstrated
the lock's safety design: escapable (Cmd+Q), detectable (immediate roster drop + re-lock on
reconnect), and self-healing (45 s / 30-min dead-man). See the lock comparison in
`docs/TT-5-FINDINGS.md` — our lock is both **stronger** (covers every display; blocks
Cmd+Tab/Force-Quit/Spotlight/Mission-Control) and **safer** (auto-unlocks on teacher loss)
than the shipped Windows lock (which prints its escape hotkey and never auto-unlocks).

## Environment
- **Teacher:** MacBook Air (Apple Silicon), `Avalonia.Teacher` via `dotnet run` →
  `TeacherSession` hosting `Teacher.Core` on `0.0.0.0:7777`. No Screen Recording needed
  (commands don't capture). `dotnet run` inherited Terminal's local-network grant (no prompt).
- **Sub-gate 1 student:** a real Windows PC ("BELL") running shipped v1.2, unmodified, pointed
  at the Mac's LAN IP.
- **Sub-gate 2 student:** the Mac Student (Sandbox), pointed at `127.0.0.1` (co-located).

## Headless companions (committed / scratchpad gates)
- **`MockStudent --teacherselftest` 22/22** (committed) — incl. the 2 new end-to-end
  command-delivery checks (real Teacher.Core → real WireClient receives `LockScreen` +
  `ForceLogoff` targeted at its EndpointId).
- **`MockStudent --selftest` PASS** (committed) — Student receives `LockScreen` (receive path).
- **TT5Gate 23/23** (scratchpad) — reliable-channel + platform-gate + confirm-policy assertions.
- **T1–T27 27/27**; full solution + Teacher + Sandbox builds 0 errors.

## Pattern consistency
Every milestone M15–M23 and TT-1…TT-4 had a LIVE gate that caught what headless tests
couldn't. TT-5 holds it: the headless gates prove the command wiring (reliable channel,
platform gate, confirm policy) and end-to-end delivery; **this LIVE run proves the commands
take real effect** — a Windows PC locks and logs off, a Mac is hard-locked, and the platform
gate is visibly correct — and surfaced the co-located-lockout observation that only shows up
on real hardware. The Mac Teacher can now **see and command** students.
