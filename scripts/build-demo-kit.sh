#!/usr/bin/env bash
# build-demo-kit.sh — assemble the ONE USB folder the demo ships on (TT-13-A demo kit).
#
# Output:  ./NTY-DEMO/
#            ├── NTY ClassroomCtrl Teacher.app   (self-contained, stable self-signed)
#            ├── NTY ClassroomCtrl.app            (self-contained, stable self-signed)
#            ├── install.sh                        (copy to /Applications + xattr -cr + TCC steps)
#            └── DEMO-RUNBOOK.md                   (the on-stage script)
#
# Copy NTY-DEMO/ to a USB stick → on each virgin demo Mac run install.sh. That's the whole deploy.
# (Build artifact; NOT committed.)
set -euo pipefail
cd "$(dirname "$0")/.."   # repo root

CONFIG="${1:-Release}"
KIT="NTY-DEMO"
TEACHER="NTY ClassroomCtrl Teacher.app"
STUDENT="NTY ClassroomCtrl.app"

echo "[1/3] building both bundles ($CONFIG, self-contained + signed)…"
./scripts/package-app.sh "$CONFIG"     >/dev/null
./scripts/package-teacher.sh "$CONFIG" >/dev/null

echo "[2/3] assembling ./$KIT/"
rm -rf "$KIT"
mkdir -p "$KIT"
cp -R "$TEACHER" "$KIT/"
cp -R "$STUDENT" "$KIT/"
cp scripts/install.sh "$KIT/"
chmod +x "$KIT/install.sh"
[[ -f docs/DEMO-RUNBOOK.md ]] && cp docs/DEMO-RUNBOOK.md "$KIT/" || echo "  (note: docs/DEMO-RUNBOOK.md not found — add it before the demo)"

echo "[3/3] verifying kit"
for app in "$TEACHER" "$STUDENT"; do
    codesign --verify --deep --strict "$KIT/$app" && echo "  ✔ $app signature OK"
done
echo
echo "OK: kit ready at ./$KIT ($(du -sh "$KIT" | cut -f1))"
ls -1 "$KIT"
echo
echo "  → copy ./$KIT to a USB stick; on each demo Mac: open the folder, run install.sh"
