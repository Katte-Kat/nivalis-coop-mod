#!/usr/bin/env bash
# Build the plugin and zip it for players: dist/NivalisCoop-<version>.zip (unzip into the game folder).
set -euo pipefail
cd "$(dirname "$0")"
VERSION=$(grep -ohP 'ModVersion\s*=\s*"\K[^"]+' NivalisCoop.Net/*.cs | head -1)
STAGE=$(mktemp -d); trap 'rm -rf "$STAGE"' EXIT
./build-plugin.sh >/dev/null
OUT=NivalisCoop.Plugin/bin/Release
DLLS=$(find "$OUT" -name NivalisCoop.dll | head -1)
SRC=$(dirname "$DLLS")
mkdir -p "$STAGE/BepInEx/plugins/NivalisCoop" dist
cp "$SRC/NivalisCoop.dll" "$SRC/NivalisCoop.Net.dll" "$STAGE/BepInEx/plugins/NivalisCoop/"
cp README.md "$STAGE/NivalisCoop-README.md"
rm -f "dist/NivalisCoop-$VERSION.zip"
(cd "$STAGE" && python3 -I -c "
import zipfile,os,sys
z=zipfile.ZipFile(sys.argv[1],'w',zipfile.ZIP_DEFLATED)
for r,_,fs in os.walk('.'):
    for f in fs: z.write(os.path.join(r,f))
" "$OLDPWD/dist/NivalisCoop-$VERSION.zip")
echo "dist/NivalisCoop-$VERSION.zip"; unzip -l "dist/NivalisCoop-$VERSION.zip" | tail -6
