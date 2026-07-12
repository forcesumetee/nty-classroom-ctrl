# Phase 26.1 Findings — v1.2.1 Bulk-Lock Burst Fix

**Symptom:** 50 endpoints, Ctrl+A → Bulk Lock → only **~18/50** locked (36%). Breakout
Room lock-all (20 endpoints) succeeded **20/20**.

**Status:** fix applied on branch `v1.2.1-bulk-lock-fix`; transport libs compile clean on
macOS; **WPF build + BulkDispatchTest run pending on Windows**.

---

## The diagnostic narrative — hypothesis was wrong, and *how* it was wrong matters

**Original hypothesis:** "50 UDP packets back-to-back → network stack / switch buffer
overflow → packets silently dropped." Fix proposed: `await Task.Delay(10)` between sends.

**Actual root cause:** it's **TCP**, and the loss is **inside our own process** — an
application-level bounded `Channel`, not the network.

1. Targeted control ops (`LockOneAsync`, `PowerOneAsync`, `ApplyPolicyToOneAsync`,
   `RevertPolicyForOneAsync`, `SendMicMuteRequestAsync`, `SendDirectMessageAsync`) don't
   address one peer — they **broadcast to every peer** and rely on receiver-side `IsForMe`
   filtering (`_peers` is keyed by transport-connection Guid, not the app EndpointId — see
   `TcpControlServer` comment). So bulk-locking N students = **N broadcast frames into
   every peer's queue.**
2. All of them called `_tcp.BroadcastAsync` → `PeerConnection.EnqueueOrDrop` → the **lossy
   `_outbox`: bounded capacity 16, `FullMode.DropOldest`** — a queue *designed* for
   screen-share frames where dropping the stale ones is correct.
3. The N enqueues are synchronous and sub-millisecond; the TCP drain is slower. Each peer's
   cap-16 queue overflows and **DropOldest silently discards the oldest ~(N−16)**, keeping
   only the last ~16. A given student locks only if *its* frame is among the survivors →
   **~16-18 of 50.** Exactly the symptom.

**Why breakout worked (20/20):** it sends **one atomic `GroupSnapshot`** for the whole
operation — a single frame that fits the cap-16 queue — not N per-student frames. That's
the "different dispatch path." It confirms the root cause from the other direction: the
problem is **N burst frames overflowing a lossy queue**, and the transport is innocent.

## The fix — route to the back-pressure-aware path that already existed

The codebase **already** has the right tool: `BroadcastReliableAsync` (`FullMode.Wait`,
cap 4), used for must-arrive Conference Start/Stop and file chunks. Lock/Power/Policy/Mute
were simply never promoted to it.

- **`ControlServer`**: added `bool reliable = false` to the 6 targeted-send methods + a
  `SendTargetedAsync` helper. `reliable=true` routes through `BroadcastReliableAsync`, whose
  `FullMode.Wait` **back-pressures the producer at TCP-drain rate** — the burst can no longer
  outrun the queue, so nothing is dropped. Default `false` keeps every single-target caller
  byte-for-byte unchanged (no burst, no need).
- **`MainViewModel`** bulk commands opt into `reliable: true` and **`await`**.
- **Wire unchanged** (same envelopes/types/bytes) → T1-T26 unaffected; v1.2 clients compatible.

### Bonus bug found: silent fire-and-forget
`BulkApplyPolicy` / `BulkRevertPolicy` dispatched with `_ = App.Server.…OneAsync(…)` —
**fire-and-forget**, so any failure was silently swallowed *and* it rode the lossy queue.
Now `async Task` + awaited on the reliable path, with `catch → AppendErrorChat` so failures
surface instead of vanishing (or crashing).

## The teachable principle (codify this)

> **The fix wasn't network-layer — `Task.Delay` wouldn't have solved it.** It masks the
> symptom by pacing sends below the overflow rate, leaving a lossy channel and a magic
> number that breaks on a slower link. **The fix was routing to the existing reliable API.**
>
> **Diagnostic principle:** when a fix strategy involves *"add a delay to make it work,"*
> **pause and check whether the code already has a back-pressure-aware path being
> bypassed.** A delay that "fixes" a drop is almost always compensating for a producer
> outrunning a bounded consumer — the real fix is `FullMode.Wait` (or an equivalent
> bounded, blocking hand-off), not a timer.
>
> **Where to look:** not `netstat`/Wireshark first — look at the `ChannelWriter` /
> queue configuration (`BoundedChannelOptions.FullMode`). "Silently dropped at scale"
> + "reliable at small N" is the signature of `DropOldest`, not packet loss.

