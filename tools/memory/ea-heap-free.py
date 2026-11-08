"""Read-only: how much of each EA GeneralAllocator heap in TS3W 1.67 is free inside its cores (docs/215 4.1 item 2). The allocator
is dlmalloc 2.7 with EA's layout (GeneralAllocator_InitBins 0x4E5710): +4 max_fast, +8 fastbins[10] (singly linked, fd at chunk +8),
+0x30 bins 1..127 (bin i = fake chunk at +0x30 + 8*(i-1), fd/bk at +8/+0xC; bin 1 = unsorted), +0x430 binmap, +0x440 top,
+0x444 last remainder, +0x448 core list sentinel (first core [heap +0x460], next at core +0x18, committed at core +4).
Chunk: +4 head = size | PREV_INUSE(1) | IS_MMAPPED(2), plus an EA flag in bit 31 (set on fastbin chunks), so size = head & 0x3FFFFFF8. ReadProcessMemory only, no lock: a list the game changes during the walk
is cut at the first pointer outside the heap's cores, so totals are a close lower bound. Usage: python -I ea-heap-free.py"""
import bisect
import ctypes
import ctypes.wintypes as wt
import struct
import subprocess
import sys
import time

HEAPS = {"main": 0x11CB864, "mono": 0x11D4BC8}
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.OpenProcess.restype = wt.HANDLE
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
out = subprocess.run(["tasklist", "/fi", "imagename eq TS3W.exe", "/fo", "csv", "/nh"], capture_output=True, text=True).stdout
pids = [int(p.split('","')[1]) for p in out.splitlines() if p.lower().startswith('"ts3w.exe"')]
if not pids:
    sys.exit("TS3W.exe not running")
h = k32.OpenProcess(0x0010 | 0x0400, False, pids[0])
_buf = ctypes.create_string_buffer(0x500)
_got = ctypes.c_size_t()


def rd(addr, n):
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), _buf, n, ctypes.byref(_got)) or _got.value != n:
        return None
    return _buf.raw[:n]


MB = 1048576
t0 = time.time()
seen_heaps = set()
for name, slot in HEAPS.items():
    hp = struct.unpack("<I", rd(slot, 4))[0]
    if not hp or hp in seen_heaps:
        continue
    seen_heaps.add(hp)
    st = rd(hp, 0x464)
    top, last_rem = struct.unpack_from("<2I", st, 0x440)
    cores, c = [], struct.unpack_from("<I", st, 0x460)[0]
    while c and c != hp + 0x448 and len(cores) < 4096:
        hdr = rd(c, 0x1C)
        if not hdr:
            break
        cores.append((c, struct.unpack_from("<I", hdr, 4)[0]))
        c = struct.unpack_from("<I", hdr, 0x18)[0]
    cores.sort()
    starts = [b for b, _ in cores]

    def in_core(p, size=16):
        i = bisect.bisect_right(starts, p) - 1
        return i >= 0 and p & 7 == 0 and p + size <= cores[i][0] + cores[i][1]

    free = {"fast": [0, 0], "small": [0, 0], "mid": [0, 0], "large": [0, 0]}  # bytes, chunks; small < 512 B, mid < 1 MiB
    biggest, cut = 0, 0
    for i in range(10):  # fastbins
        p, n = struct.unpack_from("<I", st, 8 + 4 * i)[0], 0
        while p and n < 2000000:
            if not in_core(p):
                cut += 1
                break
            ch = rd(p + 4, 8)
            if not ch:
                break
            size, fd = struct.unpack("<2I", ch)
            size &= 0x3FFFFFF8
            if size < 16 or not in_core(p, size):
                cut += 1
                break
            free["fast"][0] += size
            free["fast"][1] += 1
            p, n = fd, n + 1
    for i in range(1, 128):  # bins
        node = hp + 0x30 + 8 * (i - 1)
        p, n = struct.unpack_from("<I", st, 0x30 + 8 * (i - 1) + 8)[0], 0
        while p and p != node and n < 2000000:
            if not in_core(p):
                cut += 1
                break
            ch = rd(p + 4, 8)
            if not ch:
                break
            size, fd = struct.unpack("<2I", ch)
            size &= 0x3FFFFFF8
            if size < 16 or not in_core(p, size):
                cut += 1
                break
            k = "small" if size < 512 else "mid" if size < MB else "large"
            free[k][0] += size
            free[k][1] += 1
            biggest = max(biggest, size)
            p, n = fd, n + 1
    top_size = 0
    if top and in_core(top):
        top_size = struct.unpack("<I", rd(top + 4, 4))[0] & 0x3FFFFFF8
    core_bytes = sum(s for _, s in cores)
    fb = sum(v[0] for v in free.values()) + top_size
    print(f"{time.strftime('%H:%M:%S')} heap {name} @{hp:#x}: cores {len(cores)} committed {core_bytes / MB:.1f} MiB, free inside "
          f"{fb / MB:.1f} MiB ({100 * fb / max(core_bytes, 1):.1f}%): " +
          ", ".join(f"{k} {v[0] / MB:.1f} MiB in {v[1]}" for k, v in free.items()) +
          f", top {top_size / MB:.2f} MiB; biggest free chunk {biggest / MB:.2f} MiB; lists cut {cut}")
print(f"  ({time.time() - t0:.1f} s)")
