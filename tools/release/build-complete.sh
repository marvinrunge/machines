#!/bin/bash
# Builds the complete package: the release folder of build-windows.sh with every asset of every look along, so that a
# tester needs nothing but the game's CD, which the game only checks once (DiscCheck.cs) and never copies from:
#   classic/    the original's models and terrain, the overhauled models (remake), the HD textures (hd), the HD
#               models (built, meshy), the Wired machines, the films made HD and the minimaps, with classic/bundled.txt
#               that marks the package, and game_path.txt naming the copy beside it
#   game-copy/  the game as its setup installs it, with CD 2's films (sounds, texts and films come from there)
#   music/      the original soundtrack as MP3s (MUSIC=<folder>), films/trailer.ogv (TRAILER=<file>), if given
# The package holds the original's files and what is made from them, which belong to Night Dive Studios: it is passed
# on privately, to testers who own the CD, and never published. It refuses to run in CI (GitHub Actions publishes
# its builds); the public release stays build-windows.sh's alone, which imports from the player's own copy instead.
#
#   tools/release/build-complete.sh <version> [<name>]
#
# Run build-windows.sh <godot> <version> <exporter> first: its folder build/Machines-Remake-<version> is the base.
# The package is build/Machines-Remake-<version>[-<name>]-komplett and its zip.
#   CLASSIC=<folder>  the import with every look made (default: classic/ of the main checkout this worktree belongs to)
#   COPY=<folder>     the game copy (default: the folder classic/game_path.txt names)
#   LINK=1            hardlinks the files instead of copying them (same drive; saves the space while C: is full)
#   ZIP=0             leaves the zip out
set -e
VERSION="${1:?version, e.g. 0.8.1}"
NAME="${2:+-$2}"
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
if [ -n "$CI" ] || [ -n "$GITHUB_ACTIONS" ]; then
    echo "build-complete.sh holds the original's files and is never built in CI (they would be published)." >&2
    exit 1
fi

# The main checkout's classic/ when this is a worktree (classic/ is git-ignored and lives there).
MAIN="$(cd "$REPO" && git rev-parse --path-format=absolute --git-common-dir 2>/dev/null | sed 's#/\.git$##')"
CLASSIC="${CLASSIC:-$( [ -d "$REPO/classic/models" ] && echo "$REPO/classic" || echo "$MAIN/classic" )}"
BASE="$REPO/build/Machines-Remake-$VERSION"
OUT="$REPO/build/Machines-Remake-$VERSION$NAME-komplett"
ZIPFILE="$OUT.zip"
fail() { echo "$*" >&2; exit 1; }

[ -f "$BASE/Machines.exe" ] || fail "$BASE has no Machines.exe: run tools/release/build-windows.sh <godot> $VERSION <exporter> first"
[ -d "$CLASSIC/models" ] || fail "$CLASSIC has no models: import from the game copy first (tools/classic-export/run.sh or the game's options)"

# The import must be the one this build reads: its format (ClassicImport.Format) and the HD textures' (HdTextures.Format).
want() { sed -n "s/.*public const int Format = \([0-9]*\);.*/\1/p" "$REPO/game/scripts/$1"; }
have() { tr -d '[:space:]' < "$1" 2>/dev/null || echo 0; }
[ "$(have "$CLASSIC/format.txt")" -ge "$(want ClassicImport.cs)" ] || fail "$CLASSIC is format $(have "$CLASSIC/format.txt"), this build wants $(want ClassicImport.cs): import again"
[ "$(have "$CLASSIC/hd/format.txt")" -ge "$(want HdTextures.cs)" ] || fail "$CLASSIC/hd is missing or old: make the HD textures (MACHINES_HD=<game copy>)"
for part in planets remake hd films minimaps; do
    [ -d "$CLASSIC/$part" ] || fail "$CLASSIC/$part is missing: every look must come along"
done
for part in built meshy wired; do
    [ -d "$CLASSIC/$part" ] || echo "warning: $CLASSIC/$part is missing, the package goes without it"
done

# The game copy: the one the import came from, unless COPY names another.
if [ -z "$COPY" ]; then
    COPY="$(tr -d '\r\n' < "$CLASSIC/game_path.txt")"
    case "$COPY" in /*|?:*) ;; *) COPY="$(dirname "$CLASSIC")/$COPY" ;; esac
fi
[ -f "$COPY/models.bin" ] && [ -d "$COPY/pdata" ] || fail "$COPY is no game copy (models.bin, pdata): set COPY=<folder>"
[ -f "$COPY/flics/podland.smk" ] || [ -f "$COPY/flics/Podland.smk" ] || echo "warning: $COPY has no CD 2 films (flics/podland.smk): the campaign's films stay out"

rm -rf "$OUT" "$ZIPFILE"
# Copies keep their times: the HD textures' compressed caches (.dds) are only read while not older than their pictures.
put() { # <from> <to>
    mkdir -p "$(dirname "$2")"
    if [ "$LINK" = 1 ]; then cp -al "$1" "$2"; else cp -a "$1" "$2"; fi
}
put "$BASE" "$OUT"
rm -rf "$OUT/saves" "$OUT/replays" "$OUT/classic" "$OUT/game-copy"

mkdir -p "$OUT/classic"
for part in models planets remake hd built meshy films minimaps wired; do
    [ -d "$CLASSIC/$part" ] && put "$CLASSIC/$part" "$OUT/classic/$part"
done
for file in format.txt models.txt; do
    [ -f "$CLASSIC/$file" ] && cp -p "$CLASSIC/$file" "$OUT/classic/$file"
done
# What only the making leaves behind: logs, the HD textures' work folder.
find "$OUT/classic" \( -name '*.log' -o -name 'hd-work' -o -name '*.tmp' \) -prune -exec rm -rf {} +
put "$COPY" "$OUT/game-copy"
printf 'game-copy' > "$OUT/classic/game_path.txt"
printf 'Machines Remake %s: complete package, built %s. The game checks the player'"'"'s CD instead of importing (DiscCheck.cs).\n' \
    "$VERSION" "$(date +%Y-%m-%d)" > "$OUT/classic/bundled.txt"

if [ -n "$MUSIC" ]; then
    [ -d "$MUSIC" ] || fail "MUSIC=$MUSIC is no folder"
    put "$MUSIC" "$OUT/music"
fi
if [ -n "$TRAILER" ]; then
    [ -f "$TRAILER" ] || fail "TRAILER=$TRAILER is no file"
    mkdir -p "$OUT/films"
    cp -p "$TRAILER" "$OUT/films/trailer.ogv"
fi

# The player notes say what this package is.
cat >> "$OUT/README.txt" <<'EOF'

COMPLETE PACKAGE
  This package brings everything along: the original's models, planets, sounds and films, and the
  remake's overhauled and HD looks. It is meant only for players who own Machines: Wired for War; do not
  pass it on. The first start asks for your CD 1 in the drive, a disc image of it (.iso) or the folder
  the game is installed in, and checks a few of its files. Nothing is copied from it; once the check has
  passed, the game runs without the CD. Its answer is kept in your settings (Windows: %APPDATA%\Godot\
  app_userdata\Machines Remake\settings.cfg, [disc] checked).
EOF

du -sh "$OUT" | sed 's/^/package: /'
if [ "$ZIP" != 0 ]; then
    # Windows' own tar (bsdtar) writes standard zips, with the files' times.
    (cd "$(dirname "$OUT")" && /c/Windows/System32/tar.exe -a -c -f "$(basename "$ZIPFILE")" "$(basename "$OUT")")
    echo "built $ZIPFILE"
else
    echo "built $OUT"
fi
