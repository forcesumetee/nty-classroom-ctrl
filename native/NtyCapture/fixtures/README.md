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

### `h264-openh264-bell.fixture` — OpenH264→VT (the interop proof) — CAPTURE PENDING
- **Source:** a real, shipped Windows Student ("BELL", v1.2) streaming H.264.
- **How to (re)capture:** run the scratchpad `OpenH264Capture` tool, point BELL at the Mac's
  LAN IP; it requests H.264 on join and dumps `[SPS][PPS][IDR]` + a few deltas in this
  format. Paste the tool's `PROVENANCE` line here when captured.
- **Proves:** the shipped Windows student's OpenH264 Baseline stream (which may use **3-byte
  start codes**, unlike VideoToolbox's 4-byte) decodes on the Mac Teacher — **scenario 3
  (cross-platform).** Until captured, `TT4CGate` SKIPs this assertion and the interop is
  covered only by the TT-4-D LIVE run.
