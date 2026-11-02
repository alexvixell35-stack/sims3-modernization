"""Lifted mint handlers: EA's own handler bytes as compiled-code templates, checked against the handler (round 8 B5).

For every "generatable" opcode of docs/507 §b (no template in P0, no IL-level call or allocation, no managed throw), the
handler fragment of the embedded interpreter is read from TS3W.exe and rewritten as a template the docs/207 compiler can
place in a method body:
  - reads of the operand words through the ip (`movzx ecx, word ptr [edi + 2]`) become `mov ecx, imm32` slots the compiler
    fills from the instruction's operands;
  - the ip advance (`add edi, n`), the ip store (`mov [ebp-4], edi`) and the dispatch tail (`jmp 0xE544D5`) go;
  - a taken branch (`lea edi, [edi + ecx*2]`) becomes a "taken" slot, the fall-through tail a "next" slot;
  - a jump to an exception stub becomes an "exit" slot (P0's exit to EA's handler) after the eval-stack pointer is put
    back, so the re-run handler sees the instruction's own state;
  - calls into the CRT (memcpy, _ftol2, _allmul, ...) are relocated; a handler that writes ebx (the compiler keeps the
    argument arena there) is wrapped in push / pop ebx;
  - everything else keeps its bytes, so the x87 code and every rounding rule stay EA's.
A handler the rules cannot express (edi used otherwise, the switch table, a class lookup, a store before an exception
edge) is reported with its reason and left to a hand template.

The check: EA's handler and the lifted template (in the handler's own calling convention: edi = ip, esi = eval top,
ebp = the interpreter's C frame) run on the same mint frame under Unicorn and the state the next instruction sees is
compared, as docs/221 does for the float opcodes: eval slots, esi, locals, arguments, stack arguments, the pointer
target, the vt stack and its pointer, the item table, `this`, the branch decision, the x87 top and control word, and
which exit was taken.

  python tools/emu/run.py tools/emu/specs/mint_lift.py          # lifted templates vs EA: must be 0 mismatches
  python tools/emu/specs/mint_lift.py --write [--exe PATH]      # also writes the templates (local, git-ignored) + the census

The templates file holds EA's handler bytes, so it is written under tools/emu/specs/out/ (git-ignored by the repo's `out/`
rule) and never committed; docs/data/jit-lift-census.csv (counts and verdicts per opcode, no bytes) is the committed record.
"""
import csv
import json
import math
import os
import random
import struct
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(os.path.dirname(os.path.dirname(HERE)))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.dirname(HERE))

NAME = 'lifted mint handlers vs EA'
CONV = 'mint frame (edi = ip, esi = eval top, ebp = interpreter frame)'
DISPATCH = 0xE544D5
NULL_EXC, RANGE_EXC, OVF_EXC, CKF_EXC = 0xE5947E, 0xE5949D, 0xE57AA5, 0xE5952A
EXITS = {DISPATCH: 'next', NULL_EXC: 'NullReference', RANGE_EXC: 'IndexOutOfRange', OVF_EXC: 'Overflow', CKF_EXC: 'NotFinite'}
INTERP = (0xE54000, 0xE5A000)
OPLEN = 0x1195088          # bytes: words per opcode
FTOL2 = 0xF7EC76
SSE2_FLAG = 0x1234374
TPL_BASE, TPL_SIZE = 0x29000000, 0x10000
CASES = 12                 # random cases per opcode on top of the edge set
CW_DEFAULT = 0x027F
COVERAGE_TARGET = 0.0      # one handler is the coverage target; the pass rule is the mismatch count
NOT_RUN = {'rem.r8': 'calls the CRT _CIfmod (an x87 fprem loop this emulator does not model; docs/221 skips it too)',
           'stobj.vt': 'the value size comes from a MonoClass lookup (0xE6F5A0): no class model in the frame',
           'switch': 'reads its case table through the ip with an index (P0 compiles switch as an exit)'}
REG8 = {'eax': 0, 'ecx': 1, 'edx': 2, 'ebx': 3, 'esp': 4, 'ebp': 5, 'esi': 6, 'edi': 7}


def _csv(p):
    with open(os.path.join(REPO, 'docs', 'data', p), encoding='utf-8') as f:
        return list(csv.DictReader(f))


_OPS = {r['name']: (int(r['opcode'], 16), int(r['handler'], 16)) for r in _csv('mint-opcodes.csv')}
_SPEC = {r['name']: r for r in _csv('mint-opcode-spec.csv')}
GENERATABLE = [r['name'] for r in _csv('jit-opcode-table.csv') if r['class'] == 'generatable']


# ---------------------------------------------------------------- the lifter
class Item:
    __slots__ = ('role', 'addr', 'raw', 'target', 'reg', 'operand', 'esi', 'note')

    def __init__(self, role, addr, raw=b'', target=None, reg=None, operand=None, esi=0, note=''):
        self.role, self.addr, self.raw, self.target, self.reg, self.operand, self.esi, self.note = role, addr, raw, target, reg, operand, esi, note


