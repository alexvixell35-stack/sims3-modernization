#!/usr/bin/env bash
# Run fkie-cad cwe_checker (Ghidra-based CWE scanner) over the exe and emit JSON findings.
# cwe_checker is NOT vendored (GPL-3.0, heavy Rust build). Build it once, out of tree:
#   git clone --depth 1 https://github.com/fkie-cad/cwe_checker <dir>
#   cd <dir> && make all GHIDRA_PATH=$HOME/.local/opt/ghidra_12.1.4_PUBLIC
# This machine has no gcc; build with clang + compiler-rt (see tools/rea/README or docs/551):
#   export CC=clang CXX=clang++ CARGO_TARGET_X86_64_UNKNOWN_LINUX_GNU_LINKER=clang
#   export RUSTFLAGS="-Clink-arg=--rtlib=compiler-rt -Clink-arg=--unwindlib=libunwind -Clink-arg=-fuse-ld=lld -L$HOME/.local/opt/LLVM-23.1.4-Linux-X64/lib/clang/23/lib/x86_64-unknown-linux-gnu"
# Usage: tools/rea/cwe-checker-run.sh <path-to-cwe_checker-binary> [exe] [out.json]
set -euo pipefail
BIN="${1:?path to cwe_checker binary}"
EXE="${2:-$HOME/dev/sims3-modernization/game-copy/rea/TS3W.repack.exe}"
OUT="${3:-$HOME/dev/sims3-modernization/game-copy/analysis/cwe/ts3w-cwe.json}"
export GHIDRA_INSTALL_DIR="${GHIDRA_INSTALL_DIR:-$HOME/.local/opt/ghidra_12.1.4_PUBLIC}"
export JAVA_HOME="${JAVA_HOME:-$HOME/.local/opt/jdk-21.0.13+9}"
mkdir -p "$(dirname "$OUT")"
# 32-bit x86 PE is experimental in cwe_checker; --json gives machine-readable findings.
"$BIN" "$EXE" --json --out "$OUT" || { echo "cwe_checker failed on this PE (see above)"; exit 1; }
echo "findings -> $OUT"
python3 -c "import json,collections;d=json.load(open('$OUT'));c=collections.Counter(x.get('name','?') for x in d);print('\n'.join(f'{n}\t{k}' for k,n in c.most_common()))" 2>/dev/null || true
