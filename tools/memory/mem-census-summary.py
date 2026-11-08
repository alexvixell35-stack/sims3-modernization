"""Summarizes mem-census.py logs: per game process (pid), committed MiB per owner class at the first sample, max, and last,
the largest free block trend, and the biggest jumps between consecutive samples (time, owner, delta) to line up with loads,
saves and trips in the harness log. Owner rows are grouped: nt-heap@* -> nt-heap:other, ea:heap@* -> ea:other.
Usage: python -I mem-census-summary.py <census log> [--jumps N] [--events harness.log ...]"""
import re
import sys

path = sys.argv[1]
JUMPS = int(sys.argv[sys.argv.index("--jumps") + 1]) if "--jumps" in sys.argv else 12
EVENTS = sys.argv[sys.argv.index("--events") + 1:] if "--events" in sys.argv else []
head = re.compile(r"^(\d\d:\d\d:\d\d) pid (\d+): reserved (\d+) MiB, committed (\d+) MiB, largest free (\d+) MiB")
row = re.compile(r"^  (\S+)\s+reserved\s+([\d.]+)\s+committed\s+([\d.]+)\s+n (\d+)")


def group(o):
    return "nt-heap:other" if o.startswith("nt-heap@") else "ea:other" if o.startswith("ea:heap@") else o


runs = {}  # pid -> list of (time, committed total, largest free, {owner: committed})
cur = None
for line in open(path, encoding="utf-8", errors="replace"):
    m = head.match(line)
    if m:
        t, pid, _res, com, big = m.groups()
        cur = {}
        runs.setdefault(pid, []).append((t, int(com), int(big), cur))
        continue
    m = row.match(line)
    if m and cur is not None:
        o = group(m.group(1))
        cur[o] = cur.get(o, 0.0) + float(m.group(3))

events = []
for ev in EVENTS:  # harness lines with a leading HH:MM:SS that mention a load, save or trip
    for line in open(ev, encoding="utf-8", errors="replace"):
        m = re.match(r"^\s*(\d\d:\d\d:\d\d)\s+(.*)", line)
        if m and re.search(r"world loaded|save|travel|trip|return|load", m.group(2), re.I):
            events.append((m.group(1), m.group(2)[:90]))

for pid, samples in runs.items():
    t0, t1 = samples[0][0], samples[-1][0]
    print(f"pid {pid}: {len(samples)} samples {t0}-{t1}; committed {samples[0][1]} -> {samples[-1][1]} MiB "
          f"(max {max(s[1] for s in samples)}); largest free {samples[0][2]} -> {samples[-1][2]} MiB (min {min(s[2] for s in samples)})")
    owners = sorted({o for s in samples for o in s[3]}, key=lambda o: -max(s[3].get(o, 0) for s in samples))
    print(f"  {'owner':18s} {'first':>8s} {'max':>8s} {'last':>8s} {'last-first':>10s}")
    for o in owners:
        v = [s[3].get(o, 0.0) for s in samples]
        print(f"  {o:18s} {v[0]:8.1f} {max(v):8.1f} {v[-1]:8.1f} {v[-1] - v[0]:+10.1f}")
    jumps = []
    for a, b in zip(samples, samples[1:]):
        for o in set(a[3]) | set(b[3]):
            d = b[3].get(o, 0.0) - a[3].get(o, 0.0)
            if abs(d) >= 4:
                jumps.append((abs(d), b[0], o, d))
    jumps.sort(reverse=True)
    print(f"  biggest jumps between samples (>= 4 MiB): " + "; ".join(f"{t} {o} {d:+.0f}" for _, t, o, d in sorted(jumps[:JUMPS], key=lambda j: j[1])))
    near = [e for e in events if t0 <= e[0] <= t1]
    if near:
        print("  events: " + "; ".join(f"{t} {s}" for t, s in near[:20]))