class Lifted:
    def __init__(self, name):
        self.name = name
        self.opcode, self.handler = _OPS[name]
        self.words = 0
        self.items = []          # Items in layout order; labels are addresses
        self.labels = set()      # addresses that are jump targets
        self.calls = set()
        self.clobber_ebx = False
        self.uses = set()
        self.reason = None       # None = lifted
        self.exc_after_effect = False
        self.operands = {}       # operand offset -> (size, ext)

    @property
    def status(self):
        return 'lifted' if self.reason is None else 'hand'


def _mem(ins, i, cs):
    o = ins.operands[i]
    return o if o.type == cs.x86_const.X86_OP_MEM else None


def lift(emu, name):
    """Walk the handler fragment from its entry with a symbolic ip offset and classify every instruction."""
    import capstone as cs
    from capstone import x86_const as X
    L = Lifted(name)
    L.words = emu.read(OPLEN + L.opcode, 1)[0]
    if name in NOT_RUN:
        L.reason = NOT_RUN[name]
        return L
    md = emu.md
    REG = lambda r: ins.reg_name(r)
    # state per address: (ip delta, esi delta, side effect seen, reg -> operand offset)
    work = [(L.handler, 0, 0, False, {})]
    seen = {}
    order = []
    while work and L.reason is None:
        a, delta, esid, eff, regs = work.pop()
        while True:
            if a in seen:
                if (seen[a][0] != delta and None not in (seen[a][0], delta)) or seen[a][1] != esid:
                    L.reason = f'ip or esi offset differs at a join ({a:#x})'
                break
            if a in EXITS or not (INTERP[0] <= a < INTERP[1]):
                L.reason = f'walk left the interpreter at {a:#x}'
                break
            ins = next(md.disasm(emu.read(a, 16), a, 1), None)
            if ins is None:
                L.reason = f'undecodable byte at {a:#x}'
                break
            seen[a] = (delta, esid, eff, dict(regs))
            order.append(a)
            raw = bytes(ins.bytes)
            rd, wr = ins.regs_access()
            rdn, wrn = {REG(r) for r in rd}, {REG(r) for r in wr}
            m0 = ins.operands[0] if ins.operands else None
            m1 = ins.operands[1] if len(ins.operands) > 1 else None
            nxt = a + ins.size
            it = None
            # ---- ip arithmetic and operand reads
            if ins.mnemonic in ('add', 'sub') and m0.type == X.X86_OP_REG and REG(m0.reg) == 'edi' and m1.type == X.X86_OP_IMM:
                if delta is not None:
                    delta += m1.imm if ins.mnemonic == 'add' else -m1.imm
                it = Item('ipadv', a, raw)
            elif ins.mnemonic == 'lea' and REG(m0.reg) == 'edi' and m1.mem.base and REG(m1.mem.base) == 'edi':
                if m1.mem.index == 0:
                    if delta is not None:
                        delta += m1.mem.disp
                    it = Item('ipadv', a, raw)
                elif m1.mem.scale == 2 and REG(m1.mem.index) in regs and m1.mem.disp == 0:
                    it = Item('taken', a, raw, reg=REG(m1.mem.index), operand=regs[REG(m1.mem.index)])
                    delta = None   # ip now depends on the operand; the tail that follows is the taken path's
                else:
                    L.reason = f'edi computed from a register at {a:#x}: {ins.mnemonic} {ins.op_str}'
            elif ins.mnemonic == 'mov' and m0.type == X.X86_OP_MEM and m0.mem.base and REG(m0.mem.base) == 'ebp' and m0.mem.disp == -4 and m1.type == X.X86_OP_REG and REG(m1.reg) == 'edi':
                it = Item('ipstore', a, raw)
            elif m1 is not None and m1.type == X.X86_OP_MEM and m1.mem.base and REG(m1.mem.base) == 'edi':
                if m1.mem.index != 0 or m0.type != X.X86_OP_REG or m0.size != 4 or ins.mnemonic not in ('mov', 'movzx', 'movsx'):
                    L.reason = f'operand read the rules do not cover at {a:#x}: {ins.mnemonic} {ins.op_str}'
                elif delta is None:
                    L.reason = f'operand read after the ip became operand-dependent at {a:#x}'
                else:
                    off = delta + m1.mem.disp
                    ext = 'sx' if ins.mnemonic == 'movsx' else 'zx' if ins.mnemonic == 'movzx' else 'mov'
                    regs = dict(regs)
                    regs[REG(m0.reg)] = off
                    L.operands[off] = (m1.size, ext)
                    it = Item('imm', a, raw, reg=REG(m0.reg), operand=(off, m1.size, ext))
            elif 'edi' in rdn or 'edi' in wrn:
                L.reason = f'edi used at {a:#x}: {ins.mnemonic} {ins.op_str}'
            # ---- control flow
            elif ins.mnemonic == 'call':
                if m0.type == X.X86_OP_IMM:
                    L.calls.add(m0.imm & 0xFFFFFFFF)
                    it = Item('call', a, raw, target=m0.imm & 0xFFFFFFFF)
                else:
                    it = Item('keep', a, raw, note='call through the import table')
                    L.uses.add('import call')
                eff = True
            elif X.X86_GRP_JUMP in ins.groups:
                t = m0.imm & 0xFFFFFFFF if m0.type == X.X86_OP_IMM else None
                if t is None:
                    L.reason = f'indirect jump at {a:#x}'
                elif t == DISPATCH:
                    it = Item('tail', a, raw, note=delta)
                    if delta is not None and delta != 2 * L.words:
                        L.reason = f'the handler advances ip by {delta} bytes on the fall-through, the instruction is {2 * L.words} bytes (EA bug, docs/213 §4): the compiler form would mis-place the next instruction'
                elif t in EXITS:
                    it = Item('exc', a, raw, target=t, esi=esid)
                    if eff:
                        L.exc_after_effect = True
                else:
                    it = Item('jcc' if ins.mnemonic != 'jmp' else 'jmp', a, raw, target=t)
                    L.labels.add(t)
                    work.append((t, delta, esid, eff, dict(regs)))
                if ins.mnemonic in ('jmp',) or t == DISPATCH or (t in EXITS and ins.mnemonic == 'jmp'):
                    L.items.append(it)
                    break
            elif ins.mnemonic in ('ret', 'int3'):
                L.reason = f'{ins.mnemonic} at {a:#x}'
            if L.reason:
                break
            if it is None:
                it = Item('keep', a, raw)
                if ins.mnemonic in ('add', 'sub') and m0.type == X.X86_OP_REG and REG(m0.reg) == 'esi' and m1.type == X.X86_OP_IMM:
                    esid += m1.imm if ins.mnemonic == 'add' else -m1.imm
                elif 'esi' in wrn:
                    L.reason = f'esi written otherwise at {a:#x}: {ins.mnemonic} {ins.op_str}'
                if any(o.type == X.X86_OP_MEM and (o.access & cs.CS_AC_WRITE) for o in ins.operands) or ins.mnemonic in ('push', 'fstp', 'fst', 'fistp', 'fist', 'rep', 'movsd', 'stosd'):
                    if not (ins.mnemonic == 'push'):
                        eff = True
                for r in ('ebp', 'esp'):
                    if r in wrn and ins.mnemonic not in ('push', 'pop', 'call', 'add', 'sub'):
                        L.reason = f'{r} written at {a:#x}'
            if 'ebx' in wrn:
                L.clobber_ebx = True
            if m1 is not None and m1.type == X.X86_OP_MEM and m1.mem.base and REG(m1.mem.base) == 'ebp':
                L.uses.add({-0xC: 'vt_sp', -0x8: 'invocation', -0x10: 'runtime method', -0x14: 'locals'}.get(m1.mem.disp, f'frame{m1.mem.disp:+#x}'))
            L.items.append(it)
            a = nxt
    if L.reason is None and not any(i.role in ('tail', 'taken') for i in L.items):
        L.reason = 'no dispatch tail found'
    if L.reason is None:
        # layout: entry block first (the walk order), then the other blocks by address; every address visited once
        pos = {it.addr: k for k, it in enumerate(L.items)}
        first = [it for it in L.items if it.addr >= L.handler]
        rest = [it for it in L.items if it.addr < L.handler]
        L.items = sorted(first, key=lambda it: it.addr) + sorted(rest, key=lambda it: it.addr)
        # a block that ends without a jump must be followed by its successor address (fall-through)
        for k, it in enumerate(L.items[:-1]):
            if it.role not in ('tail', 'jmp', 'exc', 'taken') and L.items[k + 1].addr != it.addr + len(it.raw):
                if it.role == 'taken':
                    continue
                L.reason = f'fall-through broken by the layout at {it.addr:#x}'
                break
    return L


