# Session summary — 2026-07-14 (re-scope + Batch 1: TT-7 + TT-8)

## 1. Re-scope (the big one)
Customer B (Mac teacher + Mac students, 50 seats) wants **all 7 features** previously filed
"deferred/optional": remote control, demonstration, net movie, recording, breakout, exam/quiz, UDP
discovery. Full investigation (`docs/TEACHER-TRACK-ROADMAP.md` §0):
- **🟢 NO new wire needed** for any of the 7 — tags + payloads already vendored (Shared.Wire frozen).
  Exceptions: exam/quiz needs `Exam.Shared` restored; UDP discovery is a separate UDP beacon.
- The work **moved to Student + native + UI**; the Mac Student handled none of the 7.
- **Q1 (traced):** the conference relay carries **video only** → an audible conference REQUIRES
  TT-9 + TT-10 (both moved onto the critical path). **Peer-to-peer conference audio was never in the
  shipped product → net-new → business decision pending.**
- New phases slotted TT-12b/13/14–18; revised critical path: TT-7→8→9→10→11→12a→13→P35.
- **Honest %:** the Student track's "95%" was the illusion (it was ~55% vs full scope), not the
  Teacher's 37%. New companion doc `docs/STUDENT-TRACK-ROADMAP-V2.md` (11 Student items).
- **Policy enforcement = MDM-class RISK** (V2 §6): USB/print/app blocking on macOS needs MDM /
  config profiles — likely **"Windows-only" for customer B**. Sales question open.

## 2. Batch 1 — TT-7 + TT-8 (built in one pass, one LIVE session, all checkpoints PASS)
The **batched-delivery model** for LOW-risk, no-new-native, independent phases — validated.

- **TT-7** (chat/DM + hand-raise queue + reactions + notification sounds + Mac-Student send): UI+VM
  only (Teacher.Core wire was already ported). The **aggregation property** — attribute A's
  hand-raise to A's tile, NOT B's — is the multi-student shape for *aggregating* state (vs targeted =
  filtered, vs peer-relay = TT-9/11). Committed gate `--teacherselftest (0e)`.
- **TT-8** (teacher "Share My Screen" + Mac-Student display): the **EXTRACT decision** — decoder →
  shared `ClassroomCtrl.Avalonia.Media` (share must-not-diverge native interop; reused by TT-15/17).
  Teacher bundle (`package-teacher.sh`) built for the new Screen Recording TCC — a slice of TT-13
  pulled forward. Committed gate `(0f)+(1c)`.
- **LIVE (2026-07-14):** **2 Mac students on separate physical Macs** — the strongest ≥2-student
  config to date. All 10 checkpoints passed. See `docs/BATCH-1-LIVE-CONFIRMATION.md`.

## 3. Bugs found + fixed (investigation-first)
- **#5 lossy ScreenStreamStop** → reliable (dropped Stop strands a student in the takeover viewer).
- **#6 lossy SendHandLowerAsync** → reliable (dropped Recognize desyncs: student's hand stays up).
- **Windows-track follow-ups now SIX;** THREE (④⑤⑥) are one class: **control/state on the lossy
  DropOldest queue**. Recommendation to the Windows team = a **SWEEP** with the invariant *"does
  dropping this leave a stuck state? → reliable"*, not six point fixes (roadmap §7).

## 4. Status after batch 1
- Teacher track **~47%** (TT-0…TT-8 of ~19) · Student **~60%** · System **~45%**.
- Student V2 items done: #3 chat-send, #4 reaction-send, #5 teacher-screen display (+ hand-raise send
  pre-existed).
- Gates: T1–T27 · `--selftest` · `--teacherselftest` (now incl. 0e/0f/1c) — all green; Shared.Wire
  untouched; shipped repo untouched.

## 5. Next — the multi-peer + new-native cluster (where the hard part begins)
- **TT-9** student audio mixing (**new native** — per-student jitter + mix bus) → **TT-10** teacher
  audio broadcast + mic-monitor (system-loopback gap) → **TT-11** conference (**flagship multi-peer
  relay**). The separate-machine 2-Mac rig (proven this LIVE) becomes load-bearing here.
- **Two open business questions:** (1) peer-to-peer conference audio (net-new vs shipped
  teacher↔student); (2) MDM/policy (is customer B MDM-enrolled, or is policy Windows-only?).
