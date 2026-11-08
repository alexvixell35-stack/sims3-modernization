"""Read-only census of TS3W.exe's process heap (the NT heap at PEB32 ProcessHeap). ReadProcessMemory and VirtualQueryEx
only: no lock, nothing written, so a block changing while it is read can stop a walk (those bytes count as 'unparsed').
It walks each segment's backend entries (8-byte _HEAP_ENTRY; the first dword is XORed with Heap.Encoding when
EncodeFlagMask is set, and byte 3 = byte 0 ^ byte 1 ^ byte 2 checks it), the LFH user blocks among busy entries
(signature 0xF0E0D0C0; the header's offsets are encoded on Windows 8+, so the block size comes from the user block's
size and block count, checked against the blocks' own headers, see lfh()) and the big blocks on VirtualAllocdBlocks.
x86 _HEAP offsets (Windows 10/11; both signatures are checked before anything is read):
  SegmentSignature 0xFFEEFFEE +0x8, EncodeFlagMask +0x4C, Encoding +0x50, VirtualMemoryThreshold +0x5C,
  Signature 0xEEFFEEFF +0x60, TotalFreeSize +0x74 (8-byte units), VirtualAllocdBlocks +0x9C, SegmentList +0xA4
  segment: SegmentListEntry +0x10, FirstEntry +0x24, LastValidEntry +0x28
  big block (_HEAP_VIRTUAL_ALLOC_ENTRY): CommitSize +0x10, ReserveSize +0x14
  LFH user block header (at entry +8): Signature +0xC
Block sizes include the 8-byte header. It prints busy / free by size class and the block sizes holding the most.
--state FILE keeps this sample's busy blocks (backend, and LFH blocks at user block + header + i * estimated stride)
and prints, against the previous sample, the sizes that grew and what the new blocks start with (a pointer into a
module is likely a vtable).
--dump STRIDE prints the first 64 bytes of a few busy blocks of that LFH bucket and the modules their dwords point into.
--raw FILE (with --dump) writes every busy block of that bucket to FILE: 4 bytes address, then the payload.
--holders STRIDE finds who points at 64 blocks of that bucket: blocks of the process heap, then (exact pointers only)
the game's other private and image memory, read 4 MiB at a time (about 1.7 GB and a minute on TS3W).
Usage: python -I nt-heap.py [--state FILE] [--top N] [--pid PID] [--dump STRIDE [--raw FILE]] [--holders STRIDE]
       python -I nt-heap.py --self-check"""
import bisect
import ctypes
import ctypes.wintypes as wt
import json
import os
import array
import struct
import subprocess
import sys
from collections import Counter, defaultdict

SEG_SIG, HEAP_SIG, LFH_SIG = 0xFFEEFFEE, 0xEEFFEEFF, 0xF0E0D0C0
BUSY = 0x01
CLASSES = [(64, "<=64"), (256, "65-256"), (1024, "257-1K"), (4096, "1-4K"), (16384, "4-16K"), (65536, "16-64K"),
           (262144, "64-256K"), (1 << 40, ">256K")]
MIB = 1 << 20


def walk(buf, base, start, end, enc, commits=None):
    """Backend entries in buf (bytes read from base), from start to end: ([(addr, size, flags)], stop) where stop is
    the address the walk gave up at (an entry that fails its check), or None when it reached end.
    commits: the sorted committed (start, end) ranges. An entry boundary in uncommitted memory (an uncommitted range
    after the entry) jumps to the next committed range; a free entry can itself span decommitted pages."""
    out, a, i = [], start, 0
    commits = commits or [(base, base + len(buf))]
    while a + 8 <= end:
        while i < len(commits) and commits[i][1] <= a:
            i += 1
        if i == len(commits):
            return out, None
        if a < commits[i][0]:
            a = commits[i][0]
            continue
        d = struct.unpack_from("<I", buf, a - base)[0] ^ enc
        b0, b1, b2, b3 = d & 0xFF, (d >> 8) & 0xFF, (d >> 16) & 0xFF, d >> 24
        size = (b0 | b1 << 8) * 8
        if b0 ^ b1 ^ b2 != b3 or size == 0 or a + size > end:
            return out, a
        out.append((a, size, b2))
        a += size
    return out, None


