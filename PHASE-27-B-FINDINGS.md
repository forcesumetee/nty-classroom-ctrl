# Phase 27-B Findings — VideoToolbox H.264 encoding (Milestone 18, Mac-side)

**Goal:** H.264 screen streaming to the Windows Teacher for ~10× less bandwidth.
**Result:** ✅ Mac-side complete + verified — VideoToolbox encodes hardware H.264, the
bytes match the shipped OpenH264 Annex-B format exactly, and `MockTeacher
--streamtest-h264` confirms 12/12 well-formed frames at **~16 KB/frame vs MJPEG's ~124 KB**.
**LIVE Windows decode = the one remaining human step.**

**Sub-phases:** 27-B-2 native encoder · 27-B-3 service · 27-B-4 wire · 27-B-5 verify · 27-B-7 docs.

## Investigation (27-B-1) — the byte-format contract
The Teacher decodes via **H264Sharp → OpenH264 (Cisco)** (`H264DecoderWrapper`), which is
**Annex-B** and needs **in-band SPS/PPS on every IDR**. The shipped `H264EncoderWrapper`
(OpenH264) emits exactly that: concatenated Annex-B NALs, `[SPS][PPS][IDR]` on keyframes,
`SCREEN_CONTENT_REAL_TIME`, CBR, IDR every `fps×2`, 1920×1080, 1.5 Mbit/s. **My VideoToolbox
output had to match this byte-shape** — and does. No shipped-repo change, no wire change
(`Codec=H264` + `IsKeyframe` already exist).

## The VideoToolbox → OpenH264 interop details (the ones that matter)
1. **VideoToolbox emits AVCC** (length-prefixed: `[4-byte BE length][NAL]…`); OpenH264 wants
   **Annex-B** (`00 00 00 01` start codes). **Convert every NAL** in the output handler.
2. **SPS/PPS are out-of-band** in VideoToolbox (in the `CMFormatDescription`), but OpenH264
   needs them **in-band before each IDR**. On keyframes, pull them via
   `CMVideoFormatDescriptionGetH264ParameterSetAtIndex` (index 0=SPS, 1=PPS) and **prepend**
   as Annex-B → `[SPS][PPS][slices]`.
3. **Keyframe detection:** a *sync sample* = keyframe → check the absence/false of
   `kCMSampleAttachmentKey_NotSync` in the sample-attachments array.
4. **Config for low-latency screen share:** `ProfileLevel = Baseline_AutoLevel`,
   `AllowFrameReordering = false` (no B-frames), `RealTime = true`,
   `AverageBitRate`, `MaxKeyFrameInterval = fps×2`, HW-accel via
   `EnableHardwareAcceleratedVideoEncoder`. Use the block-based
   `VTCompressionSessionEncodeFrame(..., outputHandler:)` (create the session with a nil
   output callback) — cleaner than a C callback + refcon.
5. **PTS:** feed SCStream's `CMSampleBufferGetPresentationTimeStamp` straight through
   (monotonic); VideoToolbox needs increasing PTS.

## Verification (Mac, no Windows box)
`MockTeacher --streamtest-h264` — real WireClient + ScreenStreamer → `StudentStreamFrame`
{Codec=H264} → server parses Annex-B NALs. **PASS:**
- 12/12 well-formed · **first frame is a keyframe** · **all frames Annex-B** · **keyframes =
  SPS(7)+PPS(8)+SEI(6)+IDR(5)** · **deltas = slice(1)** · 1920×1080.
- **~16 KB/frame** (H.264) **vs ~124 KB/frame** (MJPEG) → **~8× smaller per frame**, and at
  1080p vs MJPEG's 1107×720 → **~10× per-pixel**. Standalone encoder run measured **1.16
  Mbit/s** sustained.
- MJPEG `--streamtest` + `--selftest` still PASS — **backward compatible** (H.264 is an
  added codec; the Teacher picks via `StudentStreamStartRequest.Codec`, default `Mjpeg`).

**Verification boundary (as agreed):** `H264Sharp`/OpenH264 is Windows-native, so Mac
verification is **NAL-structure well-formedness** (very strong for interop — VideoToolbox is
a conformant encoder producing exactly the shipped Annex-B shape). **Definitive pixel decode
= the LIVE Windows test.** (`ffprobe`/VT-decode round-trip = optional future bonus.)

## Codec selection (backward compat)
`ScreenStreamer.StartAsync(client, codec)` branches: **H264 → VideoToolbox path**, **Mjpeg →
the 27-C path**. `ConnectionViewModel` decodes `StudentStreamStartRequest.Codec` (default
MJPEG if empty). Self-tile shows "🔴 Streaming H.264 · N frames". One capture at a time
(native singleton).

## New pattern → cheat sheet §20 addendum
"Native video encode with VideoToolbox" (AVCC→Annex-B + in-band SPS/PPS + Baseline/no-reorder)
appended to §20 — reusable for the camera / conference encode subsystems.

## LIVE Windows test — the bandwidth-win moment (user-run)
1. Windows: Teacher v1.2 (its default `App.SelectedCodec` = H264 → it requests H.264).
2. Mac: `./scripts/package-app.sh && open ./ClassroomCtrl.Sandbox.app` → Connect.
3. Teacher: right-click Mac tile → **View Screen** → Mac desktop via **`RenderH264`**.
4. Note the bandwidth drop vs the M17 MJPEG run; photo for comparison.

## Constraints honored
Sandbox + `native/` + `tools/MockTeacher` only · `Shared.Wire` unchanged · shipped repo
untouched · **MJPEG still works** · T1–T26 PASS · per-sub-phase commits.

## Deferred
Multi-display · cursor toggle · region capture · adaptive bitrate · H.264 profile tuning ·
VT-decode round-trip pixel proof.
