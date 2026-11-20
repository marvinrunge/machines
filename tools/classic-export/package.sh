#!/bin/bash
# Copies the built exporter, the MSYS2 DLLs it needs and the model list into a release folder, so the game's
# importer can run it on a machine without MSYS2:
#   package.sh <target folder>        (run from an MSYS2 shell after build.sh)
set -e
export PATH=/mingw64/bin:/ucrt64/bin:/usr/bin:$PATH
TARGET="${1:?target folder, e.g. build/windows/tools/classic-export}"
HERE="$(cd "$(dirname "$0")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
EXE="$HERE/build/classic_export.exe"
[ -f "$EXE" ] || { echo "build the exporter first (build.sh)"; exit 1; }

mkdir -p "$TARGET"
cp "$EXE" "$TARGET/"
# Every DLL from MSYS2's MinGW64 and UCRT64 trees (system DLLs stay out); ldd lists them recursively.
# MSYS2's SDL2 is sdl2-compat: its SDL2.dll loads SDL3.dll at run time, which ldd cannot see, and without it the
# exporter stops with "Failed loading SDL3 library." So SDL3.dll and what it needs go along too.
LIBS=("$EXE")
[ -f /mingw64/bin/SDL3.dll ] && LIBS+=(/mingw64/bin/SDL3.dll)
for lib in "${LIBS[@]}"; do ldd "$lib"; done | awk '{print $3}' | grep -iE '/(mingw64|ucrt64)/' | sort -u \
  | while read -r dll; do cp "$dll" "$TARGET/"; done
[ -f /mingw64/bin/SDL3.dll ] && cp /mingw64/bin/SDL3.dll "$TARGET/"
if [ -f "$TARGET/SDL2.dll" ] && grep -qa 'SDL3' "$TARGET/SDL2.dll" && [ ! -f "$TARGET/SDL3.dll" ]; then
  echo "SDL2.dll is sdl2-compat but SDL3.dll is missing (pacman -S mingw-w64-x86_64-sdl3)"; exit 1
fi
# DLLs put beside the build by hand (libenet-7.dll from UCRT64, see README.md).
cp "$HERE"/build/*.dll "$TARGET/" 2>/dev/null || true
"$HERE/model-list.sh" "$REPO/data/original" > "$TARGET/models.txt"
cp "$HERE/README.md" "$TARGET/README.md"
echo "packaged $(ls "$TARGET" | wc -l) files into $TARGET"