def _imm_bytes(reg, value):
    return bytes([0xB8 + REG8[reg]]) + struct.pack('<I', value & 0xFFFFFFFF)


def _operand_value(code, off, size, ext):
    v = int.from_bytes(code[off:off + size], 'little', signed=(ext == 'sx'))
    return v & 0xFFFFFFFF


def render(L, mode, code=None, base=TPL_BASE):
    """Bytes of the template. mode 'emu': the handler's convention (ip advance, ip store and dispatch kept) with the
    operands of `code` (the instruction bytes) as immediates; mode 'p0': the compiler's form, slots left zero.
    Returns (bytes, slots): slots = [{at, kind, ...}] (p0) or [] (emu)."""
    wrap = mode == 'p0' and L.clobber_ebx
    # in the compiler form the dispatch tail after a taken branch is that path's own end: nothing to emit
    dead = set()
    for k, it in enumerate(L.items[:-1]):
        if it.role == 'taken' and mode == 'p0':
            for j in range(k + 1, len(L.items)):
                if L.items[j].role in ('ipstore', 'ipadv'):
                    continue
                if L.items[j].role == 'tail':
                    dead.add(L.items[j].addr)
                break
    cond = lambda it: len(it.raw) == 2 or it.raw[0] == 0x0F   # a conditional jump to the exception stub

    def size_of(it):
        if it.role == 'imm':
            return 5
        if it.role in ('ipadv', 'ipstore'):
            return len(it.raw) if mode == 'emu' else 0
        if it.role == 'tail':
            return 5 if mode == 'emu' else 0 if it.addr in dead else (1 if wrap else 0) + 5
        if it.role == 'taken':
            return len(it.raw) if mode == 'emu' else (1 if wrap else 0) + 5
        if it.role == 'exc':
            if mode == 'emu':
                return 6 if cond(it) else 5
            return (2 if cond(it) else 0) + (1 if wrap else 0) + (6 if it.esi else 0) + 5
        if it.role in ('jcc',):
            return 6
        if it.role in ('jmp', 'call'):
            return 5
        return len(it.raw)
    off = 1 if wrap else 0
    at = {}
    for it in L.items:
        at[it.addr] = off
        off += size_of(it)
    total = off
    out = bytearray(b'\x53' if wrap else b'')
    slots = []
    for it in L.items:
        here = len(out)
        assert here == at[it.addr], (L.name, it.addr)
        if it.role == 'imm':
            o, size, ext = it.operand
            v = _operand_value(code, o, size, ext) if mode == 'emu' else 0
            out += _imm_bytes(it.reg, v)
            if mode == 'p0':
                slots.append({'at': here + 1, 'kind': 'imm', 'reg': it.reg, 'operand': o, 'size': size, 'ext': ext})
        elif it.role in ('ipadv', 'ipstore'):
            if mode == 'emu':
                out += it.raw
        elif it.role == 'tail':
            if mode == 'emu':
                out += b'\xE9' + struct.pack('<i', DISPATCH - (base + here + 5))
            elif it.addr not in dead:
                if wrap:
                    out += b'\x5B'
                slots.append({'at': len(out) + 1, 'kind': 'next'})
                out += b'\xE9\x00\x00\x00\x00'
        elif it.role == 'taken':
            if mode == 'emu':
                out += it.raw
            else:
                if wrap:
                    out += b'\x5B'
                slots.append({'at': len(out) + 1, 'kind': 'taken', 'operand': it.operand})
                out += b'\xE9\x00\x00\x00\x00'
        elif it.role == 'exc':
            cc = (it.raw[1] & 0x0F if it.raw[0] == 0x0F else it.raw[0] & 0x0F) if cond(it) else None
            if mode == 'emu':
                if cc is None:
                    out += b'\xE9' + struct.pack('<i', it.target - (base + here + 5))
                else:
                    out += bytes([0x0F, 0x80 | cc]) + struct.pack('<i', it.target - (base + here + 6))
            else:
                body = (b'\x5B' if wrap else b'') + (b'\x81\xC6' + struct.pack('<i', -it.esi) if it.esi else b'')   # pop ebx; add esi, -delta
                if cc is not None:
                    out += bytes([0x70 | (cc ^ 1), len(body) + 5])   # the inverted condition skips the exit
                out += body
                slots.append({'at': len(out) + 1, 'kind': 'exit', 'exit': EXITS[it.target]})
                out += b'\xE9\x00\x00\x00\x00'
        elif it.role == 'jcc':
            cc = it.raw[1] & 0x0F if it.raw[0] == 0x0F else it.raw[0] & 0x0F
            out += bytes([0x0F, 0x80 | cc]) + struct.pack('<i', at[it.target] - (here + 6))
        elif it.role == 'jmp':
            out += b'\xE9' + struct.pack('<i', at[it.target] - (here + 5))
        elif it.role == 'call':
            if mode == 'emu':
                out += b'\xE8' + struct.pack('<i', it.target - (base + here + 5))
            else:
                slots.append({'at': here + 1, 'kind': 'call', 'target': it.target})
                out += b'\xE8\x00\x00\x00\x00'
        else:
            out += it.raw
    assert len(out) == total, (L.name, len(out), total)
    return bytes(out), slots


