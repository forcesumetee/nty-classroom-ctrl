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
 * H.264 DECODE — VTDecompressionSession (TT-4-B, Sources/H264Decoder.swift).
 * The inverse of the encoder above: wire Annex-B NAL bytes (keyframe =
 * [SPS][PPS][IDR], delta = [slice]; 3- OR 4-byte start codes) → BGRA CVPixelBuffer,
 * delivered SYNCHRONOUSLY per feed (WaitForAsynchronousFrames) so the managed
 * codec-dispatch seam stays synchronous. Decodes the shape BOTH the shipped Windows
 * OpenH264 student and our Mac VideoToolbox student emit.
 *
 * HANDLE-BASED (multi-instance) — unlike the singleton capture/encode ABI above —
 * because the teacher may open 1–4 screen-view windows → 1–4 concurrent decoders.
 * Lifecycle: create → feed* → destroy (exactly once per handle). No display / no
 * Screen Recording permission needed (VideoToolbox operates on buffers).
 * ==========================================================================*/

/*
 * Decoded BGRA frame callback. `bgra` points to the decoded frame (valid ONLY during
 * the call — copy before returning). `bytesPerRow` may exceed width*4 (row padding) —
 * honor it (the M16 stride gotcha).
 */
typedef void (*nty_decoded_cb)(void *ctx, const uint8_t *bgra,
                               int width, int height, int bytesPerRow);

/*
 * nty_h264_decoder_create — create a decoder instance. Returns an opaque handle, or
 *   NULL on failure. The session itself is created lazily on the first keyframe (it
 *   needs the in-band SPS/PPS).
 * nty_h264_decoder_feed — feed one wire frame's Annex-B bytes. Returns 1 if a BGRA
 *   frame was delivered via `cb` (synchronously, before returning), 0 if none yet
 *   (delta before any keyframe / SPS-PPS only), negative on error.
 * nty_h264_decoder_destroy — destroy the instance (call EXACTLY once per handle);
 *   tears down the VTDecompressionSession.
 */
void *nty_h264_decoder_create(void);
int   nty_h264_decoder_feed(void *handle, const uint8_t *annexb, int length,
                            int isKeyframe, nty_decoded_cb cb, void *ctx);
void  nty_h264_decoder_destroy(void *handle);

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

/*
 * Audio playback — Phase 29-F (path A: play the Teacher's broadcast audio).
 * SEPARATE AVAudioEngine + state from capture (can play while capturing). A jitter
 * buffer prebuffers ~3 frames (~300 ms) before starting, then schedules per frame,
 * dropping beyond ~10 pending frames to bound latency.
 * ⚠ Feedback: playing (A) while the mic streams (B) in the same room loops sound —
 * no AEC in M20; use headphones or test A/B separately.
 *   nty_audio_play_start — start playback engine at sampleRate×channels (0 → 16000/1).
 *     Returns 0, -3 already running, -7 engine failed.
 *   nty_audio_play_pcm   — enqueue one PCM16-LE frame (call-scoped bytes).
 *   nty_audio_play_stop  — stop playback + release. Safe when idle.
 */
int nty_audio_play_start(int sampleRate, int channels);
void nty_audio_play_pcm(const uint8_t *data, int length);
void nty_audio_play_stop(void);

/*
 * Multi-source mixer — TT-9-C (path A of TT-9: teacher mixes N students' mics).
 * The REUSABLE CORE, keyed by an opaque Int32 source id — TT-11's student peer mixer
 * reuses the same ABI. SEPARATE engine/state from capture + single-stream playback.
 *
 * Invariant: ONE AVAudioPlayerNode per source, summed by mainMixerNode. A starved node
 * plays SILENCE and never blocks → one stalled sender never silences the mix (the
 * shipped NAudio ReadFully semantics, structural). Per-source gain = 1/sqrt(count)
 * (power-preserving; avoids the shipped mixer's un-normalized clipping). The CAP is a
 * managed-layer policy (TeacherAudioMixer); the core mixes whatever it is given.
 *   nty_mix_start          — start the mix engine (0 → 16000/1). 0, -3 running, -7 engine.
 *   nty_mix_add            — register a source (player node + gain recompute). Idempotent.
 *   nty_mix_push           — enqueue one PCM16-LE frame for a source (dropped if unregistered).
 *   nty_mix_remove         — remove a source (stop + detach + gain recompute). Disconnect cleanup.
 *   nty_mix_stop           — tear the engine down. Safe when idle.
 *   nty_mix_active_count   — number of registered sources.
 *   nty_mix_rendered_frames— output buffers rendered (advances while the mix plays).
 *   nty_mix_output_rms     — last mixed-output RMS 0..100 (level meter).
 *   nty_mix_source_played  — frames PLAYED for a source (freezes on stall; -1 if unregistered).
 */
