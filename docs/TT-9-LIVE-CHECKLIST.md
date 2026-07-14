# TT-9 — ≥2-Mac LIVE checklist (teacher hears N students, mixed)

**Gate:** the distinguishing property is *"the teacher hears **both** students, **mixed**, neither
drowning nor dropping the other — and when one student's stream stops, the other keeps playing."*
Not "the teacher hears audio." Headless proof (stall + cap + CPU) is done — see
`docs/TT-9-FINDINGS.md`; this is the human-in-the-room confirmation on the Batch-1 2-Mac rig.

## Config (honest)
- **Mac Teacher** — the `ClassroomCtrl.Avalonia.Teacher` app. Needs an **audio output device**
  (built-in speakers or headphones). **No mic / no AEC needed** for TT-9: the teacher *plays* the
  mixed student audio; students do not play it back, so there is no echo loop (AEC is a TT-11 peer
  concern, not TT-9).
- **2 Mac students on SEPARATE physical Macs** (the Batch-1 rig) — each the Sandbox app with
  **Microphone permission granted** (bundle grant, or grant the CLI/Terminal). Students stream their
  mic only when the teacher opens it (`MicMonitorStart` from the Listen toggle).
- The load-bearing variable here is **two real mics on two machines mixing at once** — separate Macs
  fully satisfy it (topology is genuinely load-bearing for TT-9, unlike TT-7/TT-8).

## Checkpoints
| # | Action | Expected (PASS) |
|---|---|---|
| 1 | Teacher right-clicks student **A** → **Listen to mic** (checkable) | A's tile shows the 🎙 badge; header shows "🔊 Listening to 1 mic"; teacher **hears A** |
| 2 | Teacher also opens **Listen** on student **B** | header "🔊 Listening to 2 mics"; teacher **hears A and B MIXED** |
| 3 | **A and B talk at the same time** | **both are audible** — neither drowns the other, no clipping (gain-normalization) — the distinguishing positive |
| 4 | **B keeps talking; A goes silent / A mutes / A's stream stalls** | **B stays uninterrupted** — silence from A never gates B (the non-blocking invariant, live) |
| 5 | **A quits (disconnect) while B talks** | A's tile + badge clear; **B keeps playing**; no stuck audio (disconnect cleanup) |
| 6 | Teacher toggles **Listen off** on B | B's mic closes (`MicMonitorStop`); mix goes silent; badge clears; no residual/looping audio |
| 7 | (observational) audio quality with both loud | intelligible, not distorted/clipped (gain-norm working) |

## Notes
- **Cap (12) is NOT exercisable with 2 Macs** — it's proven headlessly (`--mixtest 15/25/50`: 12
  mixed, all counted, "mixing first 12" surfaced). At 2 Macs the header just reads "2 mics".
- If the teacher **hears nothing**, check: (a) the teacher Mac has an output device; (b) the student
  granted **Microphone**; (c) the student tile got the 🎙 badge (the `MicMonitorStart` reached it).
  A missing output device makes the mixer no-op (logged) — it never crashes the teacher.
- **Headless pre-proof already green:** `--mixtest 3/10/15/25/50` (stall + cap), `--teacherselftest`,
  `--selftest`. Teacher-only CPU at N=25 **and** N=50 ≈ 10% of one core (cap-bounded).
