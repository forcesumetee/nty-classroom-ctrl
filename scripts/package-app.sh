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

echo "[2/5] publishing Sandbox ($CONFIG, $RID, SELF-CONTAINED)"
# TT-13-B: SELF-CONTAINED — bundles the .NET runtime INTO the .app so it runs on a lab Mac with no
# .NET installed. Removes the per-machine "install .NET first" step (the 50-machine deployment
# blocker). Larger bundle (~150 MB) but that's a non-issue for USB/network deploy.
PUB="$(mktemp -d)"
dotnet publish "$PROJ" -c "$CONFIG" -r "$RID" --self-contained true \
    -p:EnableWindowsTargeting=true -o "$PUB" >/dev/null

echo "[3/5] assembling ${APP}"
rm -rf "$APP"
mkdir -p "${APP}/Contents/MacOS"
cp -R "$PUB"/. "${APP}/Contents/MacOS/"
# Dylib NEXT TO the apphost (Contents/MacOS/) = the .NET DllImport base-directory search path.
cp "native/NtyCapture/${DYLIB}" "${APP}/Contents/MacOS/"
cp scripts/Info.plist "${APP}/Contents/Info.plist"
rm -rf "$PUB"
# Strip debug symbols: never shipped, and codesign misdetects .pdb as unsigned nested code
# ("code object is not signed at all: In subcomponent …pdb"), breaking the bundle seal.
find "${APP}/Contents/MacOS" -name "*.pdb" -delete

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

echo "[5/5] codesign (hardened runtime + entitlements, notarization-ready)"
# shellcheck source=lib-sign.sh
source scripts/lib-sign.sh
sign_bundle "$APP" "scripts/nty.entitlements"

echo "OK: built ${APP}"
echo
echo "  Menubar-only (LSUIElement=true): no Dock icon — find the status item in the menu bar;"
echo "  its 'Show Debug Window' reveals the Sandbox tabs (camera/mic/lock testing)."
echo
echo "  AUTO-START (32-E 'Start at Login'): move the bundle to a STABLE location first, e.g."
echo "      mv \"${APP}\" /Applications/     # the LaunchAgent self-targets the bundle path"
echo
echo "  Permissions: first launch opens the onboarding window; grant Screen Recording then RELAUNCH;"
echo "  Camera/Mic prompt on first use; Accessibility via System Settings ▸ Privacy ▸ Accessibility."
echo "  With the STABLE self-signed identity, grants SURVIVE rebuilds (TT-13-B); with ad-hoc they"
echo "  re-prompt on rebuild. Gatekeeper trust (clean download double-click) still needs P35 notarization."
