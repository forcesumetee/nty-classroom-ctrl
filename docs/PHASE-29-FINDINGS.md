# Phase 29 Findings — Bidirectional audio (Milestone 20, Mac-side)

**Goal:** bidirectional audio between the Mac student and the shipped Windows Teacher,
over existing wire envelopes — no shipped-repo change, no wire change.
**Result:** ✅ **LIVE-CONFIRMED (as scoped, 2026-07-13)** — Mac-side bidirectional audio is
complete and correct; **3 of 4 live paths confirmed**, the 4th (Teacher-mic → Mac) blocked by a
shipped-Windows-Teacher mic-capture issue (traced: not a Mac defect, not a wire issue, not path C).
`MockTeacher --audiotest` proves both directions structurally (15/15 valid PCM); the live
system-audio test proves the Mac playback chain end-to-end. See `docs/PHASE-29-LIVE-CONFIRMATION.md`.

**Sub-phases:** 29-A investigation · 29-B native mic capture · 29-D service + Audio tab ·
29-E mic wire-in (path B) · 29-F native playback + wire-in (path A) · 29-G `--audiotest`
+ T27 + bandwidth · 29-I docs.

## Investigation (29-A) — the pivot: Mic Monitor, not Conference
The written plan assumed a Conference-style trigger. Investigation of the shipped repo
found something better (the **4th** wrong-direction catch across Sessions 3–7):

1. **Audio is raw PCM everywhere** — 16000 Hz, mono, 16-bit signed LE, 100 ms/3200-byte
   frames. **No Opus/AAC/Concentus, no codec.** So macOS produces/consumes it natively —
   **no codec dylib at all** (planned sub-phase 29-C dropped, not padded).
