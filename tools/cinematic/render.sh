#!/bin/bash
# Records the cinematic trailer (game/scripts/Cinematic.cs, data/maps/trailer.scn) as an MP4:
#   1. Godot's movie writer draws the film frame by frame at a fixed rate: PNGs and the game's sound as a WAV;
#   2. ffmpeg blends each few frames into one (motion blur, as a film camera's open shutter), lays the original's
#      intro music under the game's sound and writes H.264.
#   tools/cinematic/render.sh <out.mp4> [preview]
# preview: 1280x720, 60 frames a second blended in twos, drawn at the window's size (a few minutes).
# Otherwise 2560x1440, 120 frames a second blended in fours, drawn at 1.5 times the size (about an hour,
# about 20 GB of frames on the way, deleted at the end unless KEEP=1).
# GODOT: the console Godot .NET (default: winget's); MUSIC: the soundtrack's intro (default: Track 00 in the music
# folder of the game's settings); GAME_VOLUME, MUSIC_VOLUME: the mix (default 1 and 0.45); FRAMES: where the frames
# go (default: beside the output). Needs the ffmpeg of
# tools/film-upscale/fetch.sh, and the game built (dotnet build game/Machines.csproj).
set -e
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="${1:?usage: render.sh <out.mp4> [preview]}"
FFMPEG="$ROOT/tools/film-upscale/bin/ffmpeg/ffmpeg.exe"
GODOT="${GODOT:-$LOCALAPPDATA/Microsoft/WinGet/Packages/GodotEngine.GodotEngine.Mono_Microsoft.Winget.Source_8wekyb3d8bbwe/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"
if [ -z "$MUSIC" ]; then
    SETTINGS="$APPDATA/Godot/app_userdata/Machines Remake/settings.cfg"
    FOLDER="$(sed -n 's/^music_folder="\(.*\)"$/\1/p' "$SETTINGS" 2>/dev/null | head -1)"
    MUSIC="$FOLDER/Track 00 - Intro.mp3"
fi
[ -f "$MUSIC" ] || { echo "No music at '$MUSIC': set MUSIC to the soundtrack's intro."; exit 1; }

if [ "$2" = preview ]; then
    WIDTH=1280 HEIGHT=720 FPS=60 BLEND=2 SCALE=1
else
    WIDTH=2560 HEIGHT=1440 FPS=120 BLEND=4 SCALE=1.5
fi
LENGTH=30
FRAMES="${FRAMES:-${OUT%.*}-frames}"
rm -rf "${FRAMES:?}"
mkdir -p "$FRAMES"

# The movie writer records at the project's window size (display/window/size/viewport_*), which only a project
# override changes: game/override.cfg for this run, gone afterwards. The film draws at that size itself
# (MACHINES_CINEMATIC_SIZE), so the window can stay small.
OVERRIDE="$ROOT/game/override.cfg"
[ -e "$OVERRIDE" ] && { echo "$OVERRIDE exists already; move it away first."; exit 1; }
trap 'rm -f "$OVERRIDE"' EXIT
printf '[display]\n\nwindow/size/viewport_width=%s\nwindow/size/viewport_height=%s\n' $WIDTH $HEIGHT > "$OVERRIDE"

# The film quits by itself when it is over.
MACHINES_CINEMATIC=trailer MACHINES_MAP=trailer,hilly MACHINES_LOOK="Hd,models=overhauled" MACHINES_WEATHER=0 \
MACHINES_CINEMATIC_SCALE=$SCALE MACHINES_CINEMATIC_SIZE=${WIDTH}x${HEIGHT} \
    "$GODOT" --path "$ROOT/game" --resolution 1280x720 --fixed-fps $FPS --write-movie "$FRAMES/frame.png" \
    > "$FRAMES/godot.log" 2>&1
rm -f "$OVERRIDE"
grep "cinematic:\|recorded in" "$FRAMES/godot.log" | head -3

# Each output frame is the mean of the last BLEND drawn. The film records the game 20 dB quieter, as the whole
# battle firing at once would clip: it is brought back up here and compressed, so that the engines of the quiet
# takes are heard and the battle stays loud without breaking up, over the music, which comes in with the picture and
# goes out after the title.
"$FFMPEG" -v error -y -framerate $FPS -i "$FRAMES/frame%08d.png" -i "$FRAMES/frame.wav" -t $LENGTH -i "$MUSIC" \
    -filter_complex "[0:v]tmix=frames=$BLEND,fps=30,format=yuv420p[v];\
[1:a]volume=20dB,volume=${GAME_VOLUME:-1},acompressor=threshold=-20dB:ratio=3:attack=3:release=200:makeup=2[game];\
[2:a]volume=${MUSIC_VOLUME:-0.45},afade=t=in:d=1.5,afade=t=out:st=$((LENGTH - 2)):d=2[music];\
[game][music]amix=inputs=2:normalize=0,alimiter=limit=0.95:level=disabled[a]" \
    -map "[v]" -map "[a]" -c:v libx264 -preset slow -crf 15 -c:a aac -b:a 256k -movflags +faststart -t $LENGTH "$OUT"
[ "$KEEP" = 1 ] || rm -rf "${FRAMES:?}"
echo "Written: $OUT"
