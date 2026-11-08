"""Read-only breakdown of mem-census.py's 'unlabeled' allocations in a running TS3W.exe (VirtualQueryEx + ReadProcessMemory).

memcensus labels the allocations (run here with its output hidden). Then, for each unlabeled one:
- protection of its committed pages (RW, RWX = code such as a JIT, WC / NOCACHE = a GPU mapping);
- readable or not (ReadProcessMemory fails on some driver mappings);
- an NT heap big block (Flink / Blink at +0 / +4 pointing at another big block or into a heap segment);
- from up to SAMPLE bytes at three places: the share of zero bytes and the module most of its pointers point into
  (a driver's own pools hold pointers to its own code and data).
Prints committed MiB by (hint, protection) and the biggest allocations with their hint.
--state FILE keeps each allocation's hint; with a previous FILE, also prints the change by hint since then.
Usage: python -I mem-unlabeled.py [--top N] [--sample BYTES] [--state FILE] [--self-check]"""
import contextlib
import ctypes
import io
import json
import os
import struct
import sys
from collections import Counter, defaultdict
from ctypes import wintypes as wt

HERE = os.path.dirname(os.path.abspath(__file__))
MIB = 1 << 20
PROT = {0x02: "R", 0x04: "RW", 0x08: "WCOPY", 0x10: "X", 0x20: "RX", 0x40: "RWX", 0x80: "RWXC"}


def prot_name(p):
    base = PROT.get(p & 0xFF, f"{p & 0xFF:#x}")
    return base + ("+NOCACHE" if p & 0x200 else "") + ("+WC" if p & 0x400 else "") + ("+GUARD" if p & 0x100 else "")


def pointer_owner(words, mods):
    """Module (name) that most of these dwords point into, with its share of the non-zero dwords."""
    hits, nz = Counter(), 0
    for w in words:
        if not w:
            continue
        nz += 1
        if all(32 <= (w >> s) & 0xFF < 127 for s in (0, 8, 16, 24)):
            continue  # four printable bytes: text, not a pointer (text fell into amdvlk32's range, al330)
        lo, hi = 0, len(mods) - 1
        while lo <= hi:  # mods sorted by base: binary search
            mid = (lo + hi) // 2
            b, e, n = mods[mid]
            if w < b:
                hi = mid - 1
            elif w >= e:
                lo = mid + 1
            else:
                hits[n] += 1
                break
    if not hits:
        return None, 0.0
    n, c = hits.most_common(1)[0]
    return n, c / max(nz, 1)


def self_check():
    mods = [(0x1000, 0x2000, "a.dll"), (0x5000, 0x9000, "b.dll")]
    assert pointer_owner([0x1004, 0x5000, 0x5004, 0, 0x8FFF, 0x9000], mods) == ("b.dll", 0.6)
    assert pointer_owner([0, 0, 7], mods) == (None, 0.0)
    assert pointer_owner([0x61626364, 0x1004], [(0x1000, 0x2000, "a.dll"), (0x61000000, 0x62000000, "t.dll")]) == ("a.dll", 0.5)
    assert prot_name(0x404) == "RW+WC" and prot_name(0x40) == "RWX"
    print("mem-unlabeled self-check ok")


