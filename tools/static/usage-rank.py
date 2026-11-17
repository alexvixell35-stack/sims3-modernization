#!/usr/bin/env python3
"""Rank EA gameplay types by real use in our own runs (docs/589).

Usage: usage-rank.py <ts3-runs dir> <decompiled root> <function-coverage.csv> <out.csv> [jit-hot.csv]

Sources (read-only):
  phase-*/health.jsonl   per-Sim head interaction snapshots; consecutive `since_min` deltas give
                         sim-minutes per InteractionDefinition, and a change of head interaction
                         per Sim between snapshots counts as one start.
  phase-*/aut*.json      autonomy census `top` rows "Definition @ ObjectClass": test calls and ms.
  jit-hot.csv            sampled hot managed methods (class column), when given.
Each row is a type (definition outer class, object class, or hot-method class). A type's file is found
by its class name under the decompiled root; hand-read comes from function-coverage CSV.
"""
import csv, glob, json, os, re, sys
from collections import defaultdict

runs, root, cov_path, out_path = sys.argv[1:5]
jit_path = sys.argv[5] if len(sys.argv) > 5 else None

minutes = defaultdict(float)   # type -> sim-minutes as head interaction (or its target object)
starts = defaultdict(int)      # type -> interaction starts seen
aut_calls = defaultdict(int)   # type -> autonomy test calls
aut_ms = defaultdict(float)
jit_samples = defaultdict(float)
sim_minutes_total = 0.0


def outer(defn):
    # "Ns.Outer+Definition" -> "Ns.Outer"; generic arity and nested parts dropped
    return re.sub(r"`\d+", "", defn.split("+")[0]).strip()


for path in sorted(glob.glob(os.path.join(runs, "phase-*", "health.jsonl"))):
    prev = None
    for line in open(path, errors="replace"):
        try:
            r = json.loads(line)["result"]
            sims = r["sims"]
            t = float(r["since_min"])
        except (ValueError, KeyError, TypeError):
            continue
        cur = {s.get("id"): s.get("definition") or "" for s in sims}
        if prev is not None and t > prev[0]:
            dt = t - prev[0]
            for sid, d in prev[1].items():
                if d:
                    minutes[outer(d)] += dt
                    sim_minutes_total += dt
                if d and cur.get(sid) and cur[sid] != d:
                    starts[outer(cur[sid])] += 1
        prev = (t, cur)

for path in glob.glob(os.path.join(runs, "phase-*", "aut*.json")):
    try:
        top = json.load(open(path, errors="replace"))["result"].get("top") or []
    except (ValueError, KeyError, OSError):
        continue
    for row in top:
        key = row.get("key", "")
        defn, _, obj = key.partition(" @ ")
        for t in {outer(defn), obj.strip()}:
            if t:
                aut_calls[t] += int(row.get("calls", 0))
                aut_ms[t] += float(row.get("ms", 0))

if jit_path and os.path.exists(jit_path):
    for row in csv.DictReader(open(jit_path)):
        frame = row.get("frame", "")
        if frame.startswith("Sims3."):
            jit_samples[frame.rsplit(".", 1)[0]] += float(row.get("samples") or 0)

# class name -> full name and file, from the decompiled tree (one walk)
files = {}
for dirpath, _, names in os.walk(root):
    ns = os.path.basename(dirpath)
    for n in names:
        if n.endswith(".cs"):
            files.setdefault(n[:-3], []).append((ns + "." + n[:-3], os.path.relpath(os.path.join(dirpath, n), root)))
read = {}
for row in csv.DictReader(open(cov_path)):
    read[row["class"]] = row["hand-read"]


def resolve(t):
    short = t.rsplit(".", 1)[-1]
    cands = files.get(short, [])
    for full, f in cands:
        if full == t:
            return full, f
    return (cands[0] if len(cands) == 1 else (t, ""))


sim_hours = max(sim_minutes_total / 60.0, 1e-9)
rows = {}
for t in set(minutes) | set(starts) | set(aut_calls) | set(jit_samples):
    full, f = resolve(t)
    r = rows.setdefault(full, {"type": full, "file": f, "sim_minutes": 0.0, "starts": 0, "aut_calls": 0, "aut_ms": 0.0, "jit": 0.0})
    r["sim_minutes"] += minutes.get(t, 0.0)
    r["starts"] += starts.get(t, 0)
    r["aut_calls"] += aut_calls.get(t, 0)
    r["aut_ms"] += aut_ms.get(t, 0.0)
    r["jit"] += jit_samples.get(t, 0.0)

out = []
for r in rows.values():
    if not r["file"]:
        continue  # not an EA gameplay type in the decompile (UI, mscorlib, mods)
    r["uses_per_sim_hour"] = (r["starts"] + r["aut_calls"] / 100.0) / sim_hours
    # score: share of sim time + share of autonomy cost + hot-method samples, each normalised
    out.append(r)
tm = max((r["sim_minutes"] for r in out), default=1) or 1
ta = max((r["aut_ms"] for r in out), default=1) or 1
tj = max((r["jit"] for r in out), default=1) or 1
for r in out:
    r["score"] = r["sim_minutes"] / tm + r["aut_ms"] / ta + r["jit"] / tj + min(r["starts"], 200) / 200.0
    r["hand_read"] = read.get(r["type"], "no")
out.sort(key=lambda r: -r["score"])
with open(out_path, "w", newline="") as fh:
    w = csv.writer(fh)
    w.writerow(["rank", "type", "uses_per_sim_hour", "sim_minutes", "starts", "autonomy_calls", "autonomy_ms", "jit_samples", "score", "hand_read"])
    for i, r in enumerate(out, 1):
        w.writerow([i, r["type"], f"{r['uses_per_sim_hour']:.2f}", f"{r['sim_minutes']:.0f}", r["starts"], r["aut_calls"], f"{r['aut_ms']:.0f}", f"{r['jit']:.0f}", f"{r['score']:.3f}", r["hand_read"]])
print(f"{len(out)} types; {sim_minutes_total:.0f} sim-minutes observed; written {out_path}")
