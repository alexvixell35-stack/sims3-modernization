"""Mint float opcodes vs compiled-code templates (round 3 B2, docs/221).

For every float opcode of the embedded interpreter (docs/213: 87 opcodes, 83 handlers) this runs EA's handler and the
template the docs/207 compiler would emit (x86 bytes, run inside the interpreter's C frame with esi = eval stack, as P0
code runs) on the same mint frame, and compares what the next instruction sees: eval slots (all 8 bytes, so a stale high
dword counts), esi, locals, args, stack args, the object / array / pointer targets, the vt stack top, the branch
decision (ip), the x87 control word and stack depth, and which exception exit was taken.

Inputs: double / float / integer edge values (quiet and signalling NaNs with payloads, +-0, +-inf, subnormals, float
and integer range limits, x.5 ties, 2^53 + 2, 2^63, ...), products that round twice in the subnormal range at 53-bit
precision, random values, and the x87 control words 0x027F (default), 24-bit and 64-bit precision and the three other
rounding modes; both states of the CRT SSE2 flag [0x1234374] where the handler calls _ftol2_sse.

_isnan / _finite are C-semantics stubs (as docs/213); rem.r8 is not run (handler and template both call the CRT
_CIfmod); ldc.r8 has no FPU (integer moves); brfalse.r8 long form is not compiled (docs/213 §4 bug: exit to EA).

  python tools/emu/run.py tools/emu/specs/mint_float.py           # templates vs EA: must be 0 mismatches
  python tools/emu/specs/mint_float.py --controls                  # the alternatives (SSE2, integer, CLI-correct)
"""
import math
import os
import random
import struct
import sys
from fractions import Fraction

NAME = 'mint float opcodes vs templates'
TARGET = 0xE55A7D          # add.r8; coverage is reported for this handler, every case runs under the same policy
CONV = 'mint frame (edi = ip, esi = eval top, ebp = interpreter frame)'
CASES = 20                 # random values per opcode on top of the edge set
DISPATCH = 0xE544D5
NULL_EXC, RANGE_EXC, OVF_EXC, CKF_EXC = 0xE5947E, 0xE5949D, 0xE57AA5, 0xE5952A
EXITS = {DISPATCH: 'next', NULL_EXC: 'NullReference', RANGE_EXC: 'IndexOutOfRange', OVF_EXC: 'Overflow', CKF_EXC: 'NotFinite'}
SSE2_FLAG = 0x1234374      # CRT __sse2_available: _ftol2_sse takes cvttsd2si when set
FTOL2_SSE, FTOL2 = 0xF7EC40, 0xF7EC76
TPL_BASE, TPL_SIZE = 0x28000000, 0x10000   # templates (+0) and their constants (+0x8000)
CONST = TPL_BASE + 0x8000
INTERP = (0xE54000, 0xE5A000)  # the interpreter function: every handler, tail and fault stub lies inside
CW_DEFAULT = 0x027F
CWS = (0x027F, 0x007F, 0x037F, 0x067F, 0x0A7F, 0x0E7F)  # default, PC 24, PC 64, RC down, RC up, RC chop
NOTES = ['_isnan / _finite stubbed with C semantics; rem.r8 not run (both call the CRT _CIfmod); brfalse.r8 long form '
         'not compiled (exits to EA, docs/213 §4); the emulator x87 is a software model (docs/213 §1 limit 3)',
         'templates are hand-encoded; setup() checks every template instruction against its intended text with capstone']

# ---------------------------------------------------------------- a small x86 encoder with the text capstone prints
R = {n: i for i, n in enumerate(('eax', 'ecx', 'edx', 'ebx', 'esp', 'ebp', 'esi', 'edi'))}
R8 = ('al', 'cl', 'dl', 'bl')
h = lambda v: str(v) if 0 <= v < 10 else hex(v)
SIZE = {4: 'dword ptr ', 8: 'qword ptr ', 2: 'word ptr ', 1: 'byte ptr ', 0: ''}


class Mem:
    def __init__(self, base=None, disp=0, index=None, scale=1):
        self.base, self.disp, self.index, self.scale = base, disp, index, scale

    def enc(self, reg):
        b = R[self.base] if self.base else None
        if b is None and self.index is None:
            return bytes([(reg << 3) | 5]) + struct.pack('<I', self.disp & 0xFFFFFFFF)
        sib = lambda base: bytes([({1: 0, 2: 1, 4: 2, 8: 3}[self.scale] << 6) | ((4 if self.index is None else R[self.index]) << 3) | base])
        if b is None:  # [index*scale + disp32]
            return bytes([(reg << 3) | 4]) + sib(5) + struct.pack('<I', self.disp & 0xFFFFFFFF)
        mod = 0 if self.disp == 0 and b != 5 else 1 if -128 <= self.disp <= 127 else 2
        d = b'' if mod == 0 else struct.pack('<b', self.disp) if mod == 1 else struct.pack('<i', self.disp)
        if self.index is None and b != 4:
            return bytes([(mod << 6) | (reg << 3) | b]) + d
        return bytes([(mod << 6) | (reg << 3) | 4]) + sib(b) + d

    def txt(self, size):
        parts = ([self.base] if self.base else []) + ([self.index if self.scale == 1 else f'{self.index}*{self.scale}'] if self.index else [])
        s = ' + '.join(parts)
        if not parts:
            s = h(self.disp & 0xFFFFFFFF)
        elif self.disp:
            s += f' - {h(-self.disp)}' if self.disp < 0 else f' + {h(self.disp & 0xFFFFFFFF)}'
        return f'{SIZE[size]}[{s}]'


