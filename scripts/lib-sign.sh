#!/usr/bin/env bash
# lib-sign.sh — shared bundle-signing helper (TT-13-B). Sourced by package-app.sh / package-teacher.sh.
#
# Signs a .app with the STABLE self-signed identity if present (see create-signing-cert.sh), else
# falls back to ad-hoc so the script still yields a runnable bundle on a machine without the cert.
# A stable identity makes TCC's designated requirement IDENTITY-based → grants survive rebuilds;
# ad-hoc falls back to the cdhash → re-prompts on every rebuild.
#
# Hardened runtime (--options runtime) + the entitlements plist are applied in BOTH modes so the
# bundle is NOTARIZATION-READY: when a Developer ID cert arrives (P35) it is a re-sign, not a rebuild.
# --timestamp is intentionally omitted (Apple's TSA won't timestamp a non-Apple cert; P35's runbook
# adds --timestamp for the Developer ID re-sign).

NTY_SIGN_IDENTITY_CN="NTY ClassroomCtrl Dev (Self-Signed)"

# sign_bundle <app-path> <entitlements-plist>
sign_bundle() {
    local app="$1" ent="$2" id sign_args
    if security find-identity -p codesigning 2>/dev/null | grep -qF "$NTY_SIGN_IDENTITY_CN"; then
        id="$NTY_SIGN_IDENTITY_CN"
        echo "  signing identity: STABLE self-signed ('${id}') — TCC grants persist across rebuilds"
    else
        id="-"
        echo "  signing identity: AD-HOC (self-signed cert absent — run scripts/create-signing-cert.sh"
        echo "                    to make Screen-Recording/Mic grants survive rebuilds)"
    fi

    # Hardened runtime + entitlements (notarization-ready). --deep recursively signs all ~230 nested
    # Mach-O (managed .dll + native .dylib + apphost/createdump) with the identity in ONE pass and
    # correctly seals the flat .NET layout (managed .dll + runtimeconfig.json/deps.json next to the
    # apphost) — which per-file signing does NOT (the outer seal then rejects the .json as unsigned
    # nested code). --deep is discouraged for a Developer-ID *notarization* submit (P35's runbook
    # signs inner-out with a per-binary timestamp), but for the self-signed interim/demo bundle it is
    # the proven, valid path (what the project used before self-contained).
    sign_args=(--force --deep --options runtime --entitlements "$ent" --sign "$id")

    echo "  signing (--deep, hardened runtime + entitlements)…"
    codesign "${sign_args[@]}" "$app"
    codesign --verify --deep --strict "$app" && echo "  codesign verify: OK"
    echo "  designated requirement:"
    codesign -d --requirements - "$app" 2>&1 | sed 's/^/    /' | head -3
}
