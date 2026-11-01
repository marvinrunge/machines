#!/bin/bash
# Records the footage of the remake's films (game/scripts/Cinematic*.cs, tools/cinematic/films.txt), one MP4 per film
# with the game's sound and no music, for cutting into a trailer (tools/cinematic/cut.sh):
#   tools/cinematic/shoot.sh <folder> [draft|preview|final] [film ...]
# draft (the default): 640x360 at 30 frames a second, straight from the movie writer (a minute or two a film);
# preview: 1280x720, 60 frames a second blended in twos; final: 2560x1440, 120 frames a second blended in fours,
# drawn at 1.5 times the size (about two minutes a second of film, 0.7 GB of frames a second on the way).
# The films give their orders on the world's ticks, so every quality shows the same battle.
# GODOT: the console Godot .NET (default: winget's). Needs the ffmpeg of tools/film-upscale/fetch.sh, and the game
# built (dotnet build game/Machines.csproj).
set -e
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="${1:?usage: shoot.sh <folder> [draft|preview|final] [film ...]}"
QUALITY="${2:-draft}"
shift $(( $# < 2 ? $# : 2 ))
FFMPEG="$ROOT/tools/film-upscale/bin/ffmpeg/ffmpeg.exe"
GODOT="${GODOT:-$LOCALAPPDATA/Microsoft/WinGet/Packages/GodotEngine.GodotEngine.Mono_Microsoft.Winget.Source_8wekyb3d8bbwe/Godot_v4.7.2-stable_mono_win64/Godot_v4.7.2-stable_mono_win64_console.exe}"
case "$QUALITY" in
    draft) WIDTH=640 HEIGHT=360 FPS=30 BLEND=1 SCALE=1 ;;
    preview) WIDTH=1280 HEIGHT=720 FPS=60 BLEND=2 SCALE=1 ;;
    final) WIDTH=2560 HEIGHT=1440 FPS=120 BLEND=4 SCALE=1.5 ;;
    *) echo "Quality is draft, preview or final, not '$QUALITY'."; exit 1 ;;
esac
FILMS=("$@")
[ ${#FILMS[@]} -gt 0 ] || mapfile -t FILMS < <(grep -v '^//' "$ROOT/tools/cinematic/films.txt" | cut -d' ' -f1)
mkdir -p "$OUT"

# The movie writer records at the project's window size (display/window/size/viewport_*), which only a project
# override changes: game/override.cfg for these runs, gone afterwards. The film draws at that size itself
# (MACHINES_CINEMATIC_SIZE), so the window can stay small.
OVERRIDE="$ROOT/game/override.cfg"
[ -e "$OVERRIDE" ] && { echo "$OVERRIDE exists already; move it away first."; exit 1; }
trap 'rm -f "$OVERRIDE"' EXIT
printf '[display]\n\nwindow/size/viewport_width=%s\nwindow/size/viewport_height=%s\n' $WIDTH $HEIGHT > "$OVERRIDE"

for FILM in "${FILMS[@]}"; do
    read -r _ MAP WEATHER CLOCK < <(grep "^$FILM " "$ROOT/tools/cinematic/films.txt") || { echo "No film '$FILM' in films.txt."; exit 1; }
    FRAMES="$OUT/$FILM-frames"
    rm -rf "${FRAMES:?}"
    mkdir -p "$FRAMES"
    # Drafts go straight into an AVI; the finer qualities into PNGs, to be blended.
    [ "$QUALITY" = draft ] && MOVIE="$FRAMES/film.avi" || MOVIE="$FRAMES/frame.png"
    echo "$FILM: recording ($QUALITY) ..."
    ( [ "$CLOCK" != - ] && export MACHINES_CLOCK="$CLOCK"
      MACHINES_CINEMATIC=$FILM MACHINES_MAP=$MAP MACHINES_LOOK="Hd,models=overhauled" MACHINES_WEATHER=$WEATHER \
      MACHINES_CINEMATIC_SCALE=$SCALE MACHINES_CINEMATIC_SIZE=${WIDTH}x${HEIGHT} \
          "$GODOT" --path "$ROOT/game" --resolution 1280x720 --fixed-fps $FPS --write-movie "$MOVIE" \
          > "$FRAMES/godot.log" 2>&1 )
    # The takes and what happened when, for cutting.
    grep "cinematic:\|ERROR" "$FRAMES/godot.log" > "$OUT/$FILM.log" || true
    # The film records the game 20 dB quieter, as the whole battle firing at once would clip: it is brought back up
    # here and compressed, so that the engines of the quiet takes are heard and the battle stays loud without
    # breaking up.
    SOUND="volume=20dB,acompressor=threshold=-20dB:ratio=3:attack=3:release=200:makeup=2,alimiter=limit=0.95:level=disabled"
    if [ "$QUALITY" = draft ]; then
        "$FFMPEG" -v error -y -i "$MOVIE" -vf format=yuv420p -af "$SOUND" \
            -c:v libx264 -preset fast -crf 20 -c:a aac -b:a 192k -movflags +faststart "$OUT/$FILM.mp4"
    else
        # Each output frame is the mean of the last BLEND drawn (motion blur, as a film camera's open shutter).
        "$FFMPEG" -v error -y -framerate $FPS -i "$FRAMES/frame%08d.png" -i "$FRAMES/frame.wav" \
            -vf "tmix=frames=$BLEND,fps=30,format=yuv420p" -af "$SOUND" \
            -c:v libx264 -preset slow -crf 15 -c:a aac -b:a 256k -movflags +faststart -shortest "$OUT/$FILM.mp4"
    fi
    [ "$KEEP" = 1 ] || rm -rf "${FRAMES:?}"
    echo "$FILM: $OUT/$FILM.mp4"
done
