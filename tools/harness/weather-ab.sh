#!/bin/bash
# Weather cost (docs/599): one Bridgeport session, camera on the household lot, normal speed, weather cycled
# clear -> rain -> heavyrain -> snow -> blizzard -> clear (SEG_MIN minutes each, default 6; season SEASON, default winter,
# so snow builds up and stays for the last clear). Per segment: sim tick stats (ts3agent tick_stats), hitch_watch top 40,
# weather state at start and end; presented-frame times for the whole session (frame-times.js via frames-attach.py,
# FRAMES=0 skips it). Summary: node tools/harness/weather-stats.ts <run dir> (also written to weather-stats.txt).
# Drain build asi/drain-7ab14d12 with the ab-tp ini keys; asi and ini restored on exit.
B="$HOME/Documents/The Sims 3 Collection/game/prefix/drive_c/The Sims 3/Game/Bin"; cd ~/dev/ts3-runs
RUNS="$HOME/dev/ts3-runs"; REPO="$HOME/dev/sims3-modernization"; G="$HOME/Documents/The Sims 3 Collection"; PFX="$G/game/prefix"
PKG="$PFX/drive_c/users/Public/Documents/Electronic Arts/The Sims 3/Mods/Packages"
K="RouteJobPhases.Enabled=1 RouteJobPhases.HostSplit=1 RouteAsync.DoorMemo=1 RouteSnap.DoorMemoX=3 RouteAsync.KdCutCamRecheck=2 StallWatch.Enabled=1 AnimPhase.Enabled=1"
SEGS="${SEGS:-clear rain heavyrain snow blizzard clear2}"; M=${SEG_MIN:-6}; SEASON=${SEASON:-winter}
while pgrep -x TS3W.exe >/dev/null || pgrep -f 'phasesoak|s6[0-9]-run|needs-run\.sh|ab-apt\.sh' >/dev/null; do sleep 20; done
cp "$B/ts3native.asi" "$B/ts3native.asi.bak-wx"; cp "$B/ts3native.ini" "$B/ts3native.ini.bak-wx"
restore() {
  touch "${S:-/nonexistent}/frames.stop" 2>/dev/null
  cp "$B/ts3native.asi.bak-wx" "$B/ts3native.asi"; cp "$B/ts3native.ini.bak-wx" "$B/ts3native.ini"
  cp "$RUNS/pkg/user-play.package" "$PKG/ts3fixes.package" 2>/dev/null   # the player's own package back, as phasesoak does
  echo "restored $(sha256sum "$B/ts3native.asi" | cut -c1-16)"
}
trap restore EXIT
cp asi/drain-7ab14d12/ts3native/ts3native.asi "$B/ts3native.asi"; ./iniset.py "$B/ts3native.ini" $K
S="$RUNS/weather-$(date +%Y%m%d-%H%M)"; mkdir -p "$S"
cp "$REPO/build/ts3agent.package" "$PKG/ts3agent.package"
cp "${FIXPKG:-$REPO/build/ts3fixes.package}" "$PKG/ts3fixes.package" || { echo "ts3fixes install failed"; exit 1; }
EARLYFIX="${EARLYFIX-$(node "$REPO/tools/harness/proven-fixes.ts")}"
before=$(ls -d "$RUNS"/auto-2* | tail -1); rm -f "$RUNS/STOP" "$RUNS/LOADING" "$RUNS/QUIT"
n=0; for x in $SEGS; do n=$((n+1)); done
"$RUNS/autotest.sh" 1 $((n*(M+1)+10)) Bridgeport.world > "$S/autotest.txt" 2>&1 & A=$!
for i in $(seq 120); do OUT=$(ls -d "$RUNS"/auto-2* | tail -1); [ "$OUT" != "$before" ] && break; sleep 2; done
if [ -n "$EARLYFIX" ]; then ( cd "$REPO"; j=$(for x in $EARLYFIX; do printf '"%s":true,' "$x"; done); j="{${j%,}}"; for i in $(seq 300); do r=$(timeout 20 node tools/harness/ag.mjs patches "$j" 2>/dev/null); echo "$r" | grep -q '"ok":true' && { echo "$r" | head -c 4000 > "$S/earlyfix.txt"; break; }; sleep 2; done ) & fi
until grep -q 'in live mode' "$OUT/summary.txt" 2>/dev/null; do kill -0 $A 2>/dev/null || exit 1; sleep 5; done
cd "$REPO"
ag() { local r i; for i in $(seq 40); do r=$(timeout 300 node tools/harness/ag.mjs "$@" 2>/dev/null); echo "$r" | grep -q 'previous request still pending' || break; sleep 3; done; echo "$r"; }
say() { echo "[$(date -u +%H:%M:%S)Z] $*" | tee -a "$S/phase.txt"; }
now() { date +%s%3N; }
cam() { ag camera '{"lot":"home"}' >/dev/null; }
ag autopilot '{"enabled":true,"speed":"normal","dialogs":true}' >/dev/null; ag set_speed '{"speed":"normal"}' >/dev/null
say "live; asi $(sha256sum "$B/ts3native.asi" | cut -c1-16); segments $SEGS x $M min; season $SEASON"
if [ "${FRAMES:-1}" = 1 ]; then
  W() { echo "Z:$(echo "$1" | tr / '\\')"; }
  WINEPREFIX="$PFX" WINEDEBUG=-all "$G/game/wine/bin/wine" "$(W "$HOME/.cache/ts3-toolchain/winpy/python/python.exe")" -I \
    "$(W "$REPO/tools/harness/frames-attach.py")" "$(W "$S/frames.csv")" "$(W "$S/frames.stop")" > "$S/frames.log" 2>&1 &
  sleep 15; say "frames: $(grep -m1 -E 'attached|Error|error' "$S/frames.log" | head -c 200)"