# ---------------------------------------------------------------- cases and frames
dbits = lambda x: struct.unpack('<Q', struct.pack('<d', x))[0]
D_EDGE = [dbits(v) for v in (0.0, -0.0, 1.0, -1.0, 0.5, -2.5, 1e308, -1e308, math.inf, -math.inf, 5e-324, 2147483647.5,
                             -2147483648.5, 4294967296.0, 2.0 ** 53 + 2, 2.0 ** 63, -2.0 ** 63, 127.5, 255.5, 32767.5, 65535.5,
                             3.4028235677973366e38, 1.401298464324817e-45)] + [0x7FF8000000000000, 0xFFF8000000000000, 0x7FF0000000000001]
I32_EDGE = [0, 1, -1, 2 ** 31 - 1, -2 ** 31, 0x7F, 0x80, 0xFF, 0x100, 0x7FFF, 0x8000, 0xFFFF, 31, 32, 63, 64, 65]
I64_EDGE = [0, 1, -1, 2 ** 63 - 1, -2 ** 63, 2 ** 32, 2 ** 32 - 1, -(2 ** 32), 0x7FFFFFFF, 0x80000000, -0x80000000, 0x100000000FF]


def ub(v, n):
    return v & ((1 << n) - 1)


def _tokens(s):
    s = s.strip()
    if s in ('', '-'):
        return []
    out = []
    for t in s.split(','):
        t = t.strip()
        ty = t.split(':')[-1].strip() if ':' in t else t
        ty = ty.split(' ')[0]
        out.append(ty)
    return out