M = Mem
I = lambda b, t: [(bytes(b), t)]          # one instruction: bytes and the text capstone should print
x87 = lambda op, ext, m, size, mn: I(bytes([op]) + m.enc(ext), f'{mn} {m.txt(size)}')
fld_d = lambda m: x87(0xD9, 0, m, 4, 'fld')
fld_q = lambda m: x87(0xDD, 0, m, 8, 'fld')
fstp_d = lambda m: x87(0xD9, 3, m, 4, 'fstp')
fstp_q = lambda m: x87(0xDD, 3, m, 8, 'fstp')
fop_q = lambda ext, mn, m: x87(0xDC, ext, m, 8, mn)
fild_d = lambda m: x87(0xDB, 0, m, 4, 'fild')
fild_q = lambda m: x87(0xDF, 5, m, 8, 'fild')
fisttp_q = lambda m: x87(0xDD, 1, m, 8, 'fisttp')
fcomp_q = lambda m: x87(0xDC, 3, m, 8, 'fcomp')
fistp_q = lambda m: x87(0xDF, 7, m, 8, 'fistp')
fnstcw = lambda m: x87(0xD9, 7, m, 2, 'fnstcw')
fldcw = lambda m: x87(0xD9, 5, m, 2, 'fldcw')
movzx_w = lambda r, m: I(b'\x0F\xB7' + m.enc(R[r]), f'movzx {r}, {m.txt(2)}')
OR_EAX_C00 = I(b'\x0D\x00\x0C\x00\x00', 'or eax, 0xc00')
FCHS, FLDZ, FSTP0 = I(b'\xD9\xE0', 'fchs'), I(b'\xD9\xEE', 'fldz'), I(b'\xDD\xD8', 'fstp st(0)')
FUCOMIP, FNSTSW = I(b'\xDF\xE9', 'fucompi st(1)'), I(b'\xDF\xE0', 'fnstsw ax')
movsd_x0 = lambda m: I(b'\xF2\x0F\x10' + m.enc(0), f'movsd xmm0, {m.txt(8)}')
movsd_m = lambda m: I(b'\xF2\x0F\x11' + m.enc(0), f'movsd {m.txt(8)}, xmm0')
sse_op = lambda op, mn, m: I(b'\xF2\x0F' + bytes([op]) + m.enc(0), f'{mn} xmm0, {m.txt(8)}')
cvttsd2si = lambda r, m: I(b'\xF2\x0F\x2C' + m.enc(R[r]), f'cvttsd2si {r}, {m.txt(8)}')
mov_rm = lambda r, m: I(b'\x8B' + m.enc(R[r]), f'mov {r}, {m.txt(4)}')
mov_mr = lambda m, r: I(b'\x89' + m.enc(R[r]), f'mov {m.txt(4)}, {r}')
mov_mi = lambda m, v: I(b'\xC7' + m.enc(0) + struct.pack('<I', v & 0xFFFFFFFF), f'mov {m.txt(4)}, {h(v & 0xFFFFFFFF)}')
add_ri = lambda r, v: I(bytes([0x83, 0xC0 | R[r], v & 0xFF]), f'add {r}, {h(v)}')
sub_ri = lambda r, v: I(bytes([0x83, 0xE8 | R[r], v & 0xFF]), f'sub {r}, {h(v)}')
sub_mi = lambda m, v: I(b'\x81' + m.enc(5) + struct.pack('<I', v), f'sub {m.txt(4)}, {h(v)}')
and_ri = lambda r, v: I(bytes([0x81, 0xE0 | R[r]]) + struct.pack('<I', v), f'and {r}, {h(v)}')
cmp_ri = lambda r, v: I(bytes([0x81, 0xF8 | R[r]]) + struct.pack('<I', v), f'cmp {r}, {h(v)}')
cmp_rm = lambda r, m: I(b'\x3B' + m.enc(R[r]), f'cmp {r}, {m.txt(4)}')
or_rm = lambda r, m: I(b'\x0B' + m.enc(R[r]), f'or {r}, {m.txt(4)}')
add_rr = lambda d, s: I(bytes([0x01, 0xC0 | (R[s] << 3) | R[d]]), f'add {d}, {s}')
test_rr = lambda a, b: I(bytes([0x85, 0xC0 | (R[b] << 3) | R[a]]), f'test {a}, {b}')
shr_ri = lambda r, v: I(bytes([0xC1, 0xE8 | R[r], v]), f'shr {r}, {h(v)}')
test_ah = lambda v: I(bytes([0xF6, 0xC4, v]), f'test ah, {h(v)}')
movsx_w = lambda r, m: I(b'\x0F\xBF' + m.enc(R[r]), f'movsx {r}, {m.txt(2)}')
CC = {'a': 0x7, 'ae': 0x3, 'b': 0x2, 'be': 0x6, 'e': 0x4, 'ne': 0x5, 'p': 0xA, 'np': 0xB, 'ge': 0xD}
setcc = lambda cc, r8: I(bytes([0x0F, 0x90 | CC[cc], 0xC0 | R8.index(r8)]), f'set{cc} {r8}')
AND_AL_CL, OR_AL_CL = I(b'\x20\xC8', 'and al, cl'), I(b'\x08\xC8', 'or al, cl')
TEST_AL = I(b'\x84\xC0', 'test al, al')
MOVZX_AL, MOVZX_AX = I(b'\x0F\xB6\xC0', 'movzx eax, al'), I(b'\x0F\xB7\xC0', 'movzx eax, ax')
MOVSX_AL, MOVSX_AX = I(b'\x0F\xBE\xC0', 'movsx eax, al'), I(b'\x0F\xBF\xC0', 'movsx eax, ax')
LEA_EDI = I(b'\x8D\x3C\x57', 'lea edi, [edi + edx*2]')
xor_sign = lambda m: I(b'\x80' + m.enc(6) + b'\x80', f'xor {m.txt(1)}, 0x80')
cvtsi2sd = lambda m: I(b'\xF2\x0F\x2A' + m.enc(0), f'cvtsi2sd xmm0, {m.txt(4)}')
cvtsd2ss = lambda m: I(b'\xF2\x0F\x5A' + m.enc(0), f'cvtsd2ss xmm0, {m.txt(8)}')
CVTSS2SD = I(b'\xF3\x0F\x5A\xC0', 'cvtss2sd xmm0, xmm0')
movss_m = lambda m: I(b'\xF3\x0F\x11' + m.enc(0), f'movss {m.txt(4)}, xmm0')
ucomisd = lambda m: I(b'\x66\x0F\x2E' + m.enc(0), f'ucomisd xmm0, {m.txt(8)}')
lbl = lambda n: [('lbl', n)]
j8 = lambda cc, n: [('j8', cc, n)]           # cc None = jmp short
j32 = lambda kind, addr: [('j32', kind, addr)]  # kind: jmp / call / je / jae / jnp


