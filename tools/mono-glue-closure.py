"""Native call closure of the second Mono thread's glue (docs/131): what EA native code the worker can reach, which
globals it reads and writes, which locks it takes, and the race set against the sim thread.

    python tools/mono-glue-closure.py [--exe game-copy/bin/TS3W.exe] [--functions game-copy/analysis/functions.csv]
                                      [--depth 5] [--cache DIR] [--csv docs/data/mono-glue-closure.csv]
                                      [--races docs/data/mono-glue-races.csv] [--summary FILE]
    python tools/mono-glue-closure.py --self-check
    python tools/mono-glue-closure.py --remitigate [--races FILE]   fill empty mitigations of an existing races csv from
        HANDOFF (docs/data/races-handoff.md), no exe needed: keeps the committed race set when a fresh index drifts

Static only (capstone 5 over a copy of the Steam exe, no Ghidra project needed). Steps:
1. Whole-exe index (cached in --cache as a pickle): per function, direct calls and tail jumps, indirect sites with a
   value guess from a linear register tracker, absolute data accesses (read/write from capstone operand access),
   fields of singletons reached through a global pointer (`mov r,[G]` then `[r+off]`), lock acquisitions
   (Lock_Acquire 0x4E16F0 with its ecx, EnterCriticalSection/TryEnterCriticalSection with the pushed argument),
   imported APIs, fs: segment reads.
2. Indirect calls resolved in docs/102 order where this tool can: imports, switch tables (internal), constants,
   global function pointers (every immediate stored to the global anywhere plus its initial data), vtable slots of a
   receiver whose vtable store is seen in the frame or in a known getter, prior facts from
   docs/data/routing-indirect-sites.csv, then the slot-signature fallback (<= 6 distinct targets = partial).
   v2 (docs/131 §9): caller-side receiver typing (docs/102 step 5). The scan tracks esp (ret N of callees from a
   pre-pass), stack arguments, the state of ecx and the pushed arguments at every call, eax at every ret, objects on
   the stack, field loads and typed field stores. A receiver `this`, argument n, callee return, constructed object
   (`thisof`), global or field is typed by evaluating those states in the callers / callee / storers (memoised,
   recursion-safe); "resolved" needs every source typed, else "partial". Vtable slots that point at code missing from
   functions.csv (e.g. the script host vtable 0x108B690) are indexed as extra functions. --no-typing turns it off.
3. Roots: (a) EA internal calls of docs/121 (v0) and docs/126 (tier-2 class-a rows), (b) every EA function the Mono
   range calls directly, (c) Mono runtime entry points of the worker path. BFS to --depth hops; hop depth+1 is the
   frontier.
4. Race set: a writable global (or singleton field) the worker set touches, where one side writes and the other
   touches it, with no lock common to every access. The sim thread is assumed to run any function of the exe.
Python because capstone is (tools/snap-vcall-sites.py precedent).
"""
import argparse
import bisect
import collections
import csv
import os
import pickle
import re
import struct
import sys

import capstone
from capstone import x86_const as X

REPO = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
MONO_LO, MONO_HI = 0xE450C0, 0xEC0000
LOCK_ACQUIRE, LOCK_RELEASE = 0x4E16F0, 0x4E17B0
PURECALL = 0xF7E8AC
NAMED_LOCKS = {0x10786C8: "lot-grid lock", 0xFE4DC4: "SimService request lock"}
# getter -> vtable of the object it returns (docs/121 §1 step 2)
GETTERS = {0x7692B0: 0x108B690, 0xD7CFD0: 0x108B818}
MANAGED_ENTRY = {0xEA1090: "mono_runtime_invoke", 0xEA1400: "mono_runtime_delegate_invoke", 0xE59FD0: "interp_invoke_with_state",
                 0xE52B60: "interp_runtime_invoke", 0xE5A600: "worker entry 0xE5A600", 0xD75FA0: "ScriptHost_InvokeDelegate"}
HEAP_FUNCS = {0x4E69D0: "GeneralAllocator(lock)", 0x4E5920: "GeneralAllocator(lock)", 0x4E6A20: "GeneralAllocator(lock)",
              0x4E6A70: "GeneralAllocator", 0x4E3F90: "EA_AllocTagged", 0x4E3F70: "EA_Free", 0xD70A10: "g_malloc",
              0xD70A30: "g_malloc0", 0xD70A60: "g_free", 0xD70A80: "g_strdup", 0xD71860: "g_realloc",
              0xE4BE30: "GC_malloc", 0xE4BEB0: "GC_free", 0xE4BF90: "GC_malloc_uncollectable", 0xE50DE0: "GC_malloc_typed",
              0xEA36F0: "mono_object_new", 0xEA30F0: "mono_array_new", 0xEA40B0: "mono_string_new",
              0xD71900: "frame arena", 0xD71A20: "frame arena", 0xD71B70: "frame arena"}
D3D_GLOBALS = {0x11CECC8, 0x11CECCC, 0x11CECD0}
THREAD_APIS = re.compile(r"GetCurrentThread|TlsGetValue|TlsSetValue|TlsAlloc|SuspendThread|ResumeThread|CreateThread|"
                         r"SetThreadPriority|GetThreadContext|SwitchToThread")
UI_DLLS = ("USER32", "GDI32", "IMM32", "COMCTL32", "SHELL32", "WINMM", "DINPUT")
COM_DLLS = ("OLE32", "OLEAUT32")
LOCK_APIS = {"EnterCriticalSection", "TryEnterCriticalSection", "WaitForSingleObject", "WaitForMultipleObjects",
             "AcquireSRWLockExclusive", "AcquireSRWLockShared"}
ATOMIC_APIS = re.compile(r"Interlocked")


# ----------------------------------------------------------------------------------------------------------- PE
class PE:
    def __init__(self, path):
        b = self.b = open(path, "rb").read()
        pe = struct.unpack_from("<I", b, 0x3C)[0]
        nsec = struct.unpack_from("<H", b, pe + 6)[0]
        opt = struct.unpack_from("<H", b, pe + 20)[0]
        self.base = struct.unpack_from("<I", b, pe + 24 + 28)[0]
        self.secs = []
        for i in range(nsec):
            o = pe + 24 + opt + i * 40
            name = b[o:o + 8].rstrip(b"\0").decode("latin1")
            vsz, va, rsz, raw = struct.unpack_from("<IIII", b, o + 8)
            ch = struct.unpack_from("<I", b, o + 36)[0]
            self.secs.append((name, self.base + va, vsz, raw, rsz, ch))
        imp_rva = struct.unpack_from("<I", b, pe + 24 + 104)[0]
        self.imports = {}
        d = imp_rva
        while True:
            oft, _, _, name_rva, ft = struct.unpack("<IIIII", self.read(self.base + d, 20))
            if not ft:
                break
            dll = self.cstr(self.base + name_rva).upper().replace(".DLL", "")
            src = oft or ft
            k = 0
            while True:
                v = struct.unpack("<I", self.read(self.base + src + 4 * k, 4))[0]
                if not v:
                    break
                nm = "#%d" % (v & 0xFFFF) if v & 0x80000000 else self.cstr(self.base + v + 2)
                self.imports[self.base + ft + 4 * k] = dll + "!" + nm
                k += 1
            d += 20

    def sec(self, a):
        for s in self.secs:
            if s[1] <= a < s[1] + max(s[2], s[4]):
                return s
        return None

    def read(self, a, n):
        s = self.sec(a)
        if not s:
            return b""
        off = a - s[1]
        if off >= s[4]:
            return b"\0" * n
        return self.b[s[3] + off: s[3] + min(off + n, s[4])]

    def dword(self, a):
        r = self.read(a, 4)
        return struct.unpack("<I", r)[0] if len(r) == 4 else None

    def cstr(self, a):
        r = self.read(a, 256)
        return r.split(b"\0", 1)[0].decode("latin1")

    def is_code(self, a):
        s = self.sec(a)
        return bool(s and s[5] & 0x20000000)

    def is_writable(self, a):
        s = self.sec(a)
        return bool(s and s[5] & 0x80000000 and not s[5] & 0x20000000)

    def is_data(self, a):
        s = self.sec(a)
        return bool(s and not s[5] & 0x20000000)


# ----------------------------------------------------------------------------------------------- per-function scan
GP = {"eax", "ebx", "ecx", "edx", "esi", "edi", "ebp"}
SUB = {"al": "eax", "ah": "eax", "ax": "eax", "bl": "ebx", "bh": "ebx", "bx": "ebx", "cl": "ecx", "ch": "ecx",
       "cx": "ecx", "dl": "edx", "dh": "edx", "dx": "edx", "si": "esi", "di": "edi", "bp": "ebp"}


