"""Leak scan of the EA main heap in a running TS3W.exe 1.67 (read-only: VirtualQueryEx + ReadProcessMemory, no lock).

First call (no FILE.base): stores every in-use chunk as the baseline.
Each later call:
- survivors: chunks not in the baseline that were also in use at the previous call (they lived one whole interval);
- up to --max of them (plus the previous call's unreferenced ones still in use) are searched for in all committed,
  writable, readable memory of the process (heaps, Mono / Boehm, globals in module data, stacks; not write-combined
  or uncached GPU mappings): an aligned dword equal to the chunk's user address (+0x10) or up to 0x1C past it. A hit
  inside the chunk itself, or inside the EA main heap but outside any in-use chunk (stale free memory), is not a
  reference.
- a survivor with no reference anywhere is a leak candidate; one that is still in use and still unreferenced at the
  next call is a confirmed leak. Prints both by label (vt:<vtable> or raw:<size>) and an estimate of the leaked bytes.
A pointer that moves while the scan runs can be missed (a false candidate); the second check is there for that.
Stale dwords in other heaps' free memory count as references, so this undercounts leaks.
Usage: python -I ea-heap-leaks.py FILE [--max 2000] [--maxsize 1048576] | --self-check"""
import array
import bisect
import collections
import ctypes
import ctypes.wintypes as wt
import os
import random
import struct
import subprocess
import sys
import time

MIB = 1 << 20
RDATA, RDATA_END, TEXT, TEXT_END = 0xF95000, 0x1146000, 0x401000, 0xF95000
WRITABLE = {0x04, 0x08, 0x40, 0x80}
INTERIOR = range(0, 0x20, 4)


def refs_in(words, base, tset, owner_of, skip):
    """(target user address, hit address) for each aligned dword of words (read at base) found in tset;
    owner_of(v) gives the target a value belongs to, skip(hit address, target) drops self / stale hits."""
    out = []
    hits = tset.intersection(words)
    if not hits:
        return out
    for i, w in enumerate(words):
        if w in hits:
            t = owner_of(w)
            a = base + 4 * i
            if not skip(a, t):
                out.append((t, a))
    return out


def self_check():
    tset = {0x1010 + d for d in INTERIOR} | {0x2010 + d for d in INTERIOR}
    owner = lambda v: 0x1010 if v < 0x2000 else 0x2010
    words = array.array("I", [0, 0x1010, 0x2014, 7, 0x1010, 0x2014])
    skip = lambda a, t: t <= a < t + 0x40  # hit inside the target itself
    r = refs_in(words, 0x2000, tset, owner, skip)
    # 0x2014 at 0x2014 lies inside target 0x2010 (self, dropped); at 0x2008 it does not
    assert r == [(0x1010, 0x2004), (0x2010, 0x2008), (0x1010, 0x2010)], r
    assert refs_in(array.array("I", [1, 2]), 0, tset, owner, skip) == []
    print("ea-heap-leaks self-check ok")


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p), ("AllocationProtect", wt.DWORD),
                ("PartitionId", wt.WORD), ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD), ("Protect", wt.DWORD),
                ("Type", wt.DWORD)]