def assemble(items, base):
    """Two passes: label addresses, then bytes; returns (code, [(addr, size, text)])."""
    size = lambda it: len(it[0]) if isinstance(it[0], bytes) else 0 if it[0] == 'lbl' else 2 if it[0] == 'j8' else 5 if it[1] in ('jmp', 'call') else 6
    labels, a = {}, base
    for it in items:
        if it[0] == 'lbl':
            labels[it[1]] = a
        a += size(it)
    code, lst, a = b'', [], base
    for it in items:
        n = size(it)
        if isinstance(it[0], bytes):
            b, t = it
        elif it[0] == 'lbl':
            continue
        elif it[0] == 'j8':
            rel = labels[it[2]] - (a + 2)
            b = bytes([0xEB if it[1] is None else 0x70 | CC[it[1]], rel & 0xFF])
            t = f'{"jmp" if it[1] is None else "j" + it[1]} {labels[it[2]]:#x}'
        else:
            rel = (it[2] - (a + n)) & 0xFFFFFFFF
            b = {'jmp': b'\xE9', 'call': b'\xE8', 'je': b'\x0F\x84', 'jae': b'\x0F\x83', 'jnp': b'\x0F\x8B'}[it[1]] + struct.pack('<I', rel)
            t = f'{it[1]} {it[2]:#x}'
        code += b
        lst.append((a, n, t))
        a += n
    return code, lst


# ---------------------------------------------------------------- templates
ESI = lambda d: M('esi', d)
EBP = lambda d: M('ebp', d)
SLOT_A, SLOT_B = ESI(-16), ESI(-8)


def branch_tail(taken_cond, long_form, words):
    """al = taken; the ip update reproduces EA's so the harness can compare decisions (compiled code jumps natively)."""
    off = (mov_rm('edx', M('edi', 2)) if long_form else movsx_w('edx', M('edi', 2)))
    return taken_cond + TEST_AL + j8('e', 'nt') + off + LEA_EDI + j8(None, 'done') + lbl('nt') + add_ri('edi', 2 * words) + lbl('done')


def fcmp(first_a, cond):
    """fucomip with a (first_a) or b in st(0); cond = list of (cc, combine)."""
    loads = fld_q(SLOT_B) + fld_q(SLOT_A) if first_a else fld_q(SLOT_A) + fld_q(SLOT_B)
    out = loads + FUCOMIP + FSTP0 + setcc(cond[0], 'al')
    if len(cond) > 1:
        out += setcc(cond[1][0], 'cl') + (AND_AL_CL if cond[1][1] == 'and' else OR_AL_CL)
    return out


def scmp(first_a, cond):
    """The same with SSE2 ucomisd (control)."""
    out = (movsd_x0(SLOT_A) + ucomisd(SLOT_B) if first_a else movsd_x0(SLOT_B) + ucomisd(SLOT_A)) + setcc(cond[0], 'al')
    if len(cond) > 1:
        out += setcc(cond[1][0], 'cl') + (AND_AL_CL if cond[1][1] == 'and' else OR_AL_CL)
    return out


# relation -> (a in st(0)?, setcc list); a = first pushed operand, b = second
REL = {'eq': (True, ['e', ('np', 'and')]), 'ge': (True, ['ae']), 'gt': (True, ['a']), 'lt': (False, ['a']), 'le': (False, ['ae']),
       'ne.un': (True, ['ne', ('p', 'or')]), 'ge.un': (False, ['be']), 'gt.un': (False, ['b']), 'le.un': (True, ['be']), 'lt.un': (True, ['b'])}
CMP_REL = {'ceq.r8': 'eq', 'cgt.r8': 'gt', 'cgt.un.r8': 'gt.un', 'clt.r8': 'lt', 'clt.un.r8': 'lt.un'}
TO_DISPATCH = j32('jmp', DISPATCH)


def c64(i):
    return M(None, CONST + 8 * i)


CONSTS = [-128.0, 127.0, -32768.0, 32767.0, -2147483648.0, 2147483647.0, 255.0, 65535.0, 4294967295.0,
          -9223372036854775808.0, 9223372036854775808.0, 4294967296.0]
OVF = {'conv.ovf.i1.r8': (-128.0, 127.0, 'i1'), 'conv.ovf.i2.r8': (-32768.0, 32767.0, 'i2'),
       'conv.ovf.i4.r8': (-2147483648.0, 2147483647.0, 'i4'), 'conv.ovf.u1.r8': (0, 255.0, 'u1'),
       'conv.ovf.u2.r8': (0, 65535.0, 'u2'), 'conv.ovf.u4.r8': (0, 4294967295.0, 'u4'),
       'conv.ovf.i4.un.r8': (0, 4294967295.0, 'u4'), 'conv.ovf.i8.r8': (-9223372036854775808.0, 9223372036854775808.0, 'i8'),
       'conv.ovf.i8.un.r8': (0, 9223372036854775808.0, 'i8'), 'conv.ovf.u8.r8': (0, 9223372036854775808.0, 'i8')}


def conv_int(kind, flag):
    """Double in the top slot -> integer in its low dword (i8: both dwords), as EA's non-checked handlers."""
    x = SLOT_B
    if kind in ('i1', 'i2', 'i4'):
        body = cvttsd2si('eax', x) if flag else fld_q(x) + j32('call', FTOL2_SSE)
        return body + {'i1': MOVSX_AL, 'i2': MOVSX_AX, 'i4': []}[kind] + mov_mr(x, 'eax')
    if kind in ('u1', 'u2'):
        return cvttsd2si('eax', x) + (MOVZX_AL if kind == 'u1' else MOVZX_AX) + mov_mr(x, 'eax')
    if kind == 'u4':  # EA's chop: control word with RC = 11, fistp qword, low dword (fisttp would do, see docs/221)
        return (fld_q(x) + fnstcw(EBP(-2)) + movzx_w('eax', EBP(-2)) + OR_EAX_C00 + mov_mr(EBP(-0x1C), 'eax') + fldcw(EBP(-0x1C))
                + fistp_q(EBP(-0x20)) + mov_rm('eax', EBP(-0x20)) + mov_mr(x, 'eax') + fldcw(EBP(-2)))
    return fld_q(x) + j32('call', FTOL2) + mov_mr(x, 'eax') + mov_mr(ESI(-4), 'edx')  # i8: the CRT _ftol2, as EA


def range_check(lo, hi):
    """EA's checked bounds, same compares: overflow when x < lo or x > hi (ordered); NaN passes (docs/213 §4)."""
    lo_ld = FLDZ if lo == 0 else fld_q(c64(CONSTS.index(lo)))
    return (lo_ld + fcomp_q(SLOT_B) + FNSTSW + test_ah(0x41) + j32('je', OVF_EXC)
            + fld_q(c64(CONSTS.index(hi))) + fcomp_q(SLOT_B) + FNSTSW + test_ah(0x05) + j32('jnp', OVF_EXC))


