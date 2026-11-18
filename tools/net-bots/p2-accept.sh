#!/bin/bash
# The P2 acceptance with real game instances (P2 design §9, S6), on one computer:
#   GODOT=<Godot console exe> tools/net-bots/p2-accept.sh <out folder> [port]
# A standalone relay (its log timestamped), five game instances, each with its own APPDATA and replay folder, every person's
# hero driven by the battle driver's online mode, pace lines and checksums every 200 ticks, and five net bots in the other
# seats: nine people at the start of a 5 vs 5 on m_desert at tier I with three lives, ten once E has joined.
#   A hosts (seat 0) and starts once nine are ready; B (seat 1) runs at most 8 ticks a second for 45 s from tick 600;
#   C (seat 5) goes out of step on purpose after tick 2400; D (seat 6) plays; the bots take seats 2, 3, 4, 7 and 8;
#   E waits in the lobby and joins the battle late, 60 s after A's world is built, into the one bot's seat (9).
# FALLBACK=1 plays three instances (A, B, C) and seven bots instead, for a computer that cannot carry five.
# SEED_APPDATA=<an APPDATA folder of an earlier run> copies its shader caches into each instance's, for a quicker warm-up;
# RES=<w>x<h> sets the instances' window size (1280x720, as five of them share one graphics card).
# Afterwards: every instance's recording is played back offline with the same checksum ticks (replay-<x>.log), the bots
# check their own, and the comparer sets everything against the relay's clock and each other (compare.txt).
set -u
OUT=${1:?out folder}; port=${2:-7961}
: "${GODOT:?set GODOT to the Godot console exe}"
ROOT=$(cd "$(dirname "$0")/../.." && pwd)
mkdir -p "$OUT"
ticks=$(seq -s, 200 200 20000)
say() { echo "$(date +%H:%M:%S) $*" | tee -a "$OUT/steps.log"; }
waitfor() { for ((i = 0; i < $3 * 4; i++)); do grep -q "$2" "$1" 2>/dev/null && return 0; sleep 0.25; done; return 1; }
stamp() { awk '{ print strftime("%H:%M:%S"), $0; fflush() }'; }

# game <x> <log> ENV=VALUE...: an instance with its own APPDATA and replay folder, tagged "-- p2-<x>" on its command line.
game() {
  local x=$1 log=$2; shift 2
  local data="$OUT/apps/$x/Godot/app_userdata/Machines Remake"
  mkdir -p "$data" "$OUT/replays/$x"
  if [ -n "${SEED_APPDATA:-}" ] && [ ! -d "$data/shader_cache" ]; then
    cp -r "$SEED_APPDATA/Godot/app_userdata/Machines Remake/shader_cache" "$SEED_APPDATA/Godot/app_userdata/Machines Remake/vulkan" "$data/" 2>/dev/null
  fi
  (cd "$ROOT" && env APPDATA="$OUT/apps/$x" MACHINES_WEATHER=0 MACHINES_REPLAY_DIR="$OUT/replays/$x" "$@" \
    "$GODOT" --path game --resolution "${RES:-1280x720}" -- "p2-$x" 2>&1 | stamp >> "$log"; echo "exit at $(date +%T)" >> "$log")
}
play() { game "$1" "$OUT/$1.log" MACHINES_BATTLE_DRIVE="$OUT/$1,online" MACHINES_BANNER_SHOTS="$OUT,$1" MACHINES_PACE=1 MACHINES_CHECKSUM_AT=$ticks "${@:2}"; }

(cd "$ROOT" && dotnet build server/Machines.Relay/Machines.Relay.csproj -v q > /dev/null && dotnet build tools/net-bots/Machines.NetBots.csproj -v q > /dev/null) \
  || { say "build failed"; exit 1; }
(cd "$ROOT" && exec dotnet server/Machines.Relay/bin/Debug/net10.0/Machines.Relay.dll --port "$port") 2>&1 | stamp > "$OUT/relay.log" &
waitfor "$OUT/relay.log" "listening" 60 || { say "the relay did not start"; exit 1; }
say "relay on $port"
relay=127.0.0.1:$port

if [ "${FALLBACK:-0}" = 1 ]; then people=10; seats=2,3,4,6,7,8,9; count=7; else people=9; seats=2,3,4,7,8; count=5; fi
play a MACHINES_MP="host,$relay,battle=m_desert,tier=1,lives=3,team=5,players=$people,name=A" &
waitfor "$OUT/relay.log" "A (.*) connected" 180 || { say "A never connected"; exit 1; }
sleep 5
play b MACHINES_MP="join,$relay,seat=1,name=B" MACHINES_THROTTLE=600,45,8 &
sleep 4
play c MACHINES_MP="join,$relay,seat=5,name=C" MACHINES_DESYNC_AT=2400 &
sleep 4
if [ "${FALLBACK:-0}" != 1 ]; then play d MACHINES_MP="join,$relay,seat=6,name=D" & sleep 4; fi
(cd "$ROOT" && exec dotnet tools/net-bots/bin/Debug/net10.0/Machines.NetBots.dll --relay "$relay" --count $count --seats $seats --name Bot --out "$OUT/bots") \
  > "$OUT/bots.log" 2>&1 &
bots=$!
say "instances and $count bots started"
games="a b c"
if [ "${FALLBACK:-0}" != 1 ]; then
  games="a b c d e"
  waitfor "$OUT/a.log" "net: loaded at tick" 600 || say "A never loaded"
  say "A's world built; E joins in 60 s"
  sleep 60
  play e MACHINES_MP="join,$relay,late=1,name=E" MACHINES_MP_LOBBY_SHOT="$OUT/e-lobby.png" &
fi
for g in $games; do waitfor "$OUT/$g.log" "exit at" 2400 || say "$g did not end"; done
say "games done"
wait "$bots"
say "bots done (exit $?)"
for pid in $(powershell -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name like 'dotnet%'\" | Where-Object { \$_.CommandLine -like '*Machines.Relay.dll --port $port*' } | ForEach-Object { \$_.ProcessId }"); do
  taskkill //F //PID "$pid" > /dev/null 2>&1
done
say "relay stopped"

# Every instance's recording played back offline, with the same checksum ticks, all at once.
for g in $games; do
  file=$(ls -t "$OUT/replays/$g"/*.mreplay 2>/dev/null | head -1)
  [ -z "$file" ] && { say "$g kept no recording"; continue; }
  game "$g-replay" "$OUT/replay-$g.log" MACHINES_REPLAY="$file" MACHINES_CHECKSUM_AT=$ticks MACHINES_MENU_GO=main MACHINES_BATTLE_DRIVE="$OUT/$g-replay" &
done
wait
say "the recordings played back"

speed="a.log"; [ "${FALLBACK:-0}" != 1 ] && speed="a.log,d.log"
for ((i = 1; i <= count; i++)); do speed="$speed,Bot $i.log"; done
logs=("$OUT"/[a-e].log "$OUT"/replay-*.log "$OUT"/bots/*.log)
(cd "$ROOT" && dotnet tools/net-bots/bin/Debug/net10.0/Machines.NetBots.dll compare "$OUT/relay.log" --at-speed "$speed" "${logs[@]}") | tee "$OUT/compare.txt"
