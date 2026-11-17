#!/usr/bin/env python3
# Real free address space of the 32-bit game under Wine, from /proc/<pid>/maps (Windows-side VirtualQuery reports the
# area Wine handed back to the Unix side as one fake reserved block, so ts3native's free/big are wrong on Linux).
# Prints: uxFree=<MB> uxBig=<MB> uxUnix=<MB above the game's views, Unix-side libs + GPU maps>
import sys, glob
pid = None
for p in glob.glob('/proc/[0-9]*/cmdline'):
    try:
        c = open(p, 'rb').read()
        if c.startswith(b'C:\\The Sims 3\\Game\\Bin\\TS3W.exe'): pid = p.split('/')[2]
    except OSError: pass
if not pid: print('uxFree=- uxBig=-'); sys.exit()
segs = []
for l in open(f'/proc/{pid}/maps'):
    a, b = [int(x, 16) for x in l.split()[0].split('-')]
    if a < 1 << 32: segs.append((a, min(b, 1 << 32), l.rstrip().endswith('.so') or '/dev/dri' in l or '.so.' in l))
gaps, prev = [], 0x10000
for a, b, _ in sorted(segs):
    if a > prev: gaps.append(a - prev)
    prev = max(prev, b)
gaps.append((1 << 32) - prev)
unix = sum(b - a for a, b, u in segs if u)
print('uxFree=%.0f uxBig=%.0f uxUnix=%.0f' % (sum(gaps) / 2**20, max(gaps) / 2**20, unix / 2**20))