def lfh(buf, base, a, size):
    """(estimated stride, blocks, busy) when the busy entry at a holds an LFH user block (signature at +0xC), with
    and the header size and busy bitmap as 4th and 5th items; (0, 0, 0, 0, 0) for one whose bitmap does not read as
    expected; None for any other entry. Block i starts at user block + header + i * stride (its payload 8 bytes later). The header's own offsets are
    encoded (Windows 8+); SizeOfBitMap (+0x14) and the bitmap (+0x1C, Buffer +0x18 points there) are not. The blocks
    fill the payload after the header, so stride <= (payload - header) / blocks rounded down to 8. That estimate is
    exact for small blocks but too big for large ones (a user block's spare bytes split over few blocks: 13512 for the
    real 12808). So the stride is the largest one at which every block's 8-byte header has bit 0x80 in its last byte
    (UnusedBytes; LFH sets it on busy and free blocks), else the estimate."""
    p = a + 8 - base
    if size < 0x40 or p + 0x20 > len(buf) or struct.unpack_from("<I", buf, p + 0xC)[0] != LFH_SIG:
        return None
    nbits, bm = struct.unpack_from("<II", buf, p + 0x14)
    nbytes = (nbits + 7) // 8
    hdr = (0x1C + 4 * ((nbits + 31) // 32) + 7) & ~7
    if not nbits or bm != a + 8 + 0x1C or hdr + 8 * nbits > size - 8 or p + 0x1C + nbytes > len(buf):
        return 0, 0, 0, 0, 0
    bits = int.from_bytes(buf[p + 0x1C:p + 0x1C + nbytes], "little") & ((1 << nbits) - 1)
    est = ((size - 8 - hdr) // nbits) & ~7
    stride = est
    if nbits > 1 and a + size - base <= len(buf):
        h7 = p + hdr + 7
        stride = next((s for s in range(est, 7, -8) if all(buf[h7 + i * s] & 0x80 for i in range(nbits))), est)
    return stride, nbits, bin(bits).count("1"), hdr, bits


def holder_of(x, ents, buf, base):
    """The busy block holding address x: (payload address, label) with label lfh:<stride> or backend:<size>, or None.
    ents: sorted (addr, size, flags) of the segment; buf: the segment's bytes from base."""
    i = bisect.bisect_right([e[0] for e in ents], x) - 1
    if i < 0:
        return None
    a, size, fl = ents[i]
    if not fl & BUSY or x >= a + size:
        return None
    lf = lfh(buf, base, a, size)
    if lf:
        stride, nb, _, hdr, bits = lf
        if not stride:
            return None
        k = (x - (a + 8 + hdr)) // stride
        if 0 <= k < nb and bits >> k & 1:
            return a + 8 + hdr + k * stride + 8, f"lfh:{stride}"
        return None
    return a + 8, f"backend:{size}"


def holders(stride, busy_lfh, segs, regions, read, enc, hint, rtype, n=64):
    """Who points at the busy LFH blocks of one bucket: n of them, spread over the bucket, are targets; every aligned
    dword of the process heap equal to a target's payload address (or inside its first 64 bytes) is a pointer, and its
    block is a holder. Prints holders by bucket, first-dword module and offset. Holders outside the process heap (a
    driver's own heap, a global, a stack) are counted after that, by region, for exact payload pointers only."""
    tg = [pa for pa, s in busy_lfh if s == stride]
    tg = tg[::max(len(tg) // n, 1)][:n]
    if not tg:
        print(f"  holders: no busy blocks of stride {stride}")
        return
    want = {}
    for pa in tg:
        for off in range(0, 64, 4):
            want[pa + off] = pa
    found, by, targets_hit = 0, Counter(), set()
    for seg in segs:
        first, last = struct.unpack("<I", read(seg + 0x24, 4))[0], struct.unpack("<I", read(seg + 0x28, 4))[0]
        commits = sorted((max(s, seg), min(e, last)) for s, e in regions.get(seg, []) if min(e, last) > max(s, seg))
        buf = bytearray(last - seg)
        for s, e in commits:
            buf[s - seg:e - seg] = read(s, e - s)
        buf = bytes(buf)
        ents, _ = walk(buf, seg, first, last, enc, commits)
        for i in [i for i, v in enumerate(array.array("I", buf)) if v in want]:
            x = seg + 4 * i
            v = struct.unpack_from("<I", buf, 4 * i)[0]
            h = holder_of(x, ents, buf, seg)
            if not h or h[0] == want[v]:
                continue
            found += 1
            targets_hit.add(want[v])
            fd = struct.unpack_from("<I", buf, h[0] - seg)[0] if h[0] + 4 <= last else 0
            by[(h[1], hint(fd), x - h[0], v - want[v])] += 1
    print(f"  holders of {len(tg)} blocks of stride {stride}: {found} pointers from busy blocks, {len(targets_hit)} "
          f"targets held; (holder bucket, its first dword, offset in holder, offset in target): count")
    for (lab, fdh, off, toff), c in by.most_common(14):
        print(f"    {lab:>14} {fdh:>16} +{off:#06x} -> +{toff:#04x}: {c}")
    # outside the process heap: private and image memory, 4 MiB at a time
    packed, out_by, out_hit, seen = {struct.pack("<I", pa): pa for pa in tg}, Counter(), set(), set(segs)
    for base, runs in regions.items():
        if base in seen or rtype.get(base) not in (0x20000, 0x1000000):
            continue
        lab = hint(base) if rtype[base] == 0x1000000 else f"private@{base:#x}"
        for s, e in runs:
            for w in range(s, e, 4 * MIB):
                b = read(w, min(4 * MIB, e - w))
                for pk, pa in packed.items():
                    j = b.find(pk)
                    while j >= 0:
                        if j % 4 == 0:
                            out_by[lab] += 1
                            out_hit.add(pa)
                        j = b.find(pk, j + 1)
    print(f"  outside the process heap: {sum(out_by.values())} pointers to {len(out_hit)} of the {len(tg)} targets; by "
          f"region (allocation base): " + ", ".join(f"{k} {c}" for k, c in out_by.most_common(10)))


def overlap(s, e, commits):
    """Committed bytes of [s, e)."""
    return sum(max(0, min(e, ce) - max(s, cs)) for cs, ce in commits)


def cls(size):
    for lim, name in CLASSES:
        if size <= lim:
            return name
    return CLASSES[-1][1]


def selftest():
    def entry(size, flags):
        b0, b1 = (size // 8) & 0xFF, (size // 8) >> 8
        return (struct.unpack("<I", bytes([b0, b1, flags, b0 ^ b1 ^ flags]))[0] ^ 0x5A5AA5A5).to_bytes(4, "little") + b"\0" * 4
    base = 0x10000
    ub = bytearray(0x200)  # an LFH user block: 20 blocks (header 0x20, (0x200 - 0x20) / 20 = 24), bits 0 and 2 busy
    struct.pack_into("<IIII", ub, 0xC, LFH_SIG, 0x12345678, 20, base + 0x20 + 8 + 0x1C)
    ub[0x1C] = 0b0101
    buf = entry(0x20, BUSY) + b"\0" * 0x18 + entry(0x208, BUSY) + bytes(ub) + entry(0x40, 0) + b"\0" * 0x38
    buf += b"\xff" * 16  # a bad entry: the walk stops here
    ents, stop = walk(buf, base, base, base + len(buf), 0x5A5AA5A5)
    assert [(a - base, s, f) for a, s, f in ents] == [(0, 0x20, 1), (0x20, 0x208, 1), (0x228, 0x40, 0)], ents
    assert stop == base + 0x268, hex(stop)
    # a free entry spanning a decommitted range, then a busy one; the walk skips the hole after the busy one
    buf2 = bytearray(0x4000)
    buf2[0:8] = entry(0x2010, 0x08)
    buf2[0x2010:0x2018] = entry(0x10, BUSY)
    buf2[0x3000:0x3008] = entry(0x20, BUSY)
    commits = [(base, base + 0x1000), (base + 0x2000, base + 0x2020), (base + 0x3000, base + 0x4000)]
    ents, stop = walk(bytes(buf2), base, base, base + 0x3020, 0x5A5AA5A5, commits)
    assert [(a - base, s) for a, s, _ in ents] == [(0, 0x2010), (0x2010, 0x10), (0x3000, 0x20)] and stop is None, ents
    assert overlap(base, base + 0x2010, commits) == 0x1010
    assert lfh(buf, base, base + 0x20, 0x208) == (24, 20, 2, 0x20, 0b0101) and lfh(buf, base, base, 0x20) is None
    # the same user block with block headers (UnusedBytes bit 0x80) every 16 bytes: the real stride is 16, not 24
    buf3 = bytearray(buf)
    for i in range(20):
        buf3[0x20 + 8 + 0x20 + i * 16 + 7] = 0x88
    assert lfh(bytes(buf3), base, base + 0x20, 0x208) == (16, 20, 2, 0x20, 0b0101), lfh(bytes(buf3), base, base + 0x20, 0x208)
    assert cls(64) == "<=64" and cls(65) == "65-256" and cls(300000) == ">256K"
    print("nt-heap self-check ok")


def main():
    args = sys.argv[1:]
    opt = lambda k, d=None: args[args.index(k) + 1] if k in args else d
    top, state_path = int(opt("--top", "15")), opt("--state")

    class MBI(ctypes.Structure):
        _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p), ("AllocationProtect", wt.DWORD),
                    ("PartitionId", wt.WORD), ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD), ("Protect", wt.DWORD),
                    ("Type", wt.DWORD)]

    k32 = ctypes.WinDLL("kernel32", use_last_error=True)
    psapi = ctypes.WinDLL("psapi", use_last_error=True)
    ntdll = ctypes.WinDLL("ntdll")
    k32.OpenProcess.restype = wt.HANDLE
    k32.VirtualQueryEx.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.POINTER(MBI), ctypes.c_size_t]
    k32.VirtualQueryEx.restype = ctypes.c_size_t
    k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    psapi.GetMappedFileNameW.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.LPWSTR, wt.DWORD]

    pid = opt("--pid")
    if pid is None:
        out = subprocess.run(["tasklist", "/fi", "imagename eq TS3W.exe", "/fo", "csv", "/nh"], capture_output=True, text=True).stdout
        pids = [int(p.split('","')[1]) for p in out.splitlines() if p.lower().startswith('"ts3w.exe"')]
        if not pids:
            sys.exit("TS3W.exe not running")
        pid = pids[0]
    pid = int(pid)
    h = k32.OpenProcess(0x0010 | 0x0400, False, pid)
    if not h:
        sys.exit(f"OpenProcess failed: {ctypes.get_last_error()}")

    def read(addr, n, w_max=4 * MIB):
        """n bytes at addr in windows of at most 4 MiB. A window that fails (one unreadable page fails all of it) is
        read again page by page; pages that still fail stay zero (a walk stops there)."""
        out = bytearray(n)
        o = 0
        while o < n:
            w = min(n - o, w_max)
            buf = ctypes.create_string_buffer(w)
            got = ctypes.c_size_t()
            if k32.ReadProcessMemory(h, ctypes.c_void_p(addr + o), buf, w, ctypes.byref(got)) and got.value == w:
                out[o:o + w] = buf.raw
            elif w_max > 0x1000:
                out[o:o + w] = read(addr + o, w, 0x1000)
            o += w
        return bytes(out)

    def u32(addr):
        return struct.unpack("<I", read(addr, 4))[0]

    peb32 = ctypes.c_void_p()
    if ntdll.NtQueryInformationProcess(h, 26, ctypes.byref(peb32), ctypes.sizeof(peb32), None) != 0 or not peb32.value:
        sys.exit("no 32-bit PEB (not a WOW64 process?)")
    heap = u32(peb32.value + 0x18)
    hdr = read(heap, 0x100)
    seg_sig, = struct.unpack_from("<I", hdr, 0x8)
    mask, enc, _, _, vmt, sig = struct.unpack_from("<IIIIII", hdr, 0x4C)
    total_free, = struct.unpack_from("<I", hdr, 0x74)
    if seg_sig != SEG_SIG or sig != HEAP_SIG:
        sys.exit(f"heap {heap:#x}: signatures {seg_sig:#x} / {sig:#x}, want {SEG_SIG:#x} / {HEAP_SIG:#x}: layout not as expected")
    if not mask:
        sys.exit(f"heap {heap:#x}: entries not encoded (EncodeFlagMask 0): not handled")

    # the address space once: committed runs per allocation base, and the modules (for first-dword hints)
    regions, mods, rtype = defaultdict(list), [], {}
    addr, mbi = 0x10000, MBI()
    while addr < 0xFFFF0000 and k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
        base, size = mbi.BaseAddress or 0, mbi.RegionSize
        if size == 0:
            break
        if mbi.State == 0x1000:
            regions[mbi.AllocationBase or base].append((base, base + size))
            rtype[mbi.AllocationBase or base] = mbi.Type
        if mbi.Type == 0x1000000 and mbi.AllocationBase == base:
            nb = ctypes.create_unicode_buffer(520)
            n = psapi.GetMappedFileNameW(h, ctypes.c_void_p(base), nb, 520)
            mods.append([base, base, os.path.basename(nb.value[:n]) if n else "?"])
        if mbi.Type == 0x1000000 and mods and mods[-1][0] == (mbi.AllocationBase or base):
            mods[-1][1] = base + size
        addr = base + size
    mods.sort()
    mod_starts = [m[0] for m in mods]

    def hint(v):
        i = bisect.bisect_right(mod_starts, v) - 1
        if i >= 0 and v < mods[i][1]:
            return mods[i][2]
        return "zero" if v == 0 else "small" if v < 0x10000 else "other"

    # segments
    segs, link, n = [], u32(heap + 0xA4), 0
    while link and link != heap + 0xA4 and n < 512:
        seg = link - 0x10
        if u32(seg + 8) != SEG_SIG:
            print(f"segment {seg:#x}: SegmentSignature wrong, list walk stopped")
            break
        segs.append(seg)
        link, n = u32(link), n + 1

    busy, free_sizes, lfh_n, lfh_b = [], Counter(), 0, 0
    lfh_by = defaultdict(lambda: [0, 0, 0, 0])  # estimated stride (0 = unread) -> user blocks, bytes, blocks, busy
    busy_lfh = []  # (payload address, estimated stride) of each busy LFH block
    seg_res = seg_com = unparsed = bad_walks = 0
    first_dw = {}
    free_com = 0
    # --dump STRIDE: first 64 bytes of a few busy blocks of that bucket, and the modules their dwords point into
    dump_stride, dumps, dump_mods = int(opt("--dump", "0")), [], Counter()
    raw_path, raws = opt("--raw"), []  # with --dump: every dumped block's whole payload, for offline field analysis
    for seg in segs:
        first, last = u32(seg + 0x24), u32(seg + 0x28)
        seg_res += last - seg
        commits = []
        for s, e in sorted(regions.get(seg, [])):
            s, e = max(s, seg), min(e, last)
            if s >= e:
                continue
            if commits and commits[-1][1] == s:
                commits[-1] = (commits[-1][0], e)
            else:
                commits.append((s, e))
        buf = bytearray(last - seg)  # the segment, uncommitted pages left zero
        for s, e in commits:
            seg_com += e - s
            buf[s - seg:e - seg] = read(s, e - s)
        buf = bytes(buf)
        ents, stop = walk(buf, seg, first, last, enc, commits)
        if stop is not None:
            bad_walks += 1
            unparsed += overlap(stop, last, commits)
            d = struct.unpack_from("<I", buf, stop - seg)[0] ^ enc
            print(f"  segment {seg:#x}: walk stopped at {stop:#x} (decoded {d:#010x}, size {(d & 0xFFFF) * 8:#x}, "
                  f"segment end {last:#x}), after {len(ents)} entries; {overlap(stop, last, commits) / MIB:.2f} MiB unparsed")
        for a, size, fl in ents:
            if not fl & BUSY:
                free_sizes[size] += 1
                free_com += overlap(a, a + size, commits)
                continue
            lf = lfh(buf, seg, a, size)
            if lf:
                lfh_n, lfh_b = lfh_n + 1, lfh_b + size
                st = lfh_by[lf[0]]
                st[0] += 1
                st[1] += size
                st[2] += lf[1]
                st[3] += lf[2]
                stride, nb, _, hdr, bits = lf
                for i in range(nb):
                    if bits >> i & 1:
                        pa = a + 8 + hdr + i * stride + 8
                        busy_lfh.append((pa, stride))
                        if pa + 4 <= last:
                            first_dw[pa] = struct.unpack_from("<I", buf, pa - seg)[0]
                        if stride == dump_stride and pa + stride - 8 <= last:
                            dumps.append((pa, buf[pa - seg:pa - seg + 64]))
                            if raw_path:
                                raws.append(struct.pack("<I", pa) + bytes(buf[pa - seg:pa - seg + stride - 8]))
                            body = struct.unpack_from(f"<{(stride - 8) // 4}I", buf, pa - seg)
                            for name in {hint(w) for w in body if w >= 0x10000} - {"other"}:
                                dump_mods[name] += 1
                continue
            busy.append((a, size))
            if a + 12 <= last:
                first_dw[a] = struct.unpack_from("<I", buf, a + 8 - seg)[0]

    big, link, n = [], u32(heap + 0x9C), 0
    while link and link != heap + 0x9C and n < 100000:
        big.append((link, u32(link + 0x10), u32(link + 0x14)))
        link, n = u32(link), n + 1

    bsz = Counter(s for _, s in busy)
    busy_b = sum(s * c for s, c in bsz.items())
    free_b = sum(s * c for s, c in free_sizes.items())
    big_b = sum(c for _, c, _ in big)
    print(f"pid {pid} process heap {heap:#x}: {len(segs)} segments reserved {seg_res / MIB:.1f} committed {seg_com / MIB:.1f} MiB; "
          f"TotalFreeSize {total_free * 8 / MIB:.1f} MiB; VirtualMemoryThreshold {vmt * 8 // 1024} KB")
    print(f"  backend busy {len(busy)} blocks {busy_b / MIB:.2f} MiB | free {sum(free_sizes.values())} blocks {free_b / MIB:.2f} MiB "
          f"({free_com / MIB:.2f} committed) | "
          f"LFH user blocks {lfh_n} {lfh_b / MIB:.2f} MiB (busy blocks about {sum(k * v[3] for k, v in lfh_by.items()) / MIB:.2f} MiB) | "
          f"unparsed {unparsed / MIB:.2f} MiB in {bad_walks} walks | big blocks {len(big)} {big_b / MIB:.2f} MiB")
    by_cls = defaultdict(lambda: [0, 0, 0, 0])
    for s, c in bsz.items():
        by_cls[cls(s)][0] += c
        by_cls[cls(s)][1] += s * c
    for s, c in free_sizes.items():
        by_cls[cls(s)][2] += c
        by_cls[cls(s)][3] += s * c
    own = defaultdict(Counter)  # stride -> where the busy blocks' first dword points
    for pa, s in busy_lfh:
        own[s][hint(first_dw.get(pa, 0))] += 1
    print("  LFH by estimated stride (B: user blocks, MiB, blocks busy / total, busy MiB; busy blocks' first dword):")
    for k in sorted(lfh_by, key=lambda k: -lfh_by[k][1])[:top]:
        v = lfh_by[k]
        print(f"    {k:6d} {v[0]:6d} {v[1] / MIB:7.2f} {v[3]:8d} / {v[2]:8d} {k * v[3] / MIB:7.2f}   " +
              ", ".join(f"{n} {c * 100 // max(v[3], 1)}%" for n, c in own[k].most_common(3)))
    if dump_stride:
        print(f"  {len(dumps)} busy blocks of stride {dump_stride}; blocks holding a pointer into a module: " +
              ", ".join(f"{n} {c}" for n, c in dump_mods.most_common(8)))
        print(f"  every {max(len(dumps) // 8, 1)}th block, first 64 bytes:")
        for pa, b in dumps[::max(len(dumps) // 8, 1)][:8]:
            print(f"    {pa:#010x}: " + " ".join(f"{w:08x}" for w in struct.unpack("<16I", b)) +
                  "  " + "".join(chr(c) if 32 <= c < 127 else "." for c in b))
    if raw_path and raws:
        open(raw_path, "wb").write(b"".join(raws))
        print(f"  wrote {len(raws)} records of 4 + {dump_stride - 8} bytes (address, payload) to {raw_path}")
    hold_stride = int(opt("--holders", "0"))
    if hold_stride:
        holders(hold_stride, busy_lfh, segs, regions, read, enc, hint, rtype)
    print("  size class (backend blocks, LFH user blocks not included): busy n / MiB | free n / MiB")
    for _, name in CLASSES:
        if name in by_cls:
            v = by_cls[name]
            print(f"    {name:>8} {v[0]:8d} {v[1] / MIB:8.2f} | {v[2]:7d} {v[3] / MIB:8.2f}")
    print(f"  busy backend block sizes holding the most (size: n, MiB):")
    for s, c in sorted(bsz.items(), key=lambda kv: -kv[0] * kv[1])[:top]:
        print(f"    {s:8d}: {c:6d} {s * c / MIB:7.2f}")
    if big:
        print("  big blocks by commit size (KB: n, MiB): " + ", ".join(
            f"{k // 1024}: {c} {k * c / MIB:.1f}" for k, c in Counter(c for _, c, _ in big).most_common(top)))

    if not state_path:
        return
    cur = {"busy": busy, "busyLfh": busy_lfh, "big": [[a, c] for a, c, _ in big], "lfhUserBlocks": [lfh_n, lfh_b],
           "lfh": {str(k): v for k, v in lfh_by.items()}}
    prev = None
    if os.path.exists(state_path):
        try:
            prev = json.load(open(state_path))
        except (OSError, ValueError):
            prev = None
    json.dump(cur, open(state_path, "w"))
    if not prev:
        print("  (state saved; the next run prints the change)")
        return
    if "busyLfh" not in prev:
        print("  (previous state has no LFH blocks: this sample is the new baseline)")
        return
    pset = {(a, s) for a, s in prev["busy"]} | {(a, s) for a, s in prev["busyLfh"]}
    cset = set(busy) | set(busy_lfh)
    new, gone = cset - pset, pset - cset
    net = Counter()
    for _, s in new:
        net[s] += s
    for _, s in gone:
        net[s] -= s
    print(f"  since last sample: busy blocks (backend and LFH) +{len(new)} blocks {sum(s for _, s in new) / MIB:.2f} MiB, "
          f"-{len(gone)} blocks {sum(s for _, s in gone) / MIB:.2f} MiB")
    print("    sizes by net growth (size: net MiB, new n, gone n):")
    nn, gn = Counter(s for _, s in new), Counter(s for _, s in gone)
    for s, b in sorted(net.items(), key=lambda kv: -kv[1])[:top]:
        if b > 0:
            print(f"      {s:8d}: {b / MIB:+7.2f} {nn[s]:6d} {gn[s]:6d}")
    hb = Counter()
    for a, s in new:
        if a in first_dw:
            hb[(hint(first_dw[a]), s)] += s
    print("    new blocks by first dword (module or kind, size: MiB):")
    for (k, s), b in hb.most_common(top):
        print(f"      {k:24s} {s:8d}: {b / MIB:6.2f}")
    pn, pb_ = prev.get("lfhUserBlocks", [0, 0])
    print(f"    LFH user blocks: {pn} -> {lfh_n}, {pb_ / MIB:.2f} -> {lfh_b / MIB:.2f} MiB")
    pl = {int(k): v for k, v in prev.get("lfh", {}).items()}
    dl = sorted(((k * (v[3] - pl.get(k, [0, 0, 0, 0])[3]), k, v[3] - pl.get(k, [0, 0, 0, 0])[3], v[1] - pl.get(k, [0, 0, 0, 0])[1])
                 for k, v in lfh_by.items()), reverse=True)
    print("    LFH busy blocks by estimated stride, change (B: blocks, busy MiB, user block MiB): " +
          ", ".join(f"{k}: {n:+d} {b / MIB:+.2f} {ub / MIB:+.2f}" for b, k, n, ub in dl[:top] if n))
    pb = sum(c for _, c in prev.get("big", []))
    print(f"    big blocks: {len(prev.get('big', []))} -> {len(big)}, {pb / MIB:.1f} -> {big_b / MIB:.1f} MiB")


if __name__ == "__main__":
    selftest() if "--self-check" in sys.argv else main()
