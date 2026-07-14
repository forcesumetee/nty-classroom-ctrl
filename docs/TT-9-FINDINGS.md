# TT-9 — Student Audio Mixing (teacher hears N students) · FINDINGS

**Built:** TT-9-B (audio-load harness) → TT-9-C (teacher multi-source mixer) → TT-9-D (measure +
stall gate). **Status: built + headlessly proven; awaiting the ≥2-Mac LIVE gate.**
No `Shared.Wire` change (0x032B/C/D + `AudioStreamFrameMessage` already vendored **and** already
deserialized in the port). Shipped Windows repo untouched.

---

## 🔴 THE ASSUMPTION (record it, challenge it) — flagged for deployment/sales

> **ASSUMPTION (unverified with customer, 2026-07-14):** the teacher opens a **small number of mics
> (~3–5)** at a time. This matches the shipped product's real operating point. It has **NOT** been
> confirmed with customer B's deployment team. If they expect **all 50 students unmuted
> simultaneously**, that is **unproven on BOTH platforms** (12.8 Mbps raw PCM ingest, un-normalized
> clipping, no cap in shipped) and needs a separate conversation.

This is a written assumption someone can challenge, not a silent one. **Action: the deployment/sales
team must confirm the real concurrent-mic expectation with customer B.** TT-9 is built to serve
*both* answers gracefully (see the cap, below): at N=3–5 the cap never triggers; at N=50 the cap
contains it and the teacher is *told*, instead of clipping and silently dropping students.

---

## Why "50 mics" is survivable — the crux (from TT-9-A, now confirmed by measurement)

The shipped product has **no scaling mechanism**: no VAD (both mic paths send continuous PCM; the one
RMS is cosmetic), no codec (raw 16 kHz PCM), no cap (`MaxInputCount` = `int.MaxValue`; the intended
gate `_micMonitorTargets` is dead code), no gain-normalization. It survives only because the
**operating point is a handful of mics** — not because the architecture scales to 50.

TT-9-D **measured** the consequence and the fix: with a **cap**, teacher-side mix work is *constant*
above the cap — **N=25 and N=50 both cost ~10% of one core teacher-only** (only 12 are mixed). So 50
open mics becomes survivable **because the cap bounds the mix** (and gain-norm prevents the clip the
shipped mixer would produce). The one load-bearing correctness property — a stalled stream must not
silence the class — is reproduced structurally (see the stall gate) and asserted.

---

## Two DELIBERATE DIVERGENCES from shipped (we fixed two things shipped got wrong)

| # | Shipped Windows `StudentAudioMixer` | Our port `TeacherAudioMixer` | Why |
|---|---|---|---|
| 1 | **No cap** (`MaxInputCount`=∞; `_micMonitorTargets` dead) — N unbounded ~256 kbps streams summed | **CAP** (default 12, configurable) with **VISIBLE degradation**: sources past the cap are counted + surfaced ("N of M open — mixing first 12"), **never silently dropped** | never drop a student the teacher believes they can hear without saying so (the TT-6 bulk-skip-report principle) |
| 2 | **No gain normalization** — sums un-normalized inputs, **clips at high N** | **Per-source gain = 1/√(activeCount)** (power-preserving), recomputed on every add/remove | N summed voices stay ~unit RMS instead of N× |

These are improvements, not parity — recorded so we don't "match shipped" by regressing them.

---

## What was built

**TT-9-B — headless audio-load harness** (`tools/MockStudent`, `--classroom N --audio`). Each replay
seat streams continuous 16 kHz mono 16-bit PCM (3200 B / 100 ms) `StudentAudioStreamFrame` (0x032C),
a distinct sine per seat (so the mix is separable + the stall test can name which stream it drops).
No VAD — continuous once open, matching the shipped student. Triggered by the real `MicMonitorStart`
or self-started via `--audio`. Reuses MockTeacher's `SyntheticTone`; MockTeacher already validates
0x032C — a ready-made counterparty. Audio-only when no Screen Recording (headless-friendly).

**TT-9-C — teacher multi-source mixer.**
- **Native (`Audio.swift` `nty_mix_*`)** — the **reusable, source-keyed core** (TT-11's student peer
  mixer will reuse the same ABI). **One `AVAudioPlayerNode` per source, summed by `mainMixerNode`** →
  the non-blocking invariant is *structural*: a starved node plays silence and never blocks. Per-source
  gain 1/√N; per-source jitter buffer (prebuffer 2, overrun-drop ~1 s); disconnect cleanup detaches
  the node (no leaked CPU); counters (`rendered_frames`/`source_played`/`output_rms`) for the gate.