## UI feedback (honest, no false ACKs)
Live progress chip "Sending i of N…" while a bulk op runs (cleared in `finally`).
Completion messages kept as-is: with the reliable path, delivery is now **guaranteed**
(TCP + Wait), so "Locked N screens" is truthful. True per-student *receipt* confirmation
("48/50, 2 failed: …") would need a new **ACK message = a wire-protocol change (T27)** —
deliberately out of scope for a compatible patch. Noted as a v1.3 candidate.

## Test without 50 PCs — `tools/BulkDispatchTest`
N loopback `TcpClient` students vs a real `TcpControlServer`; sends N targeted Locks via the
lossy vs reliable path and counts how many students received their own frame. `--sizes
2,20,50`. Loopback is too fast to drop tiny frames, so back-pressure is **induced** (slow
readers + padded frames) to mimic the constrained hotspot:
- **LOSSY** reproduces ~18/50 at scale.
- **RELIABLE** delivers **N/N**, asserted deterministically (exit 0 = fix proven).

Builds on macOS (`EnableWindowsTargeting=true`); **runs on Windows** (dependency chain pulls
in the WPF runtime).

## Verification status
| Check | Where | Status |
|---|---|---|
| Transport libs (Shared + Networking) compile | macOS | ✅ 0 errors (`EnableWindowsTargeting=true`) |
| BulkDispatchTest compiles | macOS | ✅ 0 errors |
| Full solution (WPF Teacher) build | **Windows** | ✅ 0 errors |
| BulkDispatchTest run (2/20/50 → reliable N/N) | **Windows** | ✅ 50/50 (see below) |
| T1-T26 wire-compat still PASS | **Windows** | ✅ 26/26 PASS |
| Live 50-endpoint bulk-lock (loopback proof) | **Windows** | ✅ reliable 50/50 |

## Windows Verification — 2026-07-13 (SHIP-READY)

Run on the same Windows machine as the Milestone-15 live test.

**1. `dotnet build`** → ✅ **0 errors** (NU1902 = the expected, documented Phase 24.1
MessagePack 2.5.187 pin; must NOT be "fixed" by upgrading — wire byte-compat).

**2. `tools/BulkDispatchTest`** → ✅ **FIX PROVEN**

| N | LOSSY (before) | RELIABLE (after) |
|---|---|---|
| 2  | 2/2            | **2/2** ✅ |
| 20 | 17/20 (dropped) | **20/20** ✅ |
| 50 | 23/50 (dropped) | **50/50** ✅ |

The loopback reproduction (**23/50** under induced back-pressure) mirrors the customer's
field report (**18/50** on the iPhone hotspot) — same DropOldest signature, exact numbers
vary with link conditions. After the fix: **50/50 at every size.**

**3. T1-T26 wire-compat** → ✅ **26/26 PASSED**
- Round-trip preservation ✓ · vintage (backward) decode ✓ · **forward compat — v1.2 clients
  correctly decode v1.2.1 envelopes** ✓ · all 26 message types ✓.
- Confirms the fix is a **teacher-side dispatch-channel change only** — the wire is untouched.

## SHIP READY — v1.2.1
- ✅ **Fix effective at the customer's reported scale** (50 endpoints → 50/50, up from ~18/50).
- ✅ **Wire protocol unchanged** (T1-T26 26/26; same envelopes/types/bytes).
- ✅ **Backward + forward compatible** — v1.2 clients interoperate with a v1.2.1 teacher
  unchanged; no student-side update required to benefit (fix is entirely teacher-side).
- ✅ **Single-target behavior unchanged** (reliable defaults to false for all non-bulk callers).
- ✅ **Bonus reliability**: policy bulk actions no longer silently swallow failures.
- **Deploy:** ship a **teacher-side patch installer** (v1.2 → v1.2.1). No student rollout needed.

## Files changed
- `src/ClassroomCtrl.Teacher/Services/ControlServer.cs` — `reliable` param + helper.
- `src/ClassroomCtrl.Teacher/ViewModels/MainViewModel.cs` — bulk commands: reliable + await +
  fire-and-forget fix + progress.
- `src/ClassroomCtrl.Teacher/MainWindow.xaml` — progress chip.
- `src/ClassroomCtrl.Shared/Localization/LocalizationData.cs` — `Status_BulkProgressFmt` (EN+TH).
- `tools/BulkDispatchTest/` — reproduction + fix-proof harness.
