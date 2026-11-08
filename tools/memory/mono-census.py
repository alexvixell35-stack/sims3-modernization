"""Managed heap census of a running TS3W.exe 1.67 by class (read-only: ReadProcessMemory, no lock).

Boehm 6.x layout (docs/198): sections GC_heap_sects 0x122D714 {start, bytes} x [0x11F2FC0]; block headers through
top_index 0x1233318 (addr >> 22, then (addr >> 12) & 0x3FF); header hb_sz +0 (words), hb_map +0x10 (free block when
it equals GC_invalid_map [0x11F30D8]), hb_obj_kind +0x14, mark bits +0x1C (one bit per word offset in the block, 32 words) [verified al328: the bit
patterns match the slot sizes; +0x18 is another field].
Objects of a small block are hb_sz words each from the block start; a large one fills its blocks.
An object is a Mono object when its +0 is a MonoVTable outside the GC heap whose +0 (MonoClass) has a C string name
at +0x34 and namespace at +0x38 [verified on 3 classes, al328]. Free-list links point into the heap and are skipped.
'marked' = the GC's mark bit: live at the last collection (objects allocated since are unmarked until the next one).
--state FILE keeps the counts per class; with a previous FILE, prints the classes that grew most since then.
Usage: python -I mono-census.py [--top N] [--state FILE] | --self-check"""
import array
import collections
import ctypes
import ctypes.wintypes as wt
import json
import os
import struct
import subprocess
import sys
import time

TOP, SECTS, NSECTS, INVALID = 0x1233318, 0x122D714, 0x11F2FC0, 0x11F30D8
HBLK, MIB = 4096, 1 << 20


