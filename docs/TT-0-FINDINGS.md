# TT-0 Findings — MockStudent (Teacher-track harness) · COMPLETE

**Goal:** the inverse of MockTeacher — a headless **student** stand-in so the macOS Teacher can be
proven without a Windows box or a second Mac. It is the harness backbone for every later Teacher
phase (TT-1…TT-13). Constraints held: `tools/MockStudent` + `tools/MockTeacher` only, `Shared.Wire`
unchanged, shipped Windows repo untouched, Student track not regressed.

## TT-0-A — investigation (the reuse reframe)
Roles invert vs the Student track: Mac Teacher = **server**, MockStudent = **client**. Since the Mac
Student already *is* a client, MockStudent reuses the Student-side code near-verbatim:
- `WireClient` (connect / Hello / heartbeat / `EnvelopeReceived`) — **100%, no fork**.
- `ScreenStreamer` / `CameraStreamer` / `AudioStreamer` (`StartAsync(WireClient, …)`) — **100%**;
  they emit real native-encoded frames.
So MockStudent is a **thin harness (~a few hundred LOC)**, not a rewrite. It receives + ACKs (it does
NOT enforce — no shield/tray/config). **Roadmap sanity-check:** critical path
TT-0→TT-1→TT-2→TT-3/TT-4→TT-5→TT-6 holds; reuse is *larger* than first estimated (the whole P32
layer + capture/encode reuse), so **H.264 decode (VTDecompressionSession, TT-4) is the only big new
native piece**; the reinforced risk is **scale** (decode/render ×30–40).

## TT-0-B — core (`--selftest`)
`WireClient` reused as-is + a dispatch loop (log/ACK inbound commands). `--selftest` spins a minimal
in-process teacher-stub on loopback and drives the real client through **connect → Hello (carries
EndpointId) → command receipt → heartbeat Ping/Pong** — **7/7**. Loopback ⇒ no Local Network Privacy
gate; no capture ⇒ no permissions. Runs anywhere.

## TT-0-C — real streamers + round-trip proof
`StudentAgent` mirrors the Student's `ConnectionViewModel` dispatch exactly:
`StudentStreamStart→ScreenStreamer(codec)`, `ConferenceStart→CameraStreamer(sessionId)`,
`MicMonitorStart→AudioStreamer` (+ stops, + stop-all on disconnect). Verified by pointing MockStudent
at the new **MockTeacher `--recv <mode>`** (the server half of the Student-track stream tests, waiting
for an *external* student), which structurally asserts the frames:

| Round-trip | Result |
|---|---|
| screen MJPEG | 12/12 valid (JPEG SOI/EOI) |
| screen **H.264** | 12/12 valid (keyframe + Annex-B deltas) |
| camera | 8/8 valid (JPEG) |
| audio | 8/8 valid (PCM 3200 B · 16 k/1/16) |

MockStudent emits **byte-faithful wire output**. The H.264 round-trip is literally *our M18 encoder →
wire → assertion* — the exact shape **TT-4's decode test** reuses.

## TT-0-D — `--classroom N` (the scale harness) + footprint
**Design that isolates the Teacher-side load:** capture a **golden** H.264 sample **ONCE** (keyframe +
deltas, via the real `ScreenStreamer` → a loopback recorder), then **N replay-clients in ONE process**
replay it verbatim on ~4 fps timers — **no per-client capture/encode**. So the harness does
**1 encode + N×(send)**; the Teacher does **N×(decode+render)** → the bottleneck is unambiguously the
Teacher's `VTDecompressionSession ×N + render`. Every replay starts on the golden IDR, so each
per-stream decoder syncs immediately. One process is correct (per-client work is *send*, not encode;
the only thing not replicated — 40 distinct source IPs — decode/render doesn't care about). Opt-in
`--realcapture` for variety is possible; **replay is the default**. Counterparty:
**MockTeacher `--recvmany`** (accept N, request H.264, count — validates delivery, doesn't decode).

**Results:** N=5 → 5/5 · 200 frames; N=10 → 10/10 · 400; **N=40 → 40/40 · 1600 frames**. The harness
scales to 40.

**Harness footprint at N=40 (for TT-4 planning): RSS ~100 MB, CPU ~2–8%.** The harness is **light** —
so running MockStudent *and* the Mac Teacher on the same borrowed MacBook Air in TT-4 will **not**
meaningfully contaminate the decode measurement (~5% CPU + 100 MB is negligible vs decode ×40).
*(If a future test needs even cleaner isolation, run them on separate machines — but the data says
it's unnecessary.)*

## Verification (all green)
`--selftest` 7/7 · round-trip 4/4 (mjpeg/h264/camera/audio) · `--classroom` 5/10/40 · footprint
measured · **T1–T27 PASS · all 11 MockTeacher modes PASS** · change surface = the two tools ·
`Shared.Wire` unchanged · dylib byte-unchanged · shipped repo untouched.

## What TT-0 unlocks
Every later Teacher phase gets a `--recv`-style structural proof + a `--classroom N` scale harness
for free. **Next: TT-1** — port `TcpControlServer` + the roster; the first LIVE gate is a real
Windows Student (or MockStudent) appearing in the Mac Teacher's roster. Apply the **Local Network
Privacy** memory to the Teacher bundle from day one (the listener hits the same gate).
