"""2026-11-20, docs/215 G2: live allocations >= N MB at a trace time whose stack has no TS3W frame, with the chain of
non-OS modules (innermost first). usage: python -I untr_g2.py <etlread output.txt> <cut ms> [min MB, default 4]"""
import re
import sys

LINE = re.compile(r'^([AFMU]) (\d+) tid=(\d+) base=0x([0-9A-F]+) size=0x([0-9A-F]+) (.*?) \| ?(.*)$')
SKIP = {'ntoskrnl', 'ntdll', 'wow64', 'wow64cpu', 'wow64win', 'wow64base', 'kernelbase', 'kernel32', 'win32u', 'hal', 'fltmgr',
        'dxgkrnl', 'dxgmms2', 'win32kbase', 'win32kfull', 'win32k'}
cut, mn = int(sys.argv[2]), float(sys.argv[3]) if len(sys.argv) > 3 else 4.0
live = {}
for raw in open(sys.argv[1], encoding='utf-8', errors='replace'):
    m = LINE.match(raw.rstrip('\n'))
    if not m:
        continue
    k, ms, tid, b, sz, extra, st = m.groups()
    if int(ms) > cut:
        break
    b = int(b, 16)
    if k in 'AM':
        live.setdefault(b, (k, int(ms), int(tid), int(sz, 16), extra, [s for s in st.split(' < ') if s]))
    elif k == 'F' and 'RELEASE' in extra or k == 'U':
        live.pop(b, None)
rows = []
for b, (k, ms, tid, sz, extra, fr) in live.items():
    mods = [f.split('+', 1)[0].lower() for f in fr]
    if sz < mn * 1048576 or 'ts3w' in mods:
        continue
    chain = []
    for f in fr:
        md = f.split('+', 1)[0].lower()
        if md in SKIP or md.startswith('0x') or (chain and chain[-1].split('+', 1)[0].lower() == md):
            continue
        chain.append(f)
    res = 'res' if k == 'A' and 'RESERVE' in extra and 'COMMIT' not in extra else ''
    rows.append((sz, k + res, ms, tid, b, chain[:6]))
tot = sum(r[0] for r in rows)
print('# live at %d ms, >= %.0f MB, no TS3W frame: %d blocks, %.1f MB' % (cut, mn, len(rows), tot / 1048576))
for sz, k, ms, tid, b, chain in sorted(rows, key=lambda r: -r[0]):
    print('%7.1f MB %-4s %8d ms tid=%-6d 0x%08X  %s' % (sz / 1048576, k, ms, tid, b, ' < '.join(chain) or 'os-only'))
