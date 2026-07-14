# TT-5 Findings — core commands (lock/unlock + power) · COMPLETE (targeting corrected TT-6-D)

> ⚠️ **CORRECTION (2026-07-14, found TT-6-D):** TT-5-D was a **false pass** for per-student
> targeting. The Teacher-side send was correct (each command is `CreateTargeted` at the right
> `EndpointId`, on the reliable channel) — but the **Student-side receive filter was missing** in
> the macOS port. The shipped Windows Student runs an `IsForMe` filter in its Service
> (`ClassroomWorker.IsForMe`, **verified present** — Windows customers were never exposed); the
> port collapsed Service+Agent into one process (the Sandbox) and dropped that filter, so a
> targeted `LockScreen`/power/policy was acted on by **every** connected Mac student, not just the
> target. It was invisible in TT-5-D because the Mac student and BELL were tested in **separate
> runs** (BELL, a shipped Windows Student, filters correctly, so BELL-only tests looked fine).
> **Fixed in the TT-6-D receive-side-filter step** (`StudentEnvelopeFilter.IsForMe`, default-deny,
> guarded at the top of `ConnectionViewModel.Dispatch`; committed negative gates in
> `--selftest`/`--teacherselftest` for LockScreen + StudentStreamStart). TT-5's per-student
> commands are correct **as of that fix**. The design/scope findings below stand unchanged.

**Goal:** the Mac Teacher COMMANDS a student — lock/unlock (both platforms) and power
(logoff/restart/shutdown, Windows students). The full-circle interop: a macOS Teacher
sends the exact messages the macOS Student already receives (M15 wire, M21 lock).
**LIVE gate met, all sub-gates** (2026-07-14): a shipped **Windows Student** (BELL) locks
and logs off from the Mac Teacher; a **Mac Student** (Sandbox) is hard-locked (M21 kiosk)
and correctly shows power **disabled**. Constraints held: Teacher app + Teacher.Core only,
**Shared.Wire byte-unchanged**, **shipped Windows repo untouched**.

Sub-phases: **TT-5-A** investigation · **TT-5-B** command seam + platform gate + lock/unlock ·
**TT-5-C** confirm dialog + power (platform-gated) · **TT-5-D** LIVE · **TT-5-E** this close-out.

---

## 🔴 THE SEND-PATH FINDING — the phase's headline (a third shipped defect, found by investigation)
The shipped v1.2.1 "18/50 locked" fix set `reliable:true` on the **BULK** path
(`MainViewModel` 3790/3810/3887/3921/3941/4041) — but **every per-student context-menu
command still defaults to `reliable:false`**: `LockOneAsync` (1423), policy (1610/1631),
`PowerOneAsync` (1710), mic-mute (809). Those route through `_outbox` (DropOldest, cap 16) —
**the same lossy queue that carries the teacher's outbound screen-share frames.** A
per-student command issued while the teacher is screen-sharing competes with video frames
and **can be silently evicted.** Same bug class as v1.2.1, **half-fixed, still shipping.**

That is now **three defects investigation-first has surfaced in the shipped Windows
product** — the M20 teacher-mic `WaveInEvent` fragility, the TT-1 roster namespace-gap, and
this. All three affect **both** customers at 50 seats.

**The port fixes it:** `StudentCommandController` hardcodes `reliable:true` on every command,
and the gate **asserts the CHANNEL**, not just the send. This matters: a send-only assertion
("was a command sent?") **passes with the bug present** — the command *is* sent; it just
gets evicted later under load. The recording fake sink asserts
`sink.Calls.TrueForAll(c => c.Reliable)` — the exact property the shipped per-student path
violates. Same class of trap as the count-only marshal test and the 1×1-stub decode; this
time we assert the thing that actually distinguishes correct from broken.

**Windows-track follow-up logged** (candidate v1.2.x patch; we do NOT touch the shipped repo):
pass `reliable:true` on the per-student call sites 1423/1610/1631/1710/809.

## PLATFORM DETECTION — zero Shared.Wire change (decision #2 resolved to "the field already exists")
The Teacher decides which students can be *offered* power from the student's reported OS.
That OS is **already on the wire**: `HelloMessage.OsVersion` (Key 2), byte-identical in both
repos. The port was simply **discarding** it — `StudentRoster.Entry` received the whole
`HelloMessage` but kept only EndpointId/DisplayName/MachineName. TT-5 carries `OsVersion`
through `Entry → StudentTileViewModel → CanReceivePower`. **No wire change, no new field —
we stopped dropping an existing one.**