def _operand_layout(name):
    """[(key, size, signed)] in stream order after the opcode word, from the spec's operands column."""
    s = _SPEC[name]['operands']
    lay = []
    for key, size, signed in (('w1', 2, 'i16' in s.split('w1:')[1][:6] if 'w1:' in s else False),
                              ('d1', 4, 'i32' in s.split('d1:')[1][:6] if 'd1:' in s else False),
                              ('d2', 4, False), ('q1', 8, True)):
        if key + ':' in s:
            lay.append((key, size, signed))
    return lay


def _family(name):
    sp = _SPEC[name]
    if name.startswith('b') and not name.startswith('br') or name.startswith('br'):
        return 'branch'
    if name.startswith(('ldloc.', 'ldarg.', 'stloc.', 'starg.')) and not name.endswith('.vt'):
        return 'frame'
    if name.startswith('stinarg.') and not name.endswith('.vt'):
        return 'stinarg'
    if name.endswith('.vt') or name == 'vtresult':
        return 'vt'
    if name in ('ldind.r4', 'ldind.r8', 'stind.r4', 'stind.r8', 'initobj'):
        return 'ptr'
    if name in ('ldstr', 'ldftn', 'mono_ldptr', 'ldtoken'):
        return 'items'
    if name in ('initlocals', 'ldthisa', 'stthis', 'nop'):
        return 'misc'
    return 'stack'


def _vals(rng, types, edge):
    out = []
    for i, ty in enumerate(types):
        if ty in ('r8',):
            v = edge[i] if edge and i < len(edge) else rng.choice(D_EDGE) if rng.random() < 0.5 else dbits(rng.uniform(-1e6, 1e6))
        elif ty in ('i8',):
            v = ub(edge[i] if edge and i < len(edge) else rng.choice(I64_EDGE) if rng.random() < 0.5 else rng.getrandbits(64), 64)
        else:  # i4, o, p, index, n, x, vt tokens that still take a slot
            v = ub(edge[i] if edge and i < len(edge) else rng.choice(I32_EDGE) if rng.random() < 0.5 else rng.getrandbits(32), 32) | (rng.getrandbits(32) << 32)
        out.append(v)
    return out


def cases(rng, n):
    out = []
    for name in GENERATABLE:
        if name in NOT_RUN:
            continue
        fam = _family(name)
        types = _tokens(_SPEC[name]['stack_in'])
        if fam == 'vt':
            types = [t for t in types if t != 'vt']   # the value lives on the vt stack, not in a slot
        edges = []
        if types and types[0] in ('r8', 'i8'):
            edges = [[e] * len(types) for e in (D_EDGE[:6] if types[0] == 'r8' else I64_EDGE[:6])]
            edges += [[D_EDGE[0], D_EDGE[2]] if types[0] == 'r8' else [0, 1]] if len(types) > 1 else []
        for k in range(len(edges) + n):
            edge = edges[k] if k < len(edges) else None
            c = {'op': name, 'fam': fam, 'vals': _vals(rng, types, edge), 'cw': CW_DEFAULT, 'flag': rng.getrandbits(1)}
            lay = _operand_layout(name)
            ops = {}
            for key, size, signed in lay:
                if fam == 'branch':
                    ops[key] = rng.choice((3, -2, 5, -7, 1))
                elif key == 'q1':
                    ops[key] = rng.getrandbits(64)
                elif key == 'd1' and name == 'ldc.r4':
                    ops[key] = rng.getrandbits(32)
                elif key == 'd1' and name == 'initobj':
                    ops[key] = rng.choice((1, 4, 8, 12, 16))
                elif key == 'd2' or (key == 'd1' and fam == 'vt'):
                    ops[key] = rng.choice((4, 6, 8, 12, 16))
                elif fam == 'stinarg' or (name == 'stinarg.vt' and key == 'w1'):   # an argument index, not an offset
                    ops[key] = rng.randrange(0, 6)
                elif fam == 'items':
                    ops[key] = rng.randrange(0, 8)
                elif fam in ('frame', 'vt'):
                    ops[key] = rng.choice((0, 8, 16, 24, 32))
                else:
                    ops[key] = rng.getrandbits(8 * size) if not signed else rng.randrange(-100, 100)
            c['ops'] = ops
            if '.r4' in name or '.r8' in name or name.startswith('conv.r') or name in ('ldc.r4', 'ldc.r8'):
                c['cw'] = rng.choice((CW_DEFAULT, CW_DEFAULT, 0x007F, 0x037F, 0x0E7F))
            c['label'] = f'{name} {k} ops={ops} vals={[hex(v) for v in c["vals"]]} cw={c["cw"]:#x} flag={c["flag"]}'
            out.append(c)
    return out


def code_bytes(c):
    op = _OPS[c['op']][0]
    b = struct.pack('<H', op)
    for key, size, signed in _operand_layout(c['op']):
        v = c['ops'][key]
        b += (v & ((1 << (8 * size)) - 1)).to_bytes(size, 'little')
    return b


