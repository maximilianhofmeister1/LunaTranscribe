#!/bin/zsh
# Baut LunaTranscribe als Release, signiert mit dem im Projekt hinterlegten Team,
# installiert nach /Applications und startet die App.
#
#   ./scripts/build-release.sh
set -euo pipefail
cd "$(dirname "$0")/.."

BUILD_DIR="build/release"
APP_NAME="LunaTranscribe"
DEST="/Applications/$APP_NAME.app"

echo "▸ Xcode-Projekt generieren"
xcodegen generate >/dev/null

echo "▸ Release-Build"
rm -rf "$BUILD_DIR"
xcodebuild \
  -project "$APP_NAME.xcodeproj" \
  -scheme "$APP_NAME" \
  -configuration Release \
  -destination 'platform=macOS,arch=arm64' \
  -derivedDataPath "$BUILD_DIR" \
  -allowProvisioningUpdates \
  build 2>&1 | grep -E "error:|warning: .*$APP_NAME/$APP_NAME/|BUILD" || true

APP="$BUILD_DIR/Build/Products/Release/$APP_NAME.app"
[[ -d "$APP" ]] || { echo "✗ Build fehlgeschlagen"; exit 1; }

echo "▸ Signatur prüfen"
codesign --verify --deep --strict "$APP"
codesign -dv "$APP" 2>&1 | grep -E "^(Authority|TeamIdentifier)" | head -2

echo "▸ Installieren nach $DEST"
pkill -x "$APP_NAME" 2>/dev/null || true
sleep 0.5
rm -rf "$DEST"
ditto "$APP" "$DEST"
rm -rf "$BUILD_DIR"

echo "▸ Starten"
open "$DEST"
echo "✓ Fertig. Autostart: Einstellungen → Allgemein → „Beim Anmelden automatisch starten“."
