# TT-1 LIVE Confirmation — Scenario 3: shipped Windows Student → Mac Teacher

**Date:** 2026-07-14 · **Milestone:** TT-1 (Teacher.Core — transport + router + roster) · **Gate:**
*a real student appears in the Mac Teacher's roster with working liveness.*

## Result: PASS — scenario 3 proven at the transport layer
A **real, shipped, UNMODIFIED Windows Student ("BELL")** connected to the **Mac Teacher**
(`ClassroomCtrl.Teacher.Core`, run via `tools/TeacherHost --serve`) over the LAN, appeared in the roster
with correct identity, stayed alive on heartbeat for ~65 s, and was removed correctly on disconnect —
with **zero changes to the shipped Windows product**. The wire is symmetric in both directions.

## Environment
- **Network:** iPhone hotspot (LAN `172.20.10.0/24`).
- **Teacher:** MacBook Air (Apple Silicon), `Teacher.Core` bound `0.0.0.0:7777` (`172.20.10.2:7777`) via
  `dotnet run --project tools/TeacherHost -- --serve`.
- **Student:** Windows PC "BELL" — the shipped v1.2 product, unmodified, pointed at the Mac's IP
  (manual Teacher-IP; no discovery beacon yet).
- **LNP:** passed — `dotnet run` inherited Terminal's local-network grant (no prompt).

## The log IS the evidence
```
[info] ControlServer: Peer adcd2494-3028-4b19-807a-3bd152d7ac9b TCP connected
[info] ControlServer: Student joined: BELL (BELL) endpoint=353dbe6f-197b-...
  ＋ JOIN   BELL  [BELL]  353dbe6f   → roster: 1
  · roster: 1 connected — BELL         (× 13 heartbeat snapshots, ~65 s)
[WARN] TcpControlServer: Peer adcd2494-... stale (16527 ms since last frame); closing.
[info] ControlServer: Student 353dbe6f-197b-... disconnected
  － LEAVE  353dbe6f   → roster: 0
```

## Every layer verified
- **✅ JOIN** — Hello handler → `StudentJoined` with the correct **EndpointId + DisplayName +
  MachineName**. The log shows **two distinct ids** — `peerId adcd2494` (TCP-accept) and
  `EndpointId 353dbe6f` (Hello) — exactly the namespace split TT-1-A traced.
- **✅ LIVENESS** — BELL stayed listed ~65 s (13 heartbeats). If Ping/Pong weren't working the 15 s
  stale-sweep would have reaped it at heartbeat 3. Sustained presence **is** the liveness proof.
- **✅ STALE-SWEEP** — fired at **16 527 ms**, correctly over the **15 000 ms** `StaleAfterMs` threshold.
  Ports faithfully.
- **✅ THE NAMESPACE-GAP FIX, PROVEN IN PRODUCTION** — the disconnect line reads
  `Student 353dbe6f… disconnected` — the **EndpointId**, NOT the peerId (`adcd2494`). Shipped Windows
  would have reported the peerId here, missed the roster lookup, and fallen back to `RemoveAt(Count-1)`.
  Our port reports the EndpointId and removes the right student. The TT-1-D fix is confirmed against a
  **real student**, not just a test harness.
- **✅ LNP** — the listener accepted a LAN connection under Terminal's grant.

## Headless proof (companion)
`tools/MockStudent --teacherselftest` — **20/20**: real WireClient interop, the 3-student
middle-disconnect fix, the reconnect-ownership guard, stale-sweep, and guaranteed teardown (re-bind +
zero sockets left). The permanent Teacher-side gate.

## Pattern consistency
Every Student-track milestone (M15–M23) had a LIVE gate that caught what headless tests didn't (M19
camera preset, M20 audio paths, 32-G LNP). TT-1 holds the pattern: `--teacherselftest` proves the logic
headless; **this LIVE run proves a real shipped client interoperates**. Scenario 3 (Mac Teacher +
Windows Student) is now proven at the transport layer.

## Known gaps (not blockers for TT-1)
- No UDP discovery beacon → the student needs a manual Teacher-IP (BELL used one). Later TT phase.
- Bundled Teacher (TT-13) will still need `NSLocalNetworkUsageDescription` — the dev path inherited
  Terminal's grant; don't let it hide the eventual gate.
- No Teacher UI yet (TT-2+): TeacherHost is a headless roster printer, not the app.