def frame(emu, c):
    emu.reset_heap()
    name, fam = c['op'], c['fam']
    rng = random.Random(hash(c['label']) & 0xFFFFFFFF)
    fill = lambda a, k: emu.write(a, bytes(rng.getrandbits(8) for _ in range(k)))
    F = {}
    for k, size in (('code', 64), ('eval', 0x100), ('loc', 0x40), ('args', 0x40), ('sargs', 0x40), ('ptr', 0x20), ('vt', 0x80), ('items', 0x20)):
        F[k] = emu.alloc(size)
        fill(F[k], size)
    F['aoff'], F['minv'], F['rm'] = emu.alloc(0x20), emu.alloc(0x20), emu.alloc(0x40)
    emu.write(F['aoff'], struct.pack('<8I', *[8 * i for i in range(8)]))
    fill(F['minv'], 0x20)
    emu.w32(F['minv'] + 0x10, F['args'])
    emu.w32(F['minv'] + 0x14, F['sargs'])
    if name == 'stinarg.vt':   # the stack argument holds a pointer to the caller's copy of the value
        emu.w32(F['sargs'] + 8 * c['ops']['w1'], F['vt'] + 0x08)
    fill(F['rm'], 0x40)
    emu.w32(F['rm'] + 0x14, F['items'])
    emu.w32(F['rm'] + 0x1C, F['aoff'])
    emu.write(F['rm'] + 0x3C, struct.pack('<H', 0x18))
    code = code_bytes(c)
    emu.write(F['code'], code + b'\x00' * 8)
    esi = F['eval'] + 0x40
    stack = []

    def push(v):
        emu.write(esi + 8 * len(stack), struct.pack('<Q', v))
        stack.append(v)
    vals = list(c['vals'])
    if fam == 'ptr':
        push(F['ptr'])
        if name.startswith('stind'):
            push(vals[-1])
    elif fam == 'vt' and name == 'vtresult':
        push(F['vt'] + 0x40)   # the slot pointing at the result (EA repoints it)
    elif fam == 'vt' and 'vt' in _tokens(_SPEC[name]['stack_in']):
        size = c['ops'].get('d2') or c['ops'].get('d1') or 8
        push(F['vt'] + 0x40 - ((size + 7) & ~7))   # the slot pointing at the value under vt_sp
    else:
        for v in vals:
            push(v)
    esi += 8 * len(stack)
    ebp = 0x30080000
    emu.write(ebp - 0x200, bytes(0x204))
    emu.w32(ebp - 4, F['code'])
    emu.w32(ebp - 8, F['minv'])
    emu.w32(ebp - 0xC, F['vt'] + 0x40)
    emu.w32(ebp - 0x10, F['rm'])
    emu.w32(ebp - 0x14, F['loc'])
    F.update(esi=esi, ebp=ebp, op=_OPS[name][0], code_bytes=code)
    return F


_EMU = None
_ALLOWED_EA = set()
_ALLOWED_CALLS = set()
_LIFTED = {}


def _handler_cfg(emu, entry):
    from mint_float import handler_cfg
    return handler_cfg(emu, entry)


def setup(emu):
    global _EMU
    from unicorn import UC_HOOK_CODE, UC_PROT_ALL
    _EMU = emu
    emu.stub('msvcr80.dll!_isnan', lambda e, a: int(((a(1) >> 20) & 0x7FF) == 0x7FF and bool(a(0) | (a(1) & 0xFFFFF))))
    emu.stub('msvcr80.dll!_finite', lambda e, a: int(((a(1) >> 20) & 0x7FF) != 0x7FF))

    def memmove(e, a):
        e.write(a(0), e.read(a(1), a(2)))
        return a(0)
    def memset(e, a):
        e.write(a(0), bytes([a(1) & 0xFF]) * a(2))
        return a(0)
    # the CRT thunks the handlers call (0xF7E8A6 memcpy, 0xF7E8B8 memset, the memmove import slot) end in the imports
    emu.stub('msvcr80.dll!memmove', memmove)
    emu.stub('msvcr80.dll!memcpy', memmove)
    emu.stub('msvcr80.dll!memset', memset)
    for name in GENERATABLE:
        L = lift(emu, name)
        _LIFTED[name] = L
        if L.reason is None:
            _ALLOWED_EA.update(_handler_cfg(emu, L.handler).insns)
            for fn in L.calls:
                _ALLOWED_CALLS.update(emu.cfg(fn).insns)
    for fn in (FTOL2, 0xF7EC40):
        _ALLOWED_CALLS.update(emu.cfg(fn).insns)
    _ALLOWED_EA.update(EXITS)
    emu.uc.mem_map(TPL_BASE, TPL_SIZE, UC_PROT_ALL)
    emu._cover = [_handler_cfg(emu, _OPS['ceq.i8'][1])]

    def stop(uc, addr, size, _):
        if addr != DISPATCH:
            emu._exit = addr
            uc.emu_stop()
    for a in EXITS:
        emu.uc.hook_add(UC_HOOK_CODE, stop, begin=a, end=a)


TARGET = _OPS['ceq.i8'][1]