2. **The clean capture trigger is Mic Monitor (0x0490), NOT Conference.** Unlike camera
   (which *required* Conference Mode), audio's "listen to one student" is a first-class
   shipped feature (`MicMonitorStart` → the student streams `StudentAudioStreamFrame` →
   the Teacher's `StudentAudioMixer` plays). So audio is **easier** to LIVE-test than
   camera. Conference Mode has **no wired peer audio** (its mic button falls back to this
   same path-B talkback). `MicMonitorStart` already sat in the deferred stub arm.
3. **"Bidirectional" is two independent, separately-triggered wire families**, not one
   duplex stream.

**Feasibility gate (all green):** zero shipped-repo changes (Teacher already captures,
plays, broadcasts, monitors audio); zero wire changes (every envelope exists in
`Shared.Wire`); no codec macOS lacks (raw PCM).

## The two paths
| Path | Envelopes | Direction (Mac student) | Teacher trigger |
|---|---|---|---|
| **B** talkback | `StudentAudioStreamStart/Frame/Stop` 0x032B/C/D | **mic → Teacher** | **Mic Monitor "Listen"** (0x0490) |
| **A** broadcast | `AudioStreamStart/Frame/Stop` 0x0328/9/A | **Teacher audio → Mac** | Teacher "Mic" / "Share Computer Audio" |

Both carry `AudioStreamFrameMessage` (self-describing: `PcmData, SampleRate, Channels,
BitsPerSample, TimestampUtcMs, FrameSeq`). The Teacher's `StudentAudioMixer` is hard-fixed
at 16 k/mono/16-bit, so path B sends exactly that. Path B also sends `MicStateUpdate`
(0x0642, `MicLive` true/false) so the Teacher's per-student mic indicator lights.
`SourceEndpointId` routing is via `Envelope.SenderId` (= `WireClient.EndpointId`); a Mac
client stamps it itself (no Windows Service in the middle to do it).

## AVAudioEngine capture (29-B) — a new native pattern
Distinct from the AVCaptureSession video path (screen/camera):
- **Input tap** on `AVAudioEngine.inputNode` → **`AVAudioConverter`** resamples the device
  format to **16 kHz mono int16**, framed into 100 ms/3200-byte chunks.
- **Resample is mandatory** — this Mac's mic is **48 kHz** (ratio 1/3). Every tap buffer
  is converted.
- The tap closure runs on a **real-time audio thread** — framing + RMS are kept
  allocation-light.
- **RMS 0–100 level** (`nty_audio_last_rms`, scaled ×3) drives the Audio-tab meter.
- **Own session state** (`AudState`) — independent of screen + camera.
- **Mic permission is a distinct TCC bucket** (`NSMicrophoneUsageDescription`, shown to
  the user, immediate grant — no relaunch).

## AVAudioEngine playback (29-F) — separate engine, jitter buffer
Path A plays the Teacher's broadcast/system audio through the Mac speakers via
`AVAudioPlayerNode` on a **SEPARATE engine + state** from capture (so mic and playback run
concurrently). Playback needs no TCC permission.

**Jitter buffer** — the wire delivers 100 ms frames but not on a clean cadence:
| Concern | Handling | Rationale |
|---|---|---|
| Early jitter | **Prebuffer 3 frames (~300 ms)** before `player.play()` | absorbs startup jitter so the first frames don't underrun; **~300 ms latency cost is fine for a one-way listen** (not a conversation) |
| Latency creep | **Overrun: drop the incoming frame at ≥ 10 pending (~1 s)** | you can't un-schedule from an `AVAudioPlayerNode`, so the only lever is dropping on ingress; caps latency after a burst/catch-up |
| Dry queue | **Underrun: silent gap, node auto-resumes** (no stop/restart) | a momentary gap sounds better than a restart |

int16 LE→float32 is alignment-safe (explicit byte-pair reads); the engine resamples
16 k→device rate (48 k) automatically.

## Bandwidth (measured, 29-G)
| Stream | Sustained | Notes |
|---|---|---|
| Screen H.264 (M18) | ~1.16 Mbit/s | |
| Camera JPEG (M19) | ~0.6 Mbit/s | |
| **Audio PCM (M20)** | **~288 kbit/s wire** (~277 raw) | theoretical 256 kbit/s (16000×2×8); **MessagePack envelope overhead ~4% — immaterial** (3325 B/frame wire vs 3200 B PCM); the rest is converter cadence slightly above 10 fps |
| **Three concurrent (per student)** | **≈ ~2 Mbit/s** | screen + camera + audio together |

**Network sizing note:** Mic Monitor is **one-student-at-a-time**, so the ~2 Mbit/s
concurrent figure is not a per-student-times-N blocker — but record it for classroom
network sizing (N students each screen-streaming is the real ceiling, and that's H.264 at
~1.16 Mbit/s each).

## ⚠ Feedback loop (no AEC in M20)
Path A (Mac plays teacher) + path B (Mac sends mic) in the **same room** = acoustic
feedback (howl). There is **no acoustic echo cancellation** in M20. Documented in the
native code + header. **LIVE procedure: test A and B separately, or with headphones.**
(The shipped Windows group-voice path C uses WASAPI AEC for its 3-layer strategy — out of
M20 scope; path C itself is deferred.)

## Multi-trigger default (respect the manual preview)
The native mic is a **single session**, so if the **Audio Capture tab (29-D)** is already
capturing when a `MicMonitorStart` arrives, `AudioStreamer.StartAsync` returns `-3` — we
**respect the manual preview**: log a note, emit no `StudentAudioStreamStart` (Start is
sent only after `rc==0`, so nothing spurious hits the wire). Same default as camera (28-E).

## Edge cases handled
1. **Disconnect** → stops all three streamers + playback, clears state, resets self-tile.
2. **Permission revoked mid-stream** → native frames simply stop; no crash.
3. **Native start failure** → no spurious `StudentAudioStreamStart` (ordering guarantee).
4. **Dead socket on stop** → best-effort try/catch (Teacher's PeerDisconnected implicit stop covers it).
5. **Dropped lossy Start (path A)** → playback **lazy-starts on the first frame** using the
   frame's own `SampleRate`/`Channels` (the v1.2.1 lesson: don't assume the reliable-looking
   signal arrived; the broadcast Start rides the lossy `_audioOutbox`).
6. **Inbound audio log flooding** → path-A frames (~10/s) are enqueued with an **early
   return before the per-envelope log**, so they don't flood the wire log.

## Verification (Mac, no Windows box)
`MockTeacher --audiotest` — real `WireClient` + `AudioStreamer` + `AudioCaptureService`
vs an in-process mock Teacher, BOTH directions. **PASS:**
- Path B: **15/15** valid PCM frames (3200 B, 16000/1/16), **seq 15/15** strictly
  incrementing, **endpoint 15/15** (`Envelope.SenderId`), **non-silent** RMS, lifecycle
  `Start`+`Stop`+`MicState` (MicLive true→false).
- Path A: playback **consumed 12/12** synthetic-tone frames cleanly.
- **T27** added — `AudioStreamFrameMessage` byte-compat (6 keys, 3200-byte PCM), the one
  frame DTO not previously wire-tested. **T1–T27 all PASS.**
- Native tone harness played 1.5 s of 440 Hz through the full playback chain + clean stop.

**Honest record — the harness had two bugs the pipeline did not.** The first `--audiotest`
run showed **15/15 valid frames on the very first try** (29-B→F were right all along), but
FAILED because: (a) the first-run mic-permission dialog delayed frames past the 20 s
timeout and corrupted the bandwidth window (burst after grant), and (b) an off-by-one in
the seq assertion (`seqOk == rxB`, not `rxB-1`, since the first frame's seq=1 matches
`lastSeq(0)+1`). Both were **test-harness** fixes; the capture/playback pipeline was
correct throughout. MockTeacher-first caught them before the LIVE run.

## LIVE Windows test (29-H) — ✅ CONFIRMED (as scoped) 2026-07-13
3 of 4 paths confirmed live; the 4th is a teacher-side capture issue (full writeup:
`docs/PHASE-29-LIVE-CONFIRMATION.md`):
- ✅ **Path B** — Teacher Mic Monitor "Listen" → Mac mic captured + streamed (0x032C). Teacher has
  a real audible sink (`StudentAudioMixer` → `WaveOutEvent.Play()`).
- ✅ **Path A / system audio** — Teacher "Share Computer Audio" → Mac plays it, **no perceptible
  delay** → proves the whole Mac playback chain (decode + AVAudioEngine + jitter buffer + int16→float).
- ❌ **Path A / teacher mic** → Mac hears nothing — **NOT a Mac defect.** The Teacher's Mic and
  Share-Computer-Audio buttons emit the *identical* 0x0329 envelope; the mic source uses a brittle
  fixed-format `WaveInEvent` (16000/16/1) that emits **zero frames** if the Windows mic can't open
  at that exact format. Since mic/system frames are byte-identical, a Mac that plays system 0x0329
  would play mic 0x0329 *if it arrived* — the Teacher isn't sending it. **Windows-track follow-up**
  (verify test-box mic; a v1.2.x patch could make the mic capture format-robust like the loopback).
- ✅ (correct-by-design) Manual **Audio tab** capture does NOT stream to the Teacher — the
  multi-trigger default (respect the manual preview); only Mic Monitor triggers talkback.

## Constraints honored
Sandbox + `native/` + `tools/MockTeacher` + wire-compat test only · `Shared.Wire` unchanged ·
shipped repo untouched · screen (M17/M18) + camera (M19) intact · Audio tab (29-D) intact ·
per-sub-phase commits · T1–T27 PASS.

## Deferred
Path C breakout peer voice (TargetGroupId + PTT + AEC) · acoustic echo cancellation ·
stereo / higher-rate audio · per-frame `IsSpeaking` VAD on the wire · audio recording.
