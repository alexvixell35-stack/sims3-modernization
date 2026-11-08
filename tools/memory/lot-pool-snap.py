"""Read-only snapshot of TS3W 1.67's 272-byte EA pool at 0x11EB590 (ReadProcessMemory only, no lock taken, nothing written).
Pool: +0 item size, +4 align, +8 items per core, +0xC core list (core+0 next, core+4 size), +0x10 free list (slot+0 next).
Reports cores, slots, free slots, and how many cores are completely free (what a trim could release), plus a per-core occupancy
histogram. Racy by design: pops/pushes during the walk can shift a few counts, so run it twice. Usage: python -I lot-pool-snap.py [pid]"""
import ctypes
import ctypes.wintypes as wt
import struct
import subprocess
import sys

POOL = 0x11EB590
k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.OpenProcess.restype = wt.HANDLE
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]


def find_pid():
    out = subprocess.run(["tasklist", "/fi", "imagename eq TS3W.exe", "/fo", "csv", "/nh"], capture_output=True, text=True).stdout
    for line in out.splitlines():
        parts = [p.strip('"') for p in line.split('","')]
        if parts and parts[0].lower() == "ts3w.exe":
            return int(parts[1])
    sys.exit("TS3W.exe not running")


pid = int(sys.argv[1]) if len(sys.argv) > 1 else find_pid()
h = k32.OpenProcess(0x0010 | 0x0400, False, pid)  # PROCESS_VM_READ | PROCESS_QUERY_INFORMATION
if not h:
    sys.exit(f"OpenProcess failed: {ctypes.get_last_error()}")


def read(addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
        raise OSError(f"read {n} at {addr:#x} failed ({ctypes.get_last_error()})")
    return buf.raw


item, align, per_core, core_head, free_head = struct.unpack("<5I", read(POOL, 20))
assert (item, per_core) == (272, 128), f"unexpected pool header: item {item} per core {per_core}"

cores = {}  # core base -> bytes
c, guard = core_head, 0
while c and guard < 100000:
    nxt, size = struct.unpack("<2I", read(c, 8))
    cores[c] = read(c, size)
    c, guard = nxt, guard + 1
bases = sorted(cores)
import bisect


def core_of(p):
    i = bisect.bisect_right(bases, p) - 1
    if i >= 0 and p < bases[i] + len(cores[bases[i]]):
        return bases[i]
    return None


def slots_in(b):  # the carve at 0x4E4290: first slot align(core + align + 7), linked while next <= core + size - 2*item, plus that next
    s = (b + align + 7) & ~(align - 1)
    e = b + len(cores[b]) - 2 * item
    return (e - s) // item + 2 if s <= e else 0


per = {b: slots_in(b) for b in bases}
free_per_core = dict.fromkeys(bases, 0)
seen = set()
p, outside, steps = free_head, 0, 0
limit = sum(per.values()) + 10
while p and steps < limit:
    if p in seen:
        break  # a cycle can only come from the race; stop
    seen.add(p)
    cb = core_of(p)
    if cb is None:
        outside += 1
        break
    free_per_core[cb] += 1
    p = struct.unpack_from("<I", cores[cb], p - cb)[0]
    steps += 1

total = sum(per.values())
nfree = sum(free_per_core.values())
full_free = sum(1 for b in bases if free_per_core[b] >= per[b])
hist = [0] * 9  # free slots per core in eighths: 0-15, 16-31, ..., 112-127, 128
for b in bases:
    f = free_per_core[b]
    hist[8 if f >= per[b] else min(f // 16, 7)] += 1
core_bytes = sum(len(v) for v in cores.values())
print(f"pid {pid}: cores {len(bases)} ({core_bytes / 1048576:.2f} MiB), slots {total}, free {nfree} ({100 * nfree / max(total, 1):.1f}%), "
      f"live {total - nfree}; completely free cores {full_free} ({full_free * len(cores[bases[0]]) / 1048576 if bases else 0:.2f} MiB); "
      f"walk ended: {'cycle' if p in seen else 'outside a core' if outside else 'end of list' if not p else 'step limit'}")
print(f"slots per core {sorted(set(per.values()))}; cores by free slots (0-15, 16-31, ..., 112+, all):", hist)
