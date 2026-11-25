#!/usr/bin/env bash
# Reusable environment for REA (Reverse Engineer Anything) + Ghidra on this machine.
# REA is installed under ~/.local/opt/rea (npm global prefix); Ghidra 12.1.4 + JDK 21 are local.
# Source this before any `rea ...` command:  source tools/rea/rea-env.sh
export PATH="$HOME/.local/opt/node/bin:$HOME/.local/opt/rea/bin:$HOME/.local/opt/cargo/bin:$PATH"
export GHIDRA_INSTALL_DIR="$HOME/.local/opt/ghidra_12.1.4_PUBLIC"
export JAVA_HOME="$HOME/.local/opt/jdk-21.0.13+9"
export REA_ANALYSIS_PROVIDER=ghidra
# Keep the Ghidra heap well under free RAM; do NOT run a second Ghidra/analyzeHeadless or a
# big cargo build at the same time (an 8-10G heap plus a concurrent build OOM-kills Ghidra = SIGKILL).
export GHIDRA_HEADLESS_MAXMEM="${GHIDRA_HEADLESS_MAXMEM:-8G}"
# A persistent snapshot makes the first (slow) analysis reusable by later decompiles:
export REA_SNAPSHOT="${REA_SNAPSHOT:-$HOME/dev/sims3-modernization/game-copy/analysis/rea/TS3W.snapshot.json}"
export TS3_EXE="${TS3_EXE:-$HOME/dev/sims3-modernization/game-copy/rea/TS3W.repack.exe}"
# Convenience wrapper: tools/rea/rea-env.sh decompile 0x5A44A0 --json
if [ "${BASH_SOURCE[0]}" = "$0" ] && [ $# -gt 0 ]; then
  exec rea "$1" "$TS3_EXE" "${@:2}" --provider ghidra --snapshot "$REA_SNAPSHOT"
fi
