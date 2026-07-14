#!/usr/bin/env bash
# create-signing-cert.sh — create a STABLE self-signed code-signing identity (TT-13-B).
#
# WHY: an ad-hoc signature (`codesign -s -`) has NO signing certificate, so TCC's "designated
# requirement" for the app falls back to the code-directory HASH (cdhash). Any rebuild changes the
# cdhash → macOS treats it as a DIFFERENT app → every Screen-Recording / Mic grant re-prompts. This
# has cost us re-grants all project (the TT-4-D friction).
#
# A real signing IDENTITY (even self-signed, free) makes the designated requirement IDENTITY-based
# ("signed by this leaf cert") instead of hash-based → the grant SURVIVES rebuilds. This is for
# signature STABILITY (TCC + Keychain ACL persistence), NOT Gatekeeper trust — Gatekeeper still
# won't trust a self-signed cert; that needs Developer ID + notarization (P35).
#
# Idempotent: safe to re-run. Creates a dedicated keychain so it never pollutes the login keychain.
set -euo pipefail

CN="NTY ClassroomCtrl Dev (Self-Signed)"
KC="nty-codesign.keychain-db"
KC_PW="nty-codesign"          # dedicated keychain password (local dev only; not a product secret)
P12_PW="nty"                  # transient p12 export password

WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

if security find-identity -v -p codesigning 2>/dev/null | grep -qF "$CN"; then
    echo "OK: signing identity already present:"
    security find-identity -v -p codesigning | grep -F "$CN"
    exit 0
fi

echo "[1/5] generating self-signed code-signing cert (openssl)"
# codeSigning EKU (1.3.6.1.5.5.7.3.3) is what makes it a *code-signing* identity.
openssl req -x509 -newkey rsa:2048 -keyout "$WORK/key.pem" -out "$WORK/cert.pem" \
    -days 3650 -nodes -subj "/CN=${CN}" \
    -addext "basicConstraints=critical,CA:false" \
    -addext "keyUsage=critical,digitalSignature" \
    -addext "extendedKeyUsage=critical,codeSigning" >/dev/null 2>&1

echo "[2/5] bundling key+cert into a PKCS#12"
# openssl 3 defaults to a SHA-256 PKCS#12 MAC that Apple's Security framework can't verify
# ("MAC verification failed" on import). Force the LEGACY provider + SHA-1 PBE/MAC so macOS accepts it.
openssl pkcs12 -export -inkey "$WORK/key.pem" -in "$WORK/cert.pem" \
    -out "$WORK/id.p12" -passout "pass:${P12_PW}" -name "$CN" \
    -legacy -keypbe PBE-SHA1-3DES -certpbe PBE-SHA1-3DES -macalg sha1 >/dev/null 2>&1

echo "[3/5] creating dedicated keychain ${KC}"
security delete-keychain "$KC" 2>/dev/null || true
security create-keychain -p "$KC_PW" "$KC"
security set-keychain-settings "$KC"                       # no auto-lock timeout
security unlock-keychain -p "$KC_PW" "$KC"
# add our keychain to the user search list (so codesign finds the identity), keep the existing ones
EXISTING=$(security list-keychains -d user | sed 's/[[:space:]]*"//;s/"//')
# shellcheck disable=SC2086
security list-keychains -d user -s "$KC" $EXISTING

echo "[4/5] importing identity, allowing codesign to use the private key"
security import "$WORK/id.p12" -k "$KC" -P "$P12_PW" -T /usr/bin/codesign -A >/dev/null
# let codesign use the key non-interactively (no GUI password prompt on every sign)
security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$KC_PW" "$KC" >/dev/null 2>&1 || true

echo "[5/5] verifying"
if security find-identity -v -p codesigning | grep -qF "$CN"; then
    echo "OK: signing identity created:"
    security find-identity -v -p codesigning | grep -F "$CN"
else
    echo "NOTE: identity imported but not listed as -v 'valid' (self-signed = untrusted, expected)."
    echo "codesign can still sign with it by name. Full list:"
    security find-identity -p codesigning | grep -F "$CN" || true
fi
echo
echo "Sign with:  codesign --force --options runtime --entitlements <plist> --sign \"${CN}\" <app>"