def main():
    args = sys.argv[1:]
    if "--self-check" in args:
        return self_check()
    opt = lambda k, d=None: args[args.index(k) + 1] if k in args else d
    top, sample, state_path = int(opt("--top", "20")), int(opt("--sample", "65536")), opt("--state")

    g = {"__name__": "memcensus", "__file__": os.path.join(HERE, "mem-census.py")}
    saved = sys.argv
    sys.argv = [g["__file__"], "--top", "1"]
    with contextlib.redirect_stdout(io.StringIO()):
        exec(compile(open(g["__file__"], encoding="utf-8").read(), g["__file__"], "exec"), g)
    sys.argv = saved
    h, k32, allocs, labels, MBI = g["h"], g["k32"], g["allocs"], g["labels"], g["MBI"]

    psapi = ctypes.WinDLL("psapi", use_last_error=True)

    class MODINFO(ctypes.Structure):
        _fields_ = [("base", ctypes.c_void_p), ("size", wt.DWORD), ("entry", ctypes.c_void_p)]

    hm = (wt.HMODULE * 4096)()
    need = wt.DWORD()
    psapi.EnumProcessModulesEx.argtypes = [wt.HANDLE, ctypes.POINTER(wt.HMODULE), wt.DWORD, ctypes.POINTER(wt.DWORD), wt.DWORD]
    psapi.GetModuleInformation.argtypes = [wt.HANDLE, wt.HMODULE, ctypes.POINTER(MODINFO), wt.DWORD]
    psapi.GetModuleBaseNameW.argtypes = [wt.HANDLE, wt.HMODULE, wt.LPWSTR, wt.DWORD]
    psapi.EnumProcessModulesEx(h, hm, ctypes.sizeof(hm), ctypes.byref(need), 0x01)  # 32-bit modules
    mods = []
    for m in hm[:need.value // ctypes.sizeof(wt.HMODULE)]:
        mi, nm = MODINFO(), ctypes.create_unicode_buffer(260)
        psapi.GetModuleInformation(h, m, ctypes.byref(mi), ctypes.sizeof(mi))
        psapi.GetModuleBaseNameW(h, m, nm, 260)
        if mi.base and mi.base < 1 << 32:
            mods.append((mi.base, mi.base + mi.size, nm.value))
    mods.sort()

    def read(addr, n):
        buf = ctypes.create_string_buffer(n)
        got = ctypes.c_size_t()
        ok = k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, n, ctypes.byref(got))
        return buf.raw[:got.value] if ok else None

    def committed_regions(base, res):
        out, a = [], base
        while a < base + res:
            mbi = MBI()
            if not k32.VirtualQueryEx(h, ctypes.c_void_p(a), ctypes.byref(mbi), ctypes.sizeof(mbi)) or not mbi.RegionSize:
                break
            if mbi.State == 0x1000:
                out.append((a, mbi.RegionSize, mbi.Protect))
            a += mbi.RegionSize
        return out

    unl = [b for b in allocs if labels.get(b) == "unlabeled"]
    unl_set = set(unl)
    heap_like = {b for b, l in labels.items() if l.startswith("nt-heap")}
    by = defaultdict(lambda: [0, 0])  # (hint, protection) -> [count, committed bytes]
    rows, hints = [], {}
    for b in unl:
        res, com = allocs[b][0], allocs[b][1]
        regs = committed_regions(b, res)
        prot = Counter()
        for a, s, p in regs:
            prot[prot_name(p)] += s
        pname = prot.most_common(1)[0][0] if prot else "none"
        hint = None
        if regs and pname not in ("RW+WC", "RW+NOCACHE"):
            head = read(regs[0][0], 32)
            if head is None:
                hint = "unreadable"
            else:
                fl, bl = struct.unpack_from("<2I", head)
                near = lambda x: x in unl_set or any(hb <= x < hb + allocs[hb][0] for hb in heap_like)
                if regs[0][0] == b and fl and bl and fl != b and near(fl & ~0xFFFF) and near(bl & ~0xFFFF):
                    hint = "nt-heap big block?"
        if hint is None and regs:
            zeros = total = 0
            words = []
            picks = [regs[0], regs[len(regs) // 2], regs[-1]]
            for a, s, p in picks:
                for off in (0, max(s // 2 - sample // 2, 0)):
                    d = read(a + off, min(sample, s))
                    if d:
                        total += len(d)
                        zeros += d.count(0)
                        words.extend(struct.unpack(f"<{len(d) // 4}I", d[:len(d) // 4 * 4]))
            if not total:
                hint = "unreadable" if pname not in ("RW+WC", "RW+NOCACHE") else f"gpu-mapping ({pname})"
            else:
                mod, share = pointer_owner(words, mods)
                z = zeros / total
                if pname.startswith("RWX") or pname.startswith("RX"):
                    hint = f"code ({mod or 'no module ptrs'})"
                elif z > 0.98:
                    hint = "zero-filled (committed, unused)"
                elif mod and share >= 0.25:
                    hint = f"ptrs->{mod}"
                elif mod:
                    hint = f"data (weak ptrs->{mod} {share:.0%})"
                else:
                    hint = "data (no module ptrs)"
        elif hint is None:
            hint = f"reserve only ({pname})"
        hints[b] = hint
        by[(hint, pname)][0] += 1
        by[(hint, pname)][1] += com
        rows.append((com, res, b, hint, pname, len(regs)))

    tot = sum(r[0] for r in rows)
    print(f"pid {g['pid']}: {len(rows)} unlabeled allocations, committed {tot / MIB:.1f} MiB, reserved "
          f"{sum(r[1] for r in rows) / MIB:.1f} MiB")
    print("  by hint and protection (committed MiB, allocations):")
    for (hint, pname), (n, c) in sorted(by.items(), key=lambda kv: -kv[1][1])[:top]:
        print(f"    {c / MIB:8.1f}  {n:5d}  {pname:12s} {hint}")
    print("  biggest allocations (committed / reserved MiB, regions):")
    for com, res, b, hint, pname, nr in sorted(rows, reverse=True)[:top]:
        print(f"    {b:#010x} {com / MIB:7.1f} / {res / MIB:7.1f}  {nr:4d}  {pname:12s} {hint}")
    if state_path:
        now = {f"{b:#x}": [allocs[b][1], hints[b]] for b in unl}
        if os.path.exists(state_path):
            prev = json.load(open(state_path))
            dh = defaultdict(float)
            for k, (c, hn) in now.items():
                dh[hn] += (c - prev.get(k, [0])[0]) / MIB
            for k, (c, hn) in prev.items():
                if k not in now:
                    dh[hn] -= c / MIB
            print("  change by hint since the last sample (committed MiB): " +
                  ", ".join(f"{k} {v:+.1f}" for k, v in sorted(dh.items(), key=lambda kv: -abs(kv[1])) if abs(v) >= 0.5))
        json.dump(now, open(state_path, "w"))


main()
