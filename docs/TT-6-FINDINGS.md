# TT-6 Findings — multi-select + bulk actions · COMPLETE (LIVE-confirmed) — and the wrong-blast-radius bug it exposed

**Goal:** select N students and act on them at once (bulk lock/unlock + power), the v1.2
multi-select feature. **Delivered + LIVE-confirmed (2026-07-14)** — but the LIVE gate
**found a wrong-blast-radius bug in the Student track** (a track we'd called complete), which
became the most instructive failure of the project. This close-out leads with that.

Sub-phases: **TT-6-A** investigation · **TT-6-B** selection model (macOS click idiom) ·
**TT-6-C** bulk toolbar + `ExecuteBulkAsync` · **TT-6-D** LIVE — **found the bug** → the
receive-side-filter **fix** → **re-LIVE PASS** · **TT-6-E** this close-out.
Prereq landed first: the **TT5Gate promotion** (the reliable-channel guard moved into the
committed `--teacherselftest`).

---

## 🔴 THE WRONG-BLAST-RADIUS BUG — the phase's headline
### What it was
The shipped Windows Student is **two processes**: a **Service** (`ClassroomWorker`) that runs
`IsForMe(env)` and **drops non-matching targeted envelopes**, and an **Agent** that acts,
*trusting the Service already filtered*. The macOS port collapsed these into **one process**
(the Sandbox) and **the `IsForMe` filter was never ported.** `ConnectionViewModel.Dispatch`
acted on `env.Type` **unconditionally**. The Teacher targets correctly (`CreateTargeted` sets
`TargetEndpointId`) and **broadcasts to all peers relying on client-side filtering** (the
protocol); with no client filter, every targeted command hit every Mac student.

### The blast radius
**Every** targeted command reached **every** connected Mac student — lock/unlock, power,
policy, DM, mic, and worst, **`StudentStreamStart`**: "view student A's screen" would have made
**every Mac student start capturing and streaming its screen to the teacher.** Wrong-lock is
visible and annoying; **wrong-screen-capture is a silent privacy failure.** At 50 Mac seats,
"lock these 3" locked the room, and "watch one screen" watched all of them.

### Why it hid for three phases
TT-3, TT-4, and TT-5 were all LIVE-tested with a **single Mac student** connected — and **with
one student, a whole-class broadcast and a targeted send are indistinguishable.** BELL (a
shipped Windows Student) **filters correctly**, so any BELL-only test also looked right.
**TT-5-D was a false pass**: the Mac student and BELL were exercised in *separate runs*, never
both at once.

### 🎓 THE TESTING LESSON (the part to remember)
Our gates asserted **"the command is targeted at the right `EndpointId`"** and **"the command
routes `reliable:true`"** — both true, both passing, and **neither could catch this.** The
property we never asserted was the **negative**: *a student who is NOT the target does NOT act.*
**You cannot test that with one student connected.**

This is the same class as the count-only marshal test and the 1×1-stub decode — *a gate that
passes with the bug present* — but this time it **escaped into a LIVE gate too**, because the
LIVE *configuration itself* (one student) was insufficient. Asserting the happy path (the right
student got it) is not enough; you must assert the negative (the wrong students didn't), and the
test environment must be able to express it.

> **STANDING RULE (added to the LIVE-gate checklist for every future phase):** any per-student
> command or per-student routing feature MUST be LIVE-tested with **at least two students
> connected, one of them not the target.** One-student LIVE cannot distinguish targeted from
> broadcast. (See also the committed negative gates below — assert *only the target acts*.)

### The fix
`StudentEnvelopeFilter.IsForMe` (Sandbox.Services) — **default-deny**, mirroring the shipped
`ClassroomWorker.IsForMe`: pass iff `TargetEndpointId == me`, or `TargetGroupId == my room`
(inert until breakout ports), or `TargetEndpointId == Empty` whole-class broadcast; **everything
else drops** (an unanticipated shape fails **closed**, never blasts). A **single guard at the top
of `ConnectionViewModel.Dispatch`** (after the high-freq audio bypass) closes the **entire
targeted-command class** at once. Verified: broadcasts (chat-all, conference) and Pong carry an
Empty target → still pass.

### Shipped Windows: VERIFIED CLEAN (a recorded negative result)
The shipped Windows Student **does** filter — `ClassroomWorker.IsForMe` (Service, applied
per-command). **Customer A (Windows) was never exposed.** This was **our port omission**, not a
shipped defect. Recorded here + in the session summary so nobody re-investigates it.

---

## TT-6 proper (the multi-select feature)
- **macOS click idiom — a DELIBERATE divergence from shipped.** Plain click = select only this;
  ⌘-click = toggle; Shift-click = range from the anchor; ⌘A = select all; Esc = clear. Shipped
  toggles on plain click (a Windows-ism); the port follows Finder/Photos/Mail because customer B
  is a Mac shop. Intentional platform choice, recorded so it's not read later as an oversight.
- **Bulk routes through `StudentCommandController.ExecuteBulkAsync`** — the same controller a
  single command uses. So the promoted reliable-channel guard **covers bulk by construction**: a
  bulk command **cannot go lossy without bypassing the guarded path.** The gate asserts bulk
  routes `reliable:true`.
- **Platform-aware bulk power (decision A):** bulk power **skips non-Windows students** and
  reports the skip **visibly** — "Logged off X · Y macOS skipped (not supported)". A teacher must
  never believe a command reached students it didn't. Enabled while ≥1 selected student can power;
  disabled only when none can.
- **Confirmation (decision B):** one **count-aware** confirm for bulk power ("Shut down 47
  students?"), **Cancel as the default + Escape** (a stray Enter cancels), and **required even for
  a single student** on power (the misclick cost is asymmetric — a click vs a classroom).
- **Head-of-line note (TT-6-A):** bulk is **sequential-awaited** (matches shipped), paced by the
  reliable channel at the slowest peer's drain rate — a stalled student paces the whole batch,
  which is exactly what the **"Sending i of N"** progress surfaces. Correctness over speed.
  Mitigations noted but **not pre-built**: per-command timeout · parallelize the outer loop · true
  per-socket targeting (instead of broadcast+filter).
- **Deferred (no dead buttons — the TT-2 principle):** bulk **policy** (needs the editor +
  macOS enforcement), **mic** (needs TT-9 audio), **file** (needs TT-12). The toolbar shows only
  the actions that work; each is added when its feature lands.

## Verification (all green)
- **`--teacherselftest` 65 → 74** — added the receive-side filter gates: `IsForMe` unit (Lock +
  StudentStreamStart + broadcast + **default-deny** shapes) **and** a **3-client scenario** (real
  Teacher targets A → bystander C **drops both** Lock and StudentStreamStart; IsForMe false for C,
  true for A). Still covers the reliable channel (TT-5) + selection model (TT-6-B) + bulk (TT-6-C).
- **`--selftest` 6 → 9** — real targeted frames: Lock + StudentStreamStart at **another** → filter
  FALSE (would not act); at **me** → TRUE.
- **T1–T27 27/27**; full solution + Teacher + Sandbox build 0 errors. Broadcasts + heartbeat pass
  (nothing legitimate dropped). Shared.Wire byte-unchanged; shipped Windows repo untouched.
- **Committed gate honesty:** the gates prove the *filter* (both commands, real frames) and that
  the student applies it; they do **not** headlessly prove `Dispatch` *calls* the guard
  (ConnectionViewModel needs the Avalonia dispatcher, and `LockService.Lock()` raises a real
  shield — can't run in a console gate). That wiring is verified by code review **and** the
  re-LIVE gate.
- **Re-LIVE (2026-07-14):** targeted lock reaches **only** its target; bystanders stay free.
  Honestly scoped — see `docs/TT-6-LIVE-CONFIRMATION.md` (run on one Mac, not the verbatim
  2-Mac + BELL matrix; the targeting property itself was proven).

## Corrections to the record (annotated, not rewritten)
- `TT-5-FINDINGS.md` + `TT-5-LIVE-CONFIRMATION.md`: ⚠️ correction blocks — TT-5-D false pass for
  targeting; Teacher send correct, Student filter missing; fixed here.
- `TT-3-FINDINGS.md` + `TT-4-FINDINGS.md`: note that per-student **StudentStream targeting** was
  latently broken during those phases — their LIVE passes were **valid for what they tested**
  (MJPEG/H.264 rendering of the requested screen), but "only the requested student streams" was
  **unverified** (one Mac student connected) and is fixed by the TT-6-D filter.

## What else might hide in the Student track (single-student-blind class)
This phase found a bug in a "complete" track. Being honest about the rest of that class:
- **CLOSED — the entire targeted-command class** (lock/unlock/power/policy/DM/`StudentStreamStart`/
  mic): one `IsForMe` guard covers them all, gated for Lock + StudentStream, and the design covers
  the rest identically.
- **UNVERIFIED — multi-peer *topology* features**, which are single-student-blind in a *different*
  way (not "does the student filter" but "does the peer fan-out work"):
  1. **Conference / peer-camera relay (M19)** — the gallery + who-sees-whose-camera routing is
     inherently multi-peer and was only ever exercised with one Mac student + Windows. **Highest
     remaining risk.** Recommend a dedicated **2-Mac-student** Conference re-test.
  2. **Student Demonstration (DemoFrame rebroadcast)** — one student → peers; multi-peer routing,
     if/when ported.
  3. **Future TT-9 audio mixing** (multiple mics) and **breakout/group routing** (`TargetGroupId`,
     the currently-inert `IsForMe` group branch) — build **and LIVE-test with ≥2 students from the
     start**, per the standing rule.

These are on the roadmap now (see `TEACHER-TRACK-ROADMAP.md`), not left implicit.

## What TT-6 unlocks · next
The Mac Teacher can **select N students and bulk lock/unlock/power** them, with a correct
selection model, a platform-aware toolbar, and — now — **correctly targeted delivery** (only the
selected students act). **Next: TT-7** (chat + notifications + hand-raise + reactions), with the
≥2-student LIVE rule in force and a Conference multi-peer re-test flagged.
