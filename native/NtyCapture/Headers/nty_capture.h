/*
 * nty_capture.h — C ABI for libNtyCapture.dylib (Phase 27-A)
 * ----------------------------------------------------------------------------
 * Minimal, stable C boundary between the Avalonia/.NET Sandbox and macOS
 * ScreenCaptureKit. The Swift side (Sources/NtyCapture.swift) implements these
 * via @_cdecl so they export as plain C symbols; the .NET side P/Invokes them
 * (Services/ScreenCaptureService.cs). This header is the source of truth for the
 * signatures — keep the three sides (header / Swift @_cdecl / C# LibraryImport)
 * in lockstep.
 *
 * This is the interop TEMPLATE for every Phase 27 subsystem (camera, audio,
 * screen-lock enforcement, input hooks): a tiny Swift dylib + a ~handful-function
 * C ABI + a GC-rooted callback for streamed data.
 */
#ifndef NTY_CAPTURE_H
#define NTY_CAPTURE_H

#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * Screen Recording permission (TCC).
 *
 * nty_check_permission — returns 1 if Screen Recording is already authorized for
 *   THIS binary, else 0. Never prompts (CGPreflightScreenCaptureAccess).
 *
 * nty_request_permission — triggers the system TCC prompt if the status is
 *   undetermined, and returns the authorization state AT CALL TIME (1/0).
 *   IMPORTANT: the user's decision is asynchronous (a system dialog), and for
 *   Screen Recording specifically the grant does not take effect until the app is
 *   RELAUNCHED (TCC caches the capture entitlement at process launch). So the
 *   contract is: call request → guide the user to grant → relaunch → poll
 *   nty_check_permission. (CGRequestScreenCaptureAccess.)
 */
int nty_check_permission(void);
int nty_request_permission(void);

/*
 * Frame callback. Invoked once per captured frame on ScreenCaptureKit's delivery
 * queue (NOT the main thread). `bgra` points to tightly-or-padded BGRA8888 pixels
 * and is valid ONLY for the duration of the call — the receiver must copy before
 * returning. `bytesPerRow` may exceed width*4 (row padding) — honor it.
 *   ctx         — opaque pointer passed back from nty_capture_start (unused today).
 *   bgra        — BGRA8888 base address (premultiplied alpha).
 *   width/height— pixel dimensions.
 *   bytesPerRow — stride in bytes (>= width*4).
 */
typedef void (*nty_frame_cb)(void *ctx, const uint8_t *bgra,
                             int width, int height, int bytesPerRow);

/*
 * JPEG frame callback (27-C). `jpeg` points to `length` bytes of a complete JPEG
 * (call-scoped — copy before returning). width/height are the encoded (downscaled)
 * dimensions.
 */
typedef void (*nty_jpeg_cb)(void *ctx, const uint8_t *jpeg,
                            int length, int width, int height);

/*
 * nty_capture_start — begin capturing the main display at ~fps frames/sec, invoking
 *   `cb` per frame. Returns 0 on success, negative on error (e.g. -1 not permitted,
 *   -2 no display, -3 already running). Implemented in Phase 27-A-3.
 * nty_capture_stop — stop the stream and release resources. Safe to call when idle.
 */
int nty_capture_start(int fps, nty_frame_cb cb, void *ctx);

/*
 * nty_capture_start_jpeg — capture the main display, downscale to fit maxW x maxH
 *   (aspect-preserving, never upscales), JPEG-encode at `quality` (0-100), and
 *   deliver each frame's bytes via `cb`. Returns 0 on success, negative on error.
 *   Matches the shipped StudentBroadcaster (1280x720, quality 60, ~4 fps).
 */
int nty_capture_start_jpeg(int fps, int quality, int maxW, int maxH,
                           nty_jpeg_cb cb, void *ctx);

/*
 * H.264 frame callback (27-B). `nal` = concatenated Annex-B NAL units for one frame
 * (keyframe = [SPS][PPS][IDR], delta = [slice]); call-scoped. isKeyframe = 1 for IDR.
 */
typedef void (*nty_h264_cb)(void *ctx, const uint8_t *nal,
                            int length, int width, int height, int isKeyframe);

/*
 * nty_capture_start_h264 — capture the main display at 1920x1080 and H.264-encode via
 *   VideoToolbox (Baseline, CBR, IDR every fps*2), delivering Annex-B NAL bytes per
 *   frame. `bitrateKbps` in kbit/s (e.g. 1500). Matches the shipped OpenH264 wire format.
 *   Returns 0 on success, negative on error.
 */
