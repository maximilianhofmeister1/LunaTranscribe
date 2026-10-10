#!/bin/zsh
# Baut LunaTranscribe als Release und packt es als DMG für GitHub Releases nach dist/.
#
#   ./scripts/package-release.sh
#
# Signatur: „Developer ID Application“, falls im Schlüsselbund vorhanden, sonst „Apple Development“
# (überschreibbar mit SIGN_IDENTITY="…"). Mit Developer ID wird zusätzlich notarisiert und geheftet,
# sofern das notarytool-Profil existiert (Name über NOTARY_PROFILE, Standard: luna-notary):
#
#   xcrun notarytool store-credentials luna-notary --apple-id <apple-id> --team-id <team-id>
#
# Ohne Notarisierung blockiert Gatekeeper den ersten Start; Nutzer geben die App dann unter
# Systemeinstellungen → Datenschutz & Sicherheit → „Trotzdem öffnen“ frei.
set -euo pipefail
cd "$(dirname "$0")/.."

APP_NAME="LunaTranscribe"
BUILD_DIR="build/package"
DIST_DIR="dist"
NOTARY_PROFILE="${NOTARY_PROFILE:-luna-notary}"

if [[ -z "${SIGN_IDENTITY:-}" ]]; then
  if security find-identity -v -p codesigning | grep -q "Developer ID Application"; then
    SIGN_IDENTITY="Developer ID Application"
  else
    SIGN_IDENTITY="Apple Development"
  fi
fi
# Das Team muss mitgegeben werden, sonst scheitert die Signatur der Ressourcenbündel der Swift-Pakete.
DEVELOPMENT_TEAM="${DEVELOPMENT_TEAM:-$(sed -nE 's/^DEVELOPMENT_TEAM *= *([A-Z0-9]+).*/\1/p' Config/Local.xcconfig 2>/dev/null | tail -1)}"
[[ -n "$DEVELOPMENT_TEAM" ]] || { echo "✗ Kein Team: Config/Local.xcconfig anlegen oder DEVELOPMENT_TEAM setzen"; exit 1; }
echo "▸ Signatur: $SIGN_IDENTITY (Team $DEVELOPMENT_TEAM)"

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
  CODE_SIGN_STYLE=Manual \
  CODE_SIGN_IDENTITY="$SIGN_IDENTITY" \
  DEVELOPMENT_TEAM="$DEVELOPMENT_TEAM" \
  CODE_SIGN_INJECT_BASE_ENTITLEMENTS=NO \
  OTHER_CODE_SIGN_FLAGS="--timestamp" \
  build 2>&1 | grep -E "error:|warning: .*$APP_NAME/$APP_NAME/|BUILD" || true

APP="$BUILD_DIR/Build/Products/Release/$APP_NAME.app"
[[ -d "$APP" ]] || { echo "✗ Build fehlgeschlagen"; exit 1; }

echo "▸ Signatur prüfen"
codesign --verify --deep --strict "$APP"
codesign -dv "$APP" 2>&1 | grep -E "^(Authority|TeamIdentifier)" | head -2
codesign -d --entitlements - "$APP" 2>/dev/null | grep -q "get-task-allow" \
  && { echo "✗ get-task-allow in den Entitlements – so nicht notarisierbar"; exit 1; }

VERSION=$(/usr/libexec/PlistBuddy -c "Print :CFBundleShortVersionString" "$APP/Contents/Info.plist")
DMG="$DIST_DIR/$APP_NAME-$VERSION.dmg"

echo "▸ DMG erstellen ($DMG)"
mkdir -p "$DIST_DIR"
STAGE=$(mktemp -d)
ditto "$APP" "$STAGE/$APP_NAME.app"
ln -s /Applications "$STAGE/Applications"
rm -f "$DMG"
hdiutil create -quiet -volname "$APP_NAME $VERSION" -srcfolder "$STAGE" -fs HFS+ -format UDZO "$DMG"
rm -rf "$STAGE"
codesign --sign "$SIGN_IDENTITY" --timestamp "$DMG"

if [[ "$SIGN_IDENTITY" == Developer\ ID* ]]; then
  if xcrun notarytool history --keychain-profile "$NOTARY_PROFILE" >/dev/null 2>&1; then
    echo "▸ Notarisieren (dauert meist wenige Minuten)"
    xcrun notarytool submit "$DMG" --keychain-profile "$NOTARY_PROFILE" --wait
    xcrun stapler staple "$DMG"
    spctl --assess --type open --context context:primary-signature -v "$DMG"
  else
    echo "! notarytool-Profil „$NOTARY_PROFILE“ fehlt – DMG ist signiert, aber nicht notarisiert"
  fi
else
  echo "! Kein Developer-ID-Zertifikat – DMG ist nicht notarisiert"
fi

(cd "$DIST_DIR" && shasum -a 256 "$(basename "$DMG")" > "$(basename "$DMG").sha256")
rm -rf "$BUILD_DIR"

echo "✓ Fertig: $DMG"
cat "$DMG.sha256"
