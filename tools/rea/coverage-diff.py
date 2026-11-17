#!/usr/bin/env python3
"""Coverage diff: which function entries have NO notes in docs/ or native/.

    python3 -I tools/rea/coverage-diff.py <funcs> <covered> [--near 64]

<funcs>   : file with one function entry per line "0xADDR[,name]" (from REA analyze or Ghidra).
<covered> : newline list of 0xADDR mentioned anywhere in docs/ + native/ (the "covered" set).
Prints: per-1MB-bucket totals (funcs / covered-funcs / dark-funcs) and writes the dark list
(entries with no covered address within --near bytes) to stdout after the table, prefixed 'DARK '.
"""
import sys, argparse, bisect

def norm(a):
    a = a.strip().split(",")[0]
    try: return int(a, 16) if a.lower().startswith("0x") else int(a, 16)
    except Exception: return None

ap = argparse.ArgumentParser()
ap.add_argument("funcs"); ap.add_argument("covered")
ap.add_argument("--near", type=int, default=64)
a = ap.parse_args()

cov = sorted(v for v in (norm(l) for l in open(a.covered)) if v is not None)
def covered(v):
    i = bisect.bisect_left(cov, v)
    for j in (i-1, i):
        if 0 <= j < len(cov) and abs(cov[j]-v) <= a.near: return True
    return False

funcs = []
for l in open(a.funcs):
    v = norm(l)
    if v is not None: funcs.append((v, l.strip()))
funcs.sort()

from collections import defaultdict
tot = defaultdict(int); dark = defaultdict(int); darklist = []
for v, raw in funcs:
    b = v & ~0xFFFFF
    tot[b] += 1
    if not covered(v):
        dark[b] += 1; darklist.append(raw)
print("bucket,funcs,dark,dark_pct")
for b in sorted(tot):
    print(f"0x{b:06X},{tot[b]},{dark[b]},{100*dark[b]//max(tot[b],1)}")
print(f"TOTAL,{len(funcs)},{len(darklist)},{100*len(darklist)//max(len(funcs),1)}")
for d in darklist:
    print("DARK " + d)