`StudentPlatform.CanReceivePower(os)` is **default-deny keyed on "Windows"**:
`!string.IsNullOrWhiteSpace(os) && os.Contains("Windows", OrdinalIgnoreCase)`. Power is
offered iff the student is Windows (the only platform that executes it today). Anything
unexpected — Linux, empty, null, a future platform — correctly gets **no** power actions.

### The probe that caught a wrong assumption (evidence over expectation)
I expected macOS to report `"Darwin …"` (older .NET behavior). **It doesn't** — a live probe
on this Mac showed the Mac Student's `RuntimeInformation.OSDescription` returns
**`"macOS 26.5.2"`** (and `Environment.OSVersion.VersionString` returns `"Unix 26.5.2"`).
A naïve `Contains("Darwin")` check would have **silently enabled power on Mac students** —
exactly the dead-action the platform gate exists to prevent. The default-deny-on-"Windows"
rule is robust to all of these strings because it keys on the one unambiguous marker: the
Windows contract *always* yields "Microsoft Windows NT …"; nothing else contains "Windows".

## 🔒 THE LOCK — STRONGER *and* SAFER than the shipped Windows lock
A LIVE observation crystallized why the Mac lock's design is correct. Locking a Mac Student
raises the M21 kiosk shield over **every display**, blocking Cmd+Tab, Force Quit, Spotlight,
and Mission Control. The **one** escape deliberately left open is **Cmd+Q**: a student who
quits the app gets out — but the teacher sees them **drop from the roster immediately** and
**re-locks them on reconnect.** *Escapable, but not undetectably.*

Contrast the **shipped Windows lock**, which is weaker in confinement and more dangerous in
recovery:

| | **macOS lock (M21, ours)** | **Shipped Windows lock** |
|---|---|---|
| Confinement | Blocks Cmd+Tab, Force Quit, Spotlight, Mission Control; covers **every** display | Closeable with **Alt+F4**, or by killing the Agent from **Task Manager** |
| Escape hatch | **Cmd+Q** only — and the teacher **sees the drop** and re-locks on reconnect | **Prints its escape hotkey (Ctrl+Shift+Alt+U) on the lock screen itself** |
| Teacher-loss behavior | **Auto-unlocks** — 45 s disconnect dead-man + 30-min hard cap → a student is **never stranded** | **Never auto-unlocks** on teacher loss → a student can be **stranded indefinitely** |

The recovery paths (Cmd+Q, 45 s disconnect grace, process-kill, 30-min cap) are precisely
what make the hard lock **safe to deploy**. A lock with **no way out** is the dangerous
design — which is what the shipped Windows lock gets wrong in the *other* direction. So
"Cmd+Q escapes the lock" is **not a defect** — it is the safety valve, paired with immediate
detectability. Anyone reading that line later should read it as *design working as intended*.

## CANCEL-AS-DEFAULT confirm dialog — deliberately better than shipped
Avalonia has no `MessageBox`, so the three power actions confirm via a small modal
(`ConfirmDialog`). The shipped WPF `MessageBox.Show(…YesNo, Warning)` **defaulted to Yes**.
Ours makes **Cancel the default + the Escape button**, with Confirm requiring an explicit
click — so a **stray Enter cancels a shutdown** rather than executing it. A misclicked
shutdown across 50 seats is expensive and irreversible; the safer default is the right call.
Lock/unlock are reversible and are **not** confirmed (matches shipped).

## THE COMMAND SEAM — additive, mirrors TT-3 exactly
`StudentCommandController` + `IStudentCommandSink` are the command-side analog of TT-3's
`ScreenViewController` + `IStudentStreamSource`. The controller is the single UI entry point
(`ExecuteAsync(endpointId, command, name)`); it owns the two policies that are easy to get
wrong in a view: **the send-path rule** (`reliable:true`, hardcoded) and **confirmation**
(power gated, lock/unlock never prompt). `TeacherSession` implements the sink as a thin
pass-through to the already-ported `ControlServer.LockOneAsync`/`PowerOneAsync`. The context
menu on `StudentCard` raises `CommandRequested → MainWindow → App → controller`, the same
event flow as TT-3's `DoubleTapped → StudentActivated`. The tile VM stayed a pure observable;
the app wires it. Command targeting is by **EndpointId** (no positional bug — the roster gap
was removal-only, fixed in TT-1-D).

