#!/bin/bash
# Gameplay logic scan of the decompiled EA script assemblies (docs/559). Runs our semgrep rule sets plus two greps that
# semgrep's C# parser misses, then prints a ranked summary (rule severity, then hits per file). Results stay local.
#   tools/static/gameplay-scan.sh [out-dir]      (default game-copy/analysis/gameplay-scan)
# Needs: semgrep in ~/.local/share/venvs/ts3/bin, a decompile in game-copy/decompiled (ilspycmd, docs/557).
# Memory: semgrep -j 2 --max-memory 2500 (~1 min, < 3 GB); safe next to a running game.
set -euo pipefail
repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd -P)
out=${1:-$repo/game-copy/analysis/gameplay-scan}
mkdir -p "$out"
src=("$repo/game-copy/decompiled/Sims3GameplaySystems" "$repo/game-copy/decompiled/Sims3GameplayObjects")
sg=${SEMGREP:-$HOME/.local/share/venvs/ts3/bin/semgrep}
nice -n 10 "$sg" --config "$repo/tools/static/ea-gameplay-logic.semgrep.yml" --config "$repo/tools/static/ea-script-logic.semgrep.yml" \
  --metrics=off --no-git-ignore -j 2 --max-memory 2500 --timeout 20 --json -o "$out/semgrep.json" "${src[@]}" > "$out/semgrep.log" 2>&1 || true
# greps: inclusive RandomUtil.GetInt(Count) indexing (semgrep misses it inside do/while), and sleep-wait loops whose
# condition has no exit-reason / destroyed / counter test (uncancellable waits: bowling start wait, docs/559 §3)
grep -rnE "GetInt\((0, )?[A-Za-z0-9_.]+\.(Count|Length)\)" "${src[@]}" > "$out/grep-random-inclusive.txt" || true
grep -rnP -A2 "^\s*while \((?!.*(ExitReason|HasBeenDestroyed|Count|ElapsedTime|num|i <|j <))[^;]*\)\s*$" "${src[@]}" \
  | grep -B2 "Simulator.Sleep" | grep -E "while \(" > "$out/grep-sleep-wait.txt" || true
# direct Sim.RequestWalkStyle (per-Sim count, never released by an interaction's cleanup) in a file that never
# unrequests or clears a walk style (GoHome's Run left set, docs/560 §1-2)
grep -rlE "[A-Za-z]\.RequestWalkStyle\(" "${src[@]}" | while read -r f; do
  grep -qE "UnrequestWalkStyle|ClearWalkStyle|ClearRequestedWalkStyles" "$f" && continue
  grep -nE "[A-Za-z]\.RequestWalkStyle\(" "$f" | grep -vE "(interactionInstance|base|this|SimRoutingComponent|[Ii]nteraction)\.RequestWalkStyle" | sed "s|^|$f:|"
done > "$out/grep-walkstyle-unreleased.txt" || true
python3 -I - "$out" "$repo/game-copy/decompiled/" <<'EOF'
import json, sys, collections
out, root = sys.argv[1], sys.argv[2]
d = json.load(open(out + '/semgrep.json'))
sev = {'ERROR': 0, 'WARNING': 1, 'INFO': 2}
seen, rows = set(), []
for r in d['results']:
    k = (r['path'], r['start']['line'], r['check_id'])
    if k in seen: continue
    seen.add(k)
    line = open(r['path'], errors='replace').read().split('\n')[r['start']['line'] - 1].strip()
    rows.append((sev.get(r['extra']['severity'], 3), r['check_id'].split('.')[-1], r['path'].replace(root, ''), r['start']['line'], line[:110]))
rows.sort()
per = collections.Counter(x[1] for x in rows)
print('semgrep hits per rule:', dict(per), '| parse errors:', len(d['errors']))
for n in ('grep-random-inclusive.txt', 'grep-sleep-wait.txt', 'grep-walkstyle-unreleased.txt'):
    print(n + ':', sum(1 for _ in open(out + '/' + n)))
with open(out + '/ranked.tsv', 'w') as f:
    for x in rows: f.write('\t'.join(map(str, x)) + '\n')
print('ranked list:', out + '/ranked.tsv')
EOF