/*
 * TT-10-C — "Share Computer Audio": capture SYSTEM audio (ScreenCaptureKit capturesAudio, macOS 13+)
 * and deliver 100 ms PCM16 16 kHz mono frames via nty_pcm_cb (SCK is asked for 16 kHz mono directly,
 * no resampler). TCC = Screen Recording (a SIGNED BUNDLE — a bare binary has no TCC identity). The
 * teacher broadcasts these as AudioStreamFrame 0x0329 (no wire change). Proven by nty_sysaudio_probe.
 *   nty_sysaudio_start — 0 ok, -2 no display, -3 SCK/Screen-Recording error, -4 null cb, -5 pre-13.
 *   nty_sysaudio_stop  — stop + release. Safe when idle.
 *   nty_sysaudio_probe — diagnostic (out: audioBuffers, nonSilent, screenBuffers, maxAbs×1000); 0 ok / -2 / -3 / -4 pre-13.
 */
int nty_sysaudio_start(nty_pcm_cb cb, void *ctx);
void nty_sysaudio_stop(void);
int nty_sysaudio_probe(int durationMs, int *outAudioBuffers, int *outNonSilent, int *outScreenBuffers, int *outMaxAbsMilli);

int nty_mix_start(int sampleRate, int channels);
void nty_mix_add(int sourceId);
void nty_mix_push(int sourceId, const uint8_t *data, int length);
void nty_mix_remove(int sourceId);
void nty_mix_stop(void);
int nty_mix_active_count(void);
int64_t nty_mix_rendered_frames(void);
int nty_mix_output_rms(void);
int64_t nty_mix_source_played(int sourceId);

/* ==========================================================================
 * Screen lock — Phase 30-B (Sources/Lock.swift).
 * HARD kiosk-lite lock (exceeds the soft Windows teacher-lock): a borderless
 * shield NSWindow at CGShieldingWindowLevel() on EVERY display + kiosk
 * NSApplicationPresentationOptions (disable Cmd+Tab / Force-Quit / logout /
 * Dock / menu bar / Apple menu / hide). NO Accessibility permission. Re-asserts
 * on resignActive + wake; rebuilds on display hotplug. Main-thread (dispatched
 * internally). NO in-shield escape hotkey.
 *
 * SAFETY: the DEAD-MAN switch (disconnect->45s-grace unlock, 30-min cap, wake
 * re-assert) lives in the .NET LockService (30-C). Backstop by construction:
 * the shield + options are owned by THIS process, so killing it releases the
 * presentation options (OS-enforced) + drops the windows = auto-unlock.
 *   nty_lock_show — show/refresh the shield with `message` (NULL/empty -> default).
 *   nty_lock_hide — remove the shield + clear presentation options.
 *   nty_lock_is_shown — 1 if the shield is up, else 0.
 * ==========================================================================*/
void nty_lock_show(const char *message);
void nty_lock_hide(void);
int nty_lock_is_shown(void);

