#!/usr/bin/env python3
"""Scan Ghidra/REA pseudo-C for EA-bug shapes, adapted to decompiler output.

Run with -I (untrusted-dir safety is irrelevant here, but keep the habit):
    python3 -I tools/rea/scan-pseudoc.py <input> [--covered FILE] [--min-score N]

<input> is either
  * a directory of per-function files named <hexaddr>.c (Ghidra DecompileTo), or
  * a JSONL/JSON file of REA decompile results, each {"address": "0x..","name":..,"pseudocode"|"code":..}.

--covered FILE: newline list of 0xADDR already reverse-engineered (docs/native coverage).
A function whose entry is within --near bytes of a covered address is marked covered=1
(still scanned, so you can see hits on hot known code, but ranked below dark code).

Patterns (each a heuristic on decompiler text, FP-prone by nature; verdicts need a human read):
  LOOPSUB   unsigned/size_t loop bound from a subtraction  (a-b with a<b wraps huge)
  NONULL    pointer used right after an alloc/_new/malloc with no NULL test
  IDXFILE   array/ptr indexed by a value read from a package/file/stream with no bound test
  LOCKIMBAL lock/Enter without a matching unlock/Leave on an early-return path
  ENDLESS   while(true)/for(;;) / do..while with no break/return inside
  UAF       free(x) ... later use of x (free-then-use shape)
  DBLFREE   free(x) ... free(x) with no reassignment between
  INTOVF    size/count multiply or add feeding malloc/memcpy/alloc with no overflow guard
  FLOATEQ   loop continues on float ==/!= comparison (never-terminating accumulation)

Output: CSV to stdout, ranked by score (impact*weight), covered funcs last.
"""
import sys, os, re, json, argparse, glob

# crash/corruption weight per pattern (player-impact prior; tuned, not exact)
WEIGHT = {
    "LOOPSUB": 9, "IDXFILE": 8, "UAF": 9, "DBLFREE": 8, "INTOVF": 7,
    "NONULL": 5, "LOCKIMBAL": 6, "ENDLESS": 7, "FLOATEQ": 5,
}
FILEWORDS = r"(?:read|Read|stream|Stream|package|Package|resource|Resource|dbpf|DBPF|record|Record|header|Header|param_\d|chunk|Chunk)"

def find_patterns(code: str):
    hits = []
    lines = code.splitlines()
    # LOOPSUB: for/while with (uVar - something) or (a - b) as a < bound where vars are unsigned (uVar/uint/size)
    for m in re.finditer(r"(?:for|while)\s*\([^;{]*?\b([a-zA-Z_]\w*)\s*<\s*([^;){]*-[^;){]+)\)", code):
        rhs = m.group(2)
        if re.search(r"\b(u[A-Za-z]*Var|uint|size_t|uVar\d|uStack|param_\d)\b", rhs) or "0x" in rhs:
            hits.append(("LOOPSUB", m.group(0)[:120]))
    # FLOATEQ: loop guarded by float == / != (fVar / float / double)
    for m in re.finditer(r"(?:for|while)\s*\([^;{]*\b(f[A-Za-z]\w*|dVar\d+|fVar\d+)\s*(==|!=)\s*[^;){]+\)", code):
        hits.append(("FLOATEQ", m.group(0)[:120]))
    # ENDLESS: while(true)/for(;;) with no break/return/goto in following braces (coarse: next 40 lines)
    for i, ln in enumerate(lines):
        if re.search(r"\b(while\s*\(\s*(true|1)\s*\)|for\s*\(\s*;\s*;\s*\))", ln):
            body = "\n".join(lines[i:i+60])
            if not re.search(r"\b(break|return|goto|exit|abort)\b", body):
                hits.append(("ENDLESS", ln.strip()[:120]))
    # NONULL: p = (alloc/new/malloc)(...) then p-> or *p on next ~3 lines with no `if (p` between
    for m in re.finditer(r"([a-zA-Z_]\w*)\s*=\s*[^;]*\b(operator_new|malloc|calloc|_new|Alloc|alloc|CreateInstance)\b[^;]*;", code):
        var = m.group(1); tail = code[m.end():m.end()+220]
        if re.search(rf"\b{re.escape(var)}\s*(->|\[)", tail) and not re.search(rf"if\s*\([^)]*{re.escape(var)}", tail):
            hits.append(("NONULL", m.group(0)[:120]))
    # IDXFILE: array[idx] / ptr[idx] where idx came from a *(something file-ish) with no bound check nearby
    for m in re.finditer(r"([a-zA-Z_]\w*)\s*\[\s*([a-zA-Z_]\w*)\s*\]", code):
        idx = m.group(2)
        src = re.search(rf"{re.escape(idx)}\s*=\s*[^;]*{FILEWORDS}[^;]*;", code)
        if src and not re.search(rf"if\s*\([^)]*{re.escape(idx)}\b[^)]*[<>]", code):
            hits.append(("IDXFILE", (src.group(0)[:70] + " -> " + m.group(0))[:120]))
    # LOCK imbalance: count Enter/Lock vs Leave/Unlock, and a return between them
    locks = len(re.findall(r"\b(EnterCriticalSection|_lock|Lock\(|acquire|WaitForSingleObject)\b", code))
    unlocks = len(re.findall(r"\b(LeaveCriticalSection|_unlock|Unlock\(|release|ReleaseMutex)\b", code))
    if locks and unlocks and locks != unlocks:
        hits.append(("LOCKIMBAL", f"locks={locks} unlocks={unlocks}"))
    # UAF / DBLFREE
    for m in re.finditer(r"\b(free|_free|operator_delete|Release|DestroyInstance)\s*\(\s*([a-zA-Z_]\w*)\s*\)", code):
        var = m.group(2); tail = code[m.end():m.end()+300]
        if re.search(rf"\b(free|_free|operator_delete)\s*\(\s*{re.escape(var)}\s*\)", tail) and not re.search(rf"{re.escape(var)}\s*=", tail[:tail.find(var) if var in tail else 0]+tail):
            hits.append(("DBLFREE", m.group(0)[:120]))
        if re.search(rf"\b{re.escape(var)}\s*(->|\[)", tail) and not re.search(rf"{re.escape(var)}\s*=", tail):
            hits.append(("UAF", m.group(0)[:120]))
    # INTOVF: malloc/memcpy/alloc fed by a*b or a<<n or a+b with no explicit overflow check
    for m in re.finditer(r"\b(malloc|calloc|memcpy|memmove|operator_new|Alloc)\s*\([^;]*?([a-zA-Z_]\w*\s*[*+]\s*[a-zA-Z_0-9]\w*|<<)[^;]*?\)", code):
        hits.append(("INTOVF", m.group(0)[:120]))
    return hits