def widen_f32(bits):
    """float32 bits -> float64 bits as fld dword / fstp qword does: exact; NaN quieted (bit 51 set); payload kept."""
    s, e, m = bits >> 31, (bits >> 23) & 0xFF, bits & 0x7FFFFF
    if e == 0xFF:
        return (s << 63) | (0x7FF << 52) | ((m | (0x400000 if m else 0)) << 29)
    if e == 0:
        if m == 0:
            return s << 63
        e = 1
        while not m & 0x800000:
            m <<= 1
            e -= 1
        m &= 0x7FFFFF
    return (s << 63) | ((e - 127 + 1023) << 52) | (m << 29)


def template(c, alt=False):
    """Items for case c; alt = the control variant (None when there is none)."""
    n, o = c['op'], c.get('operand', 0)
    A = {'add.r8': (0, 'fadd', 0x58, 'addsd'), 'sub.r8': (4, 'fsub', 0x5C, 'subsd'), 'mul.r8': (1, 'fmul', 0x59, 'mulsd'), 'div.r8': (6, 'fdiv', 0x5E, 'divsd')}
    if n in A:
        ext, mn, sop, smn = A[n]
        if alt:
            return movsd_x0(SLOT_A) + sse_op(sop, smn, SLOT_B) + movsd_m(SLOT_A) + sub_ri('esi', 8)
        return fld_q(SLOT_A) + fop_q(ext, mn, SLOT_B) + fstp_q(SLOT_A) + sub_ri('esi', 8)
    if n == 'neg.r8':
        return xor_sign(ESI(-1)) if alt else fld_q(SLOT_B) + FCHS + fstp_q(SLOT_B)
    if n in CMP_REL:
        first_a, cond = REL[CMP_REL[n]]
        return (scmp if alt else fcmp)(first_a, cond) + MOVZX_AL + sub_ri('esi', 8) + mov_mr(ESI(-8), 'eax')
    if n.startswith('b') and '.r8' in n and not n.startswith('br'):
        rel, short = n.split('.r8')[0][1:], n.endswith('.s')
        first_a, cond = REL[rel]
        return (scmp if alt else fcmp)(first_a, cond) + sub_ri('esi', 16) + branch_tail([], not short, 2 if short else 3)
    if n in ('brtrue.r8', 'brtrue.r8.s', 'brfalse.r8.s'):
        if alt:
            return None
        cond = mov_rm('eax', ESI(-4)) + add_rr('eax', 'eax') + or_rm('eax', ESI(-8)) + setcc('ne' if 'true' in n else 'e', 'al')
        return cond + sub_ri('esi', 8) + branch_tail([], not n.endswith('.s'), 2 if n.endswith('.s') else 3)
    if n == 'ckfinite':
        return None if alt else mov_rm('eax', ESI(-4)) + and_ri('eax', 0x7FF00000) + cmp_ri('eax', 0x7FF00000) + j32('je', CKF_EXC)
    conv = {'conv.i1.r8': 'i1', 'conv.i2.r8': 'i2', 'conv.i4.r8': 'i4', 'conv.u1.r8': 'u1', 'conv.u2.r8': 'u2',
            'conv.u4.r8': 'u4', 'conv.i8.r8': 'i8', 'conv.u8.r8': 'i8'}
    if n in conv:
        if alt:
            return cvttsd2si('eax', SLOT_B) + mov_mr(SLOT_B, 'eax') if conv[n] == 'u4' else None
        return conv_int(conv[n], c['flag'])
    if n in OVF:
        if alt:
            return None
        lo, hi, kind = OVF[n]
        return range_check(lo, hi) + conv_int(kind, c['flag'])
    if n in ('conv.r4.i4', 'conv.r8.i4'):
        if alt:  # r4: what the CLI asks for (round to float); r8: SSE2
            return (fild_d(SLOT_B) + fstp_d(EBP(-0x1C)) + fld_d(EBP(-0x1C)) + fstp_q(SLOT_B)) if n == 'conv.r4.i4' else cvtsi2sd(SLOT_B) + movsd_m(SLOT_B)
        return fild_d(SLOT_B) + fstp_q(SLOT_B)
    if n in ('conv.r4.i8', 'conv.r8.i8'):
        if alt:
            return (fild_q(SLOT_B) + fstp_d(EBP(-0x1C)) + fld_d(EBP(-0x1C)) + fstp_q(SLOT_B)) if n == 'conv.r4.i8' else None
        return fild_q(SLOT_B) + fstp_q(SLOT_B)
    if n == 'conv.r4.r8':
        if alt:
            return cvtsd2ss(SLOT_B) + CVTSS2SD + movsd_m(SLOT_B)
        return fld_q(SLOT_B) + fstp_d(EBP(-0x1C)) + fld_d(EBP(-0x1C)) + fstp_q(SLOT_B)
    if n == 'conv.r.un.i4':
        if alt:  # zero-extend + fild qword: exact, no add (differs at 24-bit precision)
            return mov_mi(ESI(-4), 0) + fild_q(SLOT_B) + fstp_q(SLOT_B)
        return (mov_rm('eax', SLOT_B) + fild_d(SLOT_B) + test_rr('eax', 'eax') + j8('ge', 'pos')
                + fop_q(0, 'fadd', c64(CONSTS.index(4294967296.0))) + lbl('pos') + fstp_q(SLOT_B))
    if n == 'conv.r.un.i8':  # s + (s < 0 ? 2^64 : -0.0): one rounding at the add, as EA's lo63 + 2^63
        return None if alt else (mov_rm('eax', ESI(-4)) + shr_ri('eax', 31) + fild_q(SLOT_B)
                                 + fop_q(0, 'fadd', M(None, CONST + 0x80, 'eax', 8)) + fstp_q(SLOT_B))
    if n == 'ldc.r4':
        if alt:  # constant widened at compile time (two immediates): the x87 rule, NaN quieted
            w = widen_f32(o)
            return mov_mi(ESI(0), w & 0xFFFFFFFF) + mov_mi(ESI(4), w >> 32) + add_ri('esi', 8)
        return mov_mi(EBP(-0x1C), o) + fld_d(EBP(-0x1C)) + fstp_q(ESI(0)) + add_ri('esi', 8)
    # ---- moves: r4 sources widen (fld dword / fstp qword); r4 stores drop EA's dword round trip; r8 goes through x87
    r4 = '.r4' in n
    ld = fld_d if r4 else fld_q
    st = fstp_d if r4 else fstp_q
    if n.startswith('ldloc.') or n.startswith('ldarg.'):
        base = mov_rm('edx', EBP(-0x14)) if n.startswith('ldloc') else mov_rm('edx', EBP(-8)) + mov_rm('edx', M('edx', 0x10))
        return None if alt else base + ld(M('edx', o)) + fstp_q(ESI(0)) + add_ri('esi', 8)
    if n.startswith('stloc.') or n.startswith('starg.'):
        base = mov_rm('edx', EBP(-0x14)) if n.startswith('stloc') else mov_rm('edx', EBP(-8)) + mov_rm('edx', M('edx', 0x10))
        if alt:
            return base + (cvtsd2ss(SLOT_B) + movss_m(M('edx', o)) if r4 else
                           mov_rm('eax', SLOT_B) + mov_mr(M('edx', o), 'eax') + mov_rm('eax', ESI(-4)) + mov_mr(M('edx', o + 4), 'eax')) + sub_ri('esi', 8)
        return base + fld_q(SLOT_B) + st(M('edx', o)) + sub_ri('esi', 8)
    if n.startswith('stinarg.'):
        return None if alt else (mov_rm('ecx', EBP(-8)) + mov_rm('edx', M('ecx', 0x14)) + fld_q(M('edx', 8 * o))
                                 + mov_rm('edx', EBP(-0x10)) + mov_rm('edx', M('edx', 0x1C)) + mov_rm('edx', M('edx', 4 * o))
                                 + mov_rm('ecx', M('ecx', 0x10)) + st(M('edx', 0, 'ecx', 1)))
    if n.startswith('ldfld.') or n.startswith('vtldfld.'):
        vt = sub_mi(EBP(-0xC), (c['vtsize'] + 7) & ~7) if n.startswith('vt') else []
        return None if alt else mov_rm('eax', SLOT_B) + test_rr('eax', 'eax') + j32('je', NULL_EXC) + vt + ld(M('eax', o)) + fstp_q(SLOT_B)
    if n.startswith('stfld.'):
        return None if alt else (mov_rm('eax', SLOT_A) + test_rr('eax', 'eax') + j32('je', NULL_EXC) + fld_q(SLOT_B)
                                 + st(M('eax', o)) + sub_ri('esi', 16))
    if n.startswith('ldind.'):
        return None if alt else mov_rm('eax', SLOT_B) + ld(M('eax', 0)) + fstp_q(SLOT_B)
    if n.startswith('stind.'):
        return None if alt else mov_rm('eax', SLOT_A) + fld_q(SLOT_B) + st(M('eax', 0)) + sub_ri('esi', 16)
    if n.startswith('ldelem.'):
        return None if alt else (mov_rm('eax', SLOT_A) + test_rr('eax', 'eax') + j32('je', NULL_EXC) + mov_rm('ecx', SLOT_B)
                                 + cmp_rm('ecx', M('eax', 8)) + j32('jae', RANGE_EXC) + ld(M('eax', 0x10, 'ecx', 4 if r4 else 8))
                                 + fstp_q(SLOT_A) + sub_ri('esi', 8))
    if n.startswith('stelem.'):
        return None if alt else (mov_rm('eax', ESI(-24)) + test_rr('eax', 'eax') + j32('je', NULL_EXC) + mov_rm('ecx', SLOT_A)
                                 + cmp_rm('ecx', M('eax', 8)) + j32('jae', RANGE_EXC) + fld_q(SLOT_B)
                                 + st(M('eax', 0x10, 'ecx', 4 if r4 else 8)) + sub_ri('esi', 24))
    raise KeyError(n)


