#!/usr/bin/env bash
# build.sh — compile libNtyCapture.dylib from the Swift sources (Phase 27-A).
# Produces a C-ABI dylib (via @_cdecl) that the .NET Sandbox P/Invokes.
set -euo pipefail
cd "$(dirname "$0")"

OUT="libNtyCapture.dylib"
DEPLOY_TARGET="arm64-apple-macos12.3"   # ScreenCaptureKit minimum is 12.3

echo "▶ building $OUT (target $DEPLOY_TARGET)…"
swiftc -emit-library -o "$OUT" \
    -module-name NtyCapture \
    -target "$DEPLOY_TARGET" \
    -O \
    -framework ScreenCaptureKit \
    -framework AVFoundation \
    -framework AppKit \
    -framework VideoToolbox \
    -framework CoreMedia \
    -framework CoreVideo \
    -framework CoreImage \
    -framework CoreGraphics \
    -framework ApplicationServices \
    -framework Foundation \
    Sources/*.swift

echo "✔ built $(pwd)/$OUT"
echo "▶ exported C symbols:"
nm -gU "$OUT" | grep -E "nty_" || true

# The Sandbox .csproj copies this dylib next to the managed output on build (so
# `dotnet run` finds it) and package-app.sh copies it into the .app bundle.
