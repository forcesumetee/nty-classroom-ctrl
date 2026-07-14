#!/usr/bin/env bash
# install.sh — one-shot installer for a virgin demo Mac (ships INSIDE the NTY-DEMO USB folder).
#
# The bundles are SELF-CONTAINED (no .NET needed) and STABLE self-signed — but NOT notarized, so a
# Mac that sees them as "downloaded" (browser/AirDrop/unzipped-download) quarantines them and
# Gatekeeper blocks launch. USB copy usually does NOT set quarantine, but we clear it unconditionally
# so it works either way. This is the interim (no-Apple-account) deploy path; the notarized path is P35.
#
# Run:  double-click, or  ./install.sh   (from the NTY-DEMO folder). Re-runnable.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"

TEACHER="NTY ClassroomCtrl Teacher.app"
STUDENT="NTY ClassroomCtrl.app"

echo "╔══════════════════════════════════════════════════════════════╗"
echo "║  NTY ClassroomCtrl — demo installer                          ║"
echo "╚══════════════════════════════════════════════════════════════╝"
echo

# /Applications is writable by admin users without sudo; fall back to sudo if not.
can_write_apps() { [[ -w /Applications ]]; }

install_one() {
    local app="$1"
    [[ -d "$HERE/$app" ]] || { echo "  (skip: $app not in this folder)"; return; }
    echo "▶ Installing: $app"
    if can_write_apps; then
        rm -rf "/Applications/$app"
        cp -R "$HERE/$app" "/Applications/"
        xattr -cr "/Applications/$app" 2>/dev/null || true
    else
        echo "  (need admin to write /Applications — you may be prompted)"
        sudo rm -rf "/Applications/$app"
        sudo cp -R "$HERE/$app" "/Applications/"
        sudo xattr -cr "/Applications/$app" 2>/dev/null || true
    fi
    echo "  ✔ copied to /Applications and de-quarantined (xattr -cr)"
}

install_one "$TEACHER"
install_one "$STUDENT"

cat <<'EOT'

────────────────────────────────────────────────────────────────
DONE. Both apps are in /Applications.

WHICH APP ON WHICH MAC
  • TEACHER Mac  → open "NTY ClassroomCtrl Teacher"
  • STUDENT Macs → open "NTY ClassroomCtrl"  (menu-bar app: look TOP-RIGHT,
                    no Dock icon; its menu has "Show Debug Window")

FIRST-LAUNCH PERMISSIONS (System Settings ▸ Privacy & Security)
Grant these, then RELAUNCH the app (Screen Recording is cached at launch):

  TEACHER Mac:
    1. Screen Recording   — REQUIRED for "Share My Screen" + "Share Computer Audio"
    2. Microphone         — REQUIRED for "Talk to Class"        (prompts on first use)
    3. Local Network      — REQUIRED so students can connect    (prompts on first launch → Allow)

  STUDENT Macs:
    1. Screen Recording   — REQUIRED so the teacher can see the screen
    2. Local Network      — REQUIRED to reach the teacher       (prompts on first launch → Allow)
    3. Microphone         — optional (teacher "Listen to mic")  (prompts on first use)
    4. Accessibility      — optional (hardens screen-lock; lock works without it)

  ⚙️  Screen Recording + Accessibility live in System Settings ▸ Privacy & Security
      and need you to click the lock / authenticate as an ADMIN to toggle them.
      Camera / Microphone / Local Network show a prompt the first time — just Allow.

NETWORK
  • All Macs on the SAME wired LAN (or the same Wi-Fi). Teacher listens on port 7777.
  • On a student, enter the TEACHER's IP + 7777. Find it on the Teacher's header
    ("Listening on …") or:  ipconfig getifaddr en0

Full script: DEMO-RUNBOOK.md (in this folder).
────────────────────────────────────────────────────────────────
EOT
