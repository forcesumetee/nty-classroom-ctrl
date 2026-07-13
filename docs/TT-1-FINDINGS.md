# TT-1 Findings — Teacher.Core: transport + router + roster · COMPLETE (LIVE-confirmed)

**Goal:** the first real Mac Teacher code — port the shipped Windows Teacher's server layer
(`TcpControlServer` transport + `ControlServer` router) into a UI-agnostic library and add a correct
roster, so a real student appears in the Mac Teacher's roster with working liveness. **LIVE gate met:
a shipped, unmodified Windows Student joined the Mac Teacher's roster over the LAN** (scenario 3).
Constraints held: `src/ClassroomCtrl.Teacher.Core` is new, **Shared.Wire unchanged**, Student track not
regressed, **shipped Windows repo untouched**.

## What shipped
`src/ClassroomCtrl.Teacher.Core` (class library, UI-agnostic — the windowed `Avalonia.Teacher` is TT-6+):
- `TcpControlServer` + `PeerConnection` + `ITransport` — the length-prefixed TCP wire-server.
- `ControlServer` — the MessagePack message router (Hello handler, join-state replay, all relay arms).
- `StudentRoster` — new, EndpointId-keyed, no positional fallback.
Harnesses: `tools/MockStudent --teacherselftest` (headless gate, 20/20) and `tools/TeacherHost --serve`
(the thin LIVE host; not the Teacher app).

## TT-1-A — server-layer map
- **`TcpControlServer` (672 LOC): 100% portable C#** (TcpListener / NetworkStream / Channels /
  MessagePack; zero Windows deps). Accept loop → per-connection read loop + one dedicated writer task.
  Ping/Pong auto-answered *inside* the connection (never surfaced to app code). Stale-sweep at
  **`StaleAfterMs = 15_000`** (student pings every 5 s → 3× margin), swept every 2 s.
- **`ControlServer` (1,883 LOC): portable except 6 `App.LogDebug` calls** → an injected `ILogger`.
  A pure MessagePack (de)serialize + `Channel` fan-out + `event` dispatch hub. Porting it front-loaded
  every Conference/breakout relay arm — later phases inherit the routing for free.
- On Hello the Teacher replays **GroupSnapshot + active Conference share/cam state** to the joiner —
  **NOT `LockScreen`** (a rejoining student is not re-locked; confirms the M21 trace). No explicit ack;
  liveness is the transport Pong.

## The send-path PORT RULE — write it down so nobody regresses it
Each `PeerConnection` has **five** bounded channels drained by one writer loop in strict priority:

| Channel | Cap | FullMode | Carries |
|---|---|---|---|
| `_reliableOutbox` | 4 | **Wait** (never drops) | file chunks, **bulk state commands** |
| `_audioOutbox` | 3 | DropOldest | screen-share audio |
| `_voiceOutbox` | 8 | Wait + TryWrite-skip | Tier-3 group voice |
| `_inputOutbox` | 32 | Wait + TryWrite-skip | remote-control input |
| `_outbox` | 16 | **DropOldest** (lossy) | screen-share **video** |

`ControlServer.SendTargetedAsync(env, ct, reliable)` forks `reliable ? BroadcastReliableAsync :
BroadcastAsync` (reliable → `_reliableOutbox`; lossy → `_outbox`).

> **RULE:** state-changing commands (lock / policy / power / mic / DM / bulk) → **`reliable: true`**
> (`_reliableOutbox`, Wait, never drops). Only hot **video/audio frames** use the lossy DropOldest
> queues. **Never route a command through the lossy path — that WAS the v1.2.1 bulk-lock bug** (a burst
> of locks got silently evicted from the 16-deep DropOldest video queue). The port keeps both channels
> + the `reliable` bool verbatim, so the correct-vs-buggy fork survives by construction. Proven both
> paths deliver (`--teacherselftest`: `BroadcastLockAsync` lossy + `LockOneAsync(reliable:true)`).

## TT-1-B / C — the port
- **TT-1-B (transport):** `cp` verbatim; `diff` vs Windows = **only 4 hunks** — an injectable bind
  address (`_bindAddress`, default `IPAddress.Any` = shipped behavior; self-tests bind `Loopback:0`
  ephemeral → no `:7777` collision, not LAN-visible, no LNP) + a `BoundPort` accessor. The 5-channel
  outbox / WriterLoop / stale-sweep / Ping-Pong are byte-identical.
- **TT-1-C (router):** `cp` verbatim; `diff` = 8 deleted lines (ctor sig, `_tcp` line, 5 `App.LogDebug`).
  `App.LogDebug` → a private `LogDebug(string)` routing to `ILogger` as a **structured arg**
  (`"{DebugMessage}", message`) so stray braces in filenames/exceptions can't trip ILogger's placeholder
  parser. Injectable bind threaded through the ctor.