TYPABLE = {"obj", "objp", "this", "arg", "ld", "imm", "ret", "thisof", "fld", "fld2"}


def fdepth(s):
    return 1 + fdepth(s[1]) if s and s[0] == "fld2" else 0


def quick_facts(md0, pe, entry, end):
    """pre-pass (no operand detail): stack bytes the function pops (`ret N`, None = no ret seen) and whether it is a
    stack probe (_chkstk/_alloca_probe: moves esp by eax)"""
    retn, probe = None, False
    for ins in md0.disasm(pe.read(entry, end - entry), entry):
        if ins.mnemonic == "ret" and retn is None:
            retn = int(ins.op_str, 16) if ins.op_str else 0
        elif (ins.mnemonic == "mov" and ins.op_str in ("esp, ecx", "esp, eax")) or (
                ins.mnemonic == "xchg" and "esp" in ins.op_str and "eax" in ins.op_str):
            probe = True
    return retn, probe


def scan_function(md, pe, entry, end, entries_set, retn=None, probes=frozenset()):
    """one linear pass over [entry, end) (end exclusive); returns a dict of facts (see module doc).
    Register states: ("this",) ecx at entry, ("arg", n) stack argument n, ("imm", v), ("ld", G) value of global G,
    ("obj", V) object whose vtable store V was seen, ("objp", V) object a known getter returns, ("ret", T) eax after a
    direct call to T, ("thisof", T) the object passed as `this` to T (typed by the vtable T leaves in it), ("vtof", s)
    the vtable of s, ("slotv", s, off), ("fld", G, off), ("lea", s, off)."""
    retn = retn or {}
    code = pe.read(entry, end - entry)
    st = {"ecx": ("this",)}
    f = {"calls": [], "ind": [], "gr": set(), "gw": set(), "ga": set(), "pr": set(), "pw": set(), "locks": [],
         "apis": set(), "fs": set(), "vtstores": [], "fnstores": [], "objstores": [], "atomicw": set(), "ninsn": 0,
         "cargs": [], "iargs": {}, "rets": set(), "fnrefs": set(), "fstores": [], "ffn": []}
    last_push = None
    delta, ebp_base = 0, None   # esp relative to entry ([esp] = return address at entry); None = lost
    pushes, pending = [], 0     # pushes since the last call; bytes assumed popped by an indirect/unknown callee
    slots = {}                  # stack offset -> vtable stored there (objects on the stack)
    labels, dead = {}, False    # esp delta at forward branch targets; previous instruction was ret/jmp

    def sref(base, disp):
        if base == "esp" and delta is not None:
            return delta + disp
        if base == "ebp" and ebp_base is not None:
            return ebp_base + disp
        return None

    def argst(off):
        return ("arg", (off - 4) // 4) if off is not None and off >= 4 and off % 4 == 0 else None

    for ins in md.disasm(code, entry):
        f["ninsn"] += 1
        mn = ins.mnemonic
        ops = ins.operands
        was_pending, pending = pending, 0
        lab = labels.get(ins.address)
        if dead:  # after ret/jmp the linear successor is a branch target: take the state of its first forward branch
            delta = lab[0] if lab else None
            if lab:
                st = dict(lab[1])
            pushes = []
        elif delta is None and lab:
            delta = lab[0]
        dead = mn in ("ret", "jmp")
        if mn.startswith("j") and ops and ops[0].type == X.X86_OP_IMM and (ops[0].imm & 0xFFFFFFFF) > ins.address:
            labels.setdefault(ops[0].imm & 0xFFFFFFFF, (delta, dict(st)))
        for op in ops:
            if op.type == X.X86_OP_IMM and mn not in ("call", "jmp") and (op.imm & 0xFFFFFFFF) in entries_set:
                f["fnrefs"].add(op.imm & 0xFFFFFFFF)
        # ---- memory operands: data accesses
        for op in ops:
            if op.type != X.X86_OP_MEM:
                continue
            m = op.mem
            if m.segment == X.X86_REG_FS:
                f["fs"].add(m.disp & 0xFFFFFFFF)
                continue
            base = ins.reg_name(m.base) if m.base else None
            disp = m.disp & 0xFFFFFFFF
            acc = op.access
            if mn == "lea":
                acc = 0
            if base is None and pe.is_data(disp):
                if mn == "lea":
                    f["ga"].add(disp)
                if acc & capstone.CS_AC_READ:
                    f["gr"].add(disp)
                if acc & capstone.CS_AC_WRITE:
                    f["gw"].add(disp)
                    if ins.prefix[0] == X.X86_PREFIX_LOCK or mn in ("xchg", "cmpxchg", "xadd"):
                        f["atomicw"].add(disp)
            elif base in st and m.index == 0:
                s = st[base]
                if s[0] == "ld":  # field of an object a global points to
                    key = (s[1], disp)
                    if acc & capstone.CS_AC_READ:
                        f["pr"].add(key)
                    if acc & capstone.CS_AC_WRITE:
                        f["pw"].add(key)
                elif s[0] == "imm" and pe.is_data((s[1] + disp) & 0xFFFFFFFF):
                    a = (s[1] + disp) & 0xFFFFFFFF
                    if acc & capstone.CS_AC_READ:
                        f["gr"].add(a)
                    if acc & capstone.CS_AC_WRITE:
                        f["gw"].add(a)
        # ---- stores of immediates (vtables, function pointers) and object pointers
        if mn == "mov" and len(ops) == 2 and ops[0].type == X.X86_OP_MEM:
            m = ops[0].mem
            base = ins.reg_name(m.base) if m.base else None
            if ops[1].type == X.X86_OP_IMM:
                v = ops[1].imm & 0xFFFFFFFF
                if base is None and m.index == 0:
                    tgt = ("g", m.disp & 0xFFFFFFFF)
                elif base in st and st[base][0] == "imm":
                    tgt = ("g", (st[base][1] + m.disp) & 0xFFFFFFFF)
                else:
                    tgt = ("r", base, m.disp)
                if v in entries_set:
                    f["fnstores"].append((tgt, v))
                    if base in st and st[base][0] in TYPABLE and m.index == 0:
                        f["ffn"].append((m.disp & 0xFFFFFFFF, st[base], v))  # function pointer into a typed owner
                elif pe.is_data(v) and not pe.is_writable(v) and pe.is_code(pe.dword(v) or 0):
                    f["vtstores"].append((tgt, v))   # any displacement: secondary bases and stack objects too
                    so = sref(base, m.disp) if m.index == 0 else None
                    if so is not None:
                        slots[so] = v
                    elif m.disp == 0 and base in GP:
                        st[base] = ("obj", v) if st.get(base, ("?",))[0] != "imm" else st[base]
            elif ops[1].type == X.X86_OP_REG and base in st and m.index == 0 and m.disp and st[base][0] in TYPABLE:
                s = st.get(ins.reg_name(ops[1].reg))
                if s and s[0] in TYPABLE and fdepth(s) < 2:
                    f["fstores"].append((m.disp & 0xFFFFFFFF, st[base], s))  # typed value into a field of a typed owner
            elif ops[1].type == X.X86_OP_REG and base is None and m.index == 0:
                r = ins.reg_name(ops[1].reg)
                s = st.get(r)
                if s and s[0] in TYPABLE:
                    f["objstores"].append((m.disp & 0xFFFFFFFF, s))
                if s and s[0] == "imm" and s[1] in entries_set:
                    f["fnstores"].append((("g", m.disp & 0xFFFFFFFF), s[1]))
        # ---- calls and jumps
        if mn in ("call", "jmp"):
            op = ops[0] if ops else None
            args = tuple(reversed(pushes[-6:])) if mn == "call" else ()
            ecx0, eax0 = st.get("ecx"), st.get("eax")
            tgt_direct = None
            if op is not None and op.type == X.X86_OP_IMM:
                t = op.imm & 0xFFFFFFFF
                if mn == "call" or not (entry <= t < end):
                    tgt_direct = t
                    f["calls"].append((ins.address, t))
                    f["cargs"].append((t, ecx0, args))
                    if mn == "jmp":
                        f["rets"].add(("ret", t))
                    if t == LOCK_ACQUIRE:
                        f["locks"].append(lock_id(st.get("ecx")))
                    if t in GETTERS:
                        st = {k: v for k, v in st.items() if k not in ("eax", "ecx", "edx")}
                        st["eax"] = ("objp", GETTERS[t])
                        delta = None if delta is None or retn.get(t) is None else delta + retn[t]
                        pushes, last_push = [], None
                        continue
            elif op is not None:
                kind, val = None, None
                if op.type == X.X86_OP_MEM:
                    m = op.mem
                    base = ins.reg_name(m.base) if m.base else None
                    if base is None and m.index == 0:
                        a = m.disp & 0xFFFFFFFF
                        if a in pe.imports:
                            api = pe.imports[a]
                            f["apis"].add(api)
                            nm = api.split("!", 1)[1]
                            if nm in LOCK_APIS:
                                f["locks"].append(cs_id(last_push, st))
                            kind, val = "import", api
                        else:
                            kind, val = "gfn", a
                    elif m.index and mn == "jmp":
                        kind, val = "switch", m.disp & 0xFFFFFFFF
                    elif base in st:
                        kind, val = "slot", (st[base], m.disp & 0xFFFFFFFF)
                    else:
                        kind, val = "slot", (("?",), m.disp & 0xFFFFFFFF)
                else:
                    r = ins.reg_name(op.reg)
                    s = st.get(r, ("?",))
                    if s[0] == "imm":
                        kind, val = "const", s[1]
                    elif s[0] == "ld":
                        kind, val = "gfn", s[1]
                    elif s[0] == "slotv":
                        kind, val = "slot", (s[1], s[2])
                    elif s[0] == "iat":
                        kind, val = "import", pe.imports[s[1]]
                        f["apis"].add(val)
                    else:
                        kind, val = "reg", s
                f["ind"].append((ins.address, mn, kind, val))
                f["iargs"][ins.address] = (ecx0, args)
            if mn == "call":
                st = {k: v for k, v in st.items() if k not in ("eax", "ecx", "edx")}
                if tgt_direct is not None:
                    st["eax"] = ("ret", tgt_direct)
                    if ecx0 is not None and ecx0[0] in ("ret", "?"):
                        for k in ("ebx", "esi", "edi", "ebp"):
                            if st.get(k) == ecx0:
                                st[k] = ("thisof", tgt_direct, ecx0)
                # esp after the call: callee pops ret N; unknown callees are assumed callee-pop (stdcall/thiscall)
                # and corrected by an `add esp, N` right after (cdecl)
                if delta is not None:
                    if tgt_direct in probes:  # _chkstk: esp -= eax
                        delta = delta - eax0[1] if eax0 and eax0[0] == "imm" else None
                    elif tgt_direct is not None and retn.get(tgt_direct) is not None:
                        delta += retn[tgt_direct]
                    else:
                        pending = 4 * len(pushes)
                        delta += pending
                pushes = []
            last_push = None
            continue
        if mn == "ret":
            f["rets"].add(st.get("eax") or ("?",))
            continue
        # ---- esp / ebp bookkeeping
        if len(ops) == 2 and ops[0].type == X.X86_OP_REG and ins.reg_name(ops[0].reg) == "esp":
            src = ops[1]
            if mn in ("add", "sub") and src.type == X.X86_OP_IMM and delta is not None:
                n = src.imm if mn == "add" else -src.imm
                delta += n - (was_pending if mn == "add" and n == was_pending else 0)
            elif mn == "mov" and src.type == X.X86_OP_REG and ins.reg_name(src.reg) == "ebp":
                delta = ebp_base
            elif mn == "lea" and src.type == X.X86_OP_MEM and ins.reg_name(src.mem.base) == "ebp" and ebp_base is not None:
                delta = ebp_base + src.mem.disp
            else:
                delta = None
            continue
        if mn == "leave":
            delta = None if ebp_base is None else ebp_base + 4
            continue
        if mn == "mov" and len(ops) == 2 and ops[0].type == X.X86_OP_REG and ins.reg_name(ops[0].reg) == "ebp" \
                and ops[1].type == X.X86_OP_REG and ins.reg_name(ops[1].reg) == "esp":
            ebp_base = delta
            st.pop("ebp", None)
            continue
        # ---- register tracking
        if mn == "push":
            o = ops[0] if ops else None
            s = None
            if o is not None and o.type == X.X86_OP_REG:
                s = st.get(ins.reg_name(o.reg))
                last_push = ("reg", s)
            elif o is not None and o.type == X.X86_OP_IMM:
                s = ("imm", o.imm & 0xFFFFFFFF)
                last_push = s
            else:
                last_push = None
                if o is not None and o.type == X.X86_OP_MEM and o.mem.index == 0 and o.mem.segment == 0:
                    b = ins.reg_name(o.mem.base) if o.mem.base else None
                    if b is None:
                        s = ("ld", o.mem.disp & 0xFFFFFFFF)
                    else:
                        s = argst(sref(b, o.mem.disp))
            pushes.append(s)
            if delta is not None:
                delta -= 4
            continue
        if mn == "pop":
            if delta is not None:
                delta += 4
            if pushes:
                pushes.pop()
        if len(ops) >= 1 and ops[0].type == X.X86_OP_REG and mn not in ("cmp", "test", "bt"):
            r = ins.reg_name(ops[0].reg)
            r = SUB.get(r, r)
            new = None
            if mn in ("mov", "lea") and len(ops) == 2 and r in GP:
                s = ops[1]
                if s.type == X.X86_OP_IMM:
                    new = ("imm", s.imm & 0xFFFFFFFF)
                elif s.type == X.X86_OP_REG:
                    new = st.get(ins.reg_name(s.reg))
                elif s.type == X.X86_OP_MEM and s.mem.segment == 0:
                    m = s.mem
                    base = ins.reg_name(m.base) if m.base else None
                    d = m.disp & 0xFFFFFFFF
                    so = sref(base, m.disp) if m.index == 0 and base in ("esp", "ebp") else None
                    if mn == "lea":
                        if base is None and m.index == 0:
                            new = ("imm", d)
                        elif so is not None:
                            new = ("obj", slots[so]) if so in slots else None
                        elif base in st and m.index == 0:
                            bs = st[base]
                            new = ("lea", bs, d)
                    elif base is None and m.index == 0:
                        new = ("iat", d) if d in pe.imports else ("ld", d)
                    elif so is not None:
                        new = argst(so)
                    elif base in GP and m.index == 0:
                        bs = st.get(base, ("?",))
                        if bs[0] == "imm":
                            new = ("ld", (bs[1] + d) & 0xFFFFFFFF)
                        elif d != 0 and bs[0] in TYPABLE and bs[0] not in ("ld", "fld") and fdepth(bs) < 2:
                            new = ("fld2", bs, d)
                        elif d == 0 and bs[0] in TYPABLE | {"?"}:
                            new = ("vtof", bs)
                        elif bs[0] == "vtof":
                            new = ("slotv", bs, d)
                        elif bs[0] == "vt":
                            new = ("slotv", ("vtof", ("obj", bs[1])), d)
                        elif bs[0] == "ld":
                            new = ("fld", bs[1], d)
            if r in GP:
                if new is None:
                    st.pop(r, None)
                else:
                    st[r] = new
        if mn in ("xchg", "pop", "cdq", "mul", "div", "imul", "idiv", "lodsd", "movsd", "rep movsd"):
            for r in ("eax", "edx") if mn in ("cdq", "mul", "div", "imul", "idiv") else ():
                st.pop(r, None)
            if mn in ("xchg", "pop"):
                for op in ops:
                    if op.type == X.X86_OP_REG:
                        st.pop(SUB.get(ins.reg_name(op.reg), ins.reg_name(op.reg)), None)
        if mn != "push":
            last_push = None
    return f


def lock_id(s):
    if not s:
        return "dyn"
    if s[0] == "imm":
        return NAMED_LOCKS.get(s[1], "0x%X" % s[1])
    if s[0] == "lea":
        b, d = s[1], s[2]
        if b[0] == "imm":
            a = (b[1] + d) & 0xFFFFFFFF
            return NAMED_LOCKS.get(a, "0x%X" % a)
        if b[0] == "ld":
            return "[0x%X]+0x%X" % (b[1], d)
        return "member+0x%X" % d
    if s[0] == "ld":
        return "*[0x%X]" % s[1]
    if s[0] == "fld":
        return "[0x%X]->+0x%X" % (s[1], s[2])
    return "member"


def cs_id(lp, st):
    if lp is None:
        return "cs:dyn"
    if lp[0] == "imm":
        return "cs:" + NAMED_LOCKS.get(lp[1], "0x%X" % lp[1])
    return "cs:" + lock_id(lp[1])


def build_index(pe, fpath):
    rows = list(csv.DictReader(open(fpath, encoding="utf-8")))
    # functions.csv `end` is the inclusive last byte (docs/102 §1): scan [entry, end + 1)
    funcs = sorted((int(r["entry"], 16), int(r["end"], 16) + 1) for r in rows)
    entries = [e for e, _ in funcs]
    eset = set(entries)
    md0 = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    retn, probes = {}, set()
    for e, en in funcs:
        if pe.is_code(e) and en > e:
            r, p = quick_facts(md0, pe, e, en)
            if r is not None:
                retn[e] = r
            if p:
                probes.add(e)
    print("  pre-pass: %d functions with ret, %d stack probes" % (len(retn), len(probes)), file=sys.stderr, flush=True)
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    md.detail = True
    for rnd in (1, 2):
        idx = {}
        for i, (e, en) in enumerate(funcs):
            if not pe.is_code(e) or en <= e:
                continue
            idx[e] = scan_function(md, pe, e, en, eset, retn, probes)
            idx[e]["end"] = en
            if i % 10000 == 0:
                print("  index round %d %d/%d" % (rnd, i, len(funcs)), file=sys.stderr, flush=True)
        if rnd == 2:
            break
        # vtable slots that point at code with no functions.csv entry (docs/131 §7 index gap, e.g. the script host
        # vtable 0x108B690): add them as functions ending at the next known entry, then index again
        extra = set()
        for v in {v for f in idx.values() for _, v in f["vtstores"]} | set(GETTERS.values()):
            a = v
            while len(extra) < 100000 and a < v + 0x800:
                d = pe.dword(a)
                if not d or not pe.is_code(d):
                    break
                if d not in eset:
                    extra.add(d)
                a += 4
        allents = sorted(eset | extra)
        for x in extra:
            nx = allents[bisect.bisect_right(allents, x)] if bisect.bisect_right(allents, x) < len(allents) else x + 0x100
            funcs.append((x, min(nx, x + 0x4000)))
            r, p = quick_facts(md0, pe, x, min(nx, x + 0x4000))
            if r is not None:
                retn[x] = r
        funcs.sort()
        eset |= extra
        print("  %d vtable-slot functions not in functions.csv added" % len(extra), file=sys.stderr, flush=True)
    return idx


# ------------------------------------------------------------------------------------------------- resolution
class Resolver:
    def __init__(self, pe, idx, prior):
        self.pe, self.idx, self.prior = pe, idx, prior
        self.entries = sorted(idx)
        eset = set(idx)
        self.gfn = collections.defaultdict(set)   # global -> function pointers stored there
        self.objvt = collections.defaultdict(set)  # global object -> vtables stored into it
        self.gstores = collections.defaultdict(list)  # global pointer -> (function, register state stored there)
        self.callers = collections.defaultdict(list)  # function -> (caller, ecx state, pushed argument states)
        self.taken = set()  # functions whose address appears as an operand (callbacks, stored pointers)
        self._memo, self._stack = {}, set()
        self.no_typing = False  # --no-typing: only receivers typed in the frame (docs/131 v1 behaviour)
        vtrefs = set()
        for e, f in idx.items():
            for (tgt, v) in f["fnstores"]:
                if tgt[0] == "g":
                    self.gfn[tgt[1]].add(v)
            for (tgt, v) in f["vtstores"]:
                vtrefs.add(v)
                if tgt[0] == "g":
                    self.objvt[tgt[1]].add(v)
            for (g, v) in f["objstores"]:
                self.gstores[g].append((e, v))
            for (t, ecx, args) in f["cargs"]:
                self.callers[t].append((e, ecx, args))
            self.taken |= f["fnrefs"]
        # vtables: referenced rdata arrays of function entries
        self.vt = {}
        vts = sorted(v for v in vtrefs | set(GETTERS.values()) if pe.dword(v) in eset)
        self.fstores = collections.defaultdict(list)  # field offset -> (function, owner state, value state)
        self.ffn = collections.defaultdict(list)      # field offset -> (function, owner state, function pointer)
        for e, f in idx.items():
            for (d, ow, v) in f.get("fstores", ()):
                self.fstores[d].append((e, ow, v))
            for (d, ow, v) in f.get("ffn", ()):
                self.ffn[d].append((e, ow, v))
        for i, v in enumerate(vts):
            cap = vts[i + 1] if i + 1 < len(vts) else v + 0x1000
            slots = []
            a = v
            while a < cap and len(slots) < 512:
                d = pe.dword(a)
                if d not in eset:
                    break
                slots.append(d)
                a += 4
            if slots:
                self.vt[v] = slots
        self.vt_items = list(self.vt.items())
        self.memb = collections.defaultdict(set)  # function -> vtables holding it in some slot
        for v, sl in self.vt_items:
            for x in sl:
                if x != PURECALL:
                    self.memb[x].add(v)
        self.taken |= set(self.memb)
        for t in self.gfn.values():
            self.taken |= t

    def gfn_targets(self, g):
        t = set(self.gfn.get(g, ()))
        d = self.pe.dword(g)
        if d in self.idx:
            t.add(d)
        return t

    def slot(self, vt, off):
        s = self.vt.get(vt)
        return s[off // 4] if s and off % 4 == 0 and off // 4 < len(s) else None

    # ---- caller-side receiver typing (docs/102 step 5): -> (vtables, complete)
    def types(self, fn, s, depth=0):
        """vtables the object in state s (inside function fn) can carry; complete = every source was typed"""
        if s is None or s[0] not in TYPABLE or (self.no_typing and s[0] not in ("obj", "objp", "imm", "ld")):
            return set(), False
        k = s[0]
        if k in ("obj", "objp"):
            return {s[1]}, True
        if k == "imm":
            v = set(self.objvt.get(s[1], ()))
            return v, bool(v)
        key = s if k in ("ld", "ret") else (fn, s)
        if key in self._memo:
            return self._memo[key]
        if key in self._stack:
            return set(), True  # recursion contributes nothing new
        if depth > 12:
            return set(), False
        self._stack.add(key)
        try:
            out, ok = set(), True
            if k == "ld":  # every typed register store into the global
                src = self.gstores.get(s[1], [])
                ok = bool(src)
                for (c, v) in src:
                    t, c_ok = self.types(c, v, depth + 1)
                    out |= t
                    ok = ok and c_ok
            elif k == "ret":  # every eax state at a ret of the callee (constructors return this)
                rets = self.idx.get(s[1], {}).get("rets", ())
                ok = bool(rets)
                for v in rets:
                    t, c_ok = self.types(s[1], v, depth + 1)
                    out |= t
                    ok = ok and c_ok
            elif k in ("fld", "fld2"):  # field of a typed owner: typed values stored at that offset into a matching owner
                owner = ("ld", s[1]) if k == "fld" else s[1]
                own, _ = self.types(fn, owner, depth + 1)
                for (c, ow, v) in self.fstores.get(s[2], ()) if own else ():
                    if self.types(c, ow, depth + 1)[0] & own:
                        out |= self.types(c, v, depth + 1)[0]
                ok = False  # stores we cannot see (copies, containers) may put other classes there
            elif k == "thisof":  # the vtable the callee leaves in its this (last store wins in a ctor)
                for v in self.idx.get(s[1], {}).get("rets", ()):
                    if v[0] == "obj":
                        out.add(v[1])
                ok = bool(out)
                if not out and len(s) > 2:  # not a constructor: the object keeps the type it had before the call
                    out, ok = self.types(fn, s[2], depth + 1)
            elif k == "this":
                out |= self.memb.get(fn, set())  # virtual callers: the receiver's vtable holds fn
                cs = self.callers.get(fn, [])
                ok = bool(cs) or fn in self.memb
                for (c, ecx, _) in cs:
                    t, c_ok = self.types(c, ecx, depth + 1)
                    out |= t
                    ok = ok and c_ok
            elif k == "arg":
                cs = self.callers.get(fn, [])
                ok = bool(cs) and fn not in self.taken  # an address-taken function has callers we cannot see
                for (c, _, args) in cs:
                    v = args[s[1]] if s[1] < len(args) else None
                    t, c_ok = self.types(c, v, depth + 1)
                    out |= t
                    ok = ok and c_ok
            r = (out, ok and bool(out))
        finally:
            self._stack.discard(key)
        self._memo[key] = r
        return r

    def resolve(self, fn, site, mn, kind, val, sig):
        """-> (status, basis, targets, api)"""
        if site in self.prior:
            st, tg = self.prior[site]
            return st, "docs/102 prior", tg, None
        if kind == "import":
            return "resolved", "import", set(), val
        if kind == "switch":
            return "resolved", "switch", set(), None
        if kind == "const":
            return ("resolved", "constant", {val}, None) if val in self.idx else ("unresolved", "constant?", set(), None)
        if kind == "gfn":
            t = self.gfn_targets(val)
            return ("resolved" if t else "unresolved", "global fn ptr 0x%X" % val, t, None)
        if kind == "slot":
            st, off = val
            if st[0] == "vtof":
                vts, complete = self.types(fn, st[1])
                t = {self.slot(v, off) for v in vts} - {None, PURECALL}
                if t:
                    how = "obj" if st[1][0] in ("obj", "objp", "imm") else "caller"
                    if complete:
                        return "resolved", "receiver typed (%s)" % how, t, None
                    return "partial", "receiver typed, some sources untyped (%s)" % how, t, None
                return self.sigfall(off, sig)
            if st[0] in ("imm",):  # call [A+off]: table entry in data
                a = (st[1] + off) & 0xFFFFFFFF
                t = self.gfn_targets(a)
                return ("resolved" if t else "unresolved", "table 0x%X" % a, t, None)
            if st[0] in TYPABLE:
                return self.fnfield(fn, st, off)
            return "unresolved", "unknown receiver", set(), None
        if kind == "reg" and val[0] in ("fld", "fld2"):
            return self.fnfield(fn, ("ld", val[1]) if val[0] == "fld" else val[1], val[2])
        return "unresolved", "register", set(), None

    def fnfield(self, fn, owner, off):
        """call through a function pointer field: pointers stored at that offset into an owner of a matching class"""
        own, _ = self.types(fn, owner)
        t = {v for (c, ow, v) in self.ffn.get(off, ()) if own and self.types(c, ow)[0] & own}
        if t:
            return "partial", "fn ptr field typed", t, None
        return "unresolved", "fn ptr field", set(), None

    def sigfall(self, off, sig):
        offs = sig or {off}
        mx = max(offs)
        t = set()
        for v, s in self.vt_items:
            if len(s) * 4 > mx and all(o % 4 == 0 for o in offs):
                x = s[off // 4]
                if x != PURECALL:
                    t.add(x)
                    if len(t) > 6:
                        return "unresolved", "slot signature (>6)", set(), None
        return ("partial", "slot signature", t, None) if t else ("unresolved", "slot signature (none)", set(), None)


def load_prior(path):
    out = {}
    if not os.path.exists(path):
        return out
    for r in csv.DictReader(open(path, encoding="utf-8")):
        if r["status"] not in ("resolved",) or r["kind"] == "import":
            continue
        t = {int(x, 16) for x in re.findall(r"0x[0-9A-Fa-f]{5,8}", r["targets"])}
        if t:
            out[int(r["site"], 16)] = ("resolved", t)
    return out


# ------------------------------------------------------------------------------------------------- roots
def load_names(path):
    n = {}
    for r in csv.DictReader(open(path, encoding="utf-8")):
        try:
            n[int(r["address"], 16)] = r["name"]
        except ValueError:
            pass
    return n


def ic_roots(repo):
    """(a): docs/121 v0 ICs + docs/126 tier-2 class-a ICs -> {addr: (name, tag)}"""
    roots = {}
    for r in csv.DictReader(open(os.path.join(repo, "docs/data/autonomy-proxy-ics.csv"), encoding="utf-8")):
        if r["address"].startswith("0x"):
            roots[int(r["address"], 16)] = (r["ic"].split("::")[-1], "a:v0/" + r["class"])
    by_name = collections.defaultdict(set)
    for r in csv.DictReader(open(os.path.join(repo, "docs/data/ic-safety.csv"), encoding="utf-8")):
        if r["address"].startswith("0x"):
            by_name[r["name"]].add(int(r["address"], 16))
    t2 = set()
    for r in csv.DictReader(open(os.path.join(repo, "docs/data/autonomy-tier2.csv"), encoding="utf-8")):
        if r["verdict"] == "blocked":
            continue
        for x in r["new_ics"].replace(";", " ").split():
            nm, _, cl = x.rpartition(":")
            if cl.startswith("a") or cl == "c":  # a, a-row/a-cut (docs/126 §4.1 snapshot rows), c
                t2.add(nm)
    missing = []
    for nm in sorted(t2):
        if not by_name.get(nm):
            missing.append(nm)
        for a in by_name.get(nm, ()):
            roots.setdefault(a, (nm, "a:tier2"))
    return roots, missing


def mono_callback_roots(idx, names):
    """(b): EA functions the Mono range calls directly"""
    out = {}
    for e, f in idx.items():
        if MONO_LO <= e < MONO_HI:
            for _, t in f["calls"]:
                if not (MONO_LO <= t < MONO_HI) and t in idx:
                    out.setdefault(t, set()).add(e)
    return {t: (names.get(t, "0x%X" % t), "b:mono->EA (%d Mono callers)" % len(c)) for t, c in out.items()}


# docs/data/races-handoff.md: mitigation of the 85 rows the address rules leave open, by group (R1-R4, R2a classes).
# Status in brackets: "built, not run" = native code behind [AutShadow] Races (default 0), no game run yet; "rule" = a
# design rule with no code; "static" = closed by a read of the bytes. Applied only where mitigation() finds nothing.
_R1 = "R1 pre-warm at world event 7 + guard witness, worker aborts while a guard is clear (Races bit 0, ledger §467) [built, not run]"
_R2 = "R2 load-time word, read-only after load: R2b witness at every cut (Races bit 1) + R2c worker drain at world events 2/3/4/5/8, gate until 7 (Races bit 2) [built, not run]"
_R2W = "R2 world-scope word replaced at load/travel: R2c worker drain at world events 2/3/4/5/8, gate until 7 (Races bit 2) + R2b witness (bit 1) [built, not run]"
_R3 = "R3 Boehm/script-host init word: M5 P3 GC lock and docs/120 rules (I-131-4) + R2b witness (Races bit 1) [built, not run]"
HANDOFF = {}
for _a in list(range(0x11EE54C, 0x11EE58C, 4)) + list(range(0x11D7508, 0x11D751C, 4)) + list(range(0x11DA048, 0x11DA05C, 4))         + [0x123437C, 0x1234380, 0x11DC168, 0x11DC16C, 0x11DC170, 0x11DC174, 0x11DC178, 0x11DC17C, 0x11DC180, 0x11DC184,
           0x11C7984, 0x11E96C0, 0x11E96C1, 0x11ECEEC, 0x11ECEED]:
    HANDOFF[_a] = _R1
HANDOFF[0x11C7E54] = "R1 same-value write (0x4F4F40 stores 0 over 0): benign, no pre-warm (ledger §467) [static]"
for _a in (0x11CB864, 0x11E9158, 0x11E9114, 0x11E8FA8, 0x11AF670, 0x11E8FC4, 0x11E90FC, 0x11C78D8, 0x11CDD00, 0x11CDD10,
           0x11CE2D0, 0x11D4BBC, 0x11D4BC0, 0x11D4BC8, 0x11ECF08, 0x11E8FC0, 0x11E90F8, 0x11E9290, 0x11E92A8, 0x11E94F8,
           0x11EB490, 0x11EBAE0):
    HANDOFF[_a] = _R2
for _a in (0x11ECBC4, 0x11ED034, 0x11CF490, 0x11D9128, 0x11DC350, 0x11D4068, 0x11D8888, 0x11D9848):
    HANDOFF[_a] = _R2W
HANDOFF[0x11D3E28] = "R2 owner not identified (swap-setter 0x72F1D0): R2b witness as load class + R2c drain (Races bits 1, 2); static owner still open [built, not run]"
HANDOFF[0x11D6A78] = "R2 runtime: callback table written only by the writing IC IC_World_RegisterCallbackDelegateImpl on the sim thread; worker reads only; R2b witness logs late registration [built, not run]"
HANDOFF[0x11ED780] = "R2 runtime: user-tool manager pointer set/cleared by script IC and dtor on the sim thread; R2b witness logs changes + R2c drain [built, not run]"
for _a in (0x114CA08, 0x114CA0C, 0x11CB928):
    HANDOFF[_a] = "R2 runtime-lazy: first fill by 0x4F98E0 (done byte 0x11CC928, never called by us): R1 guard witness, worker aborts while clear (Races bit 0) [built, not run]"
HANDOFF[0x11EE140] = "sim thread only: per-tick SSE scratch (0xD69A30, real race); IC_World_HasFloorFeaturesWorldPosImpl is an AO_CUT row filled at the cut, never pass-through; R2b witness logs it [rule]"
for _a in (0x11EE528, 0x11EE514, 0x11EE520, 0x11DC18C, 0x114C908, 0x11CC964):
    HANDOFF[_a] = _R3
HANDOFF[0x11CB920] = "R4 lazy GetACP() code page, same value from every thread: benign, no pin (R4a, ledger §461) [static]"

HOT_ROOTS = {0xD70A10, 0xD70A30, 0xD70A60, 0xD70A80, 0xD71860, 0xD71900, 0xD71A20, 0xD71B70, 0xD71800, 0xD70870,
             0xEA36F0, 0xEA30F0, 0xEA40B0, 0xEA3FE0, 0xEA3140}  # eglib heap, frame arena, interface offsets, managed allocs
WARM_C = {0xE5A600, 0xE59FD0, 0xE52B60, 0xEA1090, 0xEA1400, 0xE52D70, 0xE53E80, 0xEA2870}
RUNTIME_ROOTS = {
    0xE5A600: "worker entry (interp invoke frame)", 0xE59FD0: "interp_mono_invoke_with_state", 0xE52B60: "interp runtime-invoke entry",
    0xEA1090: "mono_runtime_invoke", 0xEA1400: "mono_runtime_delegate_invoke", 0xE6EE20: "mono_class_init",
    0xEA3370: "mono_runtime_class_init", 0xEA2AD0: "mono_class_vtable", 0xE82680: "mono_lookup_internal_call",
    0xE520E0: "EA bind helper (deferred class init)", 0xEA36F0: "mono_object_new", 0xEA30F0: "mono_array_new",
    0xEA40B0: "mono_string_new", 0xEA3FE0: "mono_string_new_utf16", 0xEA2870: "mono_ldstr", 0xEA3140: "mono_value_box",
    0xE770E0: "mono_get_exception_null_reference", 0xE772A0: "mono_get_exception_index_out_of_range",
    0xE771C0: "mono_get_exception_invalid_cast", 0xE76F00: "mono_get_exception_divide_by_zero", 0xE53740: "interp throw path",
    0xE52D70: "interp_alloc_task_state", 0xE53E80: "free task state",
}


# ------------------------------------------------------------------------------------------------- closure
def closure(idx, res, roots, depth, names):
    """BFS per root; returns hop map, roots per function, indirect sites per function, frontier"""
    hop, rootsof = {}, collections.defaultdict(set)
    sites = {}
    frontier = set()
    for r in roots:
        seen = {r: 0}
        q = collections.deque([r])
        while q:
            fn = q.popleft()
            h = seen[fn]
            f = idx.get(fn)
            if f is None:
                continue
            outs = [t for _, t in f["calls"]]
            if fn not in sites:
                sig = collections.defaultdict(set)
                for (_, _, kind, val) in f["ind"]:
                    if kind == "slot" and "'?'" not in repr(val[0]):  # unknown receivers do not share a signature
                        sig[repr(val[0])].add(val[1])
                sites[fn] = []
                for (a, mn, kind, val) in f["ind"]:
                    s = res.resolve(fn, a, mn, kind, val, sig.get(repr(val[0])) if kind == "slot" else None)
                    sites[fn].append((a, mn, kind) + s)
            for (_, _, _, status, _, tg, _) in sites[fn]:
                outs.extend(tg)
            for t in outs:
                if t not in idx:
                    continue
                if h + 1 > depth:
                    if t not in seen:
                        frontier.add(t)
                    continue
                if t not in seen:
                    seen[t] = h + 1
                    q.append(t)
        for fn, h in seen.items():
            hop[fn] = min(h, hop.get(fn, 99))
            rootsof[fn].add(r)
    frontier -= set(hop)
    return hop, rootsof, sites, frontier


def global_xrefs(idx):
    w, a = collections.defaultdict(set), collections.defaultdict(set)
    for e, f in idx.items():
        for g in f["gw"]:
            w[g].add(e)
        for g in f["gr"] | f["gw"]:
            a[g].add(e)
        for k in f["pw"]:
            w[k].add(e)
        for k in f["pr"] | f["pw"]:
            a[k].add(e)
    return w, a


def gname(k):
    return "[0x%X]->+0x%X" % k if isinstance(k, tuple) else "0x%X" % k


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=os.path.join(REPO, "game-copy", "bin", "TS3W.exe"))
    ap.add_argument("--functions", default=os.path.join(REPO, "game-copy", "analysis", "functions.csv"))
    ap.add_argument("--symbols", default=os.path.join(REPO, "tools", "ghidra", "symbols", "ts3w-1.67.symbols.csv"))
    ap.add_argument("--depth", type=int, default=5)
    ap.add_argument("--cache", default=None)
    ap.add_argument("--csv", default=os.path.join(REPO, "docs", "data", "mono-glue-closure.csv"))
    ap.add_argument("--races", default=os.path.join(REPO, "docs", "data", "mono-glue-races.csv"))
    ap.add_argument("--summary", default=None)
    ap.add_argument("--sites", default=None, help="also write one row per indirect site (docs/133: docs/data/mono-glue-sites.csv)")
    ap.add_argument("--self-check", action="store_true")
    ap.add_argument("--remitigate", action="store_true", help="only fill empty mitigations of --races from HANDOFF")
    ap.add_argument("--no-typing", action="store_true", help="no caller-side receiver typing (compare with docs/131 v1)")
    a = ap.parse_args()
    if a.self_check:
        return self_check()
    if a.remitigate:
        print("remitigated %d rows, %d still open" % remitigate(a.races))
        return 0
    pe = PE(a.exe)
    cache = os.path.join(a.cache, "mono-glue-index-v2.pkl") if a.cache else None
    if cache and os.path.exists(cache):
        idx = pickle.load(open(cache, "rb"))
    else:
        idx = build_index(pe, a.functions)
        if cache:
            pickle.dump(idx, open(cache, "wb"))
    names = load_names(a.symbols)
    res = Resolver(pe, idx, load_prior(os.path.join(REPO, "docs", "data", "routing-indirect-sites.csv")))
    res.no_typing = a.no_typing
    ra, missing = ic_roots(REPO)
    groups = {
        "a": {k: v for k, v in ra.items() if k in idx},
        "b": mono_callback_roots(idx, names),
        "c": {k: (v, "c:runtime") for k, v in RUNTIME_ROOTS.items() if k in idx},
    }
    out = []
    P = lambda *x: out.append(" ".join(str(y) for y in x))
    P("roots: a=%d b=%d c=%d; tier-2 names without an address: %s" % (len(groups["a"]), len(groups["b"]), len(groups["c"]), missing))
    per = {}
    for g, roots in groups.items():
        per[g] = closure(idx, res, roots, a.depth, names)
    allroots = {}
    for g in groups:
        for k, v in groups[g].items():
            allroots[k] = allroots.get(k, ()) + ((g,) + v,)
    hop, rootsof, sites, frontier = closure(idx, res, list(allroots), a.depth, names)
    gw, gacc = global_xrefs(idx)
    # must-held locks at entry, inside the worker closure (intersection over callers in the closure)
    direct = {fn: set(idx[fn]["locks"]) for fn in hop}
    callers = collections.defaultdict(set)
    for fn in hop:
        for _, t in idx[fn]["calls"]:
            if t in hop:
                callers[t].add(fn)
        for s in sites.get(fn, ()):
            for t in s[5]:
                if t in hop:
                    callers[t].add(fn)
    held = {fn: (set() if fn in allroots else None) for fn in hop}
    for _ in range(12):
        ch = False
        for fn in hop:
            if fn in allroots:
                continue
            acc = None
            for c in callers[fn]:
                hc = held.get(c)
                if hc is None:
                    continue
                v = hc | direct[c]
                acc = v if acc is None else acc & v
            if acc is not None and acc != held[fn]:
                held[fn] = acc
                ch = True
        if not ch:
            break
    held = {k: (v or set()) for k, v in held.items()}

    def cover(fn):
        return held.get(fn, set()) | direct.get(fn, set()) if fn in hop else set(idx[fn]["locks"])

    # ---- per-function facts and CSV
    def heap(fn):
        f = idx[fn]
        h = {HEAP_FUNCS[t] for _, t in f["calls"] if t in HEAP_FUNCS}
        h |= {x.split("!")[1] for x in f["apis"] if re.search(r"!(malloc|free|realloc|calloc|\?\?2@|\?\?3@|HeapAlloc|HeapFree|VirtualAlloc|VirtualFree|LocalAlloc|GlobalAlloc)", x)}
        if 0x11D4BC8 in f["gr"]:
            h.add("GeneralAllocator [0x11D4BC8]")
        return h

    def affinity(fn):
        f = idx[fn]
        t = set()
        for x in f["apis"]:
            dll, nm = x.split("!", 1)
            if THREAD_APIS.search(nm):
                t.add(nm)
            if dll.startswith(UI_DLLS):
                t.add("UI:" + nm)
            if dll.startswith(COM_DLLS) or nm.startswith("Co"):
                t.add("COM:" + nm)
            if dll.startswith("D3D"):
                t.add("D3D:" + nm)
        if f["fs"] - {0}:
            t.add("fs:" + ",".join("0x%X" % x for x in sorted(f["fs"] - {0})))
        if (f["gr"] | f["gw"]) & D3D_GLOBALS or any(c == 0x60E2C0 for _, c in f["calls"]):
            t.add("D3D device")
        return t

    def managed(fn):
        return {MANAGED_ENTRY[t] for _, t in idx[fn]["calls"] if t in MANAGED_ENTRY}

    def grp(fn):
        return "".join(sorted({g for g in "abc" if fn in per[g][0]}))

    def cap(xs, n=10):
        xs = sorted(xs)
        return ";".join(xs[:n]) + (";+%d" % (len(xs) - n) if len(xs) > n else "")

    rows = []
    for fn in sorted(hop):
        f = idx[fn]
        ss = sites.get(fn, [])
        rows.append({
            "function": "0x%X" % fn, "name": names.get(fn, ""), "hop": hop[fn], "groups": grp(fn),
            "roots": len(rootsof[fn]), "example_root": names.get(min(rootsof[fn]), "0x%X" % min(rootsof[fn])),
            "globals_read": cap({"0x%X" % g for g in f["gr"] if pe.is_writable(g)}),
            "globals_written": cap({"0x%X" % g for g in f["gw"] if pe.is_writable(g)}),
            "singleton_fields_written": cap({gname(k) for k in f["pw"] if pe.is_writable(k[0])}),
            "locks": cap(set(f["locks"]) | {"held:" + x for x in held.get(fn, ())}),
            "thread_affinity": cap(affinity(fn)), "heap": cap(heap(fn)), "managed_callbacks": cap(managed(fn)),
            "indirect_sites": len(ss), "indirect_unresolved": sum(1 for s in ss if s[3] == "unresolved"),
            "indirect_partial": sum(1 for s in ss if s[3] == "partial"),
        })
    with open(a.csv, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=list(rows[0]))
        w.writeheader()
        w.writerows(rows)
    if a.sites:  # docs/133: one row per indirect site, columns as docs/data/routing-indirect-sites.csv (indirect-live-vs-static.ts reads it)
        with open(a.sites, "w", newline="", encoding="utf-8") as fh:
            w = csv.writer(fh)
            w.writerow(["site", "caller", "kind", "slot offset", "targets", "status", "note"])
            for fn in sorted(hop):
                for (s, _, kind, status, basis, tg, api) in sorted(sites.get(fn, [])):
                    tgs = ";".join("0x%06X %s" % (t, names.get(t, "")) if names.get(t) else "0x%06X" % t for t in sorted(tg))
                    w.writerow(["0x%06X" % s, "0x%06X %s" % (fn, names.get(fn, "FUN_%x" % fn)), kind, "",
                                api or tgs, status, "[%s] | hop %d; groups %s" % (basis, hop[fn], grp(fn))])

    # ---- coverage per root group
    for g in "abc":
        h, ro, ss, fr = per[g]
        n = sum(len(ss.get(fn, ())) for fn in h)
        un = sum(1 for fn in h for s in ss.get(fn, ()) if s[3] == "unresolved")
        pa = sum(1 for fn in h for s in ss.get(fn, ()) if s[3] == "partial")
        byhop = collections.Counter(h.values())
        P("group %s: roots %d, functions %d (by hop %s), indirect sites %d, resolved %d, partial %d, unresolved %d (%.1f%%), hop-%d frontier %d"
          % (g, len(groups[g]), len(h), dict(sorted(byhop.items())), n, n - un - pa, pa, un, 100.0 * un / max(n, 1), a.depth + 1, len(fr)))
    n = sum(len(sites.get(fn, ())) for fn in hop)
    un = sum(1 for fn in hop for s in sites.get(fn, ()) if s[3] == "unresolved")
    pa = sum(1 for fn in hop for s in sites.get(fn, ()) if s[3] == "partial")
    P("all: functions %d, indirect sites %d, resolved %d, partial %d, unresolved %d (%.1f%%), frontier %d"
      % (len(hop), n, n - un - pa, pa, un, 100.0 * un / max(n, 1), len(frontier)))
    bas = collections.Counter(s[4].split(" 0x")[0] for fn in hop for s in sites.get(fn, ()))
    P("indirect by basis:", dict(bas.most_common()))
    # unresolved per root (top)
    ur = []
    for r in allroots:
        h1, _, s1, _ = closure(idx, res, [r], a.depth, names)
        n1 = sum(len(s1.get(fn, ())) for fn in h1)
        u1 = sum(1 for fn in h1 for s in s1.get(fn, ()) if s[3] == "unresolved")
        ur.append((r, len(h1), n1, u1))
    P("per root (root, name, group, functions, indirect, unresolved):")
    for r, nf, n1, u1 in sorted(ur, key=lambda x: (-x[3], x[0])):
        P("  0x%X %s %s %d %d %d" % (r, allroots[r][0][1], allroots[r][0][0], nf, n1, u1))

    # ---- facts across the closure
    lk = collections.Counter()
    for fn in hop:
        for l in idx[fn]["locks"]:
            lk[l] += 1
    P("locks taken in closure (lock: functions):", dict(lk.most_common(40)))
    af = collections.Counter()
    for fn in hop:
        for t in affinity(fn):
            af[t] += 1
    P("thread affinity (fact: functions):", dict(af.most_common(40)))
    mg = [(fn, managed(fn)) for fn in hop if managed(fn)]
    P("managed callbacks (%d functions):" % len(mg), ["0x%X %s %s g=%s" % (fn, names.get(fn, ""), sorted(m), grp(fn)) for fn, m in mg][:40])
    hp = collections.Counter()
    for fn in hop:
        for x in heap(fn):
            hp[x] += 1
    P("heap use (allocator: functions):", dict(hp.most_common(30)))
    for t, nm in ((0xD70A10, "g_malloc"), (0xD70A60, "g_free"), (0x4E69D0, "GeneralAllocator_Malloc"), (0xD71900, "frame arena")):
        h1, _, _, _ = closure(idx, res, [t], 3, names)
        P("lock under %s (3 hops):" % nm, sorted({l for fn in h1 for l in idx[fn]["locks"]}), "apis:",
          sorted({x for fn in h1 for x in idx[fn]["apis"] if x.split("!")[1] in LOCK_APIS or "Critical" in x}))

    # ---- race set
    def rweight(r):
        """how often a root runs on the worker: per allocation/virtual call, per invoke, per search, warm-up only"""
        g = allroots[r][0][0]
        if r in HOT_ROOTS:
            return 1000
        if g == "c":
            return 100 if r in WARM_C else (10 if "exception" in RUNTIME_ROOTS[r] else 1)
        return 10 if g == "a" else 1

    def mitigation(k, wacc):
        if k in (0x11F4170, 0x11F416C):
            return "P4 per-thread frame globals (docs/30 §4)"
        if all(0xE48E80 <= fn < 0xE51400 for fn in wacc):
            return "Boehm: M5 P3 GC lock (docs/30 §3, docs/120)"
        if k == 0x11EE5A4:
            return "native-to-managed record cursor: F1 N2mPrivate (docs/120 §9.4)"
        if all(0xD71800 <= fn < 0xD71C00 for fn in wacc):
            return "frame arena: P3 lock + ArenaFix (docs/120 §9.1-9.2)"
        if all(MONO_LO <= fn < MONO_HI for fn in wacc):
            return "Mono runtime: docs/120 §3.3 rules (warm-up, no GCHandle/reflection)"
        return HANDOFF.get(k, "") if not isinstance(k, tuple) else ""

    races = []
    for k in set(gacc):
        G = k[0] if isinstance(k, tuple) else k
        if not pe.is_writable(G):
            continue
        wacc = [fn for fn in gacc[k] if fn in hop]
        if not wacc:
            continue
        wwr = [fn for fn in wacc if fn in gw.get(k, ())]
        allw = gw.get(k, set())
        if not allw:
            continue  # never written by code: load-time data
        if wwr:
            other = gacc[k]  # sim side may touch it through any function, including the same ones
            kind = "worker writes"
        else:
            other = allw
            kind = "worker reads, sim writes"
        common = None
        for fn in set(wacc) | set(other):
            c = cover(fn)
            common = c if common is None else common & c
            if not common:
                break
        if common:
            continue
        atomic = all(k in idx[fn]["atomicw"] for fn in allw) if not isinstance(k, tuple) else False
        gs = set()
        for fn in wacc:
            gs |= set(grp(fn))
        w = max(rweight(r) for fn in wacc for r in rootsof[fn])
        score = w * (2 if wwr else 1) * len(wacc)
        ex = min(wacc, key=lambda fn: hop[fn])
        races.append({
            "global": gname(k), "kind": kind + (" (atomic)" if atomic else ""), "groups": "".join(sorted(gs)),
            "root_rate": {1000: "per alloc/call", 100: "per invoke", 10: "per search/exception", 1: "warm-up/rare"}[w],
            "mitigation": mitigation(k, wacc),
            "worker_functions": len(wacc), "worker_writers": len(wwr), "exe_writers": len(allw), "exe_accessors": len(gacc[k]),
            "example_worker_fn": "0x%X %s" % (ex, names.get(ex, "")), "example_root": names.get(min(rootsof[ex]), "0x%X" % min(rootsof[ex])),
            "min_hop": hop[ex], "score": int(score),
        })
    races.sort(key=lambda r: (bool(r["mitigation"]), -r["score"], r["global"]))
    fields = carry_columns(a.races, races)
    with open(a.races, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=fields)
        w.writeheader()
        w.writerows(races)
    kinds = collections.Counter(r["kind"] for r in races)
    P("race set: %d entries %s; by group %s" % (len(races), dict(kinds), dict(collections.Counter(r["groups"] for r in races))))
    rb = [r for r in races if "b" in r["groups"] or "c" in r["groups"]]
    P("race set reached from b or c (runs whatever the IC mode): %d" % len(rb))
    P("mitigations:", dict(collections.Counter(r["mitigation"] or "OPEN" for r in races)))
    P("open by rate:", dict(collections.Counter(r["root_rate"] for r in races if not r["mitigation"])),
      "open from b/c:", sum(1 for r in races if not r["mitigation"] and set(r["groups"]) & {"b", "c"}))
    for r in races[:40]:
        P("  ", r)
    text = "\n".join(out)
    print(text)
    if a.summary:
        open(a.summary, "w", encoding="utf-8").write(text + "\n")


def remitigate(path):
    """docs/data/races-handoff.md: fill the empty mitigation cells of a races csv from HANDOFF (by plain global); returns
    (filled, still open)"""
    with open(path, newline="", encoding="utf-8") as fh:
        rd = csv.DictReader(fh)
        fields, rows = rd.fieldnames, list(rd)
    n = 0
    for r in rows:
        g = r["global"]
        if not r["mitigation"] and g.startswith("0x") and HANDOFF.get(int(g, 16)):
            r["mitigation"], n = HANDOFF[int(g, 16)], n + 1
    rows.sort(key=lambda r: (bool(r["mitigation"]), -int(r["score"] or 0), r["global"]))
    with open(path, "w", newline="", encoding="utf-8") as fh:
        w = csv.DictWriter(fh, fieldnames=fields)
        w.writeheader()
        w.writerows(rows)
    return n, sum(1 for r in rows if not r["mitigation"])


def carry_columns(path, races):
    """Keep hand-added columns (e.g. r2a_*, races-handoff R2a) of an existing races csv: copied by global, appended after
    the tool's own columns; returns the field list"""
    own = list(races[0])
    if not os.path.exists(path):
        return own
    with open(path, newline="", encoding="utf-8") as fh:
        old = list(csv.DictReader(fh))
    extra = [c for c in (old[0] if old else {}) if c not in own]
    by = {r["global"]: r for r in old}
    for r in races:
        for c in extra:
            r[c] = by.get(r["global"], {}).get(c, "")
    return own + extra


def self_check():
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    md.detail = True

    class FakePE:
        imports = {0x2000: "KERNEL32!EnterCriticalSection"}

        def is_data(self, a): return 0x2000 <= a < 0x9000

        def is_writable(self, a): return 0x3000 <= a < 0x9000

        def is_code(self, a): return 0x1000 <= a < 0x2000
    # mov eax,[0x3000]; mov [eax+8],1; push 0x3010; call [0x2000]; mov ecx,[0x3004]; mov edx,[ecx]; call [edx+0xC]; ret
    code = bytes.fromhex("a100300000" "c7400801000000" "6810300000" "ff1500200000" "8b0d04300000" "8b11" "ff520c" "c3")

    class P2(FakePE):
        def read(self, a, n): return code[a - 0x1000:a - 0x1000 + n]
    f = scan_function(md, P2(), 0x1000, 0x1000 + len(code), set())
    assert 0x3000 in f["gr"], f
    assert (0x3000, 8) in f["pw"], f
    assert "cs:0x3010" in f["locks"], f
    assert any(k == "slot" and v[0] == ("vtof", ("ld", 0x3004)) and v[1] == 0xC for _, _, k, v in f["ind"]), f["ind"]

    # caller-side receiver typing: F news an object, runs ctor C (vtable 0x2800 = [0x1500, 0x1600]) on it, passes it
    # as stack argument 0 to G2 (`ret 4`) and as `this` to G; both dispatch on it without seeing the vtable store
    def rel(at, t): return struct.pack("<i", t - (at + 5)).hex()
    fns = {
        0x1100: "89c8" "c70000280000" "c3",                     # C: mov eax,ecx; mov [eax],0x2800; ret
        0x1300: "31c0" "c3",                                    # new: xor eax,eax; ret
        0x1400: "8b01" "ff5004" "c3",                           # G: mov eax,[ecx]; call [eax+4]; ret
        0x1500: "c3", 0x1600: "c3",
        0x1700: "8b4c2404" "8b01" "ff10" "c20400",              # G2: mov ecx,[esp+4]; mov eax,[ecx]; call [eax]; ret 4
    }
    # F: push 0x10; call new; add esp,4; mov esi,eax; mov ecx,esi; call C; push esi; call G2; mov ecx,esi; call G; ret
    F = "6a10" + "e8" + rel(0x1202, 0x1300) + "83c404" + "89c6" + "89f1" + "e8" + rel(0x120E, 0x1100) \
        + "56" + "e8" + rel(0x1214, 0x1700) + "89f1" + "e8" + rel(0x121B, 0x1400) + "c3"
    fns[0x1200] = F
    mem = {a: bytes.fromhex(h) for a, h in fns.items()}

    class P3(FakePE):
        def read(self, a, n):
            for b, c in mem.items():
                if b <= a < b + len(c):
                    return c[a - b:a - b + n]
            return b""

        def dword(self, a): return {0x2800: 0x1500, 0x2804: 0x1600}.get(a)
    pe3 = P3()
    eset = set(mem)
    md0 = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    retn = {e: quick_facts(md0, pe3, e, e + len(c))[0] for e, c in mem.items()}
    assert retn[0x1700] == 4, retn
    idx = {e: scan_function(md, pe3, e, e + len(c), eset, retn) for e, c in mem.items()}
    assert any(t == 0x1700 and a and a[0][:2] == ("thisof", 0x1100) for t, _, a in idx[0x1200]["cargs"]), idx[0x1200]["cargs"]
    res = Resolver(pe3, idx, {})
    for g, want in ((0x1400, {0x1600}), (0x1700, {0x1500})):
        (a1, mn1, k1, v1), = idx[g]["ind"]
        r = res.resolve(g, a1, mn1, k1, v1, None)
        assert r[0] == "resolved" and r[2] == want, (hex(g), r)
    import tempfile
    with tempfile.TemporaryDirectory() as td:
        fp = os.path.join(td, "races.csv")
        with open(fp, "w", newline="", encoding="utf-8") as fh:
            fh.write("global,kind,r2a_class\n0x10,old,load\n0x30,old,runtime\n")
        rs = [{"global": "0x10", "kind": "new"}, {"global": "0x20", "kind": "new"}]
        assert carry_columns(fp, rs) == ["global", "kind", "r2a_class"]
        assert rs[0]["r2a_class"] == "load" and rs[1]["r2a_class"] == "" and rs[0]["kind"] == "new", rs
        assert carry_columns(os.path.join(td, "none.csv"), [{"global": "0x10"}]) == ["global"]
        with open(fp, "w", newline="", encoding="utf-8") as fh:
            fh.write("global,mitigation,score,r2a_class\n0x11EE140,,5,runtime\n0x99,,9,\n[0x11ECBC4]->+0x1B4,,1,\n0x11D4BC8,Boehm,7,load\n")
        assert remitigate(fp) == (1, 2)
        with open(fp, newline="", encoding="utf-8") as fh:
            rr = list(csv.DictReader(fh))
        assert [r["global"] for r in rr] == ["0x99", "[0x11ECBC4]->+0x1B4", "0x11D4BC8", "0x11EE140"], rr
        assert rr[3]["mitigation"].startswith("sim thread only") and rr[3]["r2a_class"] == "runtime" and rr[2]["mitigation"] == "Boehm"
    # races handoff: 41 R1 rows plus 0x11DC17C (not in the v2 csv), 37 R2 (0x11EE140 tagged "sim thread only"), 6 R3, 1 R4
    by = collections.Counter(v.split(" ")[0] for v in HANDOFF.values())
    assert by == {"R1": 42, "R2": 36, "R3": 6, "R4": 1, "sim": 1} and HANDOFF[0x11EE140].startswith("sim thread only"), by
    print("mono-glue-closure self-check ok")


if __name__ == "__main__":
    sys.exit(main())