int nty_capture_start_h264(int fps, int bitrateKbps, nty_h264_cb cb, void *ctx);

void nty_capture_stop(void);

/* Optional stats (last delivered frame dimensions + cumulative frame count). */
int nty_last_width(void);
int nty_last_height(void);
int64_t nty_frame_count(void);

/* ==========================================================================
 * Camera (webcam) capture — Phase 28-B (Sources/Camera.swift).
 * AVCaptureSession → BGRA CVPixelBuffer → shared ImageIO JPEG encode. Camera
 * frames are JPEG-only on the wire (ConferenceCameraFrame has no codec field),
 * so there is no H.264 camera path. Independent session state from the screen
 * path above (they can run concurrently).
 * ==========================================================================*/

/*
 * Camera privacy permission (a DIFFERENT TCC bucket than Screen Recording; the
 * grant is effective immediately — no relaunch — and the Info.plist
 * NSCameraUsageDescription text IS shown to the user).
 *   nty_camera_check_permission   — 1 authorized, 0 not-determined, -1 denied/restricted. Never prompts.
 *   nty_camera_request_permission — prompts if undetermined, BLOCKS until the user decides, returns 1/0/-1.
 */
int nty_camera_check_permission(void);
int nty_camera_request_permission(void);

/*
 * Device enumeration.
 *   nty_camera_count — number of video devices (built-in + external).
 *   nty_camera_name  — copy device[index].localizedName as UTF-8 (NUL-terminated)
 *     into `buf` (capacity `bufLen`); returns bytes written (excl. NUL) or -1.
 */
int nty_camera_count(void);
int nty_camera_name(int index, uint8_t *buf, int bufLen);

/*
 * nty_camera_start_jpeg — start capturing device[deviceIndex] at a preset chosen
 *   from width x height (320x240 = the shipped peer-cam format), JPEG-encode each
 *   frame at `quality` (0-100), throttled to ~fps, delivered via `cb` (reuses the
 *   nty_jpeg_cb signature). Returns 0 on success, negative on error (-2 bad index,
 *   -3 already running, -4 null cb, -5 input, -6 output).
 * nty_camera_stop — stop the camera session. Safe to call when idle.
 */
int nty_camera_start_jpeg(int deviceIndex, int width, int height, int fps, int quality,
                          nty_jpeg_cb cb, void *ctx);
void nty_camera_stop(void);

/* Camera stats (independent from the screen stats above). */
int nty_camera_last_width(void);
int nty_camera_last_height(void);
int64_t nty_camera_frame_count(void);

/* ==========================================================================
 * Microphone capture — Phase 29-B (Sources/Audio.swift).
 * AVAudioEngine mic tap → AVAudioConverter → raw PCM (16 kHz mono 16-bit LE,
 * 100 ms / 3200-byte frames), matching the shipped NAudio wire format. No codec.
 * Independent session state (coexists with screen + camera).
 * ==========================================================================*/

/*
 * Microphone permission (distinct TCC bucket; grant effective immediately, no
 * relaunch; the Info.plist NSMicrophoneUsageDescription text IS shown).
 *   nty_audio_check_permission   — 1 authorized, 0 not-determined, -1 denied/restricted. Never prompts.
 *   nty_audio_request_permission — prompts if undetermined, BLOCKS until the user decides, returns 1/0/-1.
 */
int nty_audio_check_permission(void);
int nty_audio_request_permission(void);

/*
 * PCM frame callback. `pcm` = `length` bytes of signed 16-bit LE samples for one
 * 100 ms frame (call-scoped — copy before returning). sampleRate/channels describe
 * the frame (16000/1 today).
 */
typedef void (*nty_pcm_cb)(void *ctx, const uint8_t *pcm,
                           int length, int sampleRate, int channels);

/*
 * nty_audio_start_pcm — start mic capture, delivering 100 ms PCM16 frames at
 *   sampleRate×channels (0 → default 16000/1) via `cb`. Returns 0 on success,
 *   negative on error (-2 no input/permission, -3 already running, -4 null cb,
 *   -6 converter, -7 engine).
 * nty_audio_stop — stop capture. Safe to call when idle.
 */
int nty_audio_start_pcm(int sampleRate, int channels, nty_pcm_cb cb, void *ctx);
void nty_audio_stop(void);

/* Audio stats: cumulative frame count + last-frame RMS level (0..100 for a meter). */
int64_t nty_audio_frame_count(void);
int nty_audio_last_rms(void);

#ifdef __cplusplus
}
#endif

#endif /* NTY_CAPTURE_H */