def slots(sz_words, span):
    """Object start word offsets in a block of span bytes holding objects of sz_words words."""
    if sz_words * 4 > HBLK // 2:
        return [0]
    return list(range(0, (HBLK // 4) // sz_words * sz_words, sz_words))


def marked(marks, w):
    return (marks[w >> 5] >> (w & 31)) & 1


def self_check():
    assert slots(4, HBLK) == list(range(0, 1024, 4))
    assert slots(3, HBLK)[-1] == 1020 and len(slots(3, HBLK)) == 341
    assert slots(1024, 8192) == [0]
    m = array.array("I", [0] * 32)
    m[1] = 1 << 3
    assert marked(m, 35) and not marked(m, 34)
    print("mono-census self-check ok")


def main():
    args = sys.argv[1:]
    if args == ["--self-check"]:
        return self_check()
    top = int(args[args.index("--top") + 1]) if "--top" in args else 25
    state = args[args.index("--state") + 1] if "--state" in args else None
    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    k32.OpenProcess.restype = wt.HANDLE
    k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    out = subprocess.run(["tasklist", "/fi", "imagename eq TS3W.exe", "/fo", "csv", "/nh"], capture_output=True, text=True).stdout
    pids = [int(p.split('","')[1]) for p in out.splitlines() if p.lower().startswith('"ts3w.exe"')]
    if not pids:
        sys.exit("TS3W.exe not running")
    h = k32.OpenProcess(0x0010 | 0x0400, False, pids[0])

    def rd(a, n):
        b, g = ctypes.create_string_buffer(n), ctypes.c_size_t()
        ok = k32.ReadProcessMemory(h, ctypes.c_void_p(a), b, n, ctypes.byref(g))
        return b.raw[:g.value] if (ok or g.value) else None

    def u32(a):
        x = rd(a, 4)
        return struct.unpack("<I", x)[0] if x and len(x) == 4 else 0

    def cstr(a):
        d = rd(a, 96) if a else None
        if not d:
            return None
        s = d.split(b"\0", 1)[0]
        return s.decode() if s and all(32 <= c < 127 for c in s) else None

    t0 = time.time()
    inv = u32(INVALID)
    n = u32(NSECTS)
    sects = [struct.unpack("<2I", rd(SECTS + 8 * i, 8)) for i in range(n)]
    in_heap = lambda v: any(s <= v < s + b for s, b in sects)
    bottoms = {}

    def hdr(p):
        t = p >> 22
        if t not in bottoms:
            bi = u32(TOP + 4 * t)
            bottoms[t] = array.array("I", rd(bi, 4096) or bytes(4096)) if bi else array.array("I", bytes(4096))
        return bottoms[t][(p >> 12) & 0x3FF]

    names, knames = {}, {}

    def kname(k, depth=0):
        """Full name of a MonoClass: namespace.name, nested types as Outer/Inner (nested_in at +0x28, docs/104)."""
        if k not in knames:
            nm = cstr(u32(k + 0x34))
            if nm:
                ns = cstr(u32(k + 0x38)) or ""
                outer = u32(k + 0x28)
                if not ns and outer and depth < 4 and not in_heap(outer):
                    on = kname(outer, depth + 1)
                    nm = f"{on}/{nm}" if on else nm
                elif ns:
                    nm = f"{ns}.{nm}"
            knames[k] = nm
        return knames[k]

    def cls(vt):
        if vt not in names:
            name = None
            if vt and not in_heap(vt):
                k = u32(vt)
                if k and not in_heap(k):
                    name = kname(k)
            names[vt] = name
        return names[vt]

    agg = collections.defaultdict(lambda: [0, 0, 0, 0])  # class -> [n, bytes, marked n, marked bytes]
    used_blocks = free_bytes = other_bytes = 0
    for start, size in sects:
        p = start
        while p < start + size:
            hp = hdr(p)
            if hp < HBLK:  # no header, or a forwarding count inside a large object
                p += HBLK
                continue
            hb = rd(hp, 0x1C + 128)
            if not hb or len(hb) < 0x9C:
                p += HBLK
                continue
            sz, hmap, kind = struct.unpack_from("<I", hb, 0)[0], struct.unpack_from("<I", hb, 0x10)[0], hb[0x14]
            span = max(HBLK, (sz * 4 + HBLK - 1) // HBLK * HBLK) if sz * 4 > HBLK // 2 else HBLK
            if hmap == inv or not sz:
                free_bytes += max(sz * 4, HBLK) if hmap == inv else HBLK
                p += max(HBLK, (sz * 4 + HBLK - 1) // HBLK * HBLK) if hmap == inv else HBLK
                continue
            used_blocks += 1
            marks = array.array("I", hb[0x1C:0x9C])
            data = rd(p, HBLK)
            if not data or len(data) < HBLK:
                p += span
                continue
            words = array.array("I", data)
            osz = sz * 4
            for w in slots(sz, span):
                c = cls(words[w])
                if c is None:
                    other_bytes += osz
                    continue
                a = agg[c]
                a[0] += 1
                a[1] += osz
                if marked(marks, w):
                    a[2] += 1
                    a[3] += osz
            p += span
    tot = sum(v[1] for v in agg.values())
    totm = sum(v[3] for v in agg.values())
    print(f"{time.strftime('%H:%M:%S')} pid {pids[0]}: {len(sects)} sections {sum(b for _, b in sects) / MIB:.0f} MiB, "
          f"{used_blocks} used blocks; Mono objects {sum(v[0] for v in agg.values())} = {tot / MIB:.1f} MiB "
          f"(marked {totm / MIB:.1f}); free blocks {free_bytes / MIB:.1f} MiB; other slots {other_bytes / MIB:.1f} MiB; "
          f"{len(agg)} classes ({time.time() - t0:.1f} s)")
    print("  top classes by bytes (n, MiB, marked n, marked MiB):")
    for c, v in sorted(agg.items(), key=lambda kv: -kv[1][1])[:top]:
        print(f"    {v[1] / MIB:8.2f} {v[0]:9d}  marked {v[3] / MIB:8.2f} {v[2]:9d}  {c}")
    if state:
        if os.path.exists(state):
            prev = json.load(open(state))
            d = {c: (v[0] - prev.get(c, [0, 0])[0], v[1] - prev.get(c, [0, 0])[1]) for c, v in agg.items()}
            for c, v in prev.items():
                if c not in agg:
                    d[c] = (-v[0], -v[1])
            print(f"  change since the last sample: {(tot - sum(v[1] for v in prev.values())) / MIB:+.1f} MiB; most grown:")
            for c, (dn, db) in sorted(d.items(), key=lambda kv: -kv[1][1])[:top // 2]:
                print(f"    {db / MIB:+8.2f} MiB {dn:+9d}  {c}")
        json.dump({c: [v[0], v[1]] for c, v in agg.items()}, open(state, "w"))


main()