def main():
    args = sys.argv[1:]
    if args == ["--self-check"]:
        return self_check()
    path = args[0]
    opt = lambda k, d: int(args[args.index(k) + 1]) if k in args else d
    nmax, maxsize = opt("--max", 2000), opt("--maxsize", 1 << 20)

    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.OpenProcess.restype = wt.HANDLE
    k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    k32.VirtualQueryEx.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.POINTER(MBI), ctypes.c_size_t]
    k32.VirtualQueryEx.restype = ctypes.c_size_t
    out = subprocess.run(["tasklist", "/fi", "imagename eq TS3W.exe", "/fo", "csv", "/nh"], capture_output=True, text=True).stdout
    pids = [int(p.split('","')[1]) for p in out.splitlines() if p.lower().startswith('"ts3w.exe"')]
    if not pids:
        sys.exit("TS3W.exe not running")
    h = k32.OpenProcess(0x0010 | 0x0400, False, pids[0])

    def rd(addr, n):
        buf, got = ctypes.create_string_buffer(n), ctypes.c_size_t()
        ok = k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
        return buf.raw[:got.value] if ok or got.value else None

    def u32(a):
        x = rd(a, 4)
        return struct.unpack("<I", x)[0] if x and len(x) == 4 else 0

    vt_cache = {}

    def label(size, first):
        if RDATA <= first < RDATA_END:
            if first not in vt_cache:
                vt_cache[first] = TEXT <= u32(first) < TEXT_END
            if vt_cache[first]:
                return f"vt:{first:#x}"
        return f"raw:{size}"

    main_heap = u32(0x11CB864)
    cores, c = [], u32(main_heap + 0x460)
    while c and c != main_heap + 0x448 and len(cores) < 4096:
        cores.append((c, u32(c), u32(c + 4)))
        c = u32(c + 0x18)

    t0 = time.time()
    now, first_dw = array.array("Q"), {}
    for core, first, com in cores:  # chunk walk as in heapnew.py / eaheapprof.py
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
            if (nhead & 1) and not (head & 0x80000000) and size <= maxsize:
                now.append(p << 32 | size)
                if size >= 0x14 and p + 0x14 <= base + len(data):
                    first_dw[p] = struct.unpack_from("<I", data, p - base + 0x10)[0]
            p = nxt
    stamp = time.strftime("%H:%M:%S")
    if not os.path.exists(path + ".base"):
        open(path + ".base", "wb").write(now.tobytes())
        open(path + ".prev", "wb").write(now.tobytes())
        print(f"{stamp} pid {pids[0]}: baseline {len(now)} in-use chunks of the EA main heap ({time.time() - t0:.1f} s)")
        return
    load = lambda f: set(array.array("Q", open(f, "rb").read())) if os.path.exists(f) else set()
    base_s, prev_s, unref_prev = load(path + ".base"), load(path + ".prev"), load(path + ".unref")
    now_s = set(now)
    surv = [x for x in now if x not in base_s and x in prev_s]
    surv_b = sum(x & 0xFFFFFFFF for x in surv)
    by_lab = collections.Counter()
    for x in surv:
        p, s = x >> 32, x & 0xFFFFFFFF
        by_lab[label(s, first_dw.get(p, 0))] += s
    still = [x for x in unref_prev if x in now_s]
    random.seed(len(now))
    picks = random.sample(surv, min(nmax, len(surv)))
    targets = {x >> 32: x & 0xFFFFFFFF for x in set(picks) | set(still)}
    users = sorted(p + 0x10 for p in targets)
    tset = {u + d for u in users for d in INTERIOR}
    starts = sorted(x >> 32 for x in now)
    sizes = {x >> 32: x & 0xFFFFFFFF for x in now}
    core_ranges = sorted((cb, cb + cm) for cb, _, cm in cores)

    def owner_of(v):
        return users[bisect.bisect_right(users, v) - 1]

    def skip(a, t):
        if t - 0x10 <= a < t - 0x10 + targets[t - 0x10]:
            return True  # inside the target itself
        i = bisect.bisect_right(core_ranges, (a, 1 << 40)) - 1
        if i >= 0 and core_ranges[i][0] <= a < core_ranges[i][1]:
            j = bisect.bisect_right(starts, a) - 1
            return not (j >= 0 and a < starts[j] + sizes[starts[j]])  # main heap memory outside an in-use chunk
        return False

    held, scanned, addr = set(), 0, 0
    while addr < 1 << 32:
        mbi = MBI()
        if not k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)) or not mbi.RegionSize:
            break
        size, rbase = mbi.RegionSize, mbi.BaseAddress or 0
        if mbi.State == 0x1000 and (mbi.Protect & 0xFF) in WRITABLE and not mbi.Protect & 0x700:
            for off in range(0, size, 4 << 20):
                d = rd(rbase + off, min(4 << 20, size - off))
                if not d:
                    continue
                scanned += len(d)
                for t, _ in refs_in(array.array("I", d[:len(d) // 4 * 4]), rbase + off, tset, owner_of, skip):
                    held.add(t)
        addr = rbase + size
    unref = [p for p in targets if p + 0x10 not in held]
    confirmed = [x for x in still if (x >> 32) in unref]
    pick_unref = [p for p in unref if (p << 32 | targets[p]) in set(picks)]
    share = len(pick_unref) / max(len(picks), 1)
    print(f"{stamp} pid {pids[0]}: {len(now)} in-use chunks; survivors (new since the baseline, alive a whole interval) "
          f"{len(surv)}, {surv_b / MIB:.1f} MiB; scanned {scanned / MIB:.0f} MiB in {time.time() - t0:.1f} s")
    print("  survivors by label (MiB): " + ", ".join(f"{k} {v / MIB:.1f}" for k, v in by_lab.most_common(10)))
    print(f"  sampled {len(picks)}: {len(picks) - len(pick_unref)} referenced, {len(pick_unref)} with no reference "
          f"(candidates, {share:.1%}; about {share * surv_b / MIB:.1f} MiB if the sample holds)")
    lab_u = collections.Counter()
    for p in pick_unref:
        lab_u[label(targets[p], first_dw.get(p, 0))] += 1
    if lab_u:
        print("  candidates by label: " + ", ".join(f"{k} x{v}" for k, v in lab_u.most_common(10)))
    lab_c = collections.Counter()
    for x in confirmed:
        lab_c[label(x & 0xFFFFFFFF, first_dw.get(x >> 32, 0))] += x & 0xFFFFFFFF
    print(f"  previous candidates: {len(unref_prev)}, still in use {len(still)}, still unreferenced {len(confirmed)} "
          f"(confirmed, {sum(x & 0xFFFFFFFF for x in confirmed) / 1024:.1f} KB)" +
          ("; by label (KB): " + ", ".join(f"{k} {v / 1024:.1f}" for k, v in lab_c.most_common(8)) if lab_c else ""))
    for x in sorted(confirmed, key=lambda x: -(x & 0xFFFFFFFF))[:10]:  # what they hold: the user data's first 32 bytes
        d = rd((x >> 32) + 0x10, 32) or b""
        print(f"    {(x >> 32) + 0x10:#010x} {x & 0xFFFFFFFF:6d} B {label(x & 0xFFFFFFFF, first_dw.get(x >> 32, 0)):14s} " +
              d.hex(" ", 4) + "  " + "".join(chr(c) if 32 <= c < 127 else "." for c in d))
    open(path + ".prev", "wb").write(now.tobytes())
    open(path + ".unref", "wb").write(array.array("Q", [p << 32 | targets[p] for p in unref]).tobytes())


main()
