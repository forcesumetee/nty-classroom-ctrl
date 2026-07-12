# NtyCapture — native macOS capture helper (Phase 27-A)

A tiny **Swift → C-ABI dylib** that wraps macOS **ScreenCaptureKit** for the
Avalonia/.NET Sandbox. The .NET side P/Invokes a ~4-function C ABI and receives BGRA
frames via a callback. This keeps the Avalonia app on plain `net10.0` (no macOS
workload retarget) and isolates all ObjC/Swift framework complexity behind a stable
boundary.

**This is the interop template for every Phase 27 subsystem** (camera, audio,
screen-lock enforcement, input hooks): small Swift dylib + minimal C ABI + a
GC-rooted callback for streamed data + `Dispatcher.UIThread` marshalling on the
managed side (cheat sheet §18).

## Files
- `Headers/nty_capture.h` — the C ABI contract (source of truth).
- `Sources/NtyCapture.swift` — `@_cdecl` implementations.
- `build.sh` — compiles `libNtyCapture.dylib`.
- `libNtyCapture.dylib` — build output (git-ignored; rebuild locally).

## Build / rebuild
```bash
./build.sh          # → libNtyCapture.dylib + prints exported C symbols
```
Requirements: Xcode command-line tools (`swiftc`), macOS 12.3+ SDK. Verified with
Swift 6.1.2 on macOS 26.

The Sandbox `.csproj` copies `libNtyCapture.dylib` next to the managed output on
build (so `dotnet run` resolves `LibraryImport("NtyCapture")`), and
`scripts/package-app.sh` copies it into the `.app` bundle. **After editing the
Swift, re-run `./build.sh`, then rebuild the Sandbox** so the fresh dylib is copied.

## C ABI (keep header / Swift / C# in lockstep)
| Function | Purpose |
|---|---|
| `int nty_check_permission()` | 1 if Screen Recording authorized (no prompt) |
| `int nty_request_permission()` | trigger TCC prompt; returns status at call time |
| `int nty_capture_start(int fps, cb, ctx)` | start main-display capture (27-A-3) |
| `void nty_capture_stop()` | stop capture |

## Permission model (TCC) — important
Screen Recording is granted per **launching binary**. Key peculiarity: after the
user grants access, the entitlement **only takes effect on the next app launch**
(TCC caches it at process start). Flow: `request` → grant in System Settings →
**relaunch** → `check` returns 1. Run from the packaged `.app` (stable bundle id)
so the grant persists — an unbundled `dotnet run` attributes it to the `dotnet`
host and won't stick. See `scripts/package-app.sh`.