# ---------------------------------------------------------------- opcodes, inputs, frame
OPS = {}   # name -> (opcode, handler, words)
_RAW = """017 ldc.r4 E546EC 3|01F ldarg.r4 E584A1 2|020 ldarg.r8 E584BF 2|02B starg.r4 E5860A 2|02C starg.r8 E5862E 2|037 stinarg.r4 E5872B 2
038 stinarg.r8 E58758 2|044 ldfld.r4 E56773 2|045 ldfld.r8 E56793 2|051 vtldfld.r4 E569CE 4|052 vtldfld.r8 E569FA 4|05F stfld.r4 E56B9B 2
060 stfld.r8 E56BC4 2|073 ldloc.r4 E58859 2|074 ldloc.r8 E58874 2|07E stloc.r4 E58966 2|07F stloc.r8 E58987 2|08E ldind.r4 E5596E 1
08F ldind.r8 E55981 1|096 stind.r4 E55A1D 1|097 stind.r8 E55A39 1|0A5 brtrue.r8 E54CB0 3|0A8 brfalse.r8.s E54BA6 2|0AB brtrue.r8.s E54C58 2
0AE beq.r8 E54DB2 3|0B1 bge.r8 E54EE9 3|0B4 bgt.r8 E55020 3|0B7 blt.r8 E55157 3|0BA ble.r8 E5528E 3|0BD bne.un.r8 E553BA 3|0C0 bge.un.r8 E554C5 3
0C3 bgt.un.r8 E555F9 3|0C6 ble.un.r8 E5572D 3|0C9 blt.un.r8 E55861 3|0CC beq.r8.s E54D18 2|0CF bge.r8.s E54E4E 2|0D2 bgt.r8.s E54F85 2
0D5 blt.r8.s E550BC 2|0D8 ble.r8.s E551F3 2|0DB bne.un.r8.s E55325 2|0DE bge.un.r8.s E5542E 2|0E1 bgt.un.r8.s E55562 2|0E4 ble.un.r8.s E55696 2
0E7 blt.un.r8.s E557CA 2|10D ldelem.r4 E573D7 1|10E ldelem.r8 E573D7 1|118 stelem.r4 E574DA 1|119 stelem.r8 E574DA 1|11E add.r8 E55A7D 1
122 sub.r8 E55AD0 1|126 mul.r8 E55B36 1|129 div.r8 E55BA2 1|13A neg.r8 E55E39 1|14E conv.r.un.i4 E565C2 1|14F conv.r.un.i8 E565E0 1
152 conv.i1.r8 E55E9E 1|155 conv.u1.r8 E55EDB 1|158 conv.i2.r8 E55F2E 1|15B conv.u2.r8 E55F6B 1|15D conv.i4.r8 E55F9B 1|15F conv.u4.r8 E55FD3 1
162 conv.i8.r8 E56041 1|163 conv.r4.i4 E5605A 1|164 conv.r4.i8 E5606B 1|165 conv.r4.r8 E5607C 1|166 conv.r8.i4 E56093 1|167 conv.r8.i8 E560A4 1
16B conv.u8.r8 E56041 1|16E conv.ovf.i1.r8 E578F8 1|171 conv.ovf.u1.r8 E5799A 1|174 conv.ovf.i2.r8 E577A3 1|177 conv.ovf.u2.r8 E57845 1
17A conv.ovf.i4.r8 E57670 1|17C conv.ovf.i4.un.r8 E570FB 1|17F conv.ovf.u4.r8 E576EA 1|180 conv.ovf.i8.r8 E571A5 1|181 conv.ovf.i8.un.r8 E57168 1
183 conv.ovf.u8.r8 E57168 1|186 ceq.r8 E58071 1|18A cgt.r8 E58118 1|18D cgt.un.r8 E581B4 1|190 clt.r8 E58261 1|193 clt.un.r8 E58301 1
194 ckfinite E579ED 1"""
for _e in _RAW.replace('\n', '|').split('|'):
    _op, _n, _h, _w = _e.split()
    OPS[_n] = (int(_op, 16), int(_h, 16), int(_w))

