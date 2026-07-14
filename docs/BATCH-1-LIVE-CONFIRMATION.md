# BATCH 1 (TT-7 + TT-8) — LIVE Confirmation (2026-07-14)

**One LIVE session, two phases, per-phase checkpoints — all passed.** This is the batched-delivery
model for the batchable phases going forward: two LOW-risk, no-new-native, independent phases built
in one pass and LIVE-tested together with checkpoints attributable to each phase.

## Test config (honest scope)
- **Mac Teacher** (run as the `NTY ClassroomCtrl Teacher.app` bundle for TT-8 — Screen Recording TCC).
- **2 Mac Sandbox students on SEPARATE physical Macs** — a real separate-machine bystander (B), the
  **strongest** ≥2-student config to date (stronger than TT-6's single borrowed Mac).
- The aggregation / attribution / DM-privacy **negatives were exercised against a real non-target B**
  on its own machine.

**Scope note (what this does and does NOT cover):** for **TT-7 aggregation** (hand-raise/reaction/
chat attribution) and **TT-8 broadcast** (teacher screen to all), the load-bearing variable is *two
distinct clients with distinct EndpointIds* — which separate Macs fully satisfy. **Machine topology
is NOT load-bearing for these phases.** It **becomes** load-bearing at **TT-9/TT-11** (peer audio
mixing / conference relay — fan-out topology), where a separate-machine 2-Mac rig is required — and
this session confirms that rig is available.

## TT-7 checkpoints — PASS
| # | Checkpoint | Result |
|---|---|---|
| 1 | Teacher broadcast chat → both students see it | ✅ |
| 2 | 2 students send chat → each attributed to the right sender (2 senders → right conversations) | ✅ |
| 3 | Teacher DMs A → **only A sees it, not B** (DM privacy via IsForMe) | ✅ |
| 4 | A raises → A's tile ✋ + queue A(1); **B's tile does NOT**. B raises → queue A(1), B(2) | ✅ |
| 5 | Notification sounds on hand-raise + incoming chat | ✅ |
| 6 | Recognize A → hand clears on tile, queue, **and A's own view** (reliable — bug #6 path) | ✅ |
| 7 | A sends 👍 → A's tile only; **B's does not** | ✅ |

## TT-8 checkpoints — PASS (Teacher = bundle)
| # | Checkpoint | Result |
|---|---|---|
| 8 | Share My Screen (grant Screen Recording → relaunch) → **both** students display the teacher's screen live | ✅ |
| 9 | Stop Sharing → **both** viewers close cleanly — **no stuck viewer** (reliable Stop, bug #5) | ✅ |
| 10 | Quit Teacher mid-share → students' viewers close (disconnect path) | ✅ |

## What was proven with a real bystander
- **Attribution (TT-7):** A's hand-raise / reaction lit **A's** tile, never B's — the aggregating-
  state distinguishing negative, on separate machines.
- **DM privacy (TT-7):** A's DM was **not** delivered to B — the TT-6-D IsForMe filter holding for
  `ChatDirect`.
- **Reliable state toggles:** Recognize (bug #6) and Stop-Share (bug #5) both landed — the "stuck
  state" failures those fixes prevent did not occur.
- **Broadcast to all (TT-8):** the teacher's screen reached BOTH students; item #10 (Mac display)
  proven on real Mac students.

## Not covered (deferred, tracked)
- **Peer fan-out topology** (student↔student audio/camera relay) — TT-9/TT-11; not a batch-1 concern.
- **Scale** (50 students) — `--classroom` stress test remains; the LIVE ran 2 students.
- **H.264 teacher-share** — the teacher broadcasts MJPEG (matches shipped default); the shared
  decoder is H.264-ready, but the LIVE exercised the MJPEG path.
- **Rebuild TCC re-prompt** — the ad-hoc-signed Teacher bundle re-prompts for Screen Recording after
  a rebuild (P35 fixes; a relaunch of the same build keeps the grant).
