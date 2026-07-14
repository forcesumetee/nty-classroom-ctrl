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

## 5. Peer-audio + policy business answers (recorded)
- **Peer conference audio = YES**, but **wire-safe** — rides `VoiceAudioFrame` 0x0640 + `TargetGroupId`
  (no new wire); star-relay + student-side voice mixer; lands in **TT-11**, not a TT-9 reshape.
- **Policy = Windows-only** (customer B has no MDM) — removed as a Student-track task, recorded as a
  KNOWN LIMITATION; Teacher policy UI (when built) shows Mac = visibly unavailable.

## 6. TT-9 — Student audio mixing (teacher hears N students) ✅ COMPLETE + LIVE 2026-07-14
Approved A-phase (TT-9-A) → built **TT-9-B → TT-9-C → TT-9-D** autonomously → LIVE PASS. No
`Shared.Wire` change (0x032B/C/D + `AudioStreamFrameMessage` already vendored + deserialized).
- **The crux (headline, un-softened):** shipped Windows has **no scaling mechanism** for 50 mics — no
  VAD (RMS is "cosmetic only"), no codec (raw 16 kHz PCM, 256 kbps/mic → 12.8 Mbps ×50), **no cap**
  (`_micMonitorTargets` is DEAD CODE), not teacher-exclusive (auto-registers, no allow-list), no
  gain-norm. It works by **operating point** (teachers open a handful), **not architecture**; 50-at-once
  is unproven on **both** platforms. The one load-bearing property is the **non-blocking mixer**
  (ReadFully → starved = silence), which the port reproduces **structurally** (independent
  `AVAudioPlayerNode`s).
- **THREE deliberate divergences** (improvements, not parity): **cap** (12, configurable, VISIBLE
  degradation) · **gain-norm** (1/√N) · **one reusable source-keyed native core** (`nty_mix_*`, TT-11
  reuses it) vs shipped's two near-identical mixers.
- **🔴 ASSUMPTION (unverified w/ customer):** teacher opens ~3–5 mics — flagged for sales/deployment.
- **Measured (M2):** teacher-only ~10%/core at **N=25 and N=50** (cap holds mix work constant); idle ~0%.
  **Stall gate asserted** headlessly at N=3/10/15/25/50; **cap** at 15/25/50.
- **LIVE:** real voice (Sandbox mic) + synthetic tone (MockStudent) mixed on real `AVAudioEngine`
  hardware. One-Mac rig (co-located → headphones; no AEC in TT-9 by design). Headless carries the
  invariants; LIVE carries "real hardware renders it." See `docs/TT-9-FINDINGS.md` +
  `TT-9-LIVE-CONFIRMATION.md`. Commits `844d755`→`fd3ac11` (+ close-out).

## 7. Next — TT-10, then TT-11
- **TT-10** teacher audio broadcast + mic-monitor — **carries the last genuinely-unknown macOS
  mechanism: system-audio loopback** ("Share Computer Audio"). Teacher-mic broadcast reuses M20 (low
  risk); the loopback is the open investigation (§6/§7).
- **TT-11** conference + **peer audio** (rides 0x0640) + the **AEC spike (TT-11-A)** — **reuses the
  TT-9 mixer core**. Separate-machine 2-Mac audio topology becomes load-bearing here (still to prove).
