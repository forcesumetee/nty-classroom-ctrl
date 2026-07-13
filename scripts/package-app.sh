#!/usr/bin/env bash
# package-app.sh — assemble the Sandbox into a macOS .app bundle (Phase 27-A; finalized 32-F).
#
# Produces the SHIPPABLE-MODE student: menubar-only (LSUIElement), stable bundle id
# (com.nty.classroomctrl.student — TCC binds to it), config-persistent (32-B), onboarded (32-D),
# and auto-start-capable (32-E — enable "Start at Login" once the bundle is in a stable location).
#
# Why a bundle: TCC permissions are keyed to the launching binary + its bundle id; an unbundled
# `dotnet run` attributes grants to the `dotnet` host and won't persist. The native dylib sits in
# Contents/MacOS/ next to the apphost — that is the .NET DllImport search path, so
# [LibraryImport("NtyCapture")] resolves the BUNDLED dylib (its own deps are absolute system paths,
# so no @rpath surgery is needed). Validated LIVE M17–M22.
#
# Output: "NTY ClassroomCtrl.app" (repo root). Run:  open "./NTY ClassroomCtrl.app"
set -euo pipefail
cd "$(dirname "$0")/.."   # repo root

CONFIG="${1:-Debug}"
RID="osx-arm64"
PROJ="src/ClassroomCtrl.Avalonia.Sandbox/ClassroomCtrl.Avalonia.Sandbox.csproj"
APP="NTY ClassroomCtrl.app"
EXE="ClassroomCtrl.Avalonia.Sandbox"   # apphost / assembly name (matches Info.plist CFBundleExecutable)
DYLIB="libNtyCapture.dylib"

echo "[1/5] building native dylib"
( cd native/NtyCapture && ./build.sh >/dev/null )

echo "[2/5] publishing Sandbox ($CONFIG, $RID, framework-dependent)"
# Framework-dependent — needs the .NET runtime on the target Mac. Self-contained bundling (for
# .NET-less lab Macs) is a P35/distribution concern, kept out of this dev-level bundle.
PUB="$(mktemp -d)"
dotnet publish "$PROJ" -c "$CONFIG" -r "$RID" --self-contained false \
    -p:EnableWindowsTargeting=true -o "$PUB" >/dev/null

echo "[3/5] assembling ${APP}"
rm -rf "$APP"
mkdir -p "${APP}/Contents/MacOS"
cp -R "$PUB"/. "${APP}/Contents/MacOS/"
# Dylib NEXT TO the apphost (Contents/MacOS/) = the .NET DllImport base-directory search path.
cp "native/NtyCapture/${DYLIB}" "${APP}/Contents/MacOS/"
cp scripts/Info.plist "${APP}/Contents/Info.plist"
rm -rf "$PUB"

echo "[4/5] verifying bundle"
# apphost named in Info.plist must exist
if [[ ! -f "${APP}/Contents/MacOS/${EXE}" ]]; then
    echo "ERROR: apphost '${EXE}' missing — check assembly name vs Info.plist CFBundleExecutable." >&2
    ls "${APP}/Contents/MacOS/" | head; exit 1
fi
chmod +x "${APP}/Contents/MacOS/${EXE}"
# native dylib must be present next to the apphost
[[ -f "${APP}/Contents/MacOS/${DYLIB}" ]] || { echo "ERROR: ${DYLIB} missing from bundle" >&2; exit 1; }
# Info.plist must be well-formed + carry the STABLE id
plutil -lint "${APP}/Contents/Info.plist" >/dev/null || { echo "ERROR: Info.plist malformed" >&2; exit 1; }
BID=$(plutil -extract CFBundleIdentifier raw "${APP}/Contents/Info.plist")
[[ "$BID" == "com.nty.classroomctrl.student" ]] || { echo "ERROR: bundle id is '${BID}', expected com.nty.classroomctrl.student" >&2; exit 1; }
echo "  CFBundleIdentifier = ${BID} · LSUIElement = $(plutil -extract LSUIElement raw "${APP}/Contents/Info.plist")"
# macOS 15+/26 Local Network Privacy: without this key a bundled app is DENIED access to the
# local-subnet Teacher (the connect fails even though ping/nc work). Guard it so it can't regress.
plutil -extract NSLocalNetworkUsageDescription raw "${APP}/Contents/Info.plist" >/dev/null 2>&1 \
    || { echo "ERROR: NSLocalNetworkUsageDescription missing — the bundle can't reach the local-network Teacher (macOS 15+ LNP)." >&2; exit 1; }
# dylib deps sanity: everything should be an absolute system path (no unresolved @rpath)
if otool -L "${APP}/Contents/MacOS/${DYLIB}" | tail -n +2 | grep -q "@rpath"; then
    echo "  WARN: dylib references @rpath — may not resolve in the bundle" >&2
else
    echo "  dylib deps: all absolute system paths (no @rpath) — resolves in the bundle"
fi

echo "[5/5] ad-hoc codesign"
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP" && echo "  codesign verify: OK"

echo "OK: built ${APP}"
echo
echo "  Menubar-only (LSUIElement=true): no Dock icon — find the status item in the menu bar;"
echo "  its 'Show Debug Window' reveals the Sandbox tabs (camera/mic/lock testing)."
echo
echo "  AUTO-START (32-E 'Start at Login'): move the bundle to a STABLE location first, e.g."
echo "      mv \"${APP}\" /Applications/     # the LaunchAgent self-targets the bundle path"
echo
echo "  Permissions: grant Screen Recording (System Settings ▸ Privacy) then RELAUNCH; Camera/Mic"
echo "  prompt on first use (immediate); Accessibility via System Settings ▸ Privacy ▸ Accessibility."
echo "  Rebuild caveat: the ad-hoc signature changes on rebuild → TCC re-prompts (P35 fixes this;"
echo "  a RELAUNCH of the same built bundle keeps its grants)."
