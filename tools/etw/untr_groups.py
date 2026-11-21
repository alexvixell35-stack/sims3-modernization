"""2026-11-20, docs/215 G1: group allocations / maps >= N MB made after T ms by their first 3 non-OS frames.
usage: python -I untr_groups.py <etlread output.txt> <after ms> [min MB, default 8]"""
import collections
import re
import sys

LINE = re.compile(r'^([AFMU]) (\d+) tid=(\d+) base=0x([0-9A-F]+) size=0x([0-9A-F]+) (.*?) \| ?(.*)$')
SKIP = {'ntoskrnl', 'ntdll', 'wow64', 'wow64cpu', 'wow64win', 'wow64base', 'kernelbase', 'kernel32', 'win32u', 'hal', 'fltmgr',
        'dxgkrnl', 'dxgmms2', 'win32kbase', 'win32kfull', 'win32k'}
after, mn = int(sys.argv[2]), float(sys.argv[3]) if len(sys.argv) > 3 else 8.0
g = collections.defaultdict(lambda: [0, 0, None, None])
for raw in open(sys.argv[1], encoding='utf-8', errors='replace'):
    m = LINE.match(raw.rstrip('\n'))
    if not m or m.group(1) not in 'AM' or int(m.group(2)) < after:
        continue
    sz = int(m.group(5), 16)
    if sz < mn * 1048576:
        continue
    fr = [f for f in m.group(7).split(' < ') if f and f.split('+', 1)[0].lower() not in SKIP and not f.startswith('0x')]
    res = 'res' if m.group(1) == 'A' and 'RESERVE' in m.group(6) and 'COMMIT' not in m.group(6) else ''
    key = '%s%s %s' % (m.group(1), res, ' < '.join(fr[:3]) or 'os-only')
    r = g[key]
    r[0] += 1
    r[1] += sz
    r[2] = r[2] or int(m.group(2))
    r[3] = int(m.group(2))
for k, (n, sz, t0, t1) in sorted(g.items(), key=lambda kv: -kv[1][1])[:30]:
    print('%4d x %8.1f MB  %7d-%7d ms  %s' % (n, sz / 1048576, t0, t1, k))
