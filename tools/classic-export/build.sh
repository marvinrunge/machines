#!/bin/bash
# Builds classic_export with MSYS2 MinGW64:
#   C:\msys64\usr\bin\bash.exe -lc "/c/Projekte/machines/tools/classic-export/build.sh /c/Projekte/nightdivestudio/machines"
set -e
export PATH=/mingw64/bin:$PATH
SOURCE="${1:?path to the original engine source}"
HERE="$(cd "$(dirname "$0")" && pwd)"
mkdir -p "$HERE/build"
cd "$HERE/build"
cmake -G Ninja "$HERE" -DCMAKE_BUILD_TYPE=Release -DMACHINES_SOURCE="$SOURCE" -DDEV_BUILD=OFF \
  -DENET_INCLUDE_DIRS="$SOURCE/windows/include" -DENET_LIBRARY="$SOURCE/windows/lib64/libenet.dll.a" > configure.log 2>&1 \
  || { tail -30 configure.log; exit 1; }
ninja classic_export > build.log 2>&1 || { grep -E ': error|undefined reference' build.log | sort -u | head -40; exit 1; }
echo "built $HERE/build/classic_export.exe"