dbits = lambda x: struct.unpack('<Q', struct.pack('<d', x))[0]
fbits = lambda x: struct.unpack('<I', struct.pack('<f', x))[0]
D_EDGE = [dbits(v) for v in (
    0.0, -0.0, 1.0, -1.0, 0.5, -0.5, 1.5, 2.5, -2.5, 0.1, 1 / 3, 127.0, 127.5, 128.0, -128.0, -128.5, -129.0, 255.0, 255.5,
    256.0, 32767.0, 32767.5, -32768.5, 65535.0, 65535.5, 65536.0, 2147483647.0, 2147483647.5, 2147483648.0, -2147483648.0,
    -2147483648.5, -2147483649.0, 4294967295.0, 4294967295.5, 4294967296.0, 2.0 ** 53, 2.0 ** 53 + 2, 2.0 ** 63, -2.0 ** 63,
    2.0 ** 64, 1e308, -1e308, math.inf, -math.inf, 16777217.0, 1 + 2 ** -24, 1 + 2 ** -24 + 2 ** -52, 3.4028234663852886e38,
    3.4028235677973366e38, 3.402823669209385e38, 1.1754943508222875e-38, 1.401298464324817e-45, 7.006492321624085e-46, 1e-40,
    2.2250738585072014e-308, 5e-324)] + [0x7FF8000000000000, 0xFFF8000000000000, 0x7FF0000000000001, 0x7FF4000000000123,
                                          0xFFF0000000000001, 0x000FFFFFFFFFFFFF, 0x800FFFFFFFFFFFFF]
D_SMALL = [dbits(v) for v in (0.0, -0.0, 1.0, -1.0, 1 + 2 ** -52, 0.5, 1e308, -1e308, math.inf, -math.inf, 5e-324, -5e-324,
                              2.2250738585072014e-308)] + [0x7FF8000000000000, 0xFFF8000000000000, 0x7FF0000000000001, 0x7FF4000000000123]
F_EDGE = [0, 0x80000000, fbits(1.0), fbits(-1.5), 0x00000001, 0x807FFFFF, 0x00800000, 0x7F7FFFFF, 0x7F800000, 0xFF800000,
          0x7FC00000, 0x7F800001, 0xFFA00123, 0x3EAAAAAB, 0x4B800001]
I32_EDGE = [0, 1, -1, 2 ** 31 - 1, -2 ** 31, 16777217, -16777217, 2 ** 24 + 3, 123456789]
I64_EDGE = [0, 1, -1, 2 ** 63 - 1, -2 ** 63, 2 ** 53 + 1, -(2 ** 53 + 1), 2 ** 62 + 2 ** 10 + 1, 2 ** 32, 0x7FFFFFFFFFFFFC00,
            0x7FFFFFFFFFFFFE00, -0x7FFFFFFFFFFFFE00, 0x00000000FFFFFFFF]


def double_rounding_pairs():
    """a * b whose exact product, rounded to 53 bits first, lands on a tie of the subnormal grid (x87 at 53-bit precision
    then rounds to even, correctly rounded would go up): (1 + 2^-52)^2 * 2^-1024 and scaled / signed variants."""
    out = []
    for x, y in ((1, 1), (1, 3), (3, 1), (5, 3)):
        for t in range(-6, 7, 3):
            a = (1 + x * 2.0 ** -52) * 2.0 ** (-512 + t)
            b = (1 + y * 2.0 ** -52) * 2.0 ** (-512 - t)
            ex = Fraction(a) * Fraction(b)
            e = ex.numerator.bit_length() - ex.denominator.bit_length()
            q = ex / Fraction(2) ** (e - 52)
            while q >= 2 ** 53:
                q, e = q / 2, e + 1
            while q < 2 ** 52:
                q, e = q * 2, e - 1
            m = math.floor(q)
            r = q - m
            m += 1 if r > Fraction(1, 2) or (r == Fraction(1, 2) and m & 1) else 0
            if float(Fraction(m) * Fraction(2) ** (e - 52)) != a * b:
                for s in (1, -1):
                    out.append((dbits(s * a), dbits(b)))
    return out


def ub(v, n):
    return v & ((1 << n) - 1)


