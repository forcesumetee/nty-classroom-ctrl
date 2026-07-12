# Phase 28 — LIVE Confirmation (Milestone 19: camera peer-cam streaming)

**Date:** 2026-07-13 · **Result:** ✅ **LIVE-CONFIRMED** — the shipped Windows Teacher
v1.2 displays the Mac Sandbox's **camera** live in its **Conference Mode gallery**, over
existing `ConferenceCameraStart/Frame/Stop` envelopes. No shipped-repo change, no
protocol change.

## Test environment
| Role | Machine | Notes |
|---|---|---|
| **Teacher** (shipped **v1.2**, unmodified) | Windows PC | Conference Mode host |
| **Student** (Mac Sandbox, Avalonia + AVCaptureSession) | **borrowed Mac** | packaged `.app` |
| Network | iPhone hotspot | 172.20.10.x subnet |

## Procedure + result (visual verification by tester)
1. Mac: `./scripts/package-app.sh && open ./ClassroomCtrl.Sandbox.app` → Connection tab → **Connect**.
2. ✅ **Mac tile appeared in the Teacher's student grid.**
3. Windows Teacher: **Start Conference Mode** (broadcasts `ConferenceStart{SessionId}`).
   — NOT a right-click "View Camera" (that feature doesn't exist for a single student).
4. Mac: `ConnectionViewModel` decoded the `SessionId` → `CameraStreamer` started the camera,
   sent `ConferenceCameraStart` + `ConferenceCameraFrame{Codec-less JPEG}` upstream.
5. ✅ **The Mac's camera appeared live in the Teacher's Conference gallery tile** (routed by
   `SourceEndpointId` == the Mac's `EndpointId`), streaming **320×180 JPEG**.
6. ✅ Mac self-tile showed **"🔴 Camera live to teacher · N frames sent"**.
7. Windows: **End Conference** → ✅ **Mac stopped cleanly** (`ConferenceCameraStop`).

## Evidence
- **Structural (Mac-side, headless):** `MockTeacher --cameratest` (28-F) →
  **12/12 valid JPEG** (`FFD8..FFD9`), **12/12 correct `SourceEndpointId`**,
  `ConferenceCameraStart` + `ConferenceCameraStop` observed, 320×180 @ ~7.4 KB/frame.
- **LIVE (Windows):** tester **visual confirmation** — Mac camera displayed live in the
  Conference gallery; clean start/stop.
- **Screenshots:** **not captured** — the LIVE run was on **borrowed Mac hardware**
  (no local capture retained). This is the sole documentation gap; the structural
  `--cameratest` artifact + tester visual verification stand in for the pixel proof,
  consistent with the M17/M18 pattern (headless structural proof predicts the live decode).

## Bandwidth
| | Preset-only (28-F bug) | **Shipped (encodeJpegFitted)** |
|---|---|---|
| Delivered dims | 1920×1080 | **320×180** (16:9 fit, no distortion) |
| Per-frame | ~122 KB | **~7.4 KB** |
| Sustained @ 10 fps | ~9.6 Mbit/s | **~0.6 Mbit/s** (~16× reduction) |

## What this confirms
- **Camera peer-cam streaming** from a macOS/Avalonia student into the **shipped,
  unmodified** Windows/WPF Teacher's Conference gallery — existing JPEG envelopes,
  matched to the tile by `SourceEndpointId`.
- **Independent of screen streaming** (M17/M18): separate native session, service, and
  streamer — the two can run concurrently. Screen streaming remained intact.
- The `MockTeacher --cameratest` structural check predicted the live display (as in
  M17/M18), and **caught the preset-ignore bug headless before LIVE**.
- Zero shipped-repo changes, T1–T26 intact.

## Pattern consistency with M17 / M18
| | M17 (MJPEG screen) | M18 (H.264 screen) | **M19 (camera)** |
|---|---|---|---|
| Native API | ScreenCaptureKit + ImageIO | + VideoToolbox | **AVCaptureSession** |
| Wire path | StudentStreamStart/Frame | (same, `Codec=H264`) | **ConferenceCameraStart/Frame/Stop** |
| Teacher surface | `StudentScreenWindow` | (same) | **Conference gallery tile** |
| Structural proof | `--streamtest` | `--streamtest-h264` | **`--cameratest`** |
| LIVE proof | Windows visual | Windows visual | **Windows visual (borrowed Mac)** |

## Boundary (later phases)
Screenshots on owned hardware · multi-camera device switch · 4:3 center-crop option ·
camera + screen concurrent conference share · adaptive quality · **audio (Phase 29,
AVFoundation mic)** — completes the student-media story (screen ✓ → camera ✓ → audio).
