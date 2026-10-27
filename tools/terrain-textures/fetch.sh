#!/bin/bash
# Downloads the ground textures (layers.txt; CC0, from polyhaven.com through the Poly Haven API) into
# textures/polyhaven (git-ignored), where the remake look reads them (TerrainDetail.cs, TerrainMaterials.cs):
#   tools/terrain-textures/fetch.sh
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
OUT="$REPO/textures/polyhaven"
AGENT="MachinesRemake-texturefetch"
grep -v '^#' "$HERE/layers.txt" | while read -r layer id; do
    [ -n "$layer" ] || continue
    mkdir -p "$OUT/$layer"
    # The 1k JPEG maps the API lists for the texture; the colour map is called _diff_ or _col_, the height map _disp_.
    urls="$(curl -sSf -A "$AGENT" "https://api.polyhaven.com/files/$id" | grep -o 'https://dl.polyhaven.org/file/ph-assets/Textures/jpg/1k/[^"]*')"
    for map in albedo:'_\(diff\|col\)_1k' normal:'_nor_gl_1k' roughness:'_rough_1k' height:'_disp_1k'; do
        file="$OUT/$layer/${map%%:*}.jpg"
        [ -s "$file" ] && continue
        url="$(echo "$urls" | grep "${map#*:}\.jpg$" | head -1)"
        if [ -z "$url" ]; then
            # Not every texture has a height map; the game then takes the colour's brightness.
            [ "${map%%:*}" = height ] && continue
            echo "$id has no ${map%%:*} map"; exit 1
        fi
        curl -sSfL -A "$AGENT" -o "$file" "$url"
    done
    echo "$layer: $id"
done

# The photo skies (skies.txt): Radiance HDR files at the listed resolution.
mkdir -p "$OUT/sky"
grep -v '^#' "$HERE/skies.txt" | while read -r id size; do
    [ -n "$id" ] || continue
    file="$OUT/sky/$id.hdr"
    [ -s "$file" ] && continue
    url="$(curl -sSf -A "$AGENT" "https://api.polyhaven.com/files/$id" | grep -o "https://dl.polyhaven.org/file/ph-assets/HDRIs/hdr/$size/[^\"]*\.hdr" | head -1)"
    [ -n "$url" ] || { echo "$id has no $size HDR"; exit 1; }
    curl -sSfL -A "$AGENT" -o "$file" "$url"
    echo "sky: $id ($size)"
done
