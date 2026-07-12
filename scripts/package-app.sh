#!/usr/bin/env bash
# package-app.sh - assemble the Sandbox into a minimal macOS .app bundle (Phase 27-A).
#
# Why a bundle: Screen Recording (TCC) permission is keyed to the launching binary
# and - for screen capture specifically - only takes effect after a RELAUNCH. An
# unbundled `dotnet run` attributes the grant to the `dotnet` host and won't persist.
# A bundle with a stable CFBundleIdentifier makes the grant real and sticky.
#
# Output: ClassroomCtrl.Sandbox.app (repo root). Run it with:
#   open ./ClassroomCtrl.Sandbox.app          # or double-click in Finder
set -euo pipefail
cd "$(dirname "$0")/.."   # repo root

CONFIG="${1:-Debug}"
RID="osx-arm64"
PROJ="src/ClassroomCtrl.Avalonia.Sandbox/ClassroomCtrl.Avalonia.Sandbox.csproj"
APP="ClassroomCtrl.Sandbox.app"
EXE="ClassroomCtrl.Avalonia.Sandbox"   # apphost / assembly name (matches Info.plist)

echo "[1/4] building native dylib"
( cd native/NtyCapture && ./build.sh >/dev/null )

echo "[2/4] publishing Sandbox ($CONFIG, $RID, framework-dependent)"
PUB="$(mktemp -d)"
dotnet publish "$PROJ" -c "$CONFIG" -r "$RID" --self-contained false \
    -p:EnableWindowsTargeting=true -o "$PUB" >/dev/null

echo "[3/4] assembling ${APP}"
rm -rf "$APP"
mkdir -p "${APP}/Contents/MacOS"
cp -R "$PUB"/. "${APP}/Contents/MacOS/"
# Ensure the freshly-built dylib is present next to the binary (P/Invoke search path).
cp native/NtyCapture/libNtyCapture.dylib "${APP}/Contents/MacOS/"
cp scripts/Info.plist "${APP}/Contents/Info.plist"
rm -rf "$PUB"

# Sanity: the executable named in Info.plist must exist.
if [[ ! -f "${APP}/Contents/MacOS/${EXE}" ]]; then
    echo "ERROR: apphost '${EXE}' not found in bundle - check assembly name vs Info.plist CFBundleExecutable." >&2
    ls "${APP}/Contents/MacOS/" | head
    exit 1
fi
chmod +x "${APP}/Contents/MacOS/${EXE}"

echo "[4/4] ad-hoc codesign"
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP" && echo "  codesign verify: OK"

echo "OK: built ${APP}"
echo "  run:  open ./${APP}"
echo "  first run: press 'Request permission' -> grant Screen Recording in System"
echo "  Settings > Privacy & Security -> relaunch the app -> 'Check' shows Granted."
