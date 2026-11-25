#!/bin/bash
# Run Microsoft InferSharp (MIT, github.com/microsoft/infersharp v1.5) on the game's script assemblies (docs/555, docs/557).
# InferSharp translates IL (Mono.Cecil) to Infer's SIL and runs Infer Pulse: interprocedural null dereference, resource
# leak and thread-safety checks. No decompile needed. Results stay local (game-copy/analysis/infersharp/<name>/).
#
# Install once (release binaries, no docker):
#   mkdir -p ~/.local/opt && cd ~/.local/opt && curl -sLO \
#     https://github.com/microsoft/infersharp/releases/download/v1.5/infersharp-linux64-v1.5.tar.gz && \
#     tar xzf infersharp-linux64-v1.5.tar.gz && mv infersharp infersharp-v1.5
#
# Usage: tools/static/infersharp.sh <name> <Assembly> [<Assembly>...]   (assembly names without .dll)
#   e.g. tools/static/infersharp.sh ui UI
#        tools/static/infersharp.sh gameplay Sims3GameplaySystems Sims3GameplayObjects
# Every other DLL in game-copy/assemblies is staged as a reference but only the named ones are translated.
# Env: INFERSHARP (install dir), JOBS (infer -j, default 4), TIMEOUT (per-procedure seconds, default 60).
# Memory: run ONE assembly per call (the gameplay pair together needs several GB). Everything runs under nice -n 10,
# dotnet steps under DOTNET_GCHeapHardLimit=0x60000000 (~1.5 GB); infer workers ~200-500 MB each (JOBS=4 keeps the
# whole run under ~2-3 GB on the UI assembly, the largest translated so far).
set -euo pipefail
if [ "$#" -lt 2 ]; then sed -n '2,16p' "$0"; exit 2; fi
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)
is=${INFERSHARP:-$HOME/.local/opt/infersharp-v1.5}
name=$1; shift
out="$repo/game-copy/analysis/infersharp/$name"
rm -rf "$out"; mkdir -p "$out/stage" "$out/refs"
for a in "$@"; do cp "$repo/game-copy/assemblies/$a.dll" "$out/stage/"; done
start=$(date +%s)
# References go to refs/ (not translated).
for f in "$repo"/game-copy/assemblies/*.dll; do [ -f "$out/stage/$(basename "$f")" ] || cp "$f" "$out/refs/"; done
# Cilsil translates only methods that have debug symbols (0 methods without a PDB). ILSpy writes a portable PDB whose
# sequence points refer to its own decompiled source (line numbers in the report match `ilspycmd -genpdb` output, not
# game-copy/decompiled exactly). PDBs are cached in game-copy/analysis/infersharp/pdb.
export PATH="$HOME/.dotnet:$HOME/.dotnet/tools:$PATH" DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" DOTNET_ROLL_FORWARD=Major \
  DOTNET_GCHeapHardLimit=${DOTNET_GCHeapHardLimit:-0x60000000}
pdb="$repo/game-copy/analysis/infersharp/pdb"; mkdir -p "$pdb"
for a in "$@"; do
  if [ ! -f "$pdb/$a.pdb" ] || [ "$repo/game-copy/assemblies/$a.dll" -nt "$pdb/$a.pdb" ]; then
    rm -rf "$pdb/tmp"; mkdir -p "$pdb/tmp"   # write aside, move when complete (a killed run leaves no half PDB)
    nice -n 10 ilspycmd -genpdb -o "$pdb/tmp" -r "$repo/game-copy/assemblies" "$repo/game-copy/assemblies/$a.dll" > "$pdb/$a.log" 2>&1
    mv "$pdb/tmp/$a.pdb" "$pdb/$a.pdb"
  fi
  cp "$pdb/$a.pdb" "$out/stage/"
done
cd "$out"   # Cilsil also writes cfg.txt into the current directory
nice -n 10 "$is/Cilsil/Cilsil" translate "$out/stage" --outcfg "$out/cfg.json" --outtenv "$out/tenv.json" > "$out/translate.log" 2>&1 \
  || { echo "translate failed, see $out/translate.log"; tail -5 "$out/translate.log"; exit 1; }
# Cilsil exits 0 on an OutOfMemoryException (seen on Sims3GameplaySystems under the 1.5 GB cap: needs a larger
# DOTNET_GCHeapHardLimit, e.g. 0xC0000000, ~3 GB): no tenv.json means the translation did not finish.
[ -s "$out/tenv.json" ] && [ -s "$out/cfg.json" ] || { echo "translate incomplete (see $out/translate.log; OutOfMemory = raise DOTNET_GCHeapHardLimit)"; grep -m1 -o "OutOfMemoryException" "$out/translate.log"; exit 1; }
nice -n 10 "$is/infer/bin/infer" run --inferconfig-path "$is/.inferconfig" -j "${JOBS:-4}" --pulse-max-disjuncts 20 \
  --timeout "${TIMEOUT:-60}" --cfg-json cfg.json --tenv-json tenv.json -o infer-out > infer.log 2>&1 \
  || { echo "infer failed, see $out/infer.log"; tail -5 infer.log; exit 1; }
cp infer-out/report.txt infer-out/report.sarif . 2>/dev/null || true
echo "done in $(( $(date +%s) - start )) s: $out/report.txt"
grep -oE 'error: .*' report.txt | sort | uniq -c || true
