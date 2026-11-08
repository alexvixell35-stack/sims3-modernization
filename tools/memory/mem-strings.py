"""Most common ASCII strings (>= MINLEN chars) in an allocation of the running TS3W.exe (read-only), plus the share of
bytes that are text. Reads every committed region of the allocation, up to MAXMB.
Usage: python -I mem-strings.py ADDR [MINLEN=8] [MAXMB=64] | --self-check"""
import ctypes
import re
import subprocess
import sys
from collections import Counter
from ctypes import wintypes as wt

PAT = re.compile(rb"[\x20-\x7e]{%d,}")


class MBI(ctypes.Structure):
    _fields_ = [("BaseAddress", ctypes.c_void_p), ("AllocationBase", ctypes.c_void_p), ("AllocationProtect", wt.DWORD),
                ("RegionSize", ctypes.c_size_t), ("State", wt.DWORD), ("Protect", wt.DWORD), ("Type", wt.DWORD)]


def strings(data, minlen):
    return [m.group().decode() for m in re.finditer(rb"[\x20-\x7e]{%d,}" % minlen, data)]


def main():
    if sys.argv[1:] == ["--self-check"]:
        assert strings(b"\0\0hello world\x01abc\0longer_text_here", 8) == ["hello world", "longer_text_here"]
        return print("mem-strings self-check ok")
    base = int(sys.argv[1], 0)
    minlen = int(sys.argv[2]) if len(sys.argv) > 2 else 8
    maxb = (int(sys.argv[3]) if len(sys.argv) > 3 else 64) << 20
    k32 = ctypes.WinDLL("kernel32")
    k32.OpenProcess.restype = wt.HANDLE
    k32.ReadProcessMemory.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_size_t, ctypes.POINTER(ctypes.c_size_t)]
    k32.VirtualQueryEx.argtypes = [wt.HANDLE, ctypes.c_void_p, ctypes.POINTER(MBI), ctypes.c_size_t]
    out = subprocess.run(["tasklist", "/fi", "imagename eq TS3W.exe", "/fo", "csv", "/nh"], capture_output=True, text=True).stdout
    pid = [int(p.split('","')[1]) for p in out.splitlines() if p.lower().startswith('"ts3w.exe"')][0]
    h = k32.OpenProcess(0x0410, False, pid)
    a, total, text, c = base, 0, 0, Counter()
    while total < maxb:
        m = MBI()
        if not k32.VirtualQueryEx(h, ctypes.c_void_p(a), ctypes.byref(m), ctypes.sizeof(m)) or (m.AllocationBase or 0) != base:
            break
        if m.State == 0x1000:
            n = min(m.RegionSize, maxb - total)
            buf, got = ctypes.create_string_buffer(n), ctypes.c_size_t()
            k32.ReadProcessMemory(h, ctypes.c_void_p(a), buf, n, ctypes.byref(got))
            data = buf.raw[:got.value]
            total += len(data)
            for s in strings(data, minlen):
                text += len(s)
                c[s[:80]] += 1
        a += m.RegionSize
    print(f"{base:#010x}: read {total / 2**20:.1f} MiB, {text / max(total, 1):.1%} of bytes in strings of {minlen}+ chars, "
          f"{len(c)} distinct; top:")
    for s, n in c.most_common(25):
        print(f"  {n:7d}  {s!r}")


main()
