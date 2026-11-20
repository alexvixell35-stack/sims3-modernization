"""2026-11-20, docs/215 G1/G2: name the owners of TS3W's big allocations from a WPR trace.
usage: python -I untr_match.py <etlread output.txt> <ts3native.log of the same run> [cut ms: "live" = live at this trace time]
1. every untracked allocation in the AllocSites log (`allocsites: #N mark ev=<tag> untr ... base=0x.. MB=..`) matched by
   base to the trace's VirtualAlloc / MapFile event, with the owner frame;
2. every allocation >= 1 MB still live at the end of the trace, summed by owner (covers G2: reservations with no game frame).
Owner = innermost frame outside the OS layers and our ts3native.asi; via=ts3native means our hooks saw the call.
"""
import collections
import re
import sys

LINE = re.compile(r'^([AFMU]) (\d+) tid=(\d+) base=0x([0-9A-F]+) size=0x([0-9A-F]+) (.*?) \| ?(.*)$')
SYS = {'ntoskrnl', 'ntdll', 'wow64', 'wow64cpu', 'wow64win', 'wow64base', 'kernelbase', 'kernel32', 'win32u', 'gdi32', 'gdi32full', 'hal', 'fltmgr'}
UNTR = re.compile(r'allocsites: #(\d+) (?:mark ev=(\S+)|(\w+)) untr r=\d+ type=(\w+) base=0x([0-9A-Fa-f]+) MB=([\d.]+) comMB=([\d.]+) prot=(\w+) aprot=(\w+) owner=(\S+) first=#(\d+)')


def mod(f):
    return f.split('+', 1)[0].lower()


KERNEL = {'ntoskrnl', 'dxgkrnl', 'dxgmms2', 'win32kbase', 'win32kfull', 'win32k', 'hal', 'fltmgr', 'ntfs', 'nvlddmkm', 'amdkmdag', 'cng', 'ksecdd', 'ci'}


def owner(frames):
    """(owner frame, via ts3native, kernel-made: a kernel module other than the syscall path asked for it, last kernel module)"""
    via = any(mod(f).startswith('ts3native') for f in frames)
    kmods = [mod(f) for f in frames if mod(f) in KERNEL and mod(f) != 'ntoskrnl']
    for f in frames:
        m = mod(f)
        if m in SYS or m in KERNEL or m.startswith('ts3native') or m.startswith('0x'):
            continue
        return f, via, (kmods[-1] if kmods else '')
    # no user frame past the OS layers: the 32-bit walk stopped (no frame pointers) or the call came from the kernel
    return ('ts3native(walk ends)' if via else (kmods[-1] + '(kernel)' if kmods else 'os-only')), via, (kmods[-1] if kmods else '')


ev = []
for raw in open(sys.argv[1], encoding='utf-8', errors='replace'):
    m = LINE.match(raw.rstrip('\n'))
    if m:
        k, ms, tid, b, sz, extra, st = m.groups()
        ev.append((k, int(ms), int(tid), int(b, 16), int(sz, 16), extra, [s for s in st.split(' < ') if s]))
at_base = collections.defaultdict(list)
live = {}
cut = int(sys.argv[3]) if len(sys.argv) > 3 else None  # optional ms: "live" = live at this trace time (exit frees everything)
for e in ev:
    k, b = e[0], e[3]
    if cut is not None and e[1] > cut:
        if k in 'AM':
            at_base[b].append(e)
        continue
    if k in 'AM':
        if b not in live:
            live[b] = e
        at_base[b].append(e)
    elif k == 'F' and 'RELEASE' in e[5] or k == 'U':
        live.pop(b, None)

print('# trace: %d events (A %d, F %d, M %d, U %d)' % (len(ev), *(sum(1 for e in ev if e[0] == c) for c in 'AFMU')))
print('\n## untracked allocations from the AllocSites log, by base')
seen = {}
for raw in open(sys.argv[2], encoding='utf-8', errors='replace'):
    m = UNTR.search(raw)
    if not m:
        continue
    n, tag, per, typ, b, mb, com, prot, aprot, own, first = m.groups()
    b = int(b, 16)
    if b not in seen:  # first sighting keeps its mark tag
        seen[b] = (int(first), typ, float(mb), float(com), prot, own, tag or per)
tot = collections.defaultdict(float)
for b, (first, typ, mb, com, prot, own, tag) in sorted(seen.items(), key=lambda kv: (kv[1][0], -kv[1][2])):
    # the event that made this allocation: same base, size within 64 KB (bases are reused after a free)
    cand = [x for x in at_base.get(b, []) if abs(x[4] - mb * 1048576) <= 65536]
    e = cand[0] if cand else None
    if e:
        o, via, kmod = owner(e[6])
        desc = '%s at %d ms %s%s%s' % (e[0], e[1], o, ' via=ts3native' if via else '', ' made by ' + kmod if kmod else '')
        key = o.split('+')[0] + (' > ' + kmod if kmod else '')
    else:
        desc, key = 'NO EVENT in the trace (made before the trace, or not by VirtualAlloc / MapViewOfSection)', 'no event'
    tot[(typ, prot, key)] += mb
    print('0x%08X %-7s %6.2f MB com %6.2f prot %-5s owner=%s first=#%d %s: %s' % (b, typ, mb, com, prot, own, first, tag, desc))
    if e:
        print('     ' + ' < '.join(e[6][:14]))
print('\n## untracked MB by type / prot / owner module')
for k, v in sorted(tot.items(), key=lambda kv: -kv[1]):
    print('%8.1f  %s' % (v, ' / '.join(k)))
print('\n## live at %s (>= 1 MB), by owner module' % ('%d ms' % cut if cut is not None else 'trace end'))
by = collections.defaultdict(lambda: [0, 0])
for b, e in live.items():
    o, via, kmod = owner(e[6])
    kind = e[0] + ('res' if e[0] == 'A' and 'RESERVE' in e[5] and 'COMMIT' not in e[5] else '')  # G2: reserve-only
    k = '%s %s%s%s' % (kind, o.split('+')[0], ' via=ts3native' if via else '', ' > ' + kmod if kmod else '')
    by[k][0] += 1
    by[k][1] += e[4]
for k, (c, s) in sorted(by.items(), key=lambda kv: -kv[1][1])[:40]:
    print('%8.1f MB %4d  %s' % (s / 1048576, c, k))
