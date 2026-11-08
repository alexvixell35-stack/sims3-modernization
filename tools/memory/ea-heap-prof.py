"""Read-only heap profile of TS3W 1.67's EA main heap ([0x11CB864]): walks every chunk of every core (core +0 = first chunk at
core +0x20, +4 committed, +0x18 next; chunk +4 head = size | PREV_INUSE(1), bit 31 = EA flag on fastbin chunks, size = head &
0x3FFFFFF8; malloc returns chunk +0x10, so EA chunks carry 16 B of overhead) and groups in-use chunks by what they hold: the vtable in the first dword of the user data (vt:<addr>, a C++ object;
TS3W has no RTTI), a core of a known sub-heap (mono sub-heap [0x11D4BC8]) or of a static EA pool, else vt@+K:<addr> when a vtable sits at user offset 4..0x1C (pooled pages, containers that embed an object), else raw:<size class>:<what the first 8 bytes look like: str (ASCII), ptr (into a heap core), zero, num>.
A chunk is in use when the next chunk's PREV_INUSE bit is set and it is not a flagged fastbin chunk. ReadProcessMemory only,
4 MiB at a time, no lock: chunks the game changes during the read can be misread, so read it as a profile, not an exact count.
--diff PREV.json prints the labels that changed most since that profile.
--sizes LABEL prints that label's most common exact chunk sizes, each with the first 32 bytes of one example.
Usage: python -I ea-heap-prof.py [--top N] [--out file.json] [--diff prev.json] [--sizes LABEL]"""
import bisect
import ctypes
import ctypes.wintypes as wt
import json
import struct
import subprocess
import sys
import time

TOP = int(sys.argv[sys.argv.index("--top") + 1]) if "--top" in sys.argv else 40
OUT = sys.argv[sys.argv.index("--out") + 1] if "--out" in sys.argv else None
DIFF = sys.argv[sys.argv.index("--diff") + 1] if "--diff" in sys.argv else None
SIZES = sys.argv[sys.argv.index("--sizes") + 1] if "--sizes" in sys.argv else None
sizes = {}  # exact size -> [count, example address, first 32 bytes]
POOLS = [0x11CD8B4, 0x11D3F7C, 0x11D6718, 0x11D9D44, 0x11D9FD0, 0x11D9FFC, 0x11DA028, 0x11DA700, 0x11DA7D0, 0x11DA9B0, 0x11E9118,
         0x11EB590]
RDATA, RDATA_END, TEXT, TEXT_END = 0xF95000, 0x1146000, 0x401000, 0xF95000
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.OpenProcess.restype = wt.HANDLE
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
out = subprocess.run(["tasklist", "/fi", "imagename eq TS3W.exe", "/fo", "csv", "/nh"], capture_output=True, text=True).stdout
pids = [int(p.split('","')[1]) for p in out.splitlines() if p.lower().startswith('"ts3w.exe"')]
if not pids:
    sys.exit("TS3W.exe not running")
h = k32.OpenProcess(0x0010 | 0x0400, False, pids[0])


