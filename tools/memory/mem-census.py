"""Read-only address-space census of a running TS3W.exe 1.67 (VirtualQueryEx + ReadProcessMemory only; no lock, nothing written).
Every allocation (grouped by AllocationBase) gets an owner:
  image:<module>   MEM_IMAGE (module file name)
  mapped:<file>    MEM_MAPPED (file name, or 'pagefile' for a section without one)
  ea:<heap>        a core on an EA GeneralAllocator's circular core list (sentinel node at heap +0x448, first core
                   [heap +0x460], next at core +0x18; find-core 0x4E4840): main [0x11CB864], mono-malloc [0x11D4BC8]
                   ([0x11EE51C] held the same heap in the session checked)
  ea:direct        an EA big block from its own VirtualAlloc (0x4E50F0; chunk head at +4 = size | 2)
  nt-heap:...      a Windows heap segment (SegmentSignature 0xFFEEFFEE at +8, heap at +0x18; 'process' = PEB32 ProcessHeap)
  boehm            a Boehm GC heap section (GC_heap_sects 0x122D714, {start, bytes}, count [0x11F2FC0]; docs/198)
  stack            a private allocation holding a PAGE_GUARD page (thread stacks)
  unlabeled        anything else private
Prints MiB reserved / committed per owner class, the largest free block, and the largest unlabeled allocations.
--state FILE keeps the previous sample's allocations and prints which ones appeared or went away since then.
Usage: python -I mem-census.py [--top N] [--state FILE]"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import struct
import subprocess
import sys
import time

TOP = int(sys.argv[sys.argv.index("--top") + 1]) if "--top" in sys.argv else 12
STATE = sys.argv[sys.argv.index("--state") + 1] if "--state" in sys.argv else None
HEAPS = {"main": 0x11CB864, "mono-malloc": 0x11D4BC8, "sim-subheap": 0x11EE51C}
MEM_COMMIT, MEM_FREE, MEM_RESERVE = 0x1000, 0x10000, 0x2000
MEM_IMAGE, MEM_MAPPED, MEM_PRIVATE = 0x1000000, 0x40000, 0x20000
PAGE_GUARD = 0x100


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p), ("AllocationProtect", wt.DWORD),
                ("PartitionId", wt.WORD), ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD), ("Protect", wt.DWORD),
                ("Type", wt.DWORD)]


k32 = ctypes.WinDLL("kernel32", use_last_error=True)
psapi = ctypes.WinDLL("psapi", use_last_error=True)
k32.OpenProcess.restype = wt.HANDLE
k32.VirtualQueryEx.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.POINTER(MBI), ctypes.c_size_t]
k32.VirtualQueryEx.restype = ctypes.c_size_t
k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
psapi.GetMappedFileNameW.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.LPWSTR, wt.DWORD]

out = subprocess.run(["tasklist", "/fi", "imagename eq TS3W.exe", "/fo", "csv", "/nh"], capture_output=True, text=True).stdout
pids = [int(p.split('","')[1]) for p in out.splitlines() if p.lower().startswith('"ts3w.exe"')]
if not pids:
    sys.exit("TS3W.exe not running")
pid = pids[0]
h = k32.OpenProcess(0x0010 | 0x0400, False, pid)
if not h:
    sys.exit(f"OpenProcess failed: {ctypes.get_last_error()}")


def u32(addr):
    buf = ctypes.create_string_buffer(4)
    got = ctypes.c_size_t()
    if not k32.ReadProcessMemory(h, ctypes.c_void_p(addr), buf, 4, ctypes.byref(got)) or got.value != 4:
        return None
    return struct.unpack("<I", buf.raw)[0]


def mapped_name(addr):
    buf = ctypes.create_unicode_buffer(520)
    n = psapi.GetMappedFileNameW(h, ctypes.c_void_p(addr), buf, 520)
    return os.path.basename(buf.value[:n]) if n else ""


# owners known from the game's own tables
owner_of = {}
for name, slot in HEAPS.items():
    heap = u32(slot)
    if not heap:
        continue
    # the sentinel node is embedded at heap +0x448, so the first core is its next, [heap +0x460] (find-core 0x4E4840)
    sentinel = heap + 0x448
    c, n = u32(heap + 0x460), 0
    while c and c != sentinel and n < 4096:
        owner_of.setdefault(c, f"ea:{name}")
        c = u32(c + 0x18)
        n += 1
nsect = u32(0x11F2FC0) or 0
for i in range(min(nsect, 0x300)):
    start = u32(0x122D714 + 8 * i)
    if start:
        owner_of.setdefault(start, "boehm")  # the section start; the allocation base is matched below

# walk the 32-bit address space
allocs = {}  # base -> [reserved, committed, type, guard, first region base]
free_blocks = []
addr, mbi = 0x10000, MBI()
while addr < 0xFFFF0000 and k32.VirtualQueryEx(h, ctypes.c_void_p(addr), ctypes.byref(mbi), ctypes.sizeof(mbi)):
    base, size = mbi.BaseAddress or 0, mbi.RegionSize
    if mbi.State == MEM_FREE:
        free_blocks.append(size)
    else:
        ab = mbi.AllocationBase or base
        a = allocs.setdefault(ab, [0, 0, mbi.Type, False, base])
        a[0] += size
        if mbi.State == MEM_COMMIT:
            a[1] += size
            if mbi.Protect & PAGE_GUARD:
                a[3] = True
    addr = base + size
    if size == 0:
        break

# boehm sections: a section start inside an allocation labels that allocation
boehm_starts = sorted(k for k, v in owner_of.items() if v == "boehm")
bases = sorted(allocs)
import bisect
labels = {}
for s in boehm_starts:
    i = bisect.bisect_right(bases, s) - 1
    if i >= 0 and s < bases[i] + allocs[bases[i]][0]:
        labels[bases[i]] = "boehm"
# other EA heaps: an allocation whose header is an EA core header (+8 = its reserved size, 0 < +4 committed size <= +8) is a core;
# its list (+0x18) ends at the owning heap's sentinel node (heap +0x448), a node that is not itself a core
is_core = {}
for b, (res, com, typ, guard, _) in allocs.items():
    if typ == MEM_PRIVATE and b not in owner_of and com:
        c4, c8 = u32(b + 4), u32(b + 8)
        is_core[b] = c8 == res and c4 is not None and 0 < c4 <= c8
heap_of = {}
for b in [b for b, v in is_core.items() if v]:
    if b in heap_of:
        continue
    chain, c = [], b
    while c and len(chain) < 4096 and c in allocs and is_core.get(c):
        chain.append(c)
        c = u32(c + 0x18)
        if c == b:
            break
    heap = f"ea:heap@{c - 0x448:#x}" if c and c != b and c not in allocs else "ea:other"
    for x in chain:
        heap_of[x] = heap
owner_of.update(heap_of)

# the 32-bit PEB of this WOW64 process: ProcessHeap at +0x18 (NtQueryInformationProcess class 26 = ProcessWow64Information)
ntdll = ctypes.WinDLL("ntdll")
peb32 = ctypes.c_void_p()
proc_heap = None
if ntdll.NtQueryInformationProcess(h, 26, ctypes.byref(peb32), ctypes.sizeof(peb32), None) == 0 and peb32.value:
    proc_heap = u32(peb32.value + 0x18)
for b, (res, com, typ, guard, _) in allocs.items():
    if typ != MEM_PRIVATE or b in owner_of or not com:
        continue
    if u32(b + 8) == 0xFFEEFFEE:  # NT heap segment (or the heap's first segment): SegmentSignature, Heap at +0x18
        hp = u32(b + 0x18)
        owner_of[b] = "nt-heap:process" if hp == proc_heap else f"nt-heap@{hp:#x}"
        continue
    head = u32(b + 4)  # EA direct big block (0x4E50F0): an mmapped chunk, head = size | 2, about the allocation's size
    if head is not None and head & 7 == 2 and abs((head & ~7) - res) <= 0x10000:
        owner_of[b] = "ea:direct"
for b, (res, com, typ, guard, _) in allocs.items():
    if b in labels:
        continue
    if typ == MEM_IMAGE:
        labels[b] = "image:" + (mapped_name(b) or "?")
    elif typ == MEM_MAPPED:
        labels[b] = "mapped:" + (mapped_name(b) or "pagefile")
    elif owner_of.get(b, "").startswith(("ea:", "nt-heap")):
        labels[b] = owner_of[b]
    elif guard:
        labels[b] = "stack"
    else:
        labels[b] = "unlabeled"


def cls(label):
    if label.startswith("image:"):
        return "image"
    if label.startswith("mapped:"):
        return "mapped:pagefile" if label == "mapped:pagefile" else "mapped:file"
    return label


tot = {}
for b, (res, com, *_r) in allocs.items():
    t = tot.setdefault(cls(labels[b]), [0, 0, 0])
    t[0] += res
    t[1] += com
    t[2] += 1
MB = 1048576
stamp = time.strftime("%H:%M:%S")
print(f"{stamp} pid {pid}: reserved {sum(a[0] for a in allocs.values()) / MB:.0f} MiB, committed "
      f"{sum(a[1] for a in allocs.values()) / MB:.0f} MiB, largest free {max(free_blocks, default=0) / MB:.0f} MiB, "
      f"free total {sum(free_blocks) / MB:.0f} MiB; EA cores found {sum(1 for v in owner_of.values() if v.startswith('ea:'))}, "
      f"boehm sections {nsect}")
for k in sorted(tot, key=lambda k: -tot[k][0]):
    print(f"  {k:18s} reserved {tot[k][0] / MB:7.1f}  committed {tot[k][1] / MB:7.1f}  n {tot[k][2]}")
unl = sorted((b for b in allocs if labels[b] == "unlabeled"), key=lambda b: -allocs[b][0])
print("  largest unlabeled: " + ", ".join(f"{b:#x} {allocs[b][0] / MB:.1f}/{allocs[b][1] / MB:.1f}" for b in unl[:TOP]))

if STATE:
    now = {f"{b:#x}": [allocs[b][0], allocs[b][1], labels[b]] for b in allocs}
    try:
        prev = json.load(open(STATE))
    except (OSError, ValueError):
        prev = None
    if prev:
        new = [k for k in now if k not in prev]
        gone = [k for k in prev if k not in now]
        grew = [k for k in now if k in prev and now[k][1] > prev[k][1] + 1048576]
        def summ(keys, src):
            by = {}
            for k in keys:
                c = cls(src[k][2])
                by[c] = by.get(c, 0) + src[k][0]
            return ", ".join(f"{c} {v / MB:.1f}" for c, v in sorted(by.items(), key=lambda x: -x[1])) or "-"
        print(f"  since last: new {len(new)} ({summ(new, now)}); gone {len(gone)} ({summ(gone, prev)}); committed +1 MiB or more "
              f"in {len(grew)} ({summ(grew, now)})")
        big_new = sorted((k for k in new if now[k][2] == "unlabeled"), key=lambda k: -now[k][0])[:5]
        if big_new:
            print("  new unlabeled: " + ", ".join(f"{k} {now[k][0] / MB:.1f}/{now[k][1] / MB:.1f}" for k in big_new))
    json.dump(now, open(STATE, "w"))
