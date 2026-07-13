# H.264 decode test fixtures (TT-4)

Committed golden H.264 samples for the **headless, regression-protected** H.264-decode
gate (`TT4CGate`, Skia-enabled headless). Each is fed through the managed
`H264DecoderWrapper` → native VTDecompressionSession → BGRA `WriteableBitmap`, asserting
**exact dimensions** (per the Skia-headless rule: default headless decodes to a 1×1 stub).

## File format (`NTYH264F`)
Binary, big-endian:
```
"NTYH264F"                (8 ASCII bytes)
uint32  frameCount
repeat frameCount times:
  uint32  length          (bytes of Annex-B for this frame)
  uint8   isKeyframe      (1 = [SPS][PPS][IDR], 0 = delta [slice])
  uint32  width
  uint32  height
  byte[length]            (Annex-B NAL bytes, exactly as sent on the wire)
```

## Fixtures

### `h264-vt-synthetic.fixture` — VT→VT (customer B's path)
- **Source:** our M18 VideoToolbox encoder (`native/NtyCapture/Sources/H264Encoder.swift`),
  fed a **synthetic** 1920×1080 buffer (a solid frame with a small moving box — content is
  irrelevant to a decode test; it keeps deltas tiny).
- **Provenance:** generated 2026-07-14 by the scratchpad `H264Fixture` dumper (no screen
  capture / no permission needed — VideoToolbox operates on buffers). Regenerable anytime.
- **Proves:** the Mac Student's H.264 shape (VideoToolbox, 4-byte start codes, in-band
  SPS/PPS per keyframe, Baseline) decodes on the Mac Teacher — **customer B (Mac students).**
- 6 frames (1 keyframe + 5 deltas), 1920×1080.

### `h264-openh264-bell.fixture` — OpenH264→VT (the interop proof) ✅ CAPTURED
- **Source / provenance:** a real, shipped Windows Student ("BELL", v1.2,
  OpenH264/MediaFoundation), captured 2026-07-14 via the scratchpad `OpenH264Capture` tool.
- **Contents:** 5 frames (2 keyframes + 3 deltas), 1920×1080, 192423 bytes. Frame 1 is a
  small keyframe (6289 B — SPS [`profile_idc=0x42`=66 **Baseline**] + PPS + small IDR);
  frame 2 is a full IDR (183 KB). The shipped student emits a keyframe on stream-start
  (the `ForceKeyframe`-on-Start path) **plus** the ~2 s IDR cadence, so this fixture also
  exercises **mid-stream keyframe handling**: the re-fed SPS/PPS match the first set, so the
  decoder keeps its session rather than recreating it.
- **Start codes (measured):** **0 × 3-byte, 9 × 4-byte** (`00 00 00 01`). This build emits
  ONLY 4-byte start codes; TT-4-A noted OpenH264 *may* emit both, so the decoder's 3-byte
  parse stays proven **synthetically** (the TT-4-B round-trip rewrites a keyframe to 3-byte
  and decodes it) rather than by this real sample.
- **Proves:** OpenH264 (Windows) Baseline → VideoToolbox (Mac) decode → exact 1920×1080 —
  **scenario 3 interop, now HEADLESS + regression-protected** (previously provable only LIVE).
- **⚠ Re-capturing — deltas need screen MOTION:** the source student emits deltas only while
  its screen is CHANGING. A static screen yields keyframes only (the first attempt got 2
  keyframes, 0 deltas). Move a window / play a video on the source while capturing.
- **How to re-capture:** run the scratchpad `OpenH264Capture` tool (it requests H.264, guards
  against an MJPEG fallback, and prints a `PROVENANCE:` line), then point the student at the
  Mac's LAN IP.
