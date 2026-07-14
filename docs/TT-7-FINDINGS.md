# TT-7 Findings — Chat + hand-raise + reactions + notifications (+ Mac-Student send) · COMPLETE (LIVE-confirmed 2026-07-14)

**Goal:** two-way classroom messaging on the Mac Teacher — chat (broadcast + DM), hand-raise
with a recognize queue, reactions, notification sounds — plus the first **Mac-Student SEND**
affordances (chat + reactions). Built as **BATCH 1** with TT-8 (one build pass, one LIVE session,
per-phase checkpoints). **LIVE gate met** with **2 Mac Sandbox students on separate physical Macs**
+ the Mac Teacher — see `docs/BATCH-1-LIVE-CONFIRMATION.md`. Constraints held: **Shared.Wire
unchanged** (T1–T27 green), shipped Windows repo untouched.

Sub-phases: **TT-7-B** messaging seam · **TT-7-C** Teacher UI · **TT-7-D** Student send ·
**TT-7-E** committed aggregation gate.

---

## THE PHASE WAS UI+VM ONLY — the LOW rating held
`Teacher.Core`'s chat/hand-raise/reaction wire was **already ported** (from the re-scope: the
send methods + inbound events all existed on `ControlServer`). TT-7 added **zero wire/routing
code** — it was a `TeacherSession` passthrough seam (`ITeacherMessaging`) + Teacher VM/UI + the
Student send commands. This confirms the re-scope's core finding: *the work moved to Student +
UI, not the wire.*

## 🔴 THE AGGREGATION PROPERTY — the multi-student shape for STATE THAT AGGREGATES
The distinguishing test for aggregating state is **not** "the teacher received the hand-raise" —
it's "the teacher attributed it to the **RIGHT student's tile**." Same discipline as the TT-6-D
negative: assert **A's hand lights A's tile and NOT B's**. This is the third distinct multi-student
shape, and they need different tests:
- **Targeted commands** (lock/power/DM/screen-stream) — filtered receive-side (`IsForMe`, TT-6-D).
- **Aggregating state** (hand-raise, reaction, chat) — attributed teacher-side by `EndpointId`
  (TT-7). The negative: a non-target's tile does NOT light.
- **Peer fan-out / relay** (Conference camera/audio, demonstration) — still single-student-blind;
  TT-9/TT-11 (multi-peer cluster).

The committed gate (`--teacherselftest (0e)`, 12 checks) drives the REAL `TeacherGridViewModel` /
`ChatViewModel` via dispatcher-free `ApplyHandRaise`/`ApplyReaction`/`ApplyIncomingChat` and asserts
the negatives + the **sound channel** (a hand-raise actually plays a HandRaise sound — not just a
flag flip; the [[gate-assert-distinguishing-property]] lesson). LIVE-confirmed with a real
separate-machine bystander B.

## DM PRIVACY RIDES THE TT-6-D FILTER — which had already closed a latent DM leak
A teacher DM is `ChatDirect` (a targeted envelope); every non-recipient's
`StudentEnvelopeFilter.IsForMe` drops it. So DM privacy is **receiver-side, by construction** — the
same guard that fixed the wrong-blast-radius bug. TT-7-A confirmed that before TT-6-D, **every Mac
student saw every DM** (the missing filter leaked DMs too). LIVE: A's DM was **not** visible to B.

## SEND-PATH DISCIPLINE — DMs reliable, and BUG #6 (hand-lower)
- **DMs route `reliable:true`** (baked into the `ITeacherMessaging` seam) — `SendDirectMessageAsync`
  defaulted `reliable:false` (the TT-5-A lossy class). A DM is not an ephemeral frame.
- **🔴 SHIPPED BUG #6 — `SendHandLowerAsync` was lossy** despite its doc-comment saying "Reliable
  channel." Hand-lower (the teacher's "Recognize") is a **targeted STATE TOGGLE**: a dropped lower
  clears the teacher's UI (teacher believes they recognized the student) while the **student's hand
  stays raised** → permanent desync. **The test that decides it:** *"does dropping this leave a
  STUCK STATE?"* — hand-lower: **yes** (→ reliable); a reaction: **no** (ephemeral, self-expiring →
  lossy is correct). Same shape as bug #5 (ScreenStreamStop). Routed reliable in the port; the
  inbound-reaction relay stays lossy. See the roadmap's Windows-track sweep (§7).

## Mac-Student SEND — chat + reactions (hand-raise already existed)
Mirrors the pre-existing `RaiseHand` command: pick a `MessageType`, MessagePack the payload,
`WireClient.SendAsync` (always a broadcast envelope; the Teacher attributes by `SenderId`). Chat →
`ChatBroadcast` (a student can't DM — no targeting on the student send path, by design); reactions →
`Reaction 0x0674` (NOT the dead `ChatReaction 0x0103`). This is the first expansion into the
Student track from the Teacher track — correct, because it blocks customer B.

## Notification sounds — `afplay`, deliberately not new native
`SoundService` shells `afplay` on the stock system sounds (Ping = hand-raise, Pop = chat) behind
`ISoundService` (fakeable in the gate). Batch 1 reuses only existing native; `afplay` is a stock
CLI, so no dylib change. Sandbox caveat (P35): the Teacher is unsandboxed today, so `Process.Start`
is fine; a future App-Sandbox would switch this to NSSound — the seam localizes that.

## Verification (all green)
- **`--teacherselftest`** — PASS, incl. **(0e)** 12 aggregation/attribution checks (A→A not B,
  ordering, reaction-on-A-only, unknown-id no-op, chat attributed to sender, sound channel fired).
- **T1–T27** PASS (Shared.Wire unchanged); **`--selftest`** PASS; solution build 0 errors.
- **LIVE (2026-07-14)** — 2 Mac students on separate Macs: broadcast chat + 2-sender attribution +
  DM privacy (A not B) + hand-raise attribution (A's tile, queue A(1) B(2)) + sounds + Recognize
  clearing the hand on tile/queue/student-view (the reliable bug-#6 path) + reaction on the right
  tile only. See `docs/BATCH-1-LIVE-CONFIRMATION.md`.

## What TT-7 unlocks · next
The Mac Teacher now **sees, commands, AND communicates** with students (chat/DM/hand-raise/
reactions/sounds), and the Mac Student can talk back (chat/reactions/hand-raise). **Next: the
multi-peer cluster** — TT-9 (student audio) → TT-10 (teacher audio) → TT-11 (conference) — the
first peer-fan-out topology, where the separate-machine 2-Mac rig (proven available this LIVE)
becomes load-bearing.