def cases(rng, n):
    out = []
    rnd_d = lambda: dbits(rng.choice([rng.uniform(-1e3, 1e3), rng.uniform(-1, 1) * 2.0 ** rng.randint(-1074, 1023), rng.uniform(-2 ** 33, 2 ** 33)]))

    def add(name, label, **kw):
        out.append(dict(op=name, label=f'{name} {label}', cw=kw.pop('cw', CW_DEFAULT), flag=kw.pop('flag', 1), **kw))

    for name in OPS:
        if name in ('add.r8', 'sub.r8', 'mul.r8', 'div.r8'):
            for a in D_EDGE:
                for b in D_EDGE:
                    add(name, f'{a:#x} {b:#x}', vals=[a, b])
            for cw in CWS[1:]:
                for a in D_SMALL + [dbits(0.1), dbits(1 / 3)]:
                    for b in D_SMALL + [dbits(3.0), dbits(0.7)]:
                        add(name, f'cw {cw:#x} {a:#x} {b:#x}', vals=[a, b], cw=cw)
            if name == 'mul.r8':
                for a, b in double_rounding_pairs():
                    for cw in (CW_DEFAULT, 0x037F):
                        add(name, f'double rounding cw {cw:#x} {a:#x} {b:#x}', vals=[a, b], cw=cw)
            for _ in range(n * 5):
                add(name, 'random', vals=[rnd_d(), rnd_d()], cw=rng.choice(CWS))
        elif name in CMP_REL or (name.startswith('b') and '.r8' in name):
            for a in D_SMALL:
                for b in D_SMALL:
                    add(name, f'{a:#x} {b:#x}', vals=[a, b], operand=rng.choice([5, -7, 300]) if not name.startswith('br') else -9)
            if name.startswith('br'):
                for a in D_EDGE:
                    add(name, f'{a:#x}', vals=[a], operand=11)
        elif name in ('ldc.r4',):
            for f in F_EDGE + [rng.getrandbits(32) for _ in range(n)]:
                add(name, f'{f:#x}', operand=f, vals=[])
        elif name in ('conv.r4.i4', 'conv.r8.i4', 'conv.r.un.i4'):
            for cw in CWS:
                for v in I32_EDGE + [rng.getrandbits(32) for _ in range(n)]:
                    add(name, f'cw {cw:#x} {v}', vals=[(0xDEADBEEF << 32) | ub(v, 32)], cw=cw)
        elif name in ('conv.r4.i8', 'conv.r8.i8', 'conv.r.un.i8'):
            for cw in CWS:
                for v in I64_EDGE + [rng.getrandbits(64) for _ in range(n)]:
                    add(name, f'cw {cw:#x} {v}', vals=[ub(v, 64)], cw=cw)
        elif name.startswith('conv.') or name in ('neg.r8', 'ckfinite'):
            flags = (1, 0) if name in ('conv.i1.r8', 'conv.i2.r8', 'conv.i4.r8', 'conv.ovf.i1.r8', 'conv.ovf.i2.r8', 'conv.ovf.i4.r8') else (1,)
            for flag in flags:
                for cw in CWS:
                    for v in D_EDGE + [rnd_d() for _ in range(n)]:
                        add(name, f'flag {flag} cw {cw:#x} {v:#x}', vals=[(v & 0xFFFFFFFF) | (v & ~0xFFFFFFFF)], cw=cw, flag=flag)
        else:  # moves
            src = F_EDGE if '.r4' in name and (name.startswith('ld') or name.startswith('vtld')) else D_EDGE
            for cw in (CW_DEFAULT, 0x007F, 0x067F, 0x0A7F, 0x0E7F) if '.r4' in name and name.startswith('st') else (CW_DEFAULT,):
                for v in src:
                    kw = dict(operand=8 if 'arg' in name or 'loc' in name else 12, cw=cw, vtsize=12)
                    if 'stinarg' in name:
                        kw['operand'] = 2
                    add(name, f'cw {cw:#x} {v:#x}', vals=[v], **kw)
            if name.startswith(('ldfld', 'stfld', 'vtldfld', 'ldelem', 'stelem')):
                add(name, 'null', vals=[0x3FF0000000000000], null=True, operand=12, vtsize=12)
            if name.startswith(('ldelem', 'stelem')):
                for idx in (3, 4, -1):
                    add(name, f'index {idx}', vals=[0x400921FB54442D18], index=idx)
    return out


def frame(emu, c):
    """Mint frame for case c; returns the region map. Deterministic: the same case builds the same addresses."""
    emu.reset_heap()
    n = c['op']
    op, _, words = OPS[n]
    rng = random.Random(hash(c['label']) & 0xFFFFFFFF)
    fill = lambda a, k: emu.write(a, bytes(rng.getrandbits(8) for _ in range(k)))
    F = {}
    for k, size in (('code', 32), ('eval', 0x100), ('loc', 0x40), ('args', 0x40), ('sargs', 0x40), ('obj', 0x40), ('arr', 0x60), ('ptr', 0x10), ('vt', 0x40)):
        F[k] = emu.alloc(size)
        fill(F[k], size)
    F['aoff'], F['minv'], F['rm'] = emu.alloc(0x20), emu.alloc(0x20), emu.alloc(0x40)
    emu.write(F['aoff'], struct.pack('<8I', *[8 * i for i in range(8)]))
    emu.w32(F['minv'] + 0x10, F['args'])
    emu.w32(F['minv'] + 0x14, F['sargs'])
    emu.w32(F['rm'] + 0x1C, F['aoff'])
    emu.w32(F['arr'] + 8, 4)
    o = c.get('operand', 0)
    code = struct.pack('<H', op)
    if n == 'ldc.r4':
        code += struct.pack('<I', o)
    elif n.startswith('vtldfld'):
        code += struct.pack('<HI', o, c['vtsize'])
    elif words == 3:
        code += struct.pack('<i', o)
    elif words == 2:
        code += struct.pack('<h', o)
    emu.write(F['code'], code)
    vals = list(c['vals'])
    pv = lambda v: struct.pack('<I', ub(v, 32)) if '.r4' in n else struct.pack('<Q', v)  # r4 memory holds a float
    # the operands' memory and the stack shape per opcode
    esi = F['eval'] + 0x40
    push = lambda v: emu.write(esi + 8 * len(stack), struct.pack('<Q', v)) or stack.append(v)
    stack = []
    if n.startswith(('ldfld', 'vtldfld')):
        emu.write(F['obj'] + o, pv(vals[0]))
        push(0 if c.get('null') else F['obj'])
    elif n.startswith('stfld'):
        push(0 if c.get('null') else F['obj'])
        push(vals[0])
    elif n.startswith(('ldloc', 'ldarg')):
        emu.write((F['loc'] if 'loc' in n else F['args']) + o, pv(vals[0]))
    elif n.startswith('stinarg'):
        emu.write(F['sargs'] + 8 * o, struct.pack('<Q', vals[0]))
    elif n.startswith('ldind'):
        emu.write(F['ptr'], pv(vals[0]))
        push(F['ptr'])
    elif n.startswith('stind'):
        push(F['ptr'])
        push(vals[0])
    elif n.startswith('ldelem'):
        idx = c.get('index', 1)
        emu.write(F['arr'] + 0x10 + (4 if '.r4' in n else 8) * 1, pv(vals[0]))
        push(0 if c.get('null') else F['arr'])
        push(ub(idx, 32) | (0x55555555 << 32))
    elif n.startswith('stelem'):
        push(0 if c.get('null') else F['arr'])
        push(ub(c.get('index', 1), 32) | (0x55555555 << 32))
        push(vals[0])
    else:
        for v in vals:
            push(v)
    esi += 8 * len(stack)
    ebp = 0x30080000  # inside the emu stack region
    emu.write(ebp - 0x200, bytes(0x204))
    emu.w32(ebp - 4, F['code'])
    emu.w32(ebp - 8, F['minv'])
    emu.w32(ebp - 0xC, F['vt'] + 0x20)
    emu.w32(ebp - 0x10, F['rm'])
    emu.w32(ebp - 0x14, F['loc'])
    F.update(esi=esi, ebp=ebp, op=op)
    return F


_EMU = None
_ALLOWED_EA = set()


