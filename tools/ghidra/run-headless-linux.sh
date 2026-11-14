#!/usr/bin/env bash
# Linux replacement for run-headless.sh (the original is cygwin/Windows-pathed).
# Builds/opens a shared Ghidra project under game-copy/ghidra and runs a -postScript read-only.
# Usage: tools/ghidra/run-headless-linux.sh <Script.java> [args...]
#   First run imports + auto-analyses TS3W.repack.exe (slow, one-time). Later runs reuse the project.
# Env (override as needed):
set -euo pipefail
REPO="$(cd "$(dirname "$0")/../.." && pwd)"
GHIDRA="${GHIDRA_INSTALL_DIR:-$HOME/.local/opt/ghidra_12.1.4_PUBLIC}"
export JAVA_HOME="${JAVA_HOME:-$HOME/.local/opt/jdk-21.0.13+9}"
MAXMEM="${GHIDRA_HEADLESS_MAXMEM:-8G}"           # keep under free RAM; no concurrent Ghidra/cargo
EXE="${TS3_EXE:-$REPO/game-copy/rea/TS3W.repack.exe}"
PROJDIR="$REPO/game-copy/ghidra"
PROJ="TS3"
SCRIPT="$1"; shift || true
mkdir -p "$PROJDIR"
if [ ! -f "$PROJDIR/$PROJ.gpr" ]; then
  echo "[*] importing + analysing $EXE (one-time, slow) ..." >&2
  GHIDRA_MAXMEM="$MAXMEM" "$GHIDRA/support/analyzeHeadless" "$PROJDIR" "$PROJ" \
    -import "$EXE" -processor x86:LE:32:default -cspec windows \
    -scriptPath "$REPO/tools/ghidra" > "$REPO/game-copy/ghidra-import.log" 2>&1
fi
GHIDRA_MAXMEM="$MAXMEM" "$GHIDRA/support/analyzeHeadless" "$PROJDIR" "$PROJ" \
  -process "$(basename "$EXE")" -noanalysis -readOnly \
  -scriptPath "$REPO/tools/ghidra" -postScript "$SCRIPT" "$@" \
  > "$REPO/game-copy/ghidra-last.log" 2>&1
grep -E '>|ERROR' "$REPO/game-copy/ghidra-last.log" | grep -v WARN | cut -c1-200 | tail -8