def rd(addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
        return None
    return buf.raw


def u32(a):
    x = rd(a, 4)
    return struct.unpack("<I", x)[0] if x else 0


def cores_of(hp):
    res, c = [], u32(hp + 0x460)
    while c and c != hp + 0x448 and len(res) < 4096:
        hdr = rd(c, 0x20)
        if not hdr:
            break
        first, com = struct.unpack_from("<2I", hdr, 0)
        res.append((c, first, com))
        c = struct.unpack_from("<I", hdr, 0x18)[0]
    return res


vt_cache = {}


def is_vtable(v):  # a vtable lives in .rdata and its first slot points into .text
    if not (RDATA <= v < RDATA_END):
        return False
    if v not in vt_cache:
        f = u32(v)
        vt_cache[v] = TEXT <= f < TEXT_END
    return vt_cache[v]


def size_class(n):
    for lim, name in ((64, "<=64"), (256, "<=256"), (4096, "<=4K"), (65536, "<=64K"), (1 << 20, "<=1M")):
        if n <= lim:
            return name
    return ">1M"


t0 = time.time()
main = u32(0x11CB864)
mono = u32(0x11D4BC8)
known = {}  # chunk user address -> label, for cores of sub-heaps and pools that live inside main-heap chunks
if mono and mono != main:
    for c, _f, _com in cores_of(mono):
        known[c] = "subheap:mono"
for p in POOLS:
    c, n = u32(p + 0xC), 0
    while c and n < 200000:
        known[c] = f"pool@{p:#x}"
        c, n = u32(c), n + 1
main_cores = cores_of(main)
core_spans = sorted((c, com) for c, _f, com in main_cores)
span_starts = [c for c, _ in core_spans]


def in_main(v):
    i = bisect.bisect_right(span_starts, v) - 1
    return i >= 0 and v < core_spans[i][0] + core_spans[i][1]


groups = {}  # label -> [bytes, count]
total_used = total_free = chunks = 0
for core, first, com in main_cores:
    end = core + com
    p = first
    data, base = b"", 0
    prev_used = None
    while p + 16 <= end:
        if not (base <= p and p + 16 <= base + len(data)):  # refill a 4 MiB window that starts at this chunk
            base = p
            data = rd(base, min(4 << 20, end - base)) or b""
            if len(data) < 16:
                break
        head = struct.unpack_from("<I", data, p - base + 4)[0]
        size = head & 0x3FFFFFF8
        if size < 16 or p + size > end:
            break
        nxt = p + size
        if nxt + 8 <= base + len(data):
            nhead = struct.unpack_from("<I", data, nxt - base + 4)[0]
        else:
            nhead = u32(nxt + 4) if nxt + 8 <= end else 1
        used = (nhead & 1) and not (head & 0x80000000)
        chunks += 1
        if used:
            total_used += size
            user = p + 0x10
            if user in known:
                lab = known[user]
            else:
                v = struct.unpack_from("<I", data, p - base + 0x10)[0] if p - base + 0x14 <= len(data) else 0
                emb = None
                if not is_vtable(v):
                    for k in range(4, 0x20, 4):
                        if p - base + 0x14 + k <= len(data) and 0x10 + k + 4 <= size:
                            w = struct.unpack_from("<I", data, p - base + 0x10 + k)[0]
                            if is_vtable(w):
                                emb = f"vt@+{k:#x}:{w:#x}"
                                break
                if is_vtable(v):
                    lab = f"vt:{v:#x}"
                elif emb:
                    lab = emb
                else:
                    o = p - base + 0x10
                    w = data[o:o + 8]
                    if len(w) < 8:
                        kind = "num"
                    elif all(32 <= c < 127 for c in w[:4]):
                        kind = "str"
                    elif w == bytes(8):
                        kind = "zero"
                    elif in_main(v):
                        kind = "ptr"
                    else:
                        kind = "num"
                    lab = "raw:" + size_class(size) + ":" + kind
            if lab == SIZES:
                e = sizes.setdefault(size, [0, user, data[p - base + 0x10:p - base + 0x30]])
                e[0] += 1
            g = groups.setdefault(lab, [0, 0])
            g[0] += size
            g[1] += 1
        else:
            total_free += size
        p = nxt
MB = 1048576
print(f"{time.strftime('%H:%M:%S')} pid {pids[0]} EA main heap: {chunks} chunks, in use {total_used / MB:.1f} MiB, free {total_free / MB:.1f} MiB, "
      f"{len(groups)} labels, {len(vt_cache)} vtables checked ({time.time() - t0:.1f} s)")
for lab, (b, n) in sorted(groups.items(), key=lambda kv: -kv[1][0])[:TOP]:
    print(f"  {lab:22s} {b / MB:8.2f} MiB  n {n:8d}  avg {b // max(n, 1):7d} B")
if SIZES:
    print(f"  {SIZES} by exact chunk size:")
    for sz, (n, a, b) in sorted(sizes.items(), key=lambda kv: -kv[0] * kv[1][0])[:16]:
        print(f"    {sz:7d} B x {n:6d} = {sz * n / MB:6.2f} MiB  e.g. {a:#x}: {b.hex(' ', 4)}")
if DIFF:
    prev = json.load(open(DIFF))
    pg = prev["groups"]
    ch = sorted(((groups.get(k, [0, 0])[0] - pg.get(k, [0, 0])[0], k) for k in set(groups) | set(pg)), key=lambda x: -abs(x[0]))
    print(f"  change since {prev['time']}: in use {(total_used - prev.get('used', 0)) / MB:+.1f} MiB")
    for db, k in ch[:TOP // 2]:
        dn = groups.get(k, [0, 0])[1] - pg.get(k, [0, 0])[1]
        print(f"    {k:22s} {db / MB:+8.2f} MiB  n {dn:+8d}")
if OUT:
    json.dump({"time": time.strftime('%H:%M:%S'), "used": total_used, "groups": groups}, open(OUT, "w"))
