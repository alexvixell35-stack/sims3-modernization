"""Read-only census of TS3W 1.67's 12 static EA fixed pools (EA pool init 0x4E43D0 called with `mov ecx, <pool>`; found by a scan
of the exe). ReadProcessMemory only: no lock, nothing written, so counts can be off by a few slots under the race. Pool header:
+0 item size (aligned), +4 align, +8 items per core, +0xC core list (core+0 next, core+4 size), +0x10 free list (slot+0 next).
Cores are freed only by the pool's free-all (exit). Per pool: cores, MiB, slots, free %, completely free cores and their MiB.
Pools whose cores sum to more than --max-read MiB are reported by core headers only. Usage: python -I ea-pool-census.py [--max-read N]"""
import bisect
import ctypes
import ctypes.wintypes as wt
import struct
import subprocess
import sys
import time

POOLS = [0x11CD8B4, 0x11D3F7C, 0x11D6718, 0x11D9D44, 0x11D9FD0, 0x11D9FFC, 0x11DA028, 0x11DA700, 0x11DA7D0, 0x11DA9B0, 0x11E9118,
         0x11EB590]
MAX_READ = float(sys.argv[sys.argv.index("--max-read") + 1]) if "--max-read" in sys.argv else 160.0

k32 = ctypes.WinDLL("kernel32", use_last_error=True)
k32.OpenProcess.restype = wt.HANDLE
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
out = subprocess.run(["tasklist", "/fi", "imagename eq TS3W.exe", "/fo", "csv", "/nh"], capture_output=True, text=True).stdout
pids = [int(p.split('","')[1]) for p in out.splitlines() if p.lower().startswith('"ts3w.exe"')]
if not pids:
    sys.exit("TS3W.exe not running")
h = k32.OpenProcess(0x0010 | 0x0400, False, pids[0])
if not h:
    sys.exit(f"OpenProcess failed: {ctypes.get_last_error()}")


def read(addr, n):
    buf = ctypes.create_string_buffer(n)
    got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got)) or got.value != n:
        raise OSError(f"read {n} at {addr:#x} failed ({ctypes.get_last_error()})")
    return buf.raw


def census(pool):
    item, align, per_core, core_head, free_head = struct.unpack("<5I", read(pool, 20))
    heads, c = [], core_head
    while c and len(heads) < 200000:
        nxt, size = struct.unpack("<2I", read(c, 8))
        heads.append((c, size))
        c = nxt
    mib = sum(s for _, s in heads) / 1048576
    row = {"pool": pool, "item": item, "perCore": per_core, "cores": len(heads), "MiB": mib}
    if not heads or mib > MAX_READ or item == 0:
        return row
    cores = {b: read(b, s) for b, s in heads}
    bases = sorted(cores)

    def slots_in(b):  # the carve at 0x4E4290: first slot align(core + align + 7), linked while next <= core + size - 2*item, plus it
        s = (b + align + 7) & ~(align - 1)
        e = b + len(cores[b]) - 2 * item
        return (e - s) // item + 2 if s <= e else 0

    per = {b: slots_in(b) for b in bases}
    free = dict.fromkeys(bases, 0)
    seen, p, limit = set(), free_head, sum(per.values()) + 10
    while p and len(seen) < limit and p not in seen:
        seen.add(p)
        i = bisect.bisect_right(bases, p) - 1
        if i < 0 or p >= bases[i] + len(cores[bases[i]]):
            break
        free[bases[i]] += 1
        p = struct.unpack_from("<I", cores[bases[i]], p - bases[i])[0]
    full = [b for b in bases if free[b] >= per[b]]
    row.update(slots=sum(per.values()), free=sum(free.values()), fullFree=len(full),
               fullFreeMiB=sum(len(cores[b]) for b in full) / 1048576)
    return row


t0 = time.time()
rows = [census(p) for p in POOLS]
tot = sum(r["MiB"] for r in rows)
tf = sum(r.get("fullFreeMiB", 0) for r in rows)
print(f"pid {pids[0]}: 12 static pools {tot:.2f} MiB, completely free cores {tf:.2f} MiB ({time.time() - t0:.1f} s)")
for r in rows:
    if r["cores"] == 0:
        continue
    extra = (f" slots {r['slots']} free {100 * r['free'] / max(r['slots'], 1):.1f}% fullFree {r['fullFree']}/{r['cores']} "
             f"({r['fullFreeMiB']:.2f} MiB)") if "slots" in r else " (headers only)"
    print(f"  {r['pool']:#x} item {r['item']} x{r['perCore']}: cores {r['cores']} {r['MiB']:.2f} MiB{extra}")
