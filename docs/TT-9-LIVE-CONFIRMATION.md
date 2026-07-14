# TT-9 — LIVE Confirmation (2026-07-14)

**🎵 PASS — the mixer mixes on real hardware.**

## The run
- **Mac Teacher** = the `ClassroomCtrl.Avalonia.Teacher` app.
- **Source 1** = the real **Sandbox student** (the tester's **voice via a physical mic**).
- **Source 2** = **MockStudent** `--classroom 1 --audio --duration 300` (a **synthetic tone**).
- Both connected, both **Listen-toggled** on their tiles → the tester **heard BOTH simultaneously** —
  voice **and** tone, **mixed**, out of the Teacher's audio output.

Two genuinely distinct sources → **two `AVAudioPlayerNode`s → one `mainMixerNode`** → speaker, on
real Apple-silicon hardware. **This is the thing headless could not prove: the native audio graph
actually renders a mix on a real device, not just in simulation.**

## COVERED by the LIVE run
- ✅ **Two distinct sources mixed simultaneously** on real `AVAudioEngine` hardware (voice + tone, both
  audible — the distinguishing positive, not "audio plays").
- ✅ **The full end-to-end path:** mic capture → wire (`StudentAudioStreamFrame` 0x032C) → ControlServer
  → `TeacherAudioMixer` → per-source player nodes → `mainMixerNode` → speaker.
- ✅ **Per-tile "Listen to mic" toggle** driving mixer registration (`MicMonitorStart` → the student
  streams → the source joins the mix; the badge + header status reflect it).

## NOT covered by the LIVE run — proven HEADLESSLY instead (recorded honestly)
- ⏸ **The cap** (needs >12 sources) — proven in `--mixtest` at **N=15 / 25 / 50**: 12 mixed, all N
  counted, "mixing first 12" surfaced.
- ⏸ **The kill-one-sender stall test at scale** — proven in `--mixtest` at **N=3 / 10 / 15 / 25 / 50**:
  mix keeps rendering, survivor keeps playing, only the killed source freezes (stays registered).
- ⏸ **Two students on SEPARATE Macs** — the rig had **one borrowed Mac**, so both sources were
  **co-located**. Because a co-located mic + speaker with **no AEC** loops (AEC is **TT-11 by design**,
  not TT-9), the run used **headphones**. Separate-machine 2-Mac audio topology remains a TT-11-time
  confirmation (the flagship multi-peer relay), where machine topology is genuinely load-bearing.

## The split (why both halves are needed)
**The headless gates carry the invariants** (cap, non-blocking stall, CPU-at-scale — deterministic,
repeatable, assertable). **The LIVE run carries the "real hardware actually renders it" proof** (the
native `AVAudioEngine` graph mixing two real sources on a real device). **Neither alone is
sufficient; together they are.**

## Measurements (recorded in TT-9-FINDINGS.md)
Teacher-only CPU (MacBook Air M2): **N=25 → ~9–10% of one core · N=50 → ~10–11%** (12 mixed either
way — the cap holds mix work constant); idle → ~0% (no spin, no leak).

## Config honesty (one line)
One-Mac rig · both sources co-located · headphones (no AEC in TT-9 by design) · cap + stall + scale
proven headlessly, not at LIVE. This is weaker on *topology* than Batch-1's separate-Mac rig — TT-11
is where separate-machine audio topology becomes load-bearing and must be re-confirmed.
