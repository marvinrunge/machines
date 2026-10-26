#!/bin/bash
# Exports the classic models from your own copy of the game into classic/models (git-ignored):
#   C:\msys64\usr\bin\bash.exe -lc "/c/Projekte/machines/tools/classic-export/run.sh /c/Users/<you>/Downloads/Machines"
set -e
export PATH=/mingw64/bin:/usr/bin:$PATH
GAME="${1:?path to your Machines game folder}"
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
mkdir -p "$REPO/classic"
# The Godot client loads textures and map images straight from the game folder.
cygpath -m "$GAME" > "$REPO/classic/game_path.txt"
"$HERE/model-list.sh" "$REPO/data/original" > "$REPO/classic/models.txt"
timeout 1800 "$HERE/build/classic_export.exe" "$GAME" "$REPO/classic/models.txt" "$REPO/classic/models" "$REPO/data/original/sounds/snddef64.dat" > "$REPO/classic/export.log" 2>&1 || true
tail -15 "$REPO/classic/export.log"