/* ==========================================================================
 * Keystroke guard (CGEventTap) — Phase 31-B (Sources/Input.swift).
 * ADDITIVE hardening on top of the lock: suppresses a SMALL, explicit set of
 * system shortcuts (Spotlight, Mission Control / Exposé / Spaces, Cmd+Tab,
 * Cmd+`) to close the two residuals the Phase 30 shield can't reach. It is NOT
 * the lock — the shield + presentation options enforce the lock with zero
 * Accessibility; this tap needs Accessibility and is best-effort. Cmd+Q,
 * volume/brightness/media, and screenshots pass through untouched.
 *
 * SAFETY: the tap runs on its OWN CFRunLoop thread (a busy main thread can't
 * stall it) and FAILS OPEN — if the callback is slow the OS auto-disables the
 * tap (.tapDisabledByTimeout) with keys already flowing, and we re-enable to
 * resume; a wedged keyboard is not a reachable state. Killing the process tears
 * the tap down (OS-enforced). LockService (31-C) installs it only while locked
 * and removes it on every unlock path; nty_input_guard_stop is unconditional +
 * bounded (joins the tap thread with a 2 s ceiling — never hangs).
 * ==========================================================================*/

/*
 * Accessibility permission (required to install a SUPPRESSING tap; a different TCC
 * bucket than Screen Recording / Camera / Mic). If denied, the caller keeps the lock
 * and skips the tap (graceful degrade).
 *   nty_accessibility_check   — 1 if THIS process is trusted, else 0. Never prompts.
 *   nty_accessibility_request — prompt if undetermined; returns trust at call time (1/0).
 *     Grant is async (System Settings ▸ Privacy ▸ Accessibility) — then poll _check.
 *     Ad-hoc-signed builds may need re-granting after a rebuild.
 */
int nty_accessibility_check(void);
int nty_accessibility_request(void);

/*
 * nty_input_guard_start — install the keystroke guard. Returns 0 on success, or a
 *   negative code for the caller to DEGRADE GRACEFULLY: -1 not Accessibility-trusted
 *   (keep the lock, skip the tap), -2 tap creation failed, -3 already running.
 * nty_input_guard_stop  — UNCONDITIONAL + BOUNDED uninstall. Safe when idle; never
 *   hangs (joins the tap thread with a 2 s ceiling).
 * nty_input_guard_is_active — 1 if a tap is currently installed, else 0.
 */
int nty_input_guard_start(void);
void nty_input_guard_stop(void);
int nty_input_guard_is_active(void);

/*
 * Diagnostics / test hooks — used by MockTeacher --inputtest to prove guaranteed
 * uninstall and DEMONSTRATE fail-open. Harmless in production (the stall/synthesize
 * levers are never invoked outside the harness).
 *   nty_input_guard_suppress_count — # shortcuts swallowed since start.
 *   nty_input_guard_disabled_count — # OS auto-disable events caught + re-enabled (fail-open proof).
 *   nty_input_guard_is_enabled     — 1 if the tap is enabled (guarding), 0 if disabled (keys flow) / absent.
 *   nty_input_guard_set_stall_ms   — TEST-ONLY: force a slow callback (ms) to trip the OS watchdog. 0 = off.
 *                                    Arm it BEFORE nty_input_guard_start (thread creation publishes it).
 *   nty_input_test_synthesize      — TEST-ONLY: synthesize `count` key presses (keycode, CGEventFlags raw).
 */
int64_t nty_input_guard_suppress_count(void);
int64_t nty_input_guard_disabled_count(void);
int nty_input_guard_is_enabled(void);
void nty_input_guard_set_stall_ms(int ms);
void nty_input_test_synthesize(int keycode, uint64_t flags, int count);

/*
 * TT-13 / TOR 11.2.9 — record the teacher's SCREEN + system AUDIO to a .mov (AVAssetWriter).
 *   nty_record_start(path)  0 ok / -2 no display / -3 SCK start (grant Screen Recording + RELAUNCH)
 *                           / -4 needs macOS 13 / -5 already recording / -6 writer init
 *   nty_record_stop()       0 ok / -1 not recording
 *   nty_record_is_active()  1/0
 *   nty_record_video_frames / nty_record_audio_frames — appended-sample counters (gate + UI).
 */
int nty_record_start(const char *path);
int nty_record_stop(void);
int nty_record_is_active(void);
int64_t nty_record_video_frames(void);
int64_t nty_record_audio_frames(void);

#ifdef __cplusplus
}
#endif

#endif /* NTY_CAPTURE_H */
