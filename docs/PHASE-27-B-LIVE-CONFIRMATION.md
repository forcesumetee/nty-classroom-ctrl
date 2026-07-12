# Phase 27-B — LIVE Confirmation (Milestone 18: H.264 screen streaming)

**Date:** 2026-07-13 · **Result:** ✅ **LIVE-CONFIRMED** — the shipped Windows Teacher v1.2
displays the Mac Sandbox's desktop via **H.264** (VideoToolbox → OpenH264), at **~10× less
bandwidth** than the Milestone-17 MJPEG stream. Over **existing** wire envelopes; no
shipped-repo change, no protocol change.

## Test environment (identical to M17)
| Role | Machine | Address |
|---|---|---|
| **Teacher** (shipped **v1.2**, `App.SelectedCodec = H264` default) | Windows PC | **172.20.10.7:7777** |
| **Student** (Mac Sandbox, Avalonia + ScreenCaptureKit + VideoToolbox) | Suwijaks-MacBook-Air | 172.20.10.x |
| Network | iPhone hotspot | 172.20.10.x subnet |

Mac launched from the packaged `.app` (`./scripts/package-app.sh` → `open …`).

## Procedure + result
1. Mac: Connection tab → **Connect** to 172.20.10.7.
2. Windows Teacher: right-click the Mac tile → **View Screen**. The Teacher's default codec
   is **H264**, so it sends `StudentStreamStart { Codec=H264 }`.
3. Mac: `ScreenStreamer` starts the **VideoToolbox H.264** path → `StudentStreamFrame
   { Codec=H264, IsKeyframe }` upstream (Annex-B, in-band SPS/PPS per IDR).
4. **✅ The Mac's desktop appears live in the Teacher's `StudentScreenWindow` via `RenderH264`**
   (H264Sharp/OpenH264). Self-tile shows "🔴 Streaming H.264 · N frames sent".

## Bandwidth observations
| | Milestone 17 (MJPEG) | **Milestone 18 (H.264)** |
|---|---|---|
| Codec | MJPEG | **H.264 Baseline** |
| Resolution | 1107×720 | **1920×1080** |
| Per-frame | ~124–137 KB | **~16 KB** |
| Sustained bitrate | ~6.5 Mbit/s | **~1.16 Mbit/s** |
| Efficiency | baseline | **~8× fewer bits at ~2.4× the pixels ≈ ~10× per-pixel** |
| Quality | good | **production-grade** |

**Live observation:** ~1.16 Mbit/s at 1080p, matching the Mac-side measurement — the ~10×
per-pixel win holds on real hardware.

## What this confirms
- **Byte-compatible H.264 streaming** from a macOS/Avalonia student to the **shipped,
  unmodified** Windows/WPF Teacher — VideoToolbox output matched the shipped OpenH264 Annex-B
  format exactly (start codes + in-band SPS/PPS per IDR).
- **Backward compatible:** MJPEG (M17) still works; the codec is chosen per
  `StudentStreamStartRequest.Codec`.
- The `MockTeacher --streamtest-h264` NAL-structure check predicted the live decode exactly
  (12/12 well-formed → real display live).
- Zero shipped-repo changes, T1–T26 intact.

## Evidence
- Mac-side UI: `docs/phase-27-b-streaming-h264.png` (self-tile "Streaming H.264" + smaller frames).
- Structural proof: `MockTeacher --streamtest-h264` → `.h264` sample, 12/12 well-formed.
- **LIVE Windows screenshots:** `docs/live-test-milestone-17/` (add M18 shots here — Mac
  desktop in the Teacher via H.264, ideally with a bandwidth readout).

## Boundary (later phases)
Adaptive bitrate · multi-display · cursor toggle · region capture · H.264 profile tuning ·
camera/audio (27-D) · lock/policy enforcement.
