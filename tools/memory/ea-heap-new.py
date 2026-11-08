"""Which EA main heap blocks are new, and who holds them (TS3W 1.67, read-only; heap layout as in ea-heap-prof.py).
  save FILE        stores the addresses and sizes of every in-use chunk of at most --maxsize bytes
  diff FILE        reads the heap again: chunks (address, size) not in FILE are new; up to --max of them become targets and
                   every in-use chunk is searched for aligned dwords equal to a target's user address. Prints the new chunks
                   by size, the holders by label (vt:<addr> +offset, or raw:<chunk size>), and how many new chunks have none
                   in the main heap (held by a global, a stack, another heap, or by nothing: a leak candidate).
                   Then FILE is replaced by the current state, so diffs chain.
A reused address with the same size counts as old, so this undercounts churn, not growth. No lock: stale dwords can match.
Usage: python -I ea-heap-new.py save|diff FILE [--minsize 0] [--maxsize 256] [--max 512]"""
import array
import collections
import ctypes
import ctypes.wintypes as wt
import random
import struct
import subprocess
import sys
import time

MODE, FILE = sys.argv[1], sys.argv[2]
MAXSIZE = int(sys.argv[sys.argv.index("--maxsize") + 1]) if "--maxsize" in sys.argv else 256
MINSIZE = int(sys.argv[sys.argv.index("--minsize") + 1]) if "--minsize" in sys.argv else 0
MAX = int(sys.argv[sys.argv.index("--max") + 1]) if "--max" in sys.argv else 512
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


vt_cache = {}


def is_vtable(v):
    if not (RDATA <= v < RDATA_END):
        return False
    if v not in vt_cache:
        vt_cache[v] = TEXT <= u32(v) < TEXT_END
    return vt_cache[v]


main = u32(0x11CB864)
cores, c = [], u32(main + 0x460)
while c and c != main + 0x448 and len(cores) < 4096:
    cores.append((c, u32(c), u32(c + 4)))
    c = u32(c + 0x18)


def walk():
    """Yields (chunk address, size, window bytes, offset of the chunk in them) for every in-use chunk."""
    for core, first, com in cores:
        end, p, data, base = core + com, first, b"", 0
        while p + 16 <= end:
            if not (base <= p and p + 16 <= base + len(data)):
                base = p
                data = rd(base, min(4 << 20, end - base)) or b""
                if len(data) < 16:
                    break
            head = struct.unpack_from("<I", data, p - base + 4)[0]
            size = head & 0x3FFFFFF8
            if size < 16 or p + size > end:
                break
            nxt = p + size
            nhead = struct.unpack_from("<I", data, nxt - base + 4)[0] if nxt + 8 <= base + len(data) else (u32(nxt + 4) if nxt + 8 <= end else 1)
            if (nhead & 1) and not (head & 0x80000000):
                if p + size > base + len(data):
                    base, data = p, rd(p, size) or b""
                    if len(data) < size:
                        break
                yield p, size, data, p - base
            p = nxt


def label(size, data, o):
    v = struct.unpack_from("<I", data, o + 0x10)[0] if size >= 0x14 else 0
    return f"vt:{v:#x}" if is_vtable(v) else f"raw:{size}"


t0 = time.time()
now = array.array("Q")
for p, size, data, o in walk():
    if MINSIZE <= size <= MAXSIZE:
        now.append(p << 32 | size)
if MODE == "diff":
    old = array.array("Q")
    old.frombytes(open(FILE, "rb").read())
    olds = set(old)
    new = [x for x in now if x not in olds]
    by_size = collections.Counter(x & 0xFFFFFFFF for x in new)
    print(f"{time.strftime('%H:%M:%S')} pid {pids[0]}: chunks <= {MAXSIZE} B: {len(old)} -> {len(now)}, new {len(new)} "
          f"({sum(by_size.values()) and sum(s * n for s, n in by_size.items()) / 1048576:.2f} MiB): " +
          ", ".join(f"{s} B x{n}" for s, n in by_size.most_common(6)))
    random.seed(1)
    picks = random.sample(new, min(MAX, len(new)))
    targets = {(x >> 32) + 0x10: x >> 32 for x in picks}
    tset = set(targets)
    holders, held = collections.Counter(), set()
    contents = collections.Counter()
    for p, size, data, o in walk():
        if p in targets.values():
            w = data[o + 0x10:o + 0x18]
            contents["str" if all(32 <= b < 127 for b in w[:4]) else "zero" if w == bytes(8) else "other"] += 1
        words = array.array("I", data[o + 0x10:o + size])
        if not tset.intersection(words):
            continue
        lab = label(size, data, o)
        for i, w in enumerate(words):
            if w in tset:
                holders[(lab, 4 * i if lab.startswith("vt:") else -1)] += 1
                held.add(w)
    print(f"  sampled {len(targets)} new chunks: {len(held)} have a holder in the main heap, {len(targets) - len(held)} none; "
          f"content {dict(contents)} ({time.time() - t0:.1f} s)")
    for (lab, off), cnt in holders.most_common(16):
        print(f"    {lab:14s}" + (f" +{off:#06x}" if off >= 0 else " (raw)   ") + f"  {cnt}")
open(FILE, "wb").write(now.tobytes())
if MODE == "save":
    print(f"{time.strftime('%H:%M:%S')} pid {pids[0]}: saved {len(now)} chunks <= {MAXSIZE} B ({time.time() - t0:.1f} s)")
