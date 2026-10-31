#!/bin/bash
# Downloads the tools the game uses to make HD versions of the original's films (game/scripts/HdFilms.cs) into
# tools/film-upscale/bin (git-ignored):
#   - RIFE's portable Vulkan build (MIT, by nihui), which draws the frames between two frames;
#   - FFmpeg's essentials build for Windows (GPL, by Gyan Doshi), which writes the films as Ogg Theora for Godot.
# The enlarging itself is done by Real-ESRGAN from tools/texture-upscale (fetch that too).
#   tools/film-upscale/fetch.sh
set -e
HERE="$(cd "$(dirname "$0")" && pwd)"
RIFE_URL=https://github.com/nihui/rife-ncnn-vulkan/releases/download/20221029/rife-ncnn-vulkan-20221029-windows.zip
FFMPEG_URL=https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip
RIFE_SHA256=4b970319db2814c82b15fceed8193151560a676a9eb63f20d4877be77b98f44f
FFMPEG_SHA256=3256173f3f8bffd7df12227c68adf68025edb1832273a9530688a7bb1ed8edec

mkdir -p "$HERE/bin"
cd "$HERE/bin"
rm -rf rife ffmpeg download
mkdir download

curl -sSL -o download/rife.zip "$RIFE_URL"
mkdir download/rife
/c/Windows/System32/tar.exe -xf download/rife.zip -C download/rife
mkdir rife
# Only the program and the newest model are kept (the archive holds eleven).
cp download/rife/*/rife-ncnn-vulkan.exe download/rife/*/vcomp140*.dll download/rife/*/LICENSE rife/
cp -r download/rife/*/rife-v4.6 rife/

curl -sSL -o download/ffmpeg.zip "$FFMPEG_URL"
mkdir download/ffmpeg
/c/Windows/System32/tar.exe -xf download/ffmpeg.zip -C download/ffmpeg
mkdir ffmpeg
cp download/ffmpeg/*/bin/ffmpeg.exe download/ffmpeg/*/LICENSE ffmpeg/

rm -rf download
echo "$RIFE_SHA256  rife/rife-ncnn-vulkan.exe" | sha256sum -c -
echo "$FFMPEG_SHA256  ffmpeg/ffmpeg.exe" | sha256sum -c -
echo "RIFE and FFmpeg are in $HERE/bin"
