#!/bin/bash
# Records the hangar for the trailer (HangarScript.Film: a Reaper fitted by hand, the mouse drawn) as hangar.mp4 with
# hangar.log beside it, its beats timed for cutting:
#   tools/cinematic/shoot-hangar.sh <folder> [fast|draft]
# fast (the default): 1920x1080 at 60 frames a second blended in twos, as shoot.sh's fast; draft: 1280x720 at 30.
# A run of Wired on its own profile (the player's wired.json is not touched), in English (UI_LANG=de for German).
# LOOK and RELEASE as in shoot.sh.
set -e
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="${1:?usage: shoot-hangar.sh <folder> [fast|draft]}"
QUALITY="${2:-fast}"
FFMPEG="$ROOT/tools/film-upscale/bin/ffmpeg/ffmpeg.exe"
GODOT="${GODOT:-$LOCALAPPDATA/Microsoft/WinGet/Packages/GodotEngine.GodotEngine.Mono_Microsoft.Winget.Source_8wekyb3d8bbwe/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"
case "$QUALITY" in
    fast) WIDTH=1920 HEIGHT=1080 FPS=60 BLEND=2 ;;
    draft) WIDTH=1280 HEIGHT=720 FPS=30 BLEND=1 ;;
    *) echo "Quality is fast or draft, not '$QUALITY'."; exit 1 ;;
esac
mkdir -p "$OUT"
OUT="$(cd "$OUT" && pwd)"
FRAMES="$OUT/hangar-frames"
rm -rf "${FRAMES:?}"
mkdir -p "$FRAMES"

# The hangar lays itself out by the window, so it is drawn at the film's size (see shoot.sh for the override).
OVERRIDE="$ROOT/game/override.cfg"
[ -e "$OVERRIDE" ] && { echo "$OVERRIDE exists already; move it away first."; exit 1; }
trap 'rm -f "$OVERRIDE"' EXIT
printf '[display]\n\nwindow/size/viewport_width=%s\nwindow/size/viewport_height=%s\n' $WIDTH $HEIGHT > "$OVERRIDE"
printf '\n[editor]\n\nmovie_writer/mjpeg_quality=0.97\n' >> "$OVERRIDE"

echo "hangar: recording ($QUALITY) ..."
( [ "${RELEASE:-1}" = 1 ] && export MACHINES_MODEL_UPGRADE=0 MACHINES_NATURE=0
  MACHINES_WIRED=1 MACHINES_MAP=m_green,m_green MACHINES_WIRED_PROFILE="$FRAMES/wired.json" \
  MACHINES_HANGAR_SCRIPT="$FRAMES,film" MACHINES_LOOK="${LOOK:-Overhauled}" MACHINES_LANG="${UI_LANG:-en}" \
      "$GODOT" --path "$ROOT/game" --resolution ${WIDTH}x${HEIGHT} --fixed-fps $FPS --write-movie "$FRAMES/film.avi" \
      > "$FRAMES/godot.log" 2>&1 )
grep "hangar:\|ERROR" "$FRAMES/godot.log" > "$OUT/hangar.log" || true
SOUND="volume=6dB,alimiter=limit=0.95:level=disabled"
"$FFMPEG" -v error -y -i "$FRAMES/film.avi" -vf "tmix=frames=$BLEND,fps=30,format=yuv420p" -af "$SOUND" \
    -c:v libx264 -preset slow -crf 16 -c:a aac -b:a 256k -movflags +faststart "$OUT/hangar.mp4"
[ "$KEEP" = 1 ] || rm -rf "${FRAMES:?}"
# For the trailer, the inventory and the turntable as wide as the films' frame between their black bars (2.39:1),
# the values and the chip board cut away.
"$FFMPEG" -v error -y -i "$OUT/hangar.mp4" \
    -vf "crop=iw*0.797:ih*0.593:0:ih*0.028,scale=$WIDTH:-2:flags=lanczos,pad=$WIDTH:$HEIGHT:0:(oh-ih)/2:black,setsar=1,scale=in_range=pc:out_range=tv,format=yuv420p" \
    -c:v libx264 -preset slow -crf 16 -c:a copy "$OUT/hangar-wide.mp4"
echo "hangar: $OUT/hangar.mp4, $OUT/hangar-wide.mp4"