def handler_cfg(emu, entry):
    """Blocks of one handler fragment: from its table entry to the dispatch loop or an exception exit (emu.cfg would
    walk on through the dispatch loop into the whole interpreter). Cached in emu.functions so coverage uses it."""
    import capstone
    from emu import Function
    if entry in emu.functions:
        return emu.functions[entry]
    leaders, insns, work, seen = {entry}, {}, [entry], set()
    while work:
        a = work.pop()
        while a not in seen and a not in EXITS and INTERP[0] <= a < INTERP[1]:  # handlers and their shared tails
            seen.add(a)
            ins = next(emu.md.disasm(emu.read(a, 16), a, 1), None)
            if ins is None:  # past a no-return call on an exception path
                break
            insns[a] = ins.size
            nxt = a + ins.size
            if capstone.x86_const.X86_GRP_JUMP in ins.groups:
                t = ins.operands[0].imm & 0xFFFFFFFF if ins.operands[0].type == capstone.x86_const.X86_OP_IMM else None
                if t is not None and t not in EXITS:
                    leaders.add(t)
                    work.append(t)
                if ins.mnemonic == 'jmp':
                    break
                leaders.add(nxt)
            if ins.mnemonic in ('ret', 'int3'):
                break
            a = nxt
    f = Function(entry, f'handler_{entry:x}', sorted(l for l in leaders if l in insns), set(insns), set(), [])
    emu.functions[entry] = f
    emu.coverage.setdefault(entry, set())
    return f


def setup(emu):
    global _EMU
    from unicorn import UC_HOOK_CODE, UC_PROT_ALL
    _EMU = emu
    emu.stub('msvcr80.dll!_isnan', lambda e, a: int(((a(1) >> 20) & 0x7FF) == 0x7FF and bool(a(0) | (a(1) & 0xFFFFF))))
    emu.stub('msvcr80.dll!_finite', lambda e, a: int(((a(1) >> 20) & 0x7FF) != 0x7FF))
    for hnd in {v[1] for v in OPS.values()} | {0xE5749E, 0xE574B2, 0xE57570, 0xE57588}:
        _ALLOWED_EA.update(handler_cfg(emu, hnd).insns)
    for fn in (FTOL2_SSE, FTOL2, 0xEBBA10):
        _ALLOWED_EA.update(emu.cfg(fn).insns)
    _ALLOWED_EA.update(EXITS)
    emu.uc.mem_map(TPL_BASE, TPL_SIZE, UC_PROT_ALL)
    consts = b''.join(struct.pack('<d', v) for v in CONSTS)
    emu.write(CONST, consts)
    emu.write(CONST + 0x80, struct.pack('<dd', -0.0, 2.0 ** 64))  # conv.r.un.i8: added per sign bit
    emu._cover = [handler_cfg(emu, TARGET)]

    def stop(uc, addr, size, _):
        if addr != DISPATCH:
            emu._exit = addr
            uc.emu_stop()
    for a in EXITS:
        emu.uc.hook_add(UC_HOOK_CODE, stop, begin=a, end=a)
    check_encodings(emu)


def check_encodings(emu):
    """Every template instruction of a sample case per opcode decodes to the text the encoder meant."""
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_32)
    rng = random.Random(7)
    seen = set()
    for c in cases(rng, 1):
        for alt in (False, True):
            key = (c['op'], alt, c.get('flag'))
            if key in seen:
                continue
            seen.add(key)
            items = template(c, alt)
            if items is None:
                continue
            code, lst = assemble(items, TPL_BASE)
            for (a, size, want) in lst:
                ins = next(md.disasm(code[a - TPL_BASE:a - TPL_BASE + size], a, 1), None)
                got = f'{ins.mnemonic} {ins.op_str}'.strip() if ins else '(bad)'
                if got != want or ins.size != size:
                    raise AssertionError(f'{c["op"]}{" alt" if alt else ""}: encoded {want!r}, capstone reads {got!r}')


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
    branch = c['op'].startswith('b')
    return ('next', uc.reg_read(ux.UC_X86_REG_ESI) - F['eval'], rd('eval', 0x100), rd('loc', 0x40), rd('args', 0x40),
            rd('sargs', 0x40), rd('obj', 0x40), rd('arr', 0x60), rd('ptr', 0x10), emu.u32(F['ebp'] - 0xC) - F['vt'],
            (uc.reg_read(ux.UC_X86_REG_EDI) - F['code']) if branch else None, top, cw)


def run(emu, c):
    return _go(emu, c, OPS[c['op']][1], _ALLOWED_EA)


def _template_run(c, alt):
    items = template(c, alt)
    if items is None:
        return None
    code, _ = assemble(items + TO_DISPATCH, TPL_BASE)
    _EMU.write(TPL_BASE, code)
    _EMU.uc.ctl_remove_cache(TPL_BASE, TPL_BASE + TPL_SIZE)  # drop the previous case's translated blocks
    allowed = set(range(TPL_BASE, TPL_BASE + len(code))) | set(EXITS) | (_ALLOWED_EA if b'\xE8' in code else set())
    return _go(_EMU, c, TPL_BASE, allowed)


def reference(c):
    if c['op'] in ('rem.r8',):
        return None
    return _template_run(c, False)


def compare(got, want):
    if got == want:
        return None
    names = ('exit', 'esi', 'eval', 'locals', 'args', 'stack args', 'object', 'array', 'pointer', 'vt top', 'ip', 'x87 top', 'cw')
    diff = [f'{names[i]}: EA {g!r} / template {w!r}'[:160] for i, (g, w) in enumerate(zip(got, want)) if g != w] if len(got) == len(want) else [f'{got!r} / {want!r}']
    return '; '.join(diff)


def controls(exe=None):
    """Run the alternatives (SSE2 / integer / CLI-correct) on the same cases: count where they differ from EA, by opcode."""
    sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    from emu import Emu
    emu = Emu(exe)
    setup(emu)
    per = {}
    for c in cases(random.Random(1), CASES):
        if template(c, True) is None:
            continue
        ea = run(emu, c)
        alt = _template_run(c, True)
        k = per.setdefault(c['op'], [0, 0, []])
        k[0] += 1
        if alt != ea:
            k[1] += 1
            if len(k[2]) < 3:
                k[2].append(c['label'].split(' ', 1)[1])
    for op, (n, d, ex) in per.items():
        print(f'{op:16} {d:5} of {n:5} differ' + (f'  e.g. {"; ".join(ex)}' if ex else ''))


if __name__ == '__main__':
    if '--controls' in sys.argv:
        controls(os.environ.get('TS3W_EXE'))
    else:
        print(__doc__)
