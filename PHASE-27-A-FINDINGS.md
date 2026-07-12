# Phase 27-A Findings — ScreenCaptureKit basic capture (native interop template)

**Goal:** capture the Mac's main display via ScreenCaptureKit and show it live in the
Sandbox. **Result:** ✅ working prototype — permission flow + live BGRA capture at ~9 fps
rendered in the Screen Capture tab (`docs/phase-27-a-screencapture.png`). This establishes
the **native-interop template for every Phase 27 subsystem** (camera, audio, screen-lock
enforcement, input hooks).

**Sub-phases:** 27-A-2 permission + `.app` bundle · 27-A-3 capture (swift / net / ui) · 27-A-4 docs.

## The interop pattern (reuse this verbatim)
```
Avalonia Sandbox (net10.0, unchanged)          native/NtyCapture/ → libNtyCapture.dylib
  ScreenCaptureService (LibraryImport)  ──C ABI──▶  @_cdecl wrappers
  [UnmanagedCallersOnly] static callback  ◀─frames──  SCStream / SCStreamOutput
  (ctx = GCHandle to service)                        CVPixelBuffer → BGRA
  WriteableBitmap ← Dispatcher.UIThread.Post (§18)
```
Swift owns the framework complexity (async content query, `SCStream`, the `SCStreamOutput`
delegate, `CMSampleBuffer→CVPixelBuffer→BGRA`). The managed side is a ~7-function P/Invoke
surface + one static callback. The app never leaves plain `net10.0` + Avalonia.Desktop.

## Verified (all on this Mac, macOS 26 / Swift 6.1.2)
| Layer | Proof |
|---|---|
| dylib build | `swiftc -emit-library` → exports `_nty_*` C symbols, 0 errors |
| P/Invoke boundary | scratch: `nty_check_permission → 1`; `nty_capture_start → 0` |
| native capture pipeline | scratch: 23 frames / 2.5 s, 1470×956, **stride 5888 > 5880** |
| **managed service** | reflection test on the real `ScreenCaptureService`: 25 frames, `buf.Length == w*h*4` (stride stripped), non-zero pixels, clean stop |
| **full UI render** | headless `screencapture` scenario: live desktop frame in the Image, **fps 9.1 · 1470×956 · dropped 5** |
| `.app` bundle | `package-app.sh` → codesign verify OK, `com.nty.classroom.sandbox`, Mach-O arm64 |
| permission persistence | user-confirmed: fresh bundle → prompt → grant → relaunch → persists |

## ScreenCaptureKit peculiarities (the ones that cost time)
1. **Permission `request` is not a callback.** `CGRequestScreenCaptureAccess()` returns the
   *current* status synchronously and prompts if undetermined. The decision is async, and for
   **Screen Recording the grant only takes effect after an app RELAUNCH** (TCC caches the
   capture entitlement at process launch). Model: request → grant → relaunch → poll `check`.
   No callback needed — polling `check` after relaunch is the contract.
2. **TCC is keyed to the launching binary → bundling is mandatory.** `dotnet run` attributes
   the grant to the `dotnet` host; it won't persist. A minimal `.app` with a stable
   `CFBundleIdentifier` (ad-hoc signed) fixes it. `NSScreenCaptureUsageDescription` is
   cosmetic for Screen Recording (unlike camera/mic) — the prompt text is system-generated.
3. **Row stride ≠ width×4.** `CVPixelBufferGetBytesPerRow` returned **5888** for a 1470-wide
   frame (width×4 = 5880) — 8 bytes of padding. The managed copy MUST go row-by-row honoring
   `bytesPerRow`, or the image shears. Confirmed real, not theoretical.
4. **Buffer is call-scoped.** The `CVPixelBuffer` base address is valid only while locked
   (during the callback). Copy immediately; never keep the pointer.
5. **Async SC APIs bridged to a sync C ABI** with bounded-wait semaphores (5 s content query,
   5 s start, 3 s stop) so a hung TCC/query can't deadlock the caller.
6. **Frame status filter.** SCStream also delivers `.idle`/`.blank` frames (no new pixels);
   skip anything whose attachment status isn't `.complete` to avoid re-pushing stale frames.

## Threading / performance
- Callback fires on a dedicated dispatch queue (never main). Managed side copies (stride →
  packed) then `Dispatcher.UIThread.Post` (§18). Frames **coalesced** — newest renders,
  older-waiting count as **dropped** (5 over ~4.5 s at 12-fps target; UI kept up well).
- **Actual fps ~9.1 vs 12 target** — expected: the `.complete`-only filter skips idle frames,
  so a static screen delivers fewer than the ceiling. Motion pushes it toward the target.
- **Resolution 1470×956** = the display's *point* size (SCDisplay.width/height); a Retina
  panel's native pixels are 2× — capturing at point size is fine for a prototype (smaller
  frames, less bandwidth). Native-pixel capture is a later scaling knob.
- **GC churn (prototype, not optimized):** ~2 allocations/frame (packed byte[] + a
  WriteableBitmap when size-stable we still reassign to force redraw). At ~9 fps × 5.6 MB this
  is ~100 MB/s of short-lived garbage — fine for a prototype, no leak (GCHandle freed on stop).
  Optimizations for later: pool the byte[], reuse one WriteableBitmap + invalidate instead of
  reassign, capture at a target width.

## New pattern → cheat sheet §20
"Native macOS interop — Swift dylib + P/Invoke + streamed callback + TCC/bundle." The four
rules that bite: copy-in-callback, honor-stride, marshal-to-UI, bundle-for-TCC. 19 → **21
sections** (§20 + Reference links → §21).

## Deferred (as planned)
H.264 encoding → Phase 27-B (VideoToolbox) · send to Windows Teacher → integration · multi-
display · cursor toggle · region capture · optimization.

## Files
- `native/NtyCapture/` — Swift `@_cdecl` sources, C header, `build.sh`, README (the dylib).
- `Services/ScreenCaptureService.cs` — P/Invoke + `[UnmanagedCallersOnly]` frame callback.
- `ViewModels/ScreenCaptureViewModel.cs` + `Views/ScreenCaptureView.axaml` — tab 12.
- `scripts/package-app.sh` + `scripts/Info.plist` — the `.app` bundle.
- `tools/HeadlessCapture` — `screencapture` scenario (drives real capture headless).
