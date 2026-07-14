# TT-6 LIVE Confirmation — multi-select + bulk, and the wrong-blast-radius fix

**Date:** 2026-07-14 · **Milestone:** TT-6 (multi-select + bulk actions) + the TT-6-D
receive-side-filter fix · **Gate:** *select N students → bulk act on only them; a targeted
command reaches only its target.*

## Result: PASS — with an honest scope note

### The TT-6-D LIVE run that FOUND the bug (recorded — the most valuable LIVE result of the project)
Selecting **only** the Windows student (BELL) and hitting **Lock** locked **every** connected
student, including a **Mac student that was never selected**. This surfaced the wrong-blast-radius
bug (missing Student-side `IsForMe` filter — see `docs/TT-6-FINDINGS.md`). ① selection idiom, ③
platform-skip, and ④ confirm-safety all passed on that run; ② bulk lock **failed** on blast
radius. Sub-gate ② is exactly what a single-student config in TT-3/TT-4/TT-5 could never expose.

### The re-LIVE run after the fix — PASS
With the filter fix in place: **a targeted Lock reached only its target; the other connected
students stayed free.** The wrong-blast-radius bug is closed, confirmed against real students.

## 🔎 Honest scope — what was and wasn't covered
- **Proven:** a per-student targeted command (Lock) reaches **only** its target; bystanders are
  unaffected. This is the property that matters, and it was confirmed with more than one student
  connected.
- **Not run verbatim:** the exact **"2 Mac Sandboxes + BELL"** matrix from the re-LIVE
  instructions. The test was run on a **single borrowed Mac**, so the specific multi-Mac-student
  permutations (e.g. Mac-1 selected, Mac-2 the bystander) weren't each exercised by hand.
- **Why the conclusion still holds:** the fix is a **single default-deny guard** at the top of
  `Dispatch` keyed on `TargetEndpointId == Client.EndpointId`; it is student-identity-symmetric
  (a Mac bystander and a Windows bystander run the same check), and the committed gates
  (`--teacherselftest` 3-client + `--selftest` real-frame) assert the negative for **both** Mac and
  arbitrary endpoints, for **both** Lock and StudentStreamStart. The LIVE run confirmed the
  end-to-end behavior; the gates cover the permutations.

## TT-6 feature sub-gates (from the earlier passing run)
- ✅ **Selection idiom** — plain-click selects one, ⌘-click toggles, Shift-click ranges, ⌘A/Esc,
  floating toolbar with count.
- ✅ **Bulk power + platform skip** — mixed selection: Windows students act, Mac students skipped
  with a visible "· N macOS skipped" report; power disabled when no selected student can power.
- ✅ **Confirm safety** — Enter cancels a bulk shutdown (Cancel is the default); Confirm requires
  an explicit click.

## Environment
- **Teacher:** MacBook (Apple Silicon), `Avalonia.Teacher` via `dotnet run` → `TeacherSession` on
  `0.0.0.0:7777`.
- **Students:** BELL (real Windows v1.2, unmodified) + a Mac Student (Sandbox) on the same Mac.
- **Config caveat:** single physical Mac → the multi-Mac-student matrix was not run verbatim (see
  scope note).

## Headless companions (committed)
- `--teacherselftest` **74** — reliable-channel (TT-5) + selection model (TT-6-B) + bulk (TT-6-C) +
  **receive-side filter** (TT-6-D: IsForMe unit for Lock + StudentStreamStart + default-deny; a
  3-client scenario where bystander C drops both).
- `--selftest` **9** — real targeted frames: Lock + StudentStreamStart at another → dropped; at me
  → acted.
- **T1–T27 27/27**; full solution + Sandbox build 0 errors.

## Pattern consistency (and where it broke)
Every milestone M15–M23 and TT-1…TT-5 had a LIVE gate that caught what headless tests couldn't.
TT-6 is the phase where the LIVE gate itself was **insufficiently configured** (one student) for
three prior phases — and the moment a second student was present, it caught a bug that had been
latent since TT-3. The lesson is now a **standing rule**: per-student features are LIVE-tested with
**≥2 students, one not the target** (see `docs/TT-6-FINDINGS.md`).