## Verification (all green, on this Mac)
- **TT5Gate (scratchpad fake-sink) 23/23** — asserts the **channel** (`reliable:true` for
  Lock/Unlock/Logoff/Restart/Shutdown; **none lossy**), the **platform gate**
  (Windows→offer; macOS/Unix/Linux/empty/null→deny; case-insensitive), and the **confirm
  policy** (power YES→sent / NO→**nothing sent** / prompted once; lock/unlock **never**
  prompt). *Path: `scratchpad/TT5Gate` → `dotnet run`. References the real Avalonia.Teacher
  app so it drives the PRODUCTION controller/sink/platform — no Avalonia UI initialized.*
- **`MockStudent --teacherselftest` 22/22** — **+2 new permanent checks**: real Teacher.Core
  → real WireClient receives `LockScreen` **and** `ForceLogoff` **targeted at its EndpointId**
  (end-to-end delivery + targeting over the real transport). All 20 prior
  roster/ownership/stale-sweep/teardown checks still green.
- **`MockStudent --selftest` PASS** — the Student still receives `LockScreen` (receive-path
  non-regression; complements the send-path check above).
- **T1–T27 27/27** — wire frozen. Full solution + Teacher + Sandbox builds: **0 errors**
  (compiled bindings inside the ContextMenu resolve). Shared.Wire byte-unchanged; Student
  track (Sandbox) does not regress; shipped Windows repo untouched.
- **LIVE (TT-5-D, 2026-07-14):** all sub-gates — see `docs/TT-5-LIVE-CONFIRMATION.md`.

### Honest note on the reliable-channel assertion
The reliable-vs-lossy **channel choice is not observable over a quiescent loopback** (both
channels deliver a single un-bursted frame). So `--teacherselftest` asserts **delivery +
targeting** (committed/permanent), and **TT5Gate asserts the reliable *flag*** (the fix the
shipped per-student path omits). We assert the **fix is chosen**, not the bug's failure mode
(eviction under burst). That is the correct level — the shipped bug was "forgot the flag."

## Honest gaps (not blockers for TT-5)
- **TT5Gate is a scratchpad gate** (throwaway, per the TT-3/TT-4 precedent), so the
  reliable-**flag** guard has **no committed home** — only `--teacherselftest`'s delivery +
  targeting is permanent. **Recommended promotion (close-out):** move the
  `StudentCommandController`/`IStudentCommandSink`/`StudentPlatform` seam down into
  **Teacher.Core** (pure logic, no Avalonia dependency), then add the `reliable==true`
  fake-sink assertion into `--teacherselftest` for permanent regression protection of the
  channel choice. Flagged for approval — it's a placement fork that touches committed files.
- **Power is a no-op on Mac students** — the Sandbox has **no power handler**; the command is
  silently ignored (Student-track gap). The Teacher correctly **disables** power for Mac
  tiles (visible, with a tooltip), so no dead action ships; power is LIVE-testable only
  against a Windows student. → Student-track follow-up: **macOS power execution.**
- **Policy deferred** — needs a policy-editor dialog (USB/optical/printing + process/host
  lists + duration) **and** macOS policy enforcement, which doesn't exist (the Mac Student
  receives `PolicyApply` and shows a badge but **enforces nothing**). A reflect-only badge is
  not a feature; pair the editor with the Student-track enforcement in its own phase.
  → Student-track follow-up: **macOS policy enforcement.**
- **Mic monitor deferred** — not a context-menu action, and useless until the teacher can
  *hear* a student (TT-9 audio).
- The **ConfirmDialog's Yes/No surface** is LIVE-verified only (thin glue); the confirm
  *policy* (which matters for safety) is headless-gated at the controller.
- **Co-located-testing caveat:** locking a Mac Student that shares a machine with the Teacher
  covers the tester's whole screen, including the Teacher window — you cannot click Unlock.
  Recover via **Cmd+Q** (instant) or wait out the 45 s dead-man. This is an **artifact of
  single-machine testing, not a product issue** — in a real classroom the teacher is on a
  different Mac. Recorded so the next tester doesn't mistake it for a break.

## What TT-5 unlocks · next
The Mac Teacher can now **see** a student's screen (TT-3/TT-4) **and command** them:
lock/unlock enforced on **both** Mac (hard kiosk) and Windows (soft overlay) students, and
power (logoff/restart/shutdown) on Windows students — every command on the reliable channel,
platform-gated, power confirmed. **Next: TT-6** (multi-select + bulk actions, v1.2) — the
selection model + the floating bulk toolbar, reusing this command path (bulk already routes
`reliable:true` in the shipped code, so TT-6 inherits the correct channel from the start).
