#!/bin/bash
# The story's co-op with real game instances on one computer (prologue plan WP-M, decision 5), without the public relay:
#   GODOT=<Godot console exe> tools/net-bots/story-coop.sh <absolute out folder> [port] [seconds]
# A hosts a relay of its own (MACHINES_MP=serve, as "Host" on the LAN page does), opens a story room on the prologue and
# starts once B is in and ready: A is the Locator, B Ike, Bob the computer's companion. With LATE=1 a third instance C waits
# in the lobby and joins the running story late, taking Bob over. Each instance has its own APPDATA, prints its world's
# checksum every 100 ticks, wears the night vision goggles (the deck is dark) and saves a screenshot after <seconds> (60) of
# its match, then quits and keeps its recording. Afterwards the checksums of the ticks every instance reported are compared
# (checksums.txt; "agree" or the first tick they differ at) and every recording is played back offline against them.
# Needs the classic export in the checkout (the models), so run it in the main checkout after the merge.
set -u
OUT=${1:?absolute out folder}; port=${2:-7971}; seconds=${3:-60}
: "${GODOT:?set GODOT to the Godot console exe}"
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
mkdir -p "$OUT"
ticks=$(seq -s, 100 100 6000)
say() { echo "$(date +%H:%M:%S) $*" | tee -a "$OUT/steps.log"; }
waitfor() { for ((i = 0; i < $3 * 4; i++)); do grep -q "$2" "$1" 2>/dev/null && return 0; sleep 0.25; done; return 1; }

# game <x> ENV=VALUE...: an instance with its own APPDATA and replay folder, its log in <x>.log.
game() {
  local x=$1; shift
  local data="$OUT/apps/$x/Godot/app_userdata/Machines Remake"
  mkdir -p "$data" "$OUT/replays/$x"
  (cd "$ROOT" && env APPDATA="$OUT/apps/$x" MACHINES_WEATHER=0 MACHINES_LOOK=Overhauled MACHINES_REPLAY_DIR="$OUT/replays/$x" \
    MACHINES_CHECKSUM_AT=$ticks "$@" "$GODOT" --path game --resolution "${RES:-1280x720}" -- "story-$x" > "$OUT/$x.log" 2>&1
   echo "exit at $(date +%T)" >> "$OUT/$x.log")
}
play() { game "$1" MACHINES_SCREENSHOT="$OUT/$1.png" MACHINES_SCREENSHOT_AFTER="$seconds" MACHINES_SCREENSHOT_GOGGLES=1 "${@:2}"; }

(cd "$ROOT" && dotnet build game/Machines.csproj -v q > "$OUT/build.log" 2>&1) || { say "build failed (build.log)"; exit 1; }
relay=127.0.0.1:$port
play a MACHINES_MP="serve,$relay,story=prolog,players=2,name=Host" &
waitfor "$OUT/a.log" "mp:\|relay\|listening" 120 || say "A did not report its relay (going on)"
sleep 8
play b MACHINES_MP="join,$relay,seat=1,name=Anna" &
games="a b"
if [ "${LATE:-0}" = 1 ]; then
  waitfor "$OUT/a.log" "checksum at tick 200" 600 || say "A's story never ran to tick 200"
  play c MACHINES_MP="join,$relay,late=1,name=Zoe" MACHINES_MP_LOBBY_SHOT="$OUT/c-lobby.png" &
  games="a b c"
fi
for g in $games; do waitfor "$OUT/$g.log" "exit at" 1200 || say "$g did not end"; done
say "games done"

# The ticks every instance reported, compared.
{
  for g in $games; do grep -o "checksum at tick [0-9]*: [0-9a-f]*" "$OUT/$g.log" | sed "s/^/$g /"; done
} | awk '{ tick = $5; sub(":", "", tick); sum[tick] = sum[tick] " " $6; seen[tick]++ }
         END { n = split("'"$games"'", g, " "); bad = 0
               for (t in seen) if (seen[t] == n) { split(sum[t], s, " "); for (i = 2; i <= n; i++) if (s[i] != s[1]) { print "differ at tick " t ": " sum[t]; bad = 1 } }
               if (!bad) print "agree"; }' | sort -n -k4 > "$OUT/checksums.txt"
say "checksums: $(head -1 "$OUT/checksums.txt")"

# Every instance's recording played back offline with the same checksum ticks.
for g in $games; do
  file=$(ls -t "$OUT/replays/$g"/*.mreplay 2>/dev/null | head -1)
  [ -z "$file" ] && { say "$g kept no recording"; continue; }
  game "$g-replay" MACHINES_REPLAY="$file" MACHINES_MENU_GO=main MACHINES_SCREENSHOT="$OUT/$g-replay.png" MACHINES_SCREENSHOT_AFTER="$seconds" &
done
wait
for g in $games; do
  [ -f "$OUT/$g-replay.log" ] || continue
  # Only the ticks both reported: the recording ends where the match did, the playback where its screenshot is.
  awk 'NR == FNR { live[$4] = $5; next } ($4 in live) { both++; if (live[$4] != $5) { print "differs at tick " $4 " " live[$4] " " $5; bad = 1 } }
       END { print both + 0 " ticks compared"; exit bad }' \
    <(grep -o "checksum at tick [0-9]*: [0-9a-f]*" "$OUT/$g.log") <(grep -o "checksum at tick [0-9]*: [0-9a-f]*" "$OUT/$g-replay.log") \
    > "$OUT/replay-$g.txt" && say "$g's recording plays back the same ($(tail -1 "$OUT/replay-$g.txt"))" || say "$g's recording differs (replay-$g.txt)"
done
