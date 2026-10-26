#!/bin/bash
# Builds the Windows release zip: runs the tests, exports the Godot project, and adds the original data
# files, the classic exporter (when built) and the player notes. Nothing from the game itself is included.
#   tools/release/build-windows.sh <godot console exe> <version> [<packaged exporter folder>]
# The exporter folder is what tools/classic-export/package.sh produced; without it the zip has no importer.
set -e
GODOT="${1:?path to the Godot .NET console executable}"
VERSION="${2:?version, e.g. 0.1.0}"
EXPORTER="${3:-}"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$REPO/build/Machines-Remake-$VERSION"
NAME="Machines-Remake-$VERSION-windows"

dotnet test "$REPO/Machines.sln" -c Release

rm -rf "$OUT" "$REPO/build/$NAME.zip"
mkdir -p "$OUT"
# Godot builds the C# assemblies itself; --import first so a fresh checkout has its import cache.
"$GODOT" --headless --path "$REPO/game" --import
"$GODOT" --headless --path "$REPO/game" --export-release "Windows Desktop" "$OUT/Machines.exe"
[ -f "$OUT/Machines.exe" ] || { echo "Godot export failed"; exit 1; }

mkdir -p "$OUT/data"
cp -r "$REPO/data/original" "$OUT/data/original"
if [ -n "$EXPORTER" ]; then
    mkdir -p "$OUT/tools"
    cp -r "$EXPORTER" "$OUT/tools/classic-export"
fi
# Real-ESRGAN for the HD textures (tools/texture-upscale/fetch.sh), with its licence.
if [ -f "$REPO/tools/texture-upscale/bin/realesrgan-ncnn-vulkan.exe" ]; then
    mkdir -p "$OUT/tools/texture-upscale"
    cp -r "$REPO/tools/texture-upscale/bin" "$OUT/tools/texture-upscale/bin"
fi
cp "$REPO/LICENSE.md" "$OUT/LICENSE.md"
sed "s/{{VERSION}}/$VERSION/g" "$REPO/tools/release/README.txt" > "$OUT/README.txt"

# Windows' own tar (bsdtar) writes standard zips; PowerShell 5's Compress-Archive writes backslash paths.
(cd "$REPO/build" && /c/Windows/System32/tar.exe -a -c -f "$NAME.zip" "$(basename "$OUT")")
echo "built $REPO/build/$NAME.zip"