def _go(emu, c, start, allowed):
    from unicorn import UcError
    from unicorn import x86_const as ux
    F = frame(emu, c)
    emu.w32(SSE2_FLAG, c['flag'])
    uc = emu.uc
    for r, v in ((ux.UC_X86_REG_EDI, F['code']), (ux.UC_X86_REG_ESI, F['esi']), (ux.UC_X86_REG_EBP, F['ebp']),
                 (ux.UC_X86_REG_ESP, F['ebp'] - 0x180), (ux.UC_X86_REG_EAX, F['op']), (ux.UC_X86_REG_EDX, F['op']),
                 (ux.UC_X86_REG_EBX, 0xBAD0BAD0), (ux.UC_X86_REG_ECX, 0xBAD0BAD0), (ux.UC_X86_REG_EFLAGS, 0x202)):
        uc.reg_write(r, v)
    uc.reg_write(ux.UC_X86_REG_FPCW, c['cw'])
    uc.reg_write(ux.UC_X86_REG_FPSW, 0)
    uc.reg_write(ux.UC_X86_REG_FPTAG, 0xFFFF)
    uc.reg_write(ux.UC_X86_REG_MXCSR, 0x1F80)
    emu.allowed = allowed
    emu._fault, emu._blocks_run, emu._last_block, emu._gr, emu._gw, emu._exit = None, 0, start, set(), set(), DISPATCH
    try:
        uc.emu_start(start, DISPATCH, count=5000)
    except UcError as e:
        emu._fault = emu._fault or f'{e} at {uc.reg_read(ux.UC_X86_REG_EIP):#x}'
    if emu._fault:
        return ('fault', emu._fault)
    top = (uc.reg_read(ux.UC_X86_REG_FPSW) >> 11) & 7
    cw = uc.reg_read(ux.UC_X86_REG_FPCW)
    if emu._exit != DISPATCH:
        return (EXITS[emu._exit], top, cw)
    rd = lambda k, n: emu.read(F[k], n).hex()
    return ('next', uc.reg_read(ux.UC_X86_REG_ESI) - F['eval'], rd('eval', 0x100), rd('loc', 0x40), rd('args', 0x40),
            rd('sargs', 0x40), rd('ptr', 0x20), rd('vt', 0x80), rd('items', 0x20), emu.u32(F['ebp'] - 0xC) - F['vt'],
            emu.u32(F['minv'] + 0xC), uc.reg_read(ux.UC_X86_REG_EDI) - F['code'], top, cw)


def run(emu, c):
    return _go(emu, c, _OPS[c['op']][1], _ALLOWED_EA | _ALLOWED_CALLS)


def _run_template(c, tpl):
    _EMU.write(TPL_BASE, tpl)
    _EMU.uc.ctl_remove_cache(TPL_BASE, TPL_BASE + TPL_SIZE)
    allowed = set(range(TPL_BASE, TPL_BASE + len(tpl))) | set(EXITS) | _ALLOWED_CALLS
    return _go(_EMU, c, TPL_BASE, allowed)


def p0_bytes(L, code):
    """The compiler form placed for this instruction: every slot filled as the compiler would, with the ip bookkeeping
    the compiler does not need appended as stubs (next: ip += 2 * words; taken: ip += 2 * operand; then EA's dispatch)."""
    tpl, slots = render(L, 'p0')
    out = bytearray(tpl)
    stubs = bytearray()
    stub_at = {}

    def stub(key, advance):
        if key not in stub_at:
            stub_at[key] = len(tpl) + len(stubs)
            # add edi, advance; mov [ebp-4], edi; jmp DISPATCH
            stubs.extend(bytes([0x81, 0xC7]) + struct.pack('<i', advance) + bytes([0x89, 0x7D, 0xFC, 0xE9])
                         + struct.pack('<i', DISPATCH - (TPL_BASE + len(tpl) + len(stubs) + 14)))
        return stub_at[key]
    for sl in slots:
        at = sl['at']
        if sl['kind'] == 'imm':
            out[at:at + 4] = struct.pack('<I', _operand_value(code, sl['operand'], sl['size'], sl['ext']))
        elif sl['kind'] == 'call':
            out[at:at + 4] = struct.pack('<i', sl['target'] - (TPL_BASE + at + 4))
        elif sl['kind'] == 'next':
            out[at:at + 4] = struct.pack('<i', stub('next', 2 * L.words) - (at + 4))
        elif sl['kind'] == 'taken':
            o = sl['operand']
            size, ext = L.operands[o]
            v = int.from_bytes(code[o:o + size], 'little', signed=True)
            out[at:at + 4] = struct.pack('<i', stub(('taken', v), 2 * v) - (at + 4))
        elif sl['kind'] == 'exit':
            out[at:at + 4] = struct.pack('<i', NULL_EXC - (TPL_BASE + at + 4))   # no generatable handler has one
    return bytes(out + stubs)


def reference(c):
    """The lifted template's result: the handler-convention rendering, and the compiler form with its slots filled;
    the two must agree, else the compiler form's result is returned marked."""
    L = _LIFTED[c['op']]
    if L.reason is not None:
        return None
    code = code_bytes(c)
    tpl, _ = render(L, 'emu', code)
    r = _run_template(c, tpl)
    r2 = _run_template(c, p0_bytes(L, code))
    if r2 != r:
        return ('compiler form differs',) + tuple(r2[:2])
    return r


