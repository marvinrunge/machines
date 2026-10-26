#!/bin/bash
# Downloads Real-ESRGAN's portable Windows build (BSD-3-Clause, by Xintao Wang) into tools/texture-upscale/bin
# (git-ignored), which the game uses to make its HD textures (game/scripts/HdTextures.cs):
#   tools/texture-upscale/fetch.sh
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
URL=https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip
EXE_SHA256=07e49f7cbb4ede01ae4dd4c399d3a7e5846e3d2085c3128eff881e55cb7b1a0c

mkdir -p "$HERE/bin"
cd "$HERE/bin"
curl -sSL -o realesrgan.zip "$URL"
/c/Windows/System32/tar.exe -xf realesrgan.zip
rm -f realesrgan.zip onepiece_demo.mp4 input.jpg input2.jpg
echo "$EXE_SHA256  realesrgan-ncnn-vulkan.exe" | sha256sum -c -
cp "$HERE/LICENSE-Real-ESRGAN.txt" LICENSE.txt
echo "Real-ESRGAN is in $HERE/bin"
