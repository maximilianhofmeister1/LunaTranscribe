#!/bin/zsh
# Baut MediaRemoteAdapter.framework aus den Quellen neu und legt es unter Vendor/ ab.
# Nur nötig, wenn eine neue Adapter-Version eingespielt werden soll. Braucht cmake (brew install cmake).
set -euo pipefail
cd "$(dirname "$0")/.."
TMP=$(mktemp -d)
git clone --depth 1 https://github.com/ungive/mediaremote-adapter.git "$TMP/src"
cmake -S "$TMP/src" -B "$TMP/build" -DCMAKE_OSX_ARCHITECTURES=arm64 >/dev/null
cmake --build "$TMP/build" >/dev/null
rm -rf Vendor/MediaRemoteAdapter/MediaRemoteAdapter.framework
cp -R "$TMP/build/MediaRemoteAdapter.framework" Vendor/MediaRemoteAdapter/
cp "$TMP/src/bin/mediaremote-adapter.pl" "$TMP/src/LICENSE" Vendor/MediaRemoteAdapter/
rm -rf "$TMP"
echo "✓ Vendor/MediaRemoteAdapter aktualisiert"