fi
ag weather "{\"set\":\"clear\",\"hours\":24,\"season\":\"$SEASON\"}" > "$S/wx-season.json"; say "season: $(head -c 300 "$S/wx-season.json")"
cam; sleep 60   # season switch and camera settle
ag tick_stats '{"start":true}' >/dev/null; ag hitch_watch '{"start":true,"min_ms":100,"log_ms":300}' >/dev/null
: > "$S/segments.tsv"
for seg in $SEGS; do
  kill -0 $A 2>/dev/null || { say "session ended"; break; }
  w=${seg%2}
  ag weather "{\"set\":\"$w\",\"hours\":24}" > "$S/wx-$seg-start.json"; cam
  ag tick_stats '{"reset":true}' >/dev/null; ag hitch_watch '{"start":true,"min_ms":100,"log_ms":300}' >/dev/null
  t0=$(now); say "segment $seg: $(grep -oE '"(weather|forced|intensity|snowLevelValue|wetLevel)":[^,}]*' "$S/wx-$seg-start.json" | tr '\n' ' ')"
  e=$(( $(date +%s) + M*60 ))
  while [ "$(date +%s)" -lt "$e" ]; do sleep 60; cam; ag weather '{"get":true}' >> "$S/wx-$seg-trace.jsonl"; echo >> "$S/wx-$seg-trace.jsonl"; done
  t1=$(now)
  ag tick_stats '{"reset":true}' > "$S/tick-$seg.json"; ag hitch_watch '{"top":40}' > "$S/hw-$seg.json"; ag weather '{"get":true}' > "$S/wx-$seg-end.json"
  printf '%s\t%s\t%s\n' "$seg" "$t0" "$t1" >> "$S/segments.tsv"
  say "  end $seg: tick $(grep -oE '"(avg_ms|p95_ms|ticks_per_s)":[^,}]*' "$S/tick-$seg.json" | tr '\n' ' ') | $(grep -oE '"(weather|snowLevelValue|wetLevel)":[^,}]*' "$S/wx-$seg-end.json" | tr '\n' ' ')"
done
ag tick_stats '{"stop":true}' >/dev/null; ag hitch_watch '{"stop":true}' >/dev/null
touch "$S/frames.stop"; sleep 10
kill $A 2>/dev/null; touch "$RUNS/QUIT"; ag quit_game >/dev/null; sleep 20
WINEPREFIX="$PFX" "$G/game/wine/bin/wineserver" -k 2>/dev/null; wait $A
cp "$(ls "$OUT"/cycle-*.log 2>/dev/null | tail -1)" "$S/ts3native.log" 2>/dev/null || cp "$B/ts3native.log" "$S/ts3native.log"
node "$REPO/tools/harness/weather-stats.ts" "$S" | tee "$S/weather-stats.txt"
echo "weather done: $S"
