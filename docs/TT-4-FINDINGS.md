# TT-4 Findings — H.264 screen decode (VTDecompressionSession) · COMPLETE (LIVE-confirmed)

**Goal:** the Mac Teacher DECODES a student's H.264 screen stream — the one big new native
piece. **LIVE gate met, both sub-gates:** a **Mac Student** (our M18 VideoToolbox encoder,
customer B's path) and a **shipped Windows Student** ("BELL", OpenH264, the interop path)
each stream H.264 that renders live in the Mac Teacher. Constraints held: `native/` + Teacher
app only, Shared.Wire unchanged, **shipped Windows repo untouched**.

Sub-phases: **TT-4-A** investigation · **TT-4-B** native decoder + headless round-trip ·
**TT-4-C** managed wrapper + wiring + fixtures + Skia-headless gate · **TT-4-D** LIVE ·
**TT-4-E** this close-out.

---

## THE BITSTREAM FINDING — the phase's stated risk, retired by investigation
Every student puts the **same shape** on the wire, whatever the encoder:
- **Windows:** OpenH264 (default) or MediaFoundation HW (opt-in `UseHardwareH264` registry flag)
  — both emit decoder-compatible output (the shipped H264Sharp decoder consumes all tiers).
- **Mac:** our M18 VideoToolbox encoder.

The shape: **Annex-B, Baseline** (`profile_idc=66` — **confirmed in BELL's captured SPS**),
**in-band SPS/PPS prepended to every IDR**, **fixed 1920×1080**, IDR every ~2 s **plus a forced
keyframe on stream-start**. **One decode path handles all sources.** The decisive detail: our
M18 encoder was *built to byte-match the shipped OpenH264 output* — so the work from 10 sessions
ago was the spec for this decoder, and its AVCC→Annex-B conversion is the exact **inverse**
reference for the decoder's Annex-B→AVCC.

## THE DECODE PATH (`native/NtyCapture/Sources/H264Decoder.swift`)
Annex-B → parse NALs (**3- and 4-byte start codes**) → on a keyframe, SPS(7)/PPS(8) →
`CMVideoFormatDescriptionCreateFromH264ParameterSets` → `VTDecompressionSessionCreate`
requesting **`kCVPixelFormatType_32BGRA`** (no managed colour conversion) → VCL slices → **AVCC**
(`[4-byte BE length][NAL]`) → `CMSampleBuffer` → decode → BGRA (**`bytesPerRow` honored** — the
M16 stride gotcha) → managed `WriteableBitmap.Lock()` + row-by-row `Marshal.Copy`.

## THE ABI SHAPE — handle-based (first multi-instance native subsystem)
`nty_h264_decoder_create` / `feed` / `destroy` — **handle-based, NOT the singleton shape** the
capture/encode/camera/audio subsystems use. Reason: a student captures **one** screen; a teacher
opens **1–4** screen views → 1–4 concurrent decoders. `destroy()` is called exactly once per
handle (the managed `Dispose` enforces it); the decoder's `stop()`/`deinit` are idempotent and
tear the `VTDecompressionSession` down, so a dropped handle can't leak — and the
`ScreenViewController` guarantees `Stop` on every close path, so a handle can't outlive its window.

## THE SEAM HELD — TT-4 was additive, exactly as designed
**Synchronous feed** (`WaitForAsynchronousFrames`, safe because Baseline has no frame reordering)
preserved TT-3-C's synchronous `RenderFrame → Bitmap?` contract. The subscription, studentId
filter, request/stop lifecycle, UI-thread marshal, and `Image.Source`/`CurrentFrame` target were
**all untouched**. TT-4's diff was the H264 branch + a per-view decoder handle riding the existing
Start/Stop lifecycle — as the TT-4-A honest-scope note promised. `WriteableBitmap : Bitmap`
(verified) → `CurrentFrame` accommodated it with no type change; the dispose-previous-frame logic
treats a decoded WriteableBitmap exactly like an MJPEG Bitmap.

## THE START-CODE FINDING (honest)
BELL's real bitstream measured **0 × 3-byte, 9 × 4-byte** start codes — this shipped build emits
**4-byte only**. So the decoder's **3-byte parse is proven synthetically** (the TT-4-B round-trip
rewrites a keyframe's start codes to 3-byte and decodes it), **not by real Windows data**. TT-4-A
was right that OpenH264 *may* emit both (the shipped student's own hex-dump comment says
"`00 00 00 01` or `00 00 01`"); this build doesn't. **Keep the 3-byte path** — it costs nothing
and another OpenH264 configuration might use it.

## THE FIXTURE — the interop proof, now headless + regression-protected
`native/NtyCapture/fixtures/h264-openh264-bell.fixture` — a real shipped-Windows-Student H.264
sample (5 frames: **2 keyframes + 3 deltas**, 192 423 bytes, 1920×1080). The two keyframes come
from BELL's `ForceKeyframe`-on-Start IDR **plus** the ~2 s cadence IDR, so the fixture also
exercises **mid-stream keyframe handling** (re-fed SPS/PPS match → the decoder keeps its session,
doesn't recreate it). Committed with provenance. **This makes the OpenH264→VideoToolbox interop
proof HEADLESS and REGRESSION-PROTECTED** — previously it would have rested on a single LIVE run.
**Capture caveat (recorded):** a static source screen emits **no deltas** (the first attempt got
2 keyframes and nothing else) — the source student's screen must be actively moving.

## MJPEG FALLBACK — degraded-but-working beats a dead window
An undecodable H.264 keyframe → `Stop` + `Request(Mjpeg)` (the student honors the codec, and
`StudentStreamStop` nulls its broadcaster so the new MJPEG `Start` takes effect) + a **visible
status** ("H.264 unavailable — using MJPEG"). Visible, not silent, so a real problem can't hide;
and MJPEG is LIVE-proven (TT-3), so the view still works.

## THE SCREEN-RECORDING CAVEAT resurfaced (flag for P35, not a TT-4 defect)
Sub-gate 1 (the Mac Student) **could not run under `dotnet run`** — the Screen Recording grant is
bound to Terminal and TCC caches at process launch, so the restart-caveat kept firing even after
restarting Terminal. Running the Sandbox **as the bundle** ("NTY ClassroomCtrl.app") worked
immediately (its grant persists from 32-G). Same ad-hoc-signature friction M22/32-F recorded — a
**distribution gap that P35's Developer ID signing eliminates**, not a port defect.

## Verification (all green)
- **Native round-trip (TT-4-B, headless):** M18 encode → decode → BGRA — **13/13** (both
  start-code lengths, delta-before-keyframe drop, malformed recovery, idempotent stop).
- **Skia-headless gate (TT-4-C, TT4CGate): 18/18** — VT (our encoder) **and** OpenH264 (BELL)
  both decode → exact 1920×1080 WriteableBitmap; delta-waits; malformed keeps last frame; MJPEG
  fallback; **C-ABI create/decode/destroy ×20 no leak/crash**; disposed on Stop.
- **MJPEG path (TT3BSmoke): still green** (lifecycle + marshal + filter + controller; request
  now H.264). Non-regression: full solution build 0 errors; **T1-T27 PASS**; `--selftest` PASS;
  **`--teacherselftest` PASS**. Sandbox / Shared.Wire / Teacher.Core unchanged; shipped repo
  untouched.
- **LIVE (TT-4-D, 2026-07-14):** Mac Student (bundle) **and** BELL (Windows) → H.264 renders on
  the Mac Teacher. See `docs/TT-4-LIVE-CONFIRMATION.md`.

## What TT-4 unlocks · next
The Mac Teacher now shows a student's live screen in **H.264** (efficient, ~10× MJPEG) from
**both Windows and Mac students**, with an MJPEG safety net. Both of the Teacher track's biggest
risks — **scale** (TT-3: on-demand phantom) and **the bitstream** (TT-4: one uniform Baseline
shape) — are retired. **Next: TT-5** (core commands — lock/unlock, policy, power), the full-circle
interop where a macOS Teacher sends the exact messages the macOS Student already receives.