def compare(got, want):
    if want is None:
        return None
    if got[0] == 'fault' or want[0] == 'fault':   # a fault on either side is never a pass, equal or not
        return f'fault: EA {got[:2]!r} / lifted {want[:2]!r}'[:300]
    if got == want:
        return None
    names = ('exit', 'esi', 'eval', 'locals', 'args', 'stack args', 'pointer', 'vt', 'items', 'vt top', 'this', 'ip', 'x87 top', 'cw')
    if len(got) != len(want):
        return f'{got!r} / {want!r}'[:300]
    return '; '.join(f'{names[i]}: EA {g!r} / lifted {w!r}'[:160] for i, (g, w) in enumerate(zip(got, want)) if g != w)


NOTES = ['templates are EA\'s handler bytes with the operand reads as immediates (mint_lift.py lift()); the compiler form '
         '(tools/emu/specs/out/jit-lifted-templates.json, local) also drops the ip advance, the ip store and the dispatch tail',
         'rem.r8, stobj.vt and switch are not run (see NOT_RUN)']


# ---------------------------------------------------------------- --write: the data files
def write_files(exe):
    from emu import Emu
    emu = Emu(exe)
    setup(emu)
    rng = random.Random(1)
    cs = cases(rng, CASES)
    per = {}
    for c in cs:
        emu.reset_heap()
        ea = run(emu, c)
        tp = reference(c)
        d = compare(ea, tp)
        k = per.setdefault(c['op'], [0, 0, []])
        k[0] += 1
        if d:
            k[1] += 1
            if len(k[2]) < 2:
                k[2].append(f'{c["label"]}: {d}'[:200])
    out = {'what': 'docs/524: EA mint handlers lifted to compiler templates (tools/emu/specs/mint_lift.py --write)',
           'convention': 'esi = eval top, ebp = the interpreter frame ([ebp-8] invocation, [ebp-0xC] vt_sp, [ebp-0x10] runtime method, '
                         '[ebp-0x14] locals); slots: imm = the operand at `operand` bytes from the instruction start, `size` bytes, '
                         'ext zx/sx/mov into `reg`; next = the following instruction; taken = ip + 2 * the signed operand; '
                         'exit = P0 exit to EA\'s handler (esi already put back); call = absolute target', 'opcodes': {}}
    rows = []
    for name in GENERATABLE:
        L = _LIFTED[name]
        e = {'opcode': L.opcode, 'handler': L.handler, 'words': L.words, 'status': L.status}
        if L.reason:
            e['reason'] = L.reason
        else:
            b, slots = render(L, 'p0')
            e.update(template=b.hex(), slots=slots, calls=sorted(L.calls), clobbers=['ebx'] if L.clobber_ebx else [],
                     uses=sorted(L.uses), exitAfterSideEffect=L.exc_after_effect, operandBytes=len(L.operands))
        n, bad, ex = per.get(name, [0, 0, []])
        e['verify'] = {'cases': n, 'mismatches': bad} if n else {'cases': 0, 'note': 'not run'}
        if ex:
            e['verify']['examples'] = ex
        out['opcodes'][name] = e
        rows.append({'opcode': f'0x{L.opcode:03X}', 'name': name, 'handler': f'0x{L.handler:X}', 'words': L.words, 'status': L.status,
                     'reason': L.reason or '', 'template_bytes': len(e.get('template', '')) // 2, 'slots': len(e.get('slots', [])),
                     'calls': ' '.join(f'0x{a:X}' for a in sorted(L.calls)), 'clobbers_ebx': int(L.clobber_ebx),
                     'exit_after_side_effect': int(L.exc_after_effect), 'cases': n, 'mismatches': bad})
    p = os.path.join(REPO, 'tools', 'emu', 'specs', 'out', 'jit-lifted-templates.json')  # EA bytes: git-ignored (out/), never committed
    os.makedirs(os.path.dirname(p), exist_ok=True)
    with open(p, 'w', encoding='utf-8') as f:
        json.dump(out, f, indent=1)
    p2 = os.path.join(REPO, 'docs', 'data', 'jit-lift-census.csv')
    with open(p2, 'w', encoding='utf-8', newline='') as f:
        w = csv.DictWriter(f, fieldnames=list(rows[0].keys()))
        w.writeheader()
        w.writerows(rows)
    lifted = [r for r in rows if r['status'] == 'lifted']
    clean = [r for r in lifted if r['mismatches'] == 0 and r['cases']]
    print(f'{len(rows)} generatable: {len(lifted)} lifted ({len(clean)} with 0 mismatches over {sum(r["cases"] for r in lifted)} cases), '
          f'{len(rows) - len(lifted)} left to hand templates')
    for r in rows:
        if r['status'] != 'lifted' or r['mismatches']:
            print(f'  {r["name"]}: {r["reason"] or str(r["mismatches"]) + " mismatches"}')
    for name in GENERATABLE:
        e = out['opcodes'][name]
        if e.get('verify', {}).get('examples'):
            print('   ', e['verify']['examples'][0][:220])
    print(f'wrote {p}\n      {p2}')


if __name__ == '__main__':
    import argparse
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('--write', action='store_true')
    ap.add_argument('--exe')
    a = ap.parse_args()
    import emu as emu_mod
    exe = a.exe or emu_mod.DEFAULT_EXE
    if not os.path.exists(exe):
        print(f'SKIP: {exe} not found')
        sys.exit(0)
    if a.write:
        write_files(exe)
    else:
        ap.error('--write, or run it through tools/emu/run.py')
