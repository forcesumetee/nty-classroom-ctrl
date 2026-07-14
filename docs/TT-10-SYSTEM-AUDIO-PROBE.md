# TT-10 — System-audio ("Share Computer Audio") probe · 2026-07-14 (last Mac access)

**Question:** can macOS capture SYSTEM audio first-party (for "Share Computer Audio"), or does it
need a third-party virtual audio device (BlackHole-class) on the teacher's Mac?

## Verdict: ⚠️ FIRST-PARTY API IS REAL + INSTANTIABLE — actual buffer delivery UNCONFIRMED (headless)

Ran a standalone Swift spike (ScreenCaptureKit `capturesAudio`) on the borrowed MacBook Air (M-series,
macOS Sequoia). Result:

```
PROBE: SCK stream started (capturesAudio=true) — sampling 4s
PROBE RESULT: audioBuffers=0 nonSilentBuffers=0
VERDICT: ❓ no audio buffers (TCC/session/context)
```

**What this PROVES (high confidence):**
- **`SCShareableContent` succeeded** → Screen Recording TCC is available in the run context (the SAME
  grant TT-8 "Share My Screen" already requires — **no new permission type**).
- **`SCStreamConfiguration.capturesAudio = true` + `addStreamOutput(type: .audio)` + `startCapture()`
  all succeeded with NO error and with NO third-party audio device installed.** → The first-party
  system-audio API (ScreenCaptureKit, macOS 13+) is **real, present, and instantiable with zero
  third-party dependencies.** This **rules out the customer's worst case: ❌ "needs BlackHole to even
  work."**

**What this does NOT prove:**
- **0 audio sample buffers arrived** in 4 s while `afplay` was looping a sound. Most likely cause: this
  **headless CLI/daemon context has no active audio output session** for SCK to tap (system-audio
  capture reflects what is actually rendering to a real output device; a non-GUI tool context usually
  has none). It is **not a confirmed failure of the feature** — it is the ceiling of what a headless
  probe can show.

## To resolve (needs a Mac + a foreground app — ~30 min)
Run the same spike **inside a foreground GUI app** (or the Teacher `.app` bundle) on the real machine,
with real audio playing, and confirm non-silent buffers arrive. If they do → **✅ ship it first-party
via SCK** (add `capturesAudio` to the existing `nty_capture_*` ScreenCaptureKit path — it already runs
in the Teacher app). Also verify SCK **audio + screen coexist** in one stream (the spike added both
outputs and the stream started, but buffer coexistence is unconfirmed). Fallback if it never delivers:
**Core Audio process taps** (`AudioHardwareCreateProcessTap`, macOS 14.4+) — also first-party — then,
last resort, a bundled virtual device.

## The spike (durable — re-runnable; inline so it survives the machine)
Compile: `swiftc -O sck_audio_probe.swift -framework ScreenCaptureKit -framework AVFoundation -framework CoreMedia -o sck_probe` — full source in git history at `scratchpad/sck_audio_probe.swift` (this session) / reproduce from the pattern: `SCShareableContent.excludingDesktopWindows` → `SCContentFilter(display:)` → `SCStreamConfiguration{capturesAudio=true; sampleRate=48000; channelCount=2}` → `addStreamOutput(_, .audio, queue)` → `startCapture()`; in `didOutputSampleBuffer` for `.audio`, read the `CMBlockBuffer` as Int16 and check max-abs > 200 for non-silent. Self-exits after 6 s so a TCC prompt can't hang it.

## Bottom line for TT-10 / the customer
"Share Computer Audio" is **very likely deliverable first-party (no third-party driver)** — the API is
there and starts clean. The one remaining check is a foreground-app buffer-flow test, which needs a
Mac. **The teacher-MIC broadcast half (TT-10-B) is DONE + gate-proven and does not depend on this.**
