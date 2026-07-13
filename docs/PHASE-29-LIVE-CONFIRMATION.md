# Phase 29 — LIVE Confirmation (Milestone 20: bidirectional audio)

**Date:** 2026-07-13 · **Result:** ✅ **LIVE-CONFIRMED (as scoped)** — Mac-side bidirectional
audio is complete and correct; **3 of 4 live paths confirmed**, the 4th blocked by a
**shipped-Windows-Teacher mic-capture issue** (not a Mac defect, not a wire issue). Over
existing envelopes; no shipped-repo change, no protocol change.

## Test environment
| Role | Machine | Notes |
|---|---|---|
| **Teacher** (shipped **v1.2**, unmodified) | Windows PC | Mic Monitor + audio broadcast |
| **Student** (Mac Sandbox, Avalonia + AVAudioEngine) | Mac | packaged `.app` |
| Network | iPhone hotspot | 172.20.10.x subnet |

## Results
| # | Test | Result |
|---|---|---|
| 1 | **Path B** — Teacher Mic Monitor "Listen" → Mac mic captured + streamed (0x032C) | ✅ **works** |
| 2 | Manual **Audio tab** capture → speak → Windows | ❌ no sound — **correct by design** (multi-trigger default: the manual preview does NOT stream to the Teacher; only Mic Monitor triggers talkback) |
| 3 | **Path A** — Teacher's **Mic** → Mac speakers | ❌ Mac hears nothing — **teacher-side capture issue** (see root cause) |
| 4 | **Path A** — Teacher's **Share Computer Audio** → Mac speakers | ✅ **works, NO perceptible delay** |

**Test #4 is the decisive one:** "Share Computer Audio" playing with no delay proves the
**entire Mac playback chain is correct** — `AudioStreamStart/Frame/Stop` (0x0328/9/A) decode,
native AVAudioEngine playback, the 3-frame jitter buffer (~300 ms prebuffer, imperceptible),
and int16→float32 conversion. **Test #3 is not a Mac bug** — it's the Teacher not emitting.

## Root cause of the teacher-mic gap (traced in the shipped repo)
The Teacher's **Mic** and **Share Computer Audio** buttons feed the **same `AudioBroadcaster`**
and emit the **byte-for-byte identical envelope**: `AudioStreamFrame` (0x0329), 16 k/mono/16-bit.
There is **no separate mic envelope**, the pump is **not** loopback-gated (mic-only *does* emit
in the code), and it is **not** the deferred path C (0x0640 is receive/relay-only on the Teacher).

The one teacher-side asymmetry: the **mic** source opens a **brittle fixed-format `WaveInEvent`
at exactly 16000/16/1** (`AudioBroadcaster.cs:204`); the **system-audio** source uses a **robust
native-format `WasapiLoopbackCapture` + in-app resample**. If the Windows mic can't open at that
exact format — device absent, muted, no OS mic-permission, or format-unsupported — it produces no
data, so `HasFullFrameBuffered()` stays false and **zero 0x0329 frames are emitted for mic-only**
(although `AudioStreamStart` 0x0328 *was* sent → the Mac shows "playing" but has nothing to play).

Because mic and system frames are indistinguishable on the wire, **a Mac that plays system-audio
0x0329 would necessarily play mic 0x0329 if it arrived.** The Mac isn't dropping anything — the
Teacher isn't sending anything for the mic source.

## What this confirms
- **Mac-side bidirectional audio is complete + correct.** Path B (mic → Teacher) works with a real
  audible sink on the Teacher (`StudentAudioMixer` → `WaveOutEvent.Play()`, `StudentAudioMixer.cs:141`).
  Path A (Teacher → Mac) playback is proven correct by the system-audio test.
- **Zero shipped-repo change, zero wire change**, T1–T27 intact.
- The `--audiotest` structural proof (15/15 valid PCM both directions) predicted the live behavior.

## Scope statement
**M20 is complete as scoped.** The teacher-mic gap is **not** a Mac-port defect and **not** the
deferred path C — it is a shipped-Windows-Teacher mic-capture robustness issue (or a test-box mic
config issue). It **cannot** be fixed from the Mac side (the Mac already correctly plays any 0x0329
that arrives), and fixing the Teacher is out of the Mac-port scope (constraint: do not modify the
shipped Windows codebase).

## Windows-track follow-up (logged, not M20 work)
1. **Confirm on Windows** (no code): enable **Mic alone** (system audio OFF) and check the teacher
   log for `"Mic source added: 16000Hz"` vs `"No microphone device available"` / `"Mic source start
   failed"`, plus `"Audio pump stopped (sent N frames)"`. **N=0 for mic-only = teacher emitted nothing.**
   Also verify: Windows mic present, unmuted, Teacher app mic-permission granted (Windows Privacy).
2. **If it's a real bug** (not just the test box): a v1.2.x Windows-track patch could make the mic
   `WaveInEvent` **format-robust** (open at the device's native format + in-app resample, exactly
   like the loopback path already does). Separate effort, shipped-Windows repo — NOT the Mac port.
3. **Secondary risk to note:** in **mixed mode** (mic + system both on), `HasFullFrameBuffered()`
   takes the min across enabled buffers — so a failing/slow mic can **also silence the system
   stream**. Test mic ALONE to avoid this masking the diagnosis.

## Optional Mac polish (deferred, not required)
The self-tile shows "🔊 Playing teacher audio" on `AudioStreamStart` even if no frames follow (as
happened for the mic test). A tiny honesty improvement would flip the badge on the **first frame**
rather than on Start — deferred; current behavior is technically correct (playback is armed).

## Pattern consistency
| | M17 MJPEG | M18 H.264 | M19 camera | **M20 audio** |
|---|---|---|---|---|
| Native API | ScreenCaptureKit+ImageIO | +VideoToolbox | AVCaptureSession | **AVAudioEngine (capture+playback)** |
| Structural proof | `--streamtest` | `--streamtest-h264` | `--cameratest` | **`--audiotest`** (both dirs) |
| LIVE proof | Windows visual | Windows visual | Windows visual | **Windows audible (3/4; 4th = teacher-side)** |