def load_inputs(path):
    out = {}  # addr -> (name, code)
    if os.path.isdir(path):
        for f in glob.glob(os.path.join(path, "*.c")) + glob.glob(os.path.join(path, "*.txt")):
            addr = os.path.splitext(os.path.basename(f))[0]
            out[addr] = (addr, open(f, errors="replace").read())
    else:
        txt = open(path, errors="replace").read()
        recs = []
        try:
            j = json.loads(txt)
            recs = j if isinstance(j, list) else [j]
        except Exception:
            for line in txt.splitlines():
                line = line.strip()
                if line:
                    try: recs.append(json.loads(line))
                    except Exception: pass
        for r in recs:
            a = str(r.get("address") or r.get("addr") or r.get("entry") or "")
            c = r.get("pseudocode") or r.get("code") or r.get("decompilation") or ""
            if a and c: out[a] = (r.get("name", a), c)
    return out

def norm(a):
    try: return int(a, 16) if isinstance(a, str) and a.lower().startswith("0x") else int(a)
    except Exception: return None

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("input")
    ap.add_argument("--covered", default=None)
    ap.add_argument("--near", type=int, default=64)
    ap.add_argument("--min-score", type=int, default=1)
    a = ap.parse_args()
    covered = []
    if a.covered and os.path.exists(a.covered):
        for ln in open(a.covered):
            v = norm(ln.strip())
            if v is not None: covered.append(v)
        covered.sort()
    import bisect
    def is_covered(addr):
        v = norm(addr)
        if v is None or not covered: return 0
        i = bisect.bisect_left(covered, v)
        for j in (i-1, i):
            if 0 <= j < len(covered) and abs(covered[j]-v) <= a.near: return 1
        return 0
    rows = []
    for addr, (name, code) in load_inputs(a.input).items():
        hits = find_patterns(code)
        if not hits: continue
        kinds = {}
        for k, ev in hits: kinds.setdefault(k, ev)
        score = sum(WEIGHT.get(k, 1) for k in kinds)
        cov = is_covered(addr)
        rows.append((score, cov, addr, name, kinds))
    rows.sort(key=lambda r: (r[1], -r[0]))  # uncovered first, high score first
    print("score,covered,address,name,patterns,evidence")
    for score, cov, addr, name, kinds in rows:
        if score < a.min_score: continue
        ev = " | ".join(f"{k}:{v}" for k, v in kinds.items()).replace(",", ";").replace('"', "'")
        print(f'{score},{cov},{addr},{name},{"+".join(kinds)},"{ev[:300]}"')

if __name__ == "__main__":
    main()
