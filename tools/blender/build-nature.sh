#!/bin/bash
# Builds the remake's trees, bushes, rocks and rock spires into models/nature (git-ignored), where the remake look
# reads them (Scenery.cs). The Poly Haven scans and bark pictures and ambientCG's leaf atlases they are made from (all
# CC0) are fetched first into models/downloads, then the Blender scripts beside this one run:
#   tools/blender/build-nature.sh [path to blender]
# The ground textures must be fetched already (tools/terrain-textures/fetch.sh): the spires wear one of them.
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
BLENDER="${1:-/c/Program Files/Blender Foundation/Blender 5.2/blender.exe}"
DL="$REPO/models/downloads"
OUT="$REPO/models/nature"
PREVIEWS="$REPO/models/previews"
AGENT="MachinesRemake-modelfetch"
mkdir -p "$DL" "$OUT/small" "$PREVIEWS"
# Blender wants the operating system's own paths.
native() { if command -v cygpath > /dev/null; then cygpath -w "$1"; else echo "$1"; fi; }

# The scans: each one's .blend at 1k with the textures it includes, under <id>/textures as it expects them.
SCANS="rock_moss_set_01 boulder_01 namaqualand_boulder_02 namaqualand_boulder_05 moon_rock_01 moon_rock_03 moon_rock_05 stone_01"
for id in $SCANS; do
    [ -s "$DL/$id/$id.blend" ] && continue
    mkdir -p "$DL/$id/textures"
    json="$(curl -sSf -A "$AGENT" "https://api.polyhaven.com/files/$id")"
    curl -sSfL -A "$AGENT" -o "$DL/$id/$id.blend" "$(echo "$json" | grep -o 'https://dl.polyhaven.org/file/ph-assets/Models/blend/1k/[^"]*\.blend' | head -1)"
    for url in $(echo "$json" | grep -o "https://dl.polyhaven.org/file/ph-assets/Models/[a-z]*/1k/$id/[^\"]*" | sort -u); do
        curl -sSfL -A "$AGENT" -o "$DL/$id/textures/$(basename "$url")" "$url"
    done
    echo "scan: $id"
done

# The bark pictures, colour and normal map at 1k.
for id in palm_bark bark_platanus jolcham_oak_bark_01; do
    [ -s "$DL/bark/$id/normal.jpg" ] && continue
    mkdir -p "$DL/bark/$id"
    urls="$(curl -sSf -A "$AGENT" "https://api.polyhaven.com/files/$id" | grep -o 'https://dl.polyhaven.org/file/ph-assets/Textures/jpg/1k/[^"]*')"
    curl -sSfL -A "$AGENT" -o "$DL/bark/$id/albedo.jpg" "$(echo "$urls" | grep '_\(diff\|col\)_1k\.jpg$' | head -1)"
    curl -sSfL -A "$AGENT" -o "$DL/bark/$id/normal.jpg" "$(echo "$urls" | grep '_nor_gl_1k\.jpg$' | head -1)"
    echo "bark: $id"
done

# The leaf atlases the foliage cards are made of (ambientCG, CC0): leaves, dry leaves, long leaves, conifer sprigs.
for id in LeafSet024 LeafSet030 LeafSet022 LeafSet019; do
    [ -s "$DL/atlas/$id/${id}_1K-PNG_Opacity.png" ] && continue
    mkdir -p "$DL/atlas/$id"
    curl -sSfL -A "$AGENT" -o "$DL/atlas/$id.zip" "https://ambientcg.com/get?file=${id}_1K-PNG.zip"
    unzip -qo "$DL/atlas/$id.zip" -d "$DL/atlas/$id" 2> /dev/null || /c/Windows/System32/tar.exe -xf "$DL/atlas/$id.zip" -C "$DL/atlas/$id"
    rm "$DL/atlas/$id.zip"
    echo "atlas: $id"
done

run() { "$BLENDER" --background --factory-startup --python "$(native "$HERE/$1")" -- "${@:2}" > "$REPO/models/$1.log" 2>&1 || { echo "$1 failed, see models/$1.log"; exit 1; }; }
# The foliage cards first (twigs, fronds, boughs), then the trees made of them.
run cards.py "$(native "$DL/atlas")" "$(native "$REPO/models/cards")"
run trees.py "$(native "$DL/bark")" "$(native "$REPO/models/cards")" "$(native "$OUT")" "$(native "$PREVIEWS")"
# The big rocks in full, and every scan again much lighter for the stones scattered by the thousand.
run rocks.py "$(native "$DL")" "$(native "$OUT")" "$(native "$PREVIEWS")" 3000 $SCANS
run rocks.py "$(native "$DL")" "$(native "$OUT/small")" "$(native "$PREVIEWS/small")" 160 $SCANS
run spires.py "$(native "$REPO/textures/polyhaven")" darkrock "$(native "$OUT")" "$(native "$PREVIEWS")"
ls "$OUT" "$OUT/small"
