#!/usr/bin/env bash
# package-teacher.sh — assemble the TEACHER into a macOS .app bundle (TT-8).
#
# Why now (not TT-13): TT-8's "Share My Screen" makes the Teacher CAPTURE its own screen for the
# first time → it needs Screen Recording TCC. TCC binds a grant to the launching binary + bundle id;
# an unbundled `dotnet run` attributes it to the `dotnet`/Terminal host and won't persist reliably
# (the TT-4-D friction). So the TT-8 LIVE gate requires a Teacher BUNDLE. This is a minimal,
# dev-level slice of TT-13 packaging (framework-dependent, ad-hoc signed); P35 does Developer ID +
# notarization later.
#
# The native dylib sits in Contents/MacOS/ next to the apphost (the .NET DllImport search path), so
# [LibraryImport("NtyCapture")] resolves the BUNDLED dylib for BOTH capture (nty_capture_*) and
# student-screen decode (nty_h264_decoder_*).
#
# Output: "NTY ClassroomCtrl Teacher.app" (repo root). Run:  open "./NTY ClassroomCtrl Teacher.app"
set -euo pipefail
cd "$(dirname "$0")/.."   # repo root

CONFIG="${1:-Debug}"
RID="osx-arm64"
PROJ="src/ClassroomCtrl.Avalonia.Teacher/ClassroomCtrl.Avalonia.Teacher.csproj"
APP="NTY ClassroomCtrl Teacher.app"
EXE="ClassroomCtrl.Avalonia.Teacher"   # apphost / assembly name (matches Info.Teacher.plist CFBundleExecutable)
DYLIB="libNtyCapture.dylib"

echo "[1/5] building native dylib"
( cd native/NtyCapture && ./build.sh >/dev/null )

echo "[2/5] publishing Teacher ($CONFIG, $RID, SELF-CONTAINED)"
# TT-13-B: SELF-CONTAINED — bundles the .NET runtime into the .app (runs on a Mac with no .NET).
PUB="$(mktemp -d)"
dotnet publish "$PROJ" -c "$CONFIG" -r "$RID" --self-contained true \
    -p:EnableWindowsTargeting=true -o "$PUB" >/dev/null

echo "[3/5] assembling ${APP}"
rm -rf "$APP"
mkdir -p "${APP}/Contents/MacOS"
cp -R "$PUB"/. "${APP}/Contents/MacOS/"
cp "native/NtyCapture/${DYLIB}" "${APP}/Contents/MacOS/"
cp scripts/Info.Teacher.plist "${APP}/Contents/Info.plist"
rm -rf "$PUB"
# Strip debug symbols: never shipped, and codesign misdetects .pdb as unsigned nested code, breaking the seal.
find "${APP}/Contents/MacOS" -name "*.pdb" -delete

echo "[4/5] verifying bundle"
if [[ ! -f "${APP}/Contents/MacOS/${EXE}" ]]; then
    echo "ERROR: apphost '${EXE}' missing — check assembly name vs Info.Teacher.plist CFBundleExecutable." >&2
    ls "${APP}/Contents/MacOS/" | head; exit 1
fi
chmod +x "${APP}/Contents/MacOS/${EXE}"
[[ -f "${APP}/Contents/MacOS/${DYLIB}" ]] || { echo "ERROR: ${DYLIB} missing from bundle" >&2; exit 1; }
plutil -lint "${APP}/Contents/Info.plist" >/dev/null || { echo "ERROR: Info.plist malformed" >&2; exit 1; }
BID=$(plutil -extract CFBundleIdentifier raw "${APP}/Contents/Info.plist")
[[ "$BID" == "com.nty.classroomctrl.teacher" ]] || { echo "ERROR: bundle id is '${BID}', expected com.nty.classroomctrl.teacher" >&2; exit 1; }
echo "  CFBundleIdentifier = ${BID}"
plutil -extract NSScreenCaptureUsageDescription raw "${APP}/Contents/Info.plist" >/dev/null 2>&1 \
    || { echo "ERROR: NSScreenCaptureUsageDescription missing — Share My Screen (TT-8) needs it." >&2; exit 1; }
plutil -extract NSLocalNetworkUsageDescription raw "${APP}/Contents/Info.plist" >/dev/null 2>&1 \
    || { echo "ERROR: NSLocalNetworkUsageDescription missing — students can't reach the Teacher (macOS 15+ LNP)." >&2; exit 1; }
if otool -L "${APP}/Contents/MacOS/${DYLIB}" | tail -n +2 | grep -q "@rpath"; then
    echo "  WARN: dylib references @rpath — may not resolve in the bundle" >&2
else
    echo "  dylib deps: all absolute system paths (no @rpath) — resolves in the bundle"
fi

echo "[5/5] codesign (hardened runtime + entitlements, notarization-ready)"
# shellcheck source=lib-sign.sh
source scripts/lib-sign.sh
sign_bundle "$APP" "scripts/nty.entitlements"

echo "OK: built ${APP}"
echo
echo "  Run:  open \"./${APP}\""
echo "  First launch opens the Teacher onboarding window (TT-13-B): grant Screen Recording (for Share"
echo "  My Screen / Share Computer Audio) and Microphone (for Talk to Class), then RELAUNCH the bundle."
echo "  With the STABLE self-signed identity, grants SURVIVE rebuilds; ad-hoc re-prompts. Gatekeeper"
echo "  trust (clean download double-click) still needs P35 notarization."