## TT-1-D — the namespace-gap fix (the correctness centerpiece)
**The bug (exists in the shipped Windows Teacher):** the transport keys connections by **peerId** (a
fresh Guid at TCP-accept); the app keys students by **EndpointId** (= `Envelope.SenderId`, from the
Hello). They were never bridged — `MessageReceived` carries only the envelope; `PeerDisconnected`
carried the peerId. So `MainViewModel.OnStudentLeft` did `FirstOrDefault(x => x.EndpointId == peerId)`
— a compare across two different id namespaces that **never matches** — then fell back to
`Students.RemoveAt(Count-1)`, removing the **LAST** tile, not the one that left. The transport comment
`TcpControlServer.cs:187` even calls the proper map a *"Tier-3 follow-up"* — known, unfixed. **Invisible
at 1–2 seats; at 50 a mid-class disconnect greys out the WRONG student. BOTH shipped customers (A
Windows, B Mac) are exposed** → logged as a Windows-track follow-up (v1.2.x candidate; we do not touch
the shipped repo).

**The fix (teacher-internal C#, zero wire change):** each `PeerConnection` learns its `EndpointId` from
the first envelope carrying one (the Hello is always first) and registers it in an
`endpointId → current-peerId` map. `HandleDisconnect` reports the **EndpointId**, guarded so it fires
only if this connection is still the endpoint's current owner. `StudentLeft` now carries the EndpointId;
new `StudentRoster` removes by EndpointId with **no positional fallback**.

- **The ownership guard (flaky-wifi win, matters at 50 seats):** a student who reconnects on a new
  socket owns the endpoint; when the OLD socket finally drops (or the 15 s stale-sweep reaps it), that
  stale disconnect is **suppressed** — the reconnected student is not evicted.
- **The free repair:** because `StudentLeft` now carries the EndpointId, `ControlServer`'s
  `_activeConferenceCamSenders.Remove(id)` cleanup — a silent no-op in Windows (fed a peerId) — is now
  correct too (same for share-permission / tile-selection cleanups in the eventual VM).

**Proven in production (TT-1-F LIVE):** the real Windows Student's disconnect logged
`Student 353dbe6f… disconnected` — the **EndpointId**, not the peerId (`adcd2494`). Shipped Windows
would have logged the peerId, missed the lookup, and `RemoveAt(Count-1)`. The port removes the right
student — confirmed against a real client, not just the harness.

## TT-1-C — quiz relay DEFERRED (honest gap)
The `QuizSubmissionReceived` event + 3 Broadcast/Unlock quiz methods + the `QuizAnswerSubmit` dispatch
case need `ClassroomCtrl.Exam.Shared` (not in the Avalonia repo). Porting it would touch the frozen
Shared.Wire or add a project outside TT-1's roster/liveness scope, so all three spots are wrapped in
`#if false` (greppable, code preserved verbatim inside). `MessageType.Quiz*` wire tags already exist —
only the payload POCOs are missing. **Restore all three together when the quiz phase ports Exam.Shared.**

## TT-1-E — the headless gate (`--teacherselftest`, 20/20)
The permanent Teacher-side gate (analog of the Student track's `--selftest` / `--locktest`). Spins the
real `ControlServer` + `StudentRoster` on `127.0.0.1:0` and proves: real WireClient → roster identity +
Ping/Pong + clean-disconnect removal; **3 real clients → drop the MIDDLE → B removed, A and C remain**
(the buggy `RemoveAt(Count-1)` would have dropped C); raw clients → reconnect, ownership guard,
stale-sweep. **Guaranteed teardown:** every server under try/finally + a hard 30 s timeout, and each
scenario asserts the port re-binds after Dispose (**zero listening sockets left** — the server's analog
of "zero real taps").

## TT-1-F — LIVE (scenario 3)
See `TT-1-LIVE-CONFIRMATION.md`. A shipped, **unmodified** Windows Student ("BELL") connected to the Mac
Teacher (`Teacher.Core` via `tools/TeacherHost --serve`, `172.20.10.2:7777`, iPhone hotspot) → JOIN with
correct identity, ~65 s sustained liveness (13 heartbeats), stale-sweep at 16 527 ms (> 15 000 threshold),
correct EndpointId-based removal. **Scenario 3 (Mac Teacher + Windows Student) proven at the transport
layer with zero changes to the shipped Windows product — the wire is symmetric both ways.**

### LNP for the listener — carry to the bundle
Via `dotnet run` from Terminal, the listener inherits Terminal's Local Network grant (LIVE passed with
no prompt). **But the bundled Teacher (TT-13) will still need `NSLocalNetworkUsageDescription`** in its
Info.plist (reuse 32-F's guard) — the easy dev path must not hide the eventual gate.

### Honest gap — no discovery beacon yet
`TeacherHost` serves the TCP control port but does **not** broadcast the UDP discovery beacon (the
Windows Teacher's `TeacherBeaconService`). A discovery-only student won't auto-find it — the Windows
Student needs a **manual Teacher-IP** setting (BELL used one). UDP discovery is a later TT phase.

## Verification (all green)
`--teacherselftest` **20/20** · `diff`-verified verbatim transport (+4 hunks) and router (−8 lines) ·
full solution + MockTeacher + TeacherHost build **0 errors** · **T1–T27 PASS · MockStudent --selftest
PASS** · **LIVE: real Windows Student in the Mac roster**. Shared.Wire byte-unchanged; shipped Windows
repo untouched.

## What TT-1 unlocks
`Teacher.Core` (transport + router + roster) exists and is both headless- and LIVE-proven. Every later
Teacher phase inherits the `--teacherselftest` gate + a 40-student `--classroom` harness (TT-0).
**Next: TT-2** — the student-grid UI (the first windowed `Avalonia.Teacher` view binding to
`StudentRoster`).
