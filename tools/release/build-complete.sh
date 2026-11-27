#!/bin/bash
# Builds the complete package: build-windows.sh's release folder with the asset pack beside the game (pack/), so that
# a fresh installation looks byte for byte as the checkout it was built in. The pack holds all of classic/: the models
# and terrain exported from the original, the overhauled models (remake), the HD textures with their compressed caches
# (hd), the HD models (built, meshy), the Wired machines, the HD films and the minimaps, every file sealed under a key
# that only the game's own files give (game/scripts/AssetPack.cs). The original's files themselves are not in it: the
# import still takes them from the player's CD (or installed copy) and opens the pack with them instead of exporting
# and upscaling. Sealed or not, the pack is made from the original's assets, which belong to Night Dive Studios: the
# package is passed on privately, to players who own the game, and is never built in CI or published there.
#
#   tools/release/build-complete.sh <version> [<name>]
#
# Run build-windows.sh <godot> <version> <exporter> first: its folder build/Machines-Remake-<version> is the base.
# The package is build/Machines-Remake-<version>[-<name>]-komplett and its zip. The pack is kept between builds in
# build/asset-pack and only the files that changed are sealed again.
#   CLASSIC=<folder>  the import with every look made (default: classic/ of the main checkout this worktree belongs to)
#   COPY=<folder>     the game copy whose files make the key (default: the folder classic/game_path.txt names)
#   PARTS=a,b         only those folders of classic/ in the pack (for a quick test; a real package has all)
#   TRAILER=<file>    the remake's trailer as films/trailer.ogv
#   LINK=1            hardlinks the release folder and the pack instead of copying them (same drive)
#   ZIP=0             leaves the zip out
set -e
VERSION="${1:?version, e.g. 0.8.1}"
NAME="${2:+-$2}"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
if [ -n "$CI" ] || [ -n "$GITHUB_ACTIONS" ]; then
    echo "build-complete.sh seals assets made from the original and is never built in CI (they would be published)." >&2
    exit 1
fi

# The main checkout's classic/ when this is a worktree (classic/ is git-ignored and lives there).
MAIN="$(cd "$REPO" && git rev-parse --path-format=absolute --git-common-dir 2>/dev/null | sed 's#/\.git$##')"
CLASSIC="${CLASSIC:-$( [ -d "$REPO/classic/models" ] && echo "$REPO/classic" || echo "$MAIN/classic" )}"
BASE="$REPO/build/Machines-Remake-$VERSION"
OUT="$REPO/build/Machines-Remake-$VERSION$NAME-komplett"
PACK="$REPO/build/asset-pack"
ZIPFILE="$OUT.zip"
fail() { echo "$*" >&2; exit 1; }

[ -f "$BASE/Machines.exe" ] || fail "$BASE has no Machines.exe: run tools/release/build-windows.sh <godot> $VERSION <exporter> first"
[ -d "$CLASSIC/models" ] || fail "$CLASSIC has no models: import from the game copy first (tools/classic-export/run.sh or the game's options)"

# The import must be the one this build reads: its format (ClassicImport.Format) and the HD textures' (HdTextures.Format).
want() { sed -n "s/.*public const int Format = \([0-9]*\);.*/\1/p" "$REPO/game/scripts/$1"; }
have() { tr -d '[:space:]' < "$1" 2>/dev/null || echo 0; }
[ "$(have "$CLASSIC/format.txt")" -ge "$(want ClassicImport.cs)" ] || fail "$CLASSIC is format $(have "$CLASSIC/format.txt"), this build wants $(want ClassicImport.cs): import again"
[ "$(have "$CLASSIC/hd/format.txt")" -ge "$(want HdTextures.cs)" ] || fail "$CLASSIC/hd is missing or old: make the HD textures (MACHINES_HD=<game copy>)"
if [ -z "$PARTS" ]; then
    for part in planets remake hd built meshy wired films minimaps; do
        [ -d "$CLASSIC/$part" ] || fail "$CLASSIC/$part is missing: every look must come along"
    done
fi

# The game copy, whose files make the key: the one the import came from, unless COPY names another.
if [ -z "$COPY" ]; then
    COPY="$(tr -d '\r\n' < "$CLASSIC/game_path.txt")"
    case "$COPY" in /*|?:*) ;; *) COPY="$(dirname "$CLASSIC")/$COPY" ;; esac
fi

TOOL=(dotnet run -c Release --project "$REPO/tools/asset-pack" --)
"${TOOL[@]}" pack "$CLASSIC" "$COPY" "$PACK" $PARTS
# The pack must be the checkout's assets, every one of them: the first half of the release check
# (tools/release/check-install.sh is the second, a fresh installation from the CD against the same).
if [ -z "$PARTS" ]; then
    "${TOOL[@]}" compare "$CLASSIC" "$PACK"
fi

rm -rf "$OUT" "$ZIPFILE"
put() { # <from> <to>
    mkdir -p "$(dirname "$2")"
    if [ "$LINK" = 1 ]; then cp -al "$1" "$2"; else cp -a "$1" "$2"; fi
}
put "$BASE" "$OUT"
rm -rf "$OUT/saves" "$OUT/replays" "$OUT/classic" "$OUT/game-copy" "$OUT/pack"
put "$PACK" "$OUT/pack"
if [ -n "$TRAILER" ]; then
    [ -f "$TRAILER" ] || fail "TRAILER=$TRAILER is no file"
    mkdir -p "$OUT/films"
    cp -p "$TRAILER" "$OUT/films/trailer.ogv"
fi

# The player notes say what this package is.
cat >> "$OUT/README.txt" <<'EOF'

COMPLETE PACKAGE
  This package carries the remake's looks ready made (the "pack" folder), locked: the import opens them
  with your own copy of Machines: Wired for War instead of making them on your computer, so the game
  looks exactly as it was made. Your CD (or a disc image of it, or the installed game) is still needed
  once, for the import; nothing of the original is in this package. It is meant only for players who
  own the game; do not pass it on.
EOF

du -sh "$OUT" | sed 's/^/package: /'
if [ "$ZIP" != 0 ]; then
    # Windows' own tar (bsdtar) writes standard zips.
    (cd "$(dirname "$OUT")" && /c/Windows/System32/tar.exe -a -c -f "$(basename "$ZIPFILE")" "$(basename "$OUT")")
    echo "built $ZIPFILE"
else
    echo "built $OUT"
fi
