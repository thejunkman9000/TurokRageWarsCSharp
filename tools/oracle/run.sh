#!/usr/bin/env bash
# Records the regression oracle of a scenario.   usage: tools/oracle/run.sh <outdir> [boot|trial]
# Environment: ROM (path of baserom.us.v11.z64, required), NOBUILD=1 (skip dotnet build), BUILD_WRAP="flock /path/lock" (serialise the build),
#              RUN_WRAP (same for the run), TRW_TICKS=<n> (override the scenario length in 60 Hz retraces), ORACLE_DETAIL=0 (no per-command .detail file),
#              ORACLE_DUMP_FRAME / ORACLE_DUMP_TICK (comma lists, see src/Trw.Platform/Oracle.cs), TICK_OPS (default 400, see docs/ORACLE.md)
set -euo pipefail
out=${1:?usage: run.sh <outdir> [boot|trial]}
scenario=${2:-boot}
here=$(cd "$(dirname "$0")" && pwd); repo=$(cd "$here/../.." && pwd)
: "${ROM:?set ROM to the path of baserom.us.v11.z64}"
mkdir -p "$out"; out=$(cd "$out" && pwd)
if [ -z "${NOBUILD:-}" ]; then
  ${BUILD_WRAP:-} dotnet build "$repo/src/Trw.Game" -c Release -p:EnableMGCBItems=false --nologo -v q 2>&1 | grep -E " error |Build FAILED" && exit 1 || true
fi
# the documented unattended 1-player route (docs/TRANSPILER.md), then some walking, turning and firing once the match runs (state 13)
TRIAL_PAD="150:SDOWN:5,200:A:6,R24+500:START:12,R3+300:DOWN:20,R3+700:A:12,R3+1100:A:12,R19+600:A:12,R19+1100:LEFT:20,R19+1400:A:12,R9+400:A:12,R7+500:DOWN:20,R7+800:A:12,R10+500:A:12,R10+900:A:12,R25+400:A:12,R25+800:A:12,T13+400:SUP:300,T13+1400:Z:10"
TRIAL_PAD="$TRIAL_PAD,T13+1700:SRIGHT:90,T13+1900:SUP+Z:120,T13+2200:CUP:20,T13+2300:A:10,T13+2500:SUP+SLEFT:200,T13+2800:Z:10,T13+2900:SDOWN:100,T13+3100:SRIGHT:150,T13+3300:SUP+Z:200"
case "$scenario" in
  boot)  ticks=1200;  pad="" ;;           # logos, title, menus: 20 s of game time
  trial) ticks=30000; pad="$TRIAL_PAD" ;; # boot, menus, level load, ~6 minutes of game time with 3D play
  *) echo "unknown scenario $scenario (boot|trial)"; exit 2 ;;
esac
export TRW_DETERMINISTIC=1 TRW_TICK_OPS=${TICK_OPS:-400} TRW_TICKS=${TRW_TICKS:-$ticks} TRW_STATUS=1
export TRW_ORACLE_OUT="$out/oracle.txt" TRW_ORACLE_RAM_EVERY=${ORACLE_RAM_EVERY:-30}
[ "${ORACLE_DETAIL:-1}" = 1 ] && export TRW_ORACLE_DETAIL=1
[ -n "${ORACLE_DUMP_FRAME:-}" ] && export TRW_ORACLE_DUMP_FRAME=$ORACLE_DUMP_FRAME
[ -n "${ORACLE_DUMP_TICK:-}" ] && export TRW_ORACLE_DUMP_TICK=$ORACLE_DUMP_TICK
[ -n "$pad" ] && export TRW_PAD_SCRIPT="$pad"
rm -f "$out"/oracle.txt*
start=$(date +%s.%N)
${RUN_WRAP:-} dotnet "$repo/src/Trw.Game/bin/Release/net9.0/Trw.Game.dll" --rom "$ROM" --headless 2> "$out/stderr.log" > "$out/stdout.log" || { echo "game run failed"; tail "$out/stderr.log"; exit 1; }
end=$(date +%s.%N)
{ echo "scenario=$scenario ticks=$TRW_TICKS tick_ops=$TRW_TICK_OPS wall_s=$(echo "$end - $start" | bc)"; echo "git=$(git -C "$repo" rev-parse --short HEAD 2>/dev/null)"; } > "$out/meta.txt"
echo "$scenario: $(grep -c '^F ' "$out/oracle.txt") frames, $(grep -c '^A ' "$out/oracle.txt") audio blocks, $(grep -c '^\[tick' "$out/oracle.txt") ticks -> $out/oracle.txt ($(cat "$out/meta.txt" | head -1))"
grep -q "did not quiesce" "$out/stderr.log" && echo "WARNING: game threads did not quiesce, run is not reproducible" || true
