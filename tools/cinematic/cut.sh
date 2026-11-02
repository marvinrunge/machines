#!/bin/bash
# Cuts the films' footage (tools/cinematic/shoot.sh) into a trailer by a list of shots (tools/cinematic/cut.txt),
# the soundtrack's music under the game's sound, faded in from black and out at the end:
#   tools/cinematic/cut.sh <folder> [list] [out.mp4]
# The music comes from the soundtrack folder picked in the game's menu (music_folder in its settings.cfg), or MUSIC.
# CRF and PRESET set the picture's x264 quality (default 20 and fast; 16 and slow for the finished trailer).
set -e
ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
DIR="${1:?usage: cut.sh <folder> [list] [out.mp4]}"
LIST="${2:-$ROOT/tools/cinematic/cut.txt}"
OUT="${3:-$DIR/cut.mp4}"
FFMPEG="$ROOT/tools/film-upscale/bin/ffmpeg/ffmpeg.exe"
FOLDER="${MUSIC:-$(sed -n 's/^music_folder="\([^"]*\)".*/\1/p' "$APPDATA/Godot/app_userdata/Machines Remake/settings.cfg" | head -1)}"

INPUTS=() CHAIN="" SHOTS="" N=0 LENGTH=0
while read -r WORD REST; do
    case "$WORD" in
        ''|//*) continue ;;
        music)
            # music "<file>" <second to start at>
            TRACK="$(sed 's/^"\(.*\)" .*/\1/' <<< "$REST")"
            FROM="${REST##* }" ;;
        *)
            read -r START END <<< "$REST"
            # Each shot as many of the footage's frames (30 a second) as reach from where the list puts its cut to
            # where it puts the next: rounding every shot by itself added up to the picture running behind the music.
            FRAMES=$(awk "BEGIN { print int(($LENGTH + $END - $START) * 30 + 0.5) - int($LENGTH * 30 + 0.5) }")
            SECONDS_=$(awk "BEGIN { printf \"%.6f\", $FRAMES / 30 }")
            INPUTS+=(-ss "$START" -i "$DIR/$WORD.mp4")
            CHAIN+="[$N:v]trim=end_frame=$FRAMES,setpts=PTS-STARTPTS[v$N];[$N:a]atrim=end=$SECONDS_,asetpts=PTS-STARTPTS[a$N];"
            SHOTS+="[v$N][a$N]"
            N=$((N + 1))
            LENGTH=$(awk "BEGIN { print $LENGTH + $END - $START }") ;;
    esac
done < "$LIST"
[ -n "$TRACK" ] || { echo "No music line in $LIST."; exit 1; }
echo "$N shots, $LENGTH s, music: $TRACK from $FROM s"

# The game's sound under the music; the music fades out over the last four seconds, the picture over the last two.
FADE=$(awk "BEGIN { print $LENGTH - 2 }")
MUSIC_FADE=$(awk "BEGIN { print $LENGTH - 4 }")
"$FFMPEG" -v error -y "${INPUTS[@]}" -ss "$FROM" -i "$FOLDER/$TRACK" -filter_complex "\
${CHAIN}${SHOTS}concat=n=$N:v=1:a=1[v][game];\
[v]fade=in:st=0:d=1,fade=out:st=$FADE:d=2,format=yuv420p[picture];\
[game]volume=0.55[under];\
[$N:a]atrim=0:$LENGTH,afade=out:st=$MUSIC_FADE:d=4[music];\
[under][music]amix=inputs=2:duration=longest:normalize=0,alimiter=limit=0.95:level=disabled[sound]" \
    -map "[picture]" -map "[sound]" -t "$LENGTH" -c:v libx264 -preset "${PRESET:-fast}" -crf "${CRF:-20}" -c:a aac -b:a 192k \
    -movflags +faststart "$OUT"
echo "$OUT"