- **Managed (`TeacherAudioMixer`)** — subscribes the three `ControlServer` student-audio events that
  had **zero subscribers** (student mic PCM was decoded then dropped) and feeds the native mix,
  guid→stable-key; owns the **cap** policy + status event.
- **Wiring** — `TeacherSession` owns+subscribes the mixer, exposes `ListenTo/StopListening`
  (`MicMonitorStart/Stop`, already ported) + `MixStatusChanged`. **UI** — per-tile "Listen to mic"
  checkable toggle + green mic badge; header shows the mix/cap status.

**TT-9-D — measurement + the distinguishing gate** (`--mixtest N`, `--mixhost`).

---

## Measurements (MacBook Air M2, 2026-07-14) — all N PASS stall + cap

| N (open mics) | mixed (cap 12) | whole-process CPU¹ | **teacher-only CPU²** | stall gate |
|---|---|---|---|---|
| 3  | 3  | ~5%  | — | ✅ |
| 10 | 10 | ~6%  | — | ✅ |
| 15 | 12 | ~8%  | — | ✅ |
| 25 | 12 | ~9%  | **~9–10% of one core** | ✅ |
| 50 | 12 | ~15% | **~10–11% of one core** | ✅ |

¹ `--mixtest` runs N senders + server + mix in one process — an **upper bound**.
² `--mixhost` (teacher only) + external `--classroom N --audio` senders — the real teacher cost.
Idle (0 students) → ~0% (no spin/leak). **The cap makes teacher CPU flat above 12** — this is *why*
50 is survivable.

### The kill-one-sender STALL GATE (the distinguishing test) — ASSERTED, not observed
`--mixtest` kills one mixed sender **mid-stream with no stop message** (a true network stall) and
asserts, via native counters: **(1)** the mix keeps rendering for the class (`rendered_frames`
advances), **(2)** the survivor keeps playing (`source_played` advances), **(3)** only the killed
source freezes and **stays registered** (contributing silence, not removed). A naive shared-queue /
blocking pre-mix fails this; N independent player nodes pass it structurally. **Committed gate:**
`--mixtest` (also covers cap enforcement). PASS at N=3/10/15/25/50.

---

## VAD / silence — measured stance (not built; wire-safe if we add it)
No VAD was built (per decision — measure first). The harness sends continuous tone, so it **cannot
measure a real classroom's silence fraction**; what it confirms is that **ingest is constant
regardless of content** (every 100 ms frame sent), so at N=50 continuous the ingest is ~12.8 Mbps.
A silence gate would cut that in proportion to the (typically high) classroom silence fraction.
- **VAD is wire-safe:** it changes *how many* `AudioStreamFrameMessage`s a mic emits, **not** the
  message struct/format — so `Shared.Wire` is untouched and it also helps Windows teachers receiving
  from Mac students.
- **Recommendation:** measure the real silence fraction from a LIVE capture; if high, schedule VAD as
  a **Student-track** addition (student send-side).

## AEC — deferred to TT-11-A (correct placement)
TT-9 (teacher-talkback) needs no AEC — the shipped Path A ships without it. AEC belongs on the peer
path (everyone-hears-everyone); it is a **TT-11-A spike** (AVAudioEngine voice-processing IO,
mirroring the shipped Windows Communications-endpoint AEC + the `tools/AECSpike` precedent). **STOP
condition stands:** if voice-processing IO can't sit on M20's 48k→16k converter / single-session
playback, stop and bring it back as a business decision (headphones-only fallback).

---

## ≥2-Mac LIVE gate (what remains)
The distinguishing property: **the teacher hears BOTH students, mixed, neither drowning nor dropping
the other — and killing one student's stream leaves the other uninterrupted.** Not "the teacher hears
audio." Two separate Macs (the Batch-1 rig). See `docs/TT-9-LIVE-CHECKLIST.md`.

## Constraints honored
Shipped repo untouched · **Shared.Wire unchanged — no new wire** · Student track no-regress
(--teacherselftest / --selftest green; mixer is teacher-side additive) · per-sub-phase commits
(844d755 · 97033cb · 3494082).
