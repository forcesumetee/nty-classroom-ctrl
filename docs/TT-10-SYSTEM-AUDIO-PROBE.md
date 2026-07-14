# TT-10 — System-audio ("Share Computer Audio") probe · 2026-07-14 (last Mac access)

**Question:** can macOS capture SYSTEM audio first-party (for "Share Computer Audio" / recording a
video's soundtrack), or does it need a third-party virtual audio device (BlackHole-class)?
**Contract link:** TOR 11.2.9 requires recording the teacher's screen AND audio — if "เสียงของครู"
means SYSTEM audio (not just the mic), this answer gates a contract item. **Ask the customer which.**

## ✅ RESOLVED 2026-07-14 — YES, system audio is capturable FIRST-PARTY (and coexists with screen)
Run inside the **granted Teacher bundle** (`com.nty.classroomctrl.teacher`, which has Screen Recording
from TT-8) via a temporary `NTY_SCK_AUDIO_PROBE=1` hook → `nty_sysaudio_probe` (one SCStream, audio +
screen outputs):
```
rc=0  audioBuffers=200  nonSilent=41  screenBuffers=21  maxAbs=0.206
✅ SYSTEM AUDIO CAPTURED FIRST-PARTY (non-silent) — and it COEXISTS with screen in ONE SCStream
```
- **✅ System audio IS capturable first-party** — no third-party virtual device (no BlackHole). 200
  audio buffers in ~4 s, 41 non-silent, peak |amp| 0.206.
- **✅ COEXISTENCE HOLDS** — audio (200) AND screen (21) buffers from the **same** SCStream → the real
  use case works: teacher plays a video → students see the picture AND hear the sound from ONE stream.
- **TCC:** Screen Recording — the **SAME grant TT-8 already uses** (no new permission type). The gate is
  the **bundle identity**: a bare `swiftc` binary has no identity so TCC can't even list it (that was
  the earlier 0-buffers, NOT an API failure); a signed .app with `NSScreenCaptureUsageDescription`
  works. (Earlier headless/bare-binary runs are below, as the journey.)
- **Format:** SCK delivers **Float32** (48 kHz / 2 ch as configured); the wire needs 16 kHz mono
  PCM16 → an `AVAudioConverter` resample/downmix step (same as the M20 mic path). This is TT-10-C.
- **Contract (TOR 11.2.9):** recording the teacher's screen + audio, incl. SYSTEM audio, is
  **achievable first-party.** Still ask the customer whether "เสียงของครู" = mic-only or system audio —
  but either way is now unblocked.

---
### (history — the journey to the ✅ above)
## Verdict: ⚠️ First-party API CONFIRMED real + instantiable · audio buffer delivery UNRESOLVED by automation → needs a human run

Ran the ScreenCaptureKit `capturesAudio` path **four times** on the borrowed MacBook Air (M-series):
headless CLI, and a **foreground GUI NSApplication** (activated, real window), each at 2×2 and at
640×480 with queueDepth. **Every run:** the stream started with no error and **screen buffers flowed**,
but **audio buffers = 0** (`maxAbs=0`, no audio format ever seen).

**What is PROVEN (high confidence):**
- **The first-party API is real and instantiates with ZERO third-party audio device.**
  `SCShareableContent` + `SCStreamConfiguration.capturesAudio=true` + `addStreamOutput(.audio)` +
  `startCapture()` all succeed and the stream runs (screen frames arrive). **This RULES OUT the
  customer's worst case: ❌ "needs BlackHole to even work."** Screen Recording TCC is the gate — the
  SAME grant TT-8 already uses (no new permission type).

**What is NOT resolved:**
- **0 audio sample buffers in all 4 automated runs**, even foreground. The environment is NOT the
  trivial "no device" case — `system_profiler` shows a **real default output (MacBook Air Speakers) +
  coreaudiod running**. Two live confounds we could not eliminate by automation:
  1. **A virtual "Microsoft Teams Audio Device"** is present in the routing — if system output is
     routed through it, `afplay`'s sound may not reach the tap SCK reads.
  2. **Harness-launched process context** — the probe runs under the automation harness, not a normal
     Aqua login session; SCK audio delivery may depend on genuine session/foreground status that a
     harness-spawned GUI app doesn't fully get.

## 🔴 To resolve — a HUMAN must run it (5 min, needs a Mac; do it while you still have one)
The binary + source are in `tools/SckAudioProbe/`. Run it **from YOUR Terminal.app** (a real login
session, not automation), **with a known audio source playing** (a YouTube tab, Music, or `afplay`),
and **set System Settings ▸ Sound ▸ Output to "MacBook Air Speakers"** (not the Teams device) first:
```
cd tools/SckAudioProbe
swiftc -O sck_audio_probe_gui.swift -framework Cocoa -framework ScreenCaptureKit \
       -framework AVFoundation -framework CoreMedia -o sck_gui
# start a video/music playing on the Mac, THEN:
./sck_gui                       # a window titled "SCK Audio Probe" appears for ~7s
cat probe_gui_result.txt
```
Read `probe_gui_result.txt`:
- `✅ SYSTEM AUDIO CAPTURED FIRST-PARTY (non-silent)` → **ship it first-party** (add `capturesAudio` to
  the existing `nty_capture_*` SCK path in `native/NtyCapture` — it already runs inside the Teacher app;
  confirm it COEXISTS with the TT-8 screen-share SCK stream — TT-8 + TT-16 recording both want SCK).
  Note the reported `audioFormat` (SCK delivers Float32).
- `❌ buffers arrive but SILENT` → system audio is NOT capturable this way → a **virtual audio device
  is required** → that's a THIRD Apple-platform limitation for the customer (after power-ON + policy).
- Still `❓ 0 buffers` in a real Terminal session too → escalate: try **Core Audio process taps**
  (`AudioHardwareCreateProcessTap`, macOS 14.4+, first-party) before concluding a virtual device is
  needed.

## Bottom line
"Share Computer Audio" is **probably deliverable first-party** — the API is real and the worst case is
ruled out — but the last empirical step (do non-silent buffers actually arrive) **could not be settled
by automation** and needs a 5-minute human run in a normal session. **The teacher-MIC broadcast half
(TT-10-B) is DONE + gate-proven and does not depend on this.** Recording the teacher's MIC audio (TOR
11.2.9, if mic-only) is therefore already unblocked; system-audio recording is the open question.
