#!/bin/bash
# The release check of a complete package (build-complete.sh): installs it as a player would, into a throwaway
# copy, imports from the CD with the real game (MACHINES_IMPORT, a fresh user folder), and compares every asset of
# the installation with the checkout it was built in, byte for byte: classic/ (all the looks), the ground textures and
# the trees and rocks. Anything missing or different fails it, with the list.
#
#   tools/release/check-install.sh <package folder> <CD 1 .iso, drive or game folder> [<CD 2 .iso>]
#
#   CLASSIC=<folder>  the checkout's import to compare with (default: classic/ of the main checkout)
#   PARTS=a,b         compares only those folders of classic/ (for a package built with PARTS)
#   KEEP=1            keeps the throwaway installation (build/check-install) to look at
# The installation is hardlinked from the package where it can be, so it takes the room of what the import writes.
set -e
PACKAGE="$(cd "${1:?the folder of the complete package}" && pwd)"
SOURCE="${2:?CD 1 as a disc image, drive or folder}"
SECOND="${3:-}"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
MAIN="$(cd "$REPO" && git rev-parse --path-format=absolute --git-common-dir 2>/dev/null | sed 's#/\.git$##')"
CLASSIC="${CLASSIC:-$( [ -d "$REPO/classic/models" ] && echo "$REPO/classic" || echo "$MAIN/classic" )}"
WORK="$REPO/build/check-install"

[ -f "$PACKAGE/Machines.exe" ] && [ -f "$PACKAGE/pack/manifest.json" ] || { echo "$PACKAGE is no complete package (Machines.exe, pack/manifest.json)" >&2; exit 1; }
rm -rf "$WORK"
mkdir -p "$WORK"
cp -al "$PACKAGE" "$WORK/game" 2>/dev/null || cp -a "$PACKAGE" "$WORK/game"
mkdir -p "$WORK/user" "$WORK/home"
# CD 2 beside CD 1 brings the campaign's films along, as the game looks for it there.
if [ -n "$SECOND" ] && [ -f "$SOURCE" ]; then
    mkdir -p "$WORK/home/Downloads"
    cp -al "$SECOND" "$WORK/home/Downloads/" 2>/dev/null || cp "$SECOND" "$WORK/home/Downloads/"
fi

echo "importing from $SOURCE into a fresh installation ..."
(cd "$WORK/game" && APPDATA="$(cygpath -w "$WORK/user" 2>/dev/null || echo "$WORK/user")" MACHINES_HOME="$WORK/home" \
    MACHINES_IMPORT="$SOURCE" MACHINES_MENU_SCREENSHOT="$WORK/menu.png,main" \
    timeout 3600 ./Machines.exe > "$WORK/import.log" 2>&1) || true
grep -E "import finished|failed|Done\.|not those" "$WORK/import.log" | tail -3

TOOL=(dotnet run -c Release --project "$REPO/tools/asset-pack" --)
status=0
echo "== classic/ (every look) against $CLASSIC"
"${TOOL[@]}" compare "$CLASSIC" "$WORK/game/classic" $PARTS || status=1
for part in textures/polyhaven textures/ambientcg models/nature; do
    if [ -d "$MAIN/$part" ]; then
        echo "== $part"
        "${TOOL[@]}" compare "$MAIN/$part" "$WORK/game/$part" || status=1
    fi
done
[ -f "$WORK/game/game-copy/models.bin" ] || [ -n "$(tr -d '\r\n' < "$WORK/game/classic/game_path.txt" 2>/dev/null)" ] || { echo "no game copy after the import"; status=1; }

[ "$KEEP" = 1 ] || rm -rf "$WORK"
[ $status = 0 ] && echo "release check passed: the installation is the checkout's, byte for byte" || echo "release check FAILED"
exit $status
