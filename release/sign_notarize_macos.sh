#!/usr/bin/env bash
set -Eeuo pipefail

cd "$(dirname "$0")/.."
ROOT="$PWD"

APP="$(printenv FILEMCP_MACOS_APP_PATH 2>/dev/null || true)"
[ -n "$APP" ] || APP="$ROOT/dist/FileMCP.app"
P12_PATH="$(printenv FILEMCP_MACOS_P12_PATH 2>/dev/null || true)"
P12_PASSWORD="$(printenv FILEMCP_MACOS_P12_PASSWORD 2>/dev/null || true)"
NOTARY_KEY_PATH="$(printenv FILEMCP_APPLE_NOTARY_KEY_PATH 2>/dev/null || true)"
NOTARY_KEY_ID="$(printenv FILEMCP_APPLE_NOTARY_KEY_ID 2>/dev/null || true)"
NOTARY_ISSUER_ID="$(printenv FILEMCP_APPLE_NOTARY_ISSUER_ID 2>/dev/null || true)"

fail() {
    echo "ERROR: $1" >&2
    exit 1
}

[ -d "$APP" ] || fail "FileMCP.app does not exist. Build the macOS app first."
[ -f "$APP/Contents/Info.plist" ] || fail "FileMCP.app is missing Info.plist."
[ -f "$APP/Contents/MacOS/FileMCP" ] || fail "FileMCP.app is missing its main executable."
[ -f "$APP/Contents/MacOS/tunnel-client" ] || fail "FileMCP.app is missing the bundled tunnel-client."
[ -n "$P12_PATH" ] && [ -f "$P12_PATH" ] || fail "FILEMCP_MACOS_P12_PATH must point to the Developer ID PKCS#12 file."
[ -n "$P12_PASSWORD" ] || fail "FILEMCP_MACOS_P12_PASSWORD is required."
[ -n "$NOTARY_KEY_PATH" ] && [ -f "$NOTARY_KEY_PATH" ] || fail "FILEMCP_APPLE_NOTARY_KEY_PATH must point to an App Store Connect API key."
[ -n "$NOTARY_KEY_ID" ] || fail "FILEMCP_APPLE_NOTARY_KEY_ID is required."
[ -n "$NOTARY_ISSUER_ID" ] || fail "FILEMCP_APPLE_NOTARY_ISSUER_ID is required."

for command_name in security codesign xcrun spctl ditto openssl python3; do
    command -v "$command_name" >/dev/null 2>&1 || fail "Required release command is unavailable: $command_name"
done

ARCH="$(uname -m)"
case "$ARCH" in
    arm64|aarch64) RELEASE_ARCH="arm64" ;;
    *) fail "Production macOS release currently requires an arm64 runner because the repository vendors only darwin-arm64 tunnel-client." ;;
esac

VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$APP/Contents/Info.plist")"
case "$VERSION" in
    ''|*[!0-9.]*)
        fail "Invalid CFBundleShortVersionString."
        ;;
esac

WORK_BASE="$(printenv RUNNER_TEMP 2>/dev/null || true)"
if [ -z "$WORK_BASE" ]; then
    WORK_BASE="$(printenv TMPDIR 2>/dev/null || true)"
fi
[ -n "$WORK_BASE" ] || WORK_BASE="/tmp"

WORK_DIR="$(mktemp -d "$WORK_BASE/filemcp-signing.XXXXXX")"
KEYCHAIN="$WORK_DIR/filemcp-release.keychain-db"
KEYCHAIN_PASSWORD="$(openssl rand -hex 32)"
NOTARY_ZIP="$WORK_DIR/FileMCP-notary.zip"
NOTARY_JSON="$WORK_DIR/notary-result.json"
VERIFY_DIR="$WORK_DIR/verify"
FINAL_ZIP="$ROOT/dist/FileMCP-v$VERSION-macos-$RELEASE_ARCH.zip"

cleanup() {
    security delete-keychain "$KEYCHAIN" >/dev/null 2>&1 || true
    rm -rf "$WORK_DIR"
    KEYCHAIN_PASSWORD=""
}
trap cleanup EXIT

security create-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security set-keychain-settings -lut 21600 "$KEYCHAIN"
security unlock-keychain -p "$KEYCHAIN_PASSWORD" "$KEYCHAIN"
security import "$P12_PATH" \
    -k "$KEYCHAIN" \
    -P "$P12_PASSWORD" \
    -T /usr/bin/codesign \
    -T /usr/bin/security >/dev/null
security set-key-partition-list \
    -S apple-tool:,apple:,codesign: \
    -s \
    -k "$KEYCHAIN_PASSWORD" \
    "$KEYCHAIN" >/dev/null

IDENTITIES="$(
    security find-identity -v -p codesigning "$KEYCHAIN" 2>/dev/null |
        sed -n 's/.*"\(Developer ID Application:.*\)"/\1/p'
)"
IDENTITY_COUNT="$(printf '%s\n' "$IDENTITIES" | sed '/^[[:space:]]*$/d' | wc -l | tr -d '[:space:]')"
[ "$IDENTITY_COUNT" = "1" ] || fail "Expected exactly one Developer ID Application identity in the supplied P12."
IDENTITY="$(printf '%s\n' "$IDENTITIES" | sed -n '1p')"

HELPER="$APP/Contents/MacOS/tunnel-client"
codesign \
    --force \
    --options runtime \
    --timestamp \
    --keychain "$KEYCHAIN" \
    --sign "$IDENTITY" \
    "$HELPER"

codesign \
    --force \
    --options runtime \
    --timestamp \
    --keychain "$KEYCHAIN" \
    --sign "$IDENTITY" \
    "$APP"

codesign --verify --strict --verbose=2 "$HELPER"
codesign --verify --deep --strict --verbose=2 "$APP"

APP_DETAILS="$(codesign -d --verbose=4 "$APP" 2>&1)"
printf '%s\n' "$APP_DETAILS" | grep -Eq 'flags=.*runtime' ||
    fail "FileMCP.app is not signed with the hardened runtime."

ditto -c -k --keepParent "$APP" "$NOTARY_ZIP"

xcrun notarytool submit "$NOTARY_ZIP" \
    --key "$NOTARY_KEY_PATH" \
    --key-id "$NOTARY_KEY_ID" \
    --issuer "$NOTARY_ISSUER_ID" \
    --wait \
    --output-format json >"$NOTARY_JSON"

python3 - "$NOTARY_JSON" <<'PY'
import json
import pathlib
import sys

result = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
status = result.get("status")
if status != "Accepted":
    raise SystemExit(f"Apple notarization was not accepted: {status!r}")
print("macos-notarization: accepted")
PY

xcrun stapler staple "$APP"
xcrun stapler validate "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"
spctl --assess --type execute --verbose=4 "$APP"

rm -f "$FINAL_ZIP"
ditto -c -k --keepParent "$APP" "$FINAL_ZIP"

mkdir -p "$VERIFY_DIR"
ditto -x -k "$FINAL_ZIP" "$VERIFY_DIR"
PACKAGED_APP="$VERIFY_DIR/FileMCP.app"
[ -d "$PACKAGED_APP" ] || fail "Final ZIP is missing FileMCP.app."
codesign --verify --deep --strict --verbose=2 "$PACKAGED_APP"
xcrun stapler validate "$PACKAGED_APP"
spctl --assess --type execute --verbose=4 "$PACKAGED_APP"

echo "macos-production-signing-notarization: ok ($RELEASE_ARCH)"
shasum -a 256 "$FINAL_ZIP"