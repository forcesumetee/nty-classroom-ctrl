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
 * nty_capture_start — begin capturing the main display at ~fps frames/sec, invoking
 *   `cb` per frame. Returns 0 on success, negative on error (e.g. -1 not permitted,
 *   -2 no display, -3 already running). Implemented in Phase 27-A-3.
 * nty_capture_stop — stop the stream and release resources. Safe to call when idle.
 */
int nty_capture_start(int fps, nty_frame_cb cb, void *ctx);
void nty_capture_stop(void);

/* Optional stats (last delivered frame dimensions + cumulative frame count). */
int nty_last_width(void);
int nty_last_height(void);
int64_t nty_frame_count(void);

#ifdef __cplusplus
}
#endif

#endif /* NTY_CAPTURE_H */
