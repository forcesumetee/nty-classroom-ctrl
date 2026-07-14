# TT-10-B + TT-10-C — LIVE Confirmation (2026-07-14, final Mac session)

**🎉 PASS — all three, including the bonus.**

## 🔴 THE HEADLINE (give this sentence to sales)
**A macOS Teacher can play a video, and a SHIPPED, UNMODIFIED WINDOWS STUDENT sees the picture AND
hears the sound — with ZERO changes to the shipped Windows product.** A brand-new cross-platform
capability rode the **frozen wire** with no protocol change (`ScreenStreamFrame` + `AudioStreamFrame`
0x0329, both already vendored). This is the payoff for 14 sessions of never touching `Shared.Wire`,
and the strongest possible evidence for the customer that **their existing Windows machines keep
working with a Mac teacher.**

## What the LIVE run proved (real hardware)
1. **TT-10-B "Talk to Class":** the teacher's voice came out of the Sandbox student. MockB logged
   `AudioStreamStart → 88 frames → AudioStreamStop` — a **clean stop (bug #7 fix, verified live)**.
2. **TT-10-C "Share Computer Audio":** the video's audio came out of the Sandbox student — **868
   frames** in one run.
3. **Audio quality: NORMAL** — no pitch shift, no garbling. **SCK honors 16 kHz mono directly; the
   documented `AVAudioConverter` fallback is NOT needed** (it stays in the handover as a contingency,
   unused).
4. **⭐ BONUS (bigger than scoped): Share My Screen + Share Computer Audio SIMULTANEOUSLY, against an
   UNMODIFIED SHIPPED WINDOWS STUDENT (BELL, v1.2).** MockB's log showed `ScreenStreamFrame` and
   teacher audio frames **interleaving in one session**, then **both stopping cleanly**
   (`ScreenStreamStop` + `AudioStreamStop`, 255 audio frames). So SCK audio + SCK screen genuinely
   **coexist on real hardware** (not just in the probe) AND **interoperate cross-platform**.

## Findings (recorded)
- **SCK `capturesAudio`: ✅ first-party system audio, non-silent, and COEXISTS with an SCK screen
  capture in one SCStream — CONFIRMED ON REAL HARDWARE** (not just the probe). **TOR 11.2.9** (record
  teacher screen + audio) is achievable with **no third-party audio driver**.
- **Audio format:** SCK delivered usable 16 kHz mono — no resampling needed. The `AVAudioConverter`
  fallback stays documented but **unused**.
- **Bug #7** (audio Start/Stop lossy → student playback stuck open): fixed, and the **reliable Stop
  verified LIVE** on both features (clean `AudioStreamStop` each time).
- **Cross-platform:** unmodified shipped Windows student (**BELL, v1.2**) received teacher screen +
  teacher system audio **simultaneously**.

## Honest scope (rig)
- **One Mac** (the borrowed MacBook Air) running the Teacher bundle; the Sandbox student **co-located**
  → **headphones** (no AEC in TT-10 by design — AEC is TT-11). MockB (frame counter) + the shipped
  **Windows student BELL** as the cross-platform peer.
- **What LIVE carried:** real-hardware SCK audio+screen coexistence, cross-platform interop with an
  unmodified Windows student, audio quality (no resampler needed), and the reliable-Stop (bug #7) fix.
- **What the headless gates carry** (the negatives + scale): `--teacherselftest` — Talk/audio
  broadcast reaches **ALL** students (the distinguishing broadcast property) + the reliable channel;
  T1–T27 wire byte-compat (incl. T27 audio PCM). Neither alone is sufficient; together they are.

## Status
TT-10-B + TT-10-C **COMPLETE + LIVE**. No `Shared.Wire` change. Shipped repo untouched. Teacher track
~55%. Camera view remains **not built** (relay-entangled → `TT-CAMERA-VIEW-SCOPE.md`).
