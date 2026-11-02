// docs/506 §6, docs/521: converts the core mod's IL text patches (Find / Replace blocks of ildasm listing text, one
// PowerShell file per build) into IL patch specs (docs/506 §1): the method, its body hash per build, the inserted
// instructions with their tokens resolved per build, and an offline application of the spec whose result is compared
// with the core build's own body of that method (the converter's self-check). Reads the private core repo; writes specs
// and a census only. No EA bytes go into a spec: the body is named by size + SHA-256 and the G-M6 hash.
// Usage:
//   node tools/ilpatch-convert.ts [--core <Core repo>] [--only <file substring>]... [--write] [--csv <file>]
//        --core  the core repo (or TS3_CORE_REPO): patches\*.ps1, out\<assembly>.dll = the core build
//        --only  convert the files whose name contains the text (default: every file; the census lists them all)
//        --write write mods/ilpatch/specs/<id>.json for the converted entries (default: report only)
//        --csv   census per entry (default docs/data/ilpatch-convert.csv with --write, else none)
//   node tools/ilpatch-convert.ts --self-check     relocator and alignment on synthetic bodies (no files)
//   node tools/ilpatch-convert.ts --emit-native [<out.h>]   the specs as a C table for ts3native (default
//        native/ts3native/ilpatch-specs.h, docs/526); reads mods/ilpatch/specs only, no core repo needed
// Builds: ea-1.67 = game-copy/assemblies (always); simler90-242 = game-copy/simler90-242/dll over EA, when present
// (extract with tools/extract-assemblies.ts from the UPDATE242 package). The core patches were written against the
// UPDATE242 listing, so the method is located there first, then by id in the other builds.
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';
import { ilShape, ilShapeBody, loadStandalone, tokenText, type Asm, type Method } from './managed-callgraph.ts';

const ROOT = resolve(import.meta.dirname, '..');
const arg = (n: string, d: string) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : d; };
const args = (n: string) => process.argv.flatMap((v, i) => (v === n ? [process.argv[i + 1]] : []));
const CORE = arg('--core', process.env.TS3_CORE_REPO ?? '');
if (!CORE && !process.argv.includes('--self-check') && !process.argv.includes('--emit-native')) { console.error('ilpatch-convert: --core <Core repo> (or TS3_CORE_REPO) is required'); process.exit(2); }
const WRITE = process.argv.includes('--write');
const SPECS = join(ROOT, 'mods/ilpatch/specs');
const GAME_ASMS = new Set(['ScriptCore', 'SimIFace', 'Sims3GameplaySystems', 'Sims3GameplayObjects', 'Sims3Metadata', 'Sims3StoreObjects', 'UI']);

// ---------- opcodes (ECMA-335 III): name, code, operand kind ----------
type Kind = 'none' | 'i8' | 'u8' | 'u16' | 'i32' | 'i64' | 'f32' | 'f64' | 'br8' | 'br32' | 'tok' | 'switch';
type Op = { name: string; code: number; kind: Kind; pop: number; push: number };
const OPS: Op[] = [];
const BY_NAME = new Map<string, Op>(), BY_CODE = new Map<number, Op>();
function def(spec: string, kind: Kind, pop: number, push: number) {
  for (const part of spec.split(';')) {
    const [name, hex] = part.trim().split(/\s+/); if (!name) continue;
    const o: Op = { name, code: parseInt(hex, 16), kind, pop, push }; OPS.push(o); BY_NAME.set(name, o); BY_CODE.set(o.code, o);
  }
}
def('nop 00; break 01', 'none', 0, 0);
def('ldarg.0 02; ldarg.1 03; ldarg.2 04; ldarg.3 05; ldloc.0 06; ldloc.1 07; ldloc.2 08; ldloc.3 09', 'none', 0, 1);
def('stloc.0 0a; stloc.1 0b; stloc.2 0c; stloc.3 0d', 'none', 1, 0);
def('ldarg.s 0e; ldarga.s 0f; ldloc.s 11; ldloca.s 12', 'u8', 0, 1); def('starg.s 10; stloc.s 13', 'u8', 1, 0);
def('ldnull 14; ldc.i4.m1 15; ldc.i4.0 16; ldc.i4.1 17; ldc.i4.2 18; ldc.i4.3 19; ldc.i4.4 1a; ldc.i4.5 1b; ldc.i4.6 1c; ldc.i4.7 1d; ldc.i4.8 1e', 'none', 0, 1);
def('ldc.i4.s 1f', 'i8', 0, 1); def('ldc.i4 20', 'i32', 0, 1); def('ldc.i8 21', 'i64', 0, 1); def('ldc.r4 22', 'f32', 0, 1); def('ldc.r8 23', 'f64', 0, 1);
def('dup 25', 'none', 1, 2); def('pop 26', 'none', 1, 0); def('jmp 27', 'tok', 0, 0); def('call 28; callvirt 6f; newobj 73', 'tok', 0, 0); def('calli 29', 'tok', 0, 0);
def('ret 2a', 'none', 0, 0);
def('br.s 2b; leave.s de', 'br8', 0, 0); def('brfalse.s 2c; brtrue.s 2d', 'br8', 1, 0);
def('beq.s 2e; bge.s 2f; bgt.s 30; ble.s 31; blt.s 32; bne.un.s 33; bge.un.s 34; bgt.un.s 35; ble.un.s 36; blt.un.s 37', 'br8', 2, 0);
def('br 38; leave dd', 'br32', 0, 0); def('brfalse 39; brtrue 3a', 'br32', 1, 0);
def('beq 3b; bge 3c; bgt 3d; ble 3e; blt 3f; bne.un 40; bge.un 41; bgt.un 42; ble.un 43; blt.un 44', 'br32', 2, 0);
def('switch 45', 'switch', 1, 0);
def('ldind.i1 46; ldind.u1 47; ldind.i2 48; ldind.u2 49; ldind.i4 4a; ldind.u4 4b; ldind.i8 4c; ldind.i 4d; ldind.r4 4e; ldind.r8 4f; ldind.ref 50', 'none', 1, 1);
def('stind.ref 51; stind.i1 52; stind.i2 53; stind.i4 54; stind.i8 55; stind.r4 56; stind.r8 57; stind.i df', 'none', 2, 0);
def('add 58; sub 59; mul 5a; div 5b; div.un 5c; rem 5d; rem.un 5e; and 5f; or 60; xor 61; shl 62; shr 63; shr.un 64', 'none', 2, 1);
def('neg 65; not 66; conv.i1 67; conv.i2 68; conv.i4 69; conv.i8 6a; conv.r4 6b; conv.r8 6c; conv.u4 6d; conv.u8 6e; conv.r.un 76', 'none', 1, 1);
def('cpobj 70', 'tok', 2, 0); def('ldobj 71', 'tok', 1, 1); def('ldstr 72', 'tok', 0, 1); def('castclass 74; isinst 75; unbox 79; box 8c; unbox.any a5', 'tok', 1, 1);
def('throw 7a', 'none', 1, 0); def('ldfld 7b; ldflda 7c', 'tok', 1, 1); def('stfld 7d', 'tok', 2, 0); def('ldsfld 7e; ldsflda 7f', 'tok', 0, 1); def('stsfld 80', 'tok', 1, 0); def('stobj 81', 'tok', 2, 0);
def('conv.ovf.i1.un 82; conv.ovf.i2.un 83; conv.ovf.i4.un 84; conv.ovf.i8.un 85; conv.ovf.u1.un 86; conv.ovf.u2.un 87; conv.ovf.u4.un 88; conv.ovf.u8.un 89; conv.ovf.i.un 8a; conv.ovf.u.un 8b', 'none', 1, 1);
def('newarr 8d', 'tok', 1, 1); def('ldlen 8e', 'none', 1, 1); def('ldelema 8f', 'tok', 2, 1);
def('ldelem.i1 90; ldelem.u1 91; ldelem.i2 92; ldelem.u2 93; ldelem.i4 94; ldelem.u4 95; ldelem.i8 96; ldelem.i 97; ldelem.r4 98; ldelem.r8 99; ldelem.ref 9a', 'none', 2, 1);
def('stelem.i 9b; stelem.i1 9c; stelem.i2 9d; stelem.i4 9e; stelem.i8 9f; stelem.r4 a0; stelem.r8 a1; stelem.ref a2', 'none', 3, 0);
def('ldelem a3', 'tok', 2, 1); def('stelem a4', 'tok', 3, 0);
def('conv.ovf.i1 b3; conv.ovf.u1 b4; conv.ovf.i2 b5; conv.ovf.u2 b6; conv.ovf.i4 b7; conv.ovf.u4 b8; conv.ovf.i8 b9; conv.ovf.u8 ba', 'none', 1, 1);
def('refanyval c2', 'tok', 1, 1); def('ckfinite c3', 'none', 1, 1); def('mkrefany c6', 'tok', 1, 1); def('ldtoken d0', 'tok', 0, 1);
def('conv.u2 d1; conv.u1 d2; conv.i d3; conv.ovf.i d4; conv.ovf.u d5', 'none', 1, 1);
def('add.ovf d6; add.ovf.un d7; mul.ovf d8; mul.ovf.un d9; sub.ovf da; sub.ovf.un db', 'none', 2, 1);
def('endfinally dc', 'none', 0, 0); def('conv.u e0', 'none', 1, 1);
def('arglist fe00', 'none', 0, 1); def('ceq fe01; cgt fe02; cgt.un fe03; clt fe04; clt.un fe05', 'none', 2, 1);
def('ldftn fe06', 'tok', 0, 1); def('ldvirtftn fe07', 'tok', 1, 1);
def('ldarg fe09; ldarga fe0a; ldloc fe0c; ldloca fe0d', 'u16', 0, 1); def('starg fe0b; stloc fe0e', 'u16', 1, 0);
def('localloc fe0f', 'none', 1, 1); def('endfilter fe11', 'none', 1, 0); def('unaligned. fe12', 'u8', 0, 0); def('volatile. fe13; tail. fe14; readonly. fe1e', 'none', 0, 0);
def('initobj fe15', 'tok', 1, 0); def('constrained. fe16', 'tok', 0, 0); def('cpblk fe17', 'none', 3, 0); def('initblk fe18', 'none', 3, 0); def('no. fe19', 'u8', 0, 0);
def('rethrow fe1a', 'none', 0, 0); def('sizeof fe1c', 'tok', 0, 1); def('refanytype fe1d', 'none', 1, 1);
const SIZE: Record<Kind, number> = { none: 0, i8: 1, u8: 1, u16: 2, i32: 4, i64: 8, f32: 4, f64: 8, br8: 1, br32: 4, tok: 4, switch: -1 };
const isBr = (o: Op) => o.kind === 'br8' || o.kind === 'br32';
// the long form of a branch (br.s -> br, leave.s -> leave); other opcodes unchanged
const longForm = (o: Op): Op => o.kind === 'br8' ? BY_CODE.get(o.code === 0xde ? 0xdd : o.code + 0x0d)! : o;

// ---------- a decoded body ----------
type Ins = { at: number; op: Op; size: number; v: number; big?: bigint; raw?: Buffer; targets?: number[] };
type Clause = { flags: number; tryOff: number; tryLen: number; hOff: number; hLen: number; x: number };
type Body = { code: Buffer; maxStack: number; localsTok: number; initLocals: boolean; clauses: Clause[]; ins: Ins[] };

function decode(code: Buffer): Ins[] {
  const ins: Ins[] = []; let p = 0;
  while (p < code.length) {
    const at = p; let c = code[p++]; if (c === 0xfe) c = 0xfe00 | code[p++];
    const op = BY_CODE.get(c); if (!op) throw new Error(`opcode 0x${c.toString(16)} at ${at}`);
    const x: Ins = { at, op, size: 0, v: 0 };
    switch (op.kind) {
      case 'none': break;
      case 'i8': x.v = code.readInt8(p); p += 1; break;
      case 'u8': x.v = code[p]; p += 1; break;
      case 'u16': x.v = code.readUInt16LE(p); p += 2; break;
      case 'i32': case 'tok': x.v = code.readInt32LE(p); p += 4; break;
      case 'f32': x.raw = Buffer.from(code.subarray(p, p + 4)); p += 4; break;
      case 'i64': case 'f64': x.raw = Buffer.from(code.subarray(p, p + 8)); x.big = code.readBigInt64LE(p); p += 8; break;
      case 'br8': x.v = p + 1 + code.readInt8(p); p += 1; break;      // v = absolute target
      case 'br32': x.v = p + 4 + code.readInt32LE(p); p += 4; break;
      case 'switch': { const n = code.readUInt32LE(p); const base = p + 4 + 4 * n; x.targets = []; for (let k = 0; k < n; k++) x.targets.push(base + code.readInt32LE(p + 4 + 4 * k)); p = base; break; }
    }
    x.size = p - at; ins.push(x);
  }
  return ins;
}
// A method body at p0 of buf (II.25.4): tiny or fat header, code, fat or small exception sections.
function readBody(b: Buffer, p0: number): Body {
  if ((b[p0] & 3) === 2) { const len = b[p0] >> 2; const code = Buffer.from(b.subarray(p0 + 1, p0 + 1 + len)); return { code, maxStack: 8, localsTok: 0, initLocals: false, clauses: [], ins: decode(code) }; }
  const flags = b.readUInt16LE(p0), start = p0 + (flags >> 12) * 4, len = b.readUInt32LE(p0 + 4);
  const code = Buffer.from(b.subarray(start, start + len)); const clauses: Clause[] = [];
  let more = (flags & 0x08) !== 0;
  for (let p = (start + len + 3) & ~3; more; ) {
    const kind = b[p], fat = (kind & 0x40) !== 0, size = fat ? b.readUIntLE(p + 1, 3) : b[p + 1];
    more = (kind & 0x80) !== 0;
    if (kind & 0x01) for (let c = p + 4; c + (fat ? 24 : 12) <= p + size; c += fat ? 24 : 12)
      clauses.push(fat ? { flags: b.readUInt32LE(c), tryOff: b.readUInt32LE(c + 4), tryLen: b.readUInt32LE(c + 8), hOff: b.readUInt32LE(c + 12), hLen: b.readUInt32LE(c + 16), x: b.readUInt32LE(c + 20) }
        : { flags: b.readUInt16LE(c), tryOff: b.readUInt16LE(c + 2), tryLen: b[c + 4], hOff: b.readUInt16LE(c + 5), hLen: b[c + 7], x: b.readUInt32LE(c + 8) });
    p += (size + 3) & ~3;
  }
  return { code, maxStack: b.readUInt16LE(p0 + 2), localsTok: b.readUInt32LE(p0 + 8), initLocals: (flags & 0x10) !== 0, clauses, ins: decode(code) };
}
const rvaOf = (a: Asm, r: number) => { for (const s of a.sections) if (r >= s.va && r < s.va + s.size) return r - s.va + s.raw; throw new Error(`${a.name}: rva 0x${r.toString(16)}`); };
const bodyOf = (m: Method): Body | null => (m.rva && !(m.impl & 0x1003) && !(m.flags & 0x2400) ? readBody(m.asm.buf, rvaOf(m.asm, m.rva)) : null);
const sha = (b: Buffer) => createHash('sha256').update(b).digest('hex');

// ---------- ildasm text: lines, operands, member names ----------
type Line = { label?: number; op: Op; operand: string; text: string; name?: string };  // name: one of our labels (B77_GONE), not an IL_ offset
// Lines of a Find or Replace block: "IL_xxxx:  opcode  operand" (EA's) or "opcode operand" (ours); "//" comments and blanks
// dropped; a line that starts with neither a label nor an opcode continues the operand of the line before (ildasm wraps
// long parameter lists).
type Block = Line[] & { tail?: string };  // tail: our label on the block's last line (it names the instruction after the block)
function parseBlock(text: string): Block {
  const out: Block = [];
  let pending: string | undefined; // our label on a line of its own ("B77_GONE:"): it names the next instruction
  for (const raw of text.split(/\r?\n/)) {
    let t = raw.trim(); if (!t || t.startsWith('//') || t.startsWith('}')) continue;
    const lab = /^([A-Za-z_]\w*):\s*(.*)$/.exec(t);
    if (lab && !/^IL_[0-9a-fA-F]+$/.test(lab[1]) && !BY_NAME.has(lab[1])) { pending = lab[1]; t = lab[2].trim(); if (!t) continue; }
    const m = /^(?:IL_([0-9a-fA-F]+):\s*)?(\S+)(?:\s+(.*))?$/.exec(t);
    if (m && BY_NAME.has(m[2])) { out.push({ label: m[1] != null ? parseInt(m[1], 16) : undefined, op: BY_NAME.get(m[2])!, operand: (m[3] ?? '').trim(), text: t, name: pending }); pending = undefined; continue; }
    if (!out.length) throw new Error(`the block starts inside an operand (${t.slice(0, 40)}); the patch anchors on a wrapped line`);
    out[out.length - 1].operand = `${out[out.length - 1].operand} ${t}`.trim(); // continuation
  }
  if (pending) out.tail = pending;
  return out;
}
const PRIMS: Record<string, string> = { int32: 'int', uint32: 'uint', int64: 'long', uint64: 'ulong', int16: 'short', uint16: 'ushort', int8: 'sbyte', uint8: 'byte', float32: 'float', float64: 'double', 'native int': 'IntPtr', 'native unsigned int': 'UIntPtr', 'unsigned int32': 'uint', 'unsigned int64': 'ulong', 'unsigned int16': 'ushort', 'unsigned int8': 'byte', typedref: 'typedref' };
// A type name as ildasm writes it or as managed-callgraph renders it, to one form: no assembly prefixes, no class /
// valuetype keywords, nested types with '+', the reader's primitive names, no spaces, custom modifiers dropped.
function normType(s: string): string {
  let t = s.trim();
  t = t.replace(/\b(native unsigned int|native int|unsigned int32|unsigned int64|unsigned int16|unsigned int8)\b/g, (k) => PRIMS[k]);
  t = t.replace(/\[[A-Za-z0-9_.]+\]/g, '').replace(/\b(class|valuetype)\s+/g, '').replace(/\b(modopt|modreq)\s*\([^)]*\)\s*/g, '');
  t = t.replace(/\b(int32|uint32|int64|uint64|int16|uint16|int8|uint8|float32|float64|typedref)\b/g, (k) => PRIMS[k]);
  return t.replace(/\//g, '+').replace(/\s+/g, '');
}
// Split at top-level commas (generic arguments stay together).
function splitTop(s: string): string[] { const o: string[] = []; let d = 0, st = 0; for (let i = 0; i < s.length; i++) { const ch = s[i]; if (ch === '<' || ch === '(' || ch === '[') d++; else if (ch === '>' || ch === ')' || ch === ']') d--; else if (ch === ',' && d === 0) { o.push(s.slice(st, i)); st = i + 1; } } if (s.trim()) o.push(s.slice(st)); return o.map((x) => x.trim()).filter(Boolean); }
type MemberRef = { kind: 'method' | 'field' | 'type'; key: string; instance: boolean; params: number; ret: string };
// "instance class X Owner::Name<GArgs>(params)" | "type Owner::field" | "type" to a comparable key.
function parseMember(kind: 'method' | 'field' | 'type', text: string): MemberRef {
  if (kind === 'type') return { kind, key: normType(text), instance: false, params: 0, ret: '' };
  let t = text.trim(); const instance = /^instance\s/.test(t); if (instance) t = t.slice(9).trim();
  // the owner is the bracket-balanced word before "::" at depth 0; what precedes it is the return / field type
  let depth = 0, sep = -1;
  for (let i = 0; i < t.length; i++) { const ch = t[i]; if (ch === '<' || ch === '(' || ch === '[') depth++; else if (ch === '>' || ch === ')' || ch === ']') depth--; else if (depth === 0 && ch === ':' && t[i + 1] === ':') { sep = i; break; } }
  if (sep < 0) throw new Error(`no "::" in ${text}`);
  let j = sep - 1; depth = 0;
  for (; j >= 0; j--) { const ch = t[j]; if (ch === '>' || ch === ')' || ch === ']') depth++; else if (ch === '<' || ch === '(' || ch === '[') depth--; else if (ch === ' ' && depth === 0) break; }
  const owner = t.slice(j + 1, sep), ret = normType(t.slice(0, j + 1)); let rest = t.slice(sep + 2);
  if (kind === 'field') return { kind, key: `${normType(owner)}::${rest.trim()}`, instance, params: 0, ret };
  let name = rest, gargs: string[] = [], params: string[] = [];
  const po = (() => { let d = 0; for (let i = 0; i < rest.length; i++) { const ch = rest[i]; if (ch === '<' || ch === '[') d++; else if (ch === '>' || ch === ']') d--; else if (ch === '(' && d === 0) return i; } return -1; })();
  if (po >= 0) { params = splitTop(rest.slice(po + 1, rest.lastIndexOf(')'))); name = rest.slice(0, po); }
  const go = name.indexOf('<'); if (go >= 0) { gargs = splitTop(name.slice(go + 1, name.lastIndexOf('>'))); name = name.slice(0, go); }
  const key = `${normType(owner)}::${name.trim()}${gargs.length ? '`' + gargs.length : ''}(${params.map(normType).join(',')})${gargs.length ? '<' + gargs.map(normType).join(',') + '>' : ''}`;
  return { kind, key, instance, params: params.length, ret };
}
const TYPE_OPS = new Set(['cpobj', 'ldobj', 'castclass', 'isinst', 'unbox', 'stobj', 'box', 'newarr', 'ldelema', 'ldelem', 'stelem', 'unbox.any', 'refanyval', 'mkrefany', 'initobj', 'constrained.', 'sizeof']);
const FIELD_OPS = new Set(['ldfld', 'ldflda', 'stfld', 'ldsfld', 'ldsflda', 'stsfld']);
const memberKind = (o: Op): 'method' | 'field' | 'type' | 'other' => TYPE_OPS.has(o.name) ? 'type' : FIELD_OPS.has(o.name) ? 'field' : ['call', 'callvirt', 'newobj', 'jmp', 'ldftn', 'ldvirtftn'].includes(o.name) ? 'method' : o.name === 'ldtoken' ? 'type' : 'other';

// ---------- tokens of one build: key -> token ----------
type TokIndex = { method: Map<string, number[]>; field: Map<string, number[]>; type: Map<string, number[]> };
const tokIndexes = new WeakMap<Asm, TokIndex>();
// tokenText renders a method token as "Owner::Name`n(params):ret" (+ "<gargs>" for a MethodSpec), a field as "Owner::name:type".
function keyOfTokenText(kind: 'method' | 'field' | 'type', s: string): string | null {
  if (kind === 'type') return normType(s);
  if (kind === 'field') { const m = /^(.*)::([^:]+?)(?::(.*))?$/.exec(s); return m ? `${normType(m[1])}::${m[2]}` : null; } // a Field id has no ":type"
  let gargs = ''; if (s.endsWith('>')) { let d = 0; for (let i = s.length - 1; i >= 0; i--) { if (s[i] === '>') d++; else if (s[i] === '<') { d--; if (!d) { gargs = s.slice(i + 1, -1); s = s.slice(0, i); break; } } } }
  const m = /^(.*?)::([^:(]+?)(`\d+)?\((.*)\)(?::([^:]*))?$/.exec(s); if (!m) return null; // a MethodDef id has no ":ret"
  return `${normType(m[1])}::${m[2]}${m[3] ?? ''}(${splitTop(m[4]).map(normType).join(',')})${gargs ? '<' + splitTop(gargs).map(normType).join(',') + '>' : ''}`;
}
function tokIndex(a: Asm): TokIndex {
  let ix = tokIndexes.get(a); if (ix) return ix;
  ix = { method: new Map(), field: new Map(), type: new Map() }; tokIndexes.set(a, ix);
  const add = (m: Map<string, number[]>, k: string | null, tok: number) => { if (k) (m.get(k) ?? m.set(k, []).get(k)!).push(tok); };
  a.methods.forEach((m, i) => add(ix!.method, `${normType(m.type.full)}::${m.name}${m.arity ? '`' + m.arity : ''}(${splitTop(m.params).map(normType).join(',')})`, 0x06000000 + i + 1));
  a.fields.forEach((f, i) => add(ix!.field, keyOfTokenText('field', f.id + ':x'), 0x04000000 + i + 1));
  for (const [t, kind] of [[0x0a, 'member'], [0x2b, 'method'], [0x01, 'type'], [0x02, 'type'], [0x1b, 'type']] as const) {
    const n = a.tables[t]?.length ?? 0;
    for (let i = 1; i <= n; i++) {
      const tok = (t << 24) | i; let s: string; try { s = tokenText(a, tok); } catch { continue; }
      if (kind === 'member') { if (/\(.*\)/.test(s)) add(ix.method, keyOfTokenText('method', s), tok); else add(ix.field, keyOfTokenText('field', s), tok); }
      else if (kind === 'method') add(ix.method, keyOfTokenText('method', s), tok);
      else add(ix.type, keyOfTokenText('type', s), tok);
    }
  }
  return ix;
}
// The token of a member in one build: a MethodDef / Field / TypeDef of the module first, else the one reference; several
// references to one member (duplicate MemberRefs) take the first; a member the module never references has none.
function tokenFor(a: Asm, ref: MemberRef): number | null {
  const list = tokIndex(a)[ref.kind].get(ref.key); if (!list?.length) return null;
  const own = list.find((t) => (t >>> 24) === 0x06 || (t >>> 24) === 0x04 || (t >>> 24) === 0x02); return own ?? list[0];
}

// ---------- the patch files ----------
type Entry = { file: string; name: string; asm: string; find: string; replace: string; addsMembers: boolean };
function readPatchFiles(dir: string, only: string[]): Entry[] {
  const out: Entry[] = [];
  for (const f of readdirSync(dir).filter((n) => n.endsWith('.ps1')).sort()) {
    if (only.length && !only.some((o) => f.includes(o))) continue;
    const text = readFileSync(join(dir, f), 'utf8');
    for (const chunk of text.split(/@\{/).slice(1)) {
      const get = (k: string) => { const m = new RegExp(`\\b${k}\\s*=\\s*(?:'([^']*)'|@'\\r?\\n([\\s\\S]*?)\\r?\\n\\s*'@)`).exec(chunk); return m ? (m[1] ?? m[2]) : undefined; };
      const name = get('Name'), asm = get('File'), find = get('Find'), replace = get('Replace');
      if (name == null || asm == null || find == null || replace == null) continue;
      out.push({ file: f, name, asm, find, replace, addsMembers: /^\s*\.(method|field|class|property|event)\b/m.test(replace) });
    }
  }
  return out;
}

// ---------- locate the method of a Find block ----------
function operandMatches(x: Ins, l: Line, a: Asm): boolean {
  const k = memberKind(l.op);
  if (isBr(l.op)) return x.v === parseInt(l.operand.replace(/^IL_/, ''), 16);
  if (l.op.kind === 'tok') {
    if (k === 'other') return true; // ldstr, calli: opcode only
    const key = keyOfTokenText(k, tokenText(a, x.v)); return key != null && key === parseMember(k, l.operand).key;
  }
  if (l.op.kind === 'i8' || l.op.kind === 'i32') return x.v === parseInt(l.operand, 10);
  if (l.op.kind === 'u8' || l.op.kind === 'u16') { const m = /^V_(\d+)$/.exec(l.operand); return m ? x.v === +m[1] : true; } // V_n = local n; argument names: opcode only
  return true;
}
const sameOp = (x: Ins, l: Line) => x.op === l.op || longForm(x.op) === longForm(l.op);
function locate(a: Asm, find: Line[]): { m: Method; body: Body }[] {
  const labelled = find.filter((l) => l.label != null); if (!labelled.length) return [];
  const first = labelled[0], hits: { m: Method; body: Body }[] = [];
  for (const m of a.methods) {
    if (!m.rva || m.impl & 0x1003 || m.flags & 0x2400) continue;
    const p0 = rvaOf(a, m.rva), b = a.buf, start = (b[p0] & 3) === 2 ? p0 + 1 : p0 + (b.readUInt16LE(p0) >> 12) * 4, len = (b[p0] & 3) === 2 ? b[p0] >> 2 : b.readUInt32LE(p0 + 4);
    if (first.label! >= len) continue;
    const c = b[start + first.label!]; if (c !== (first.op.code & 0xff) && !(first.op.code > 0xff && c === 0xfe)) continue;
    let body: Body; try { body = readBody(b, p0); } catch { continue; }
    const at = new Map(body.ins.map((x) => [x.at, x]));
    if (labelled.every((l) => { const x = at.get(l.label!); return x && x.op === l.op && operandMatches(x, l, a); })) hits.push({ m, body });
  }
  return hits;
}

// ---------- alignment: Find vs Replace -> edits ----------
type Edit = { at: number; mode: 'insert' | 'replace'; entry: 'fallthrough' | 'incoming'; expect: string[]; il: string[]; maxStack: number; drop: number };
// A branch of ours: "@0x53" for an EA offset (IL_0053), the label's name for one of our lines (B77_GONE). A labelled line is "B77_GONE: op ...".
const brText = (l: Line) => { const il = /^IL_([0-9a-fA-F]+)$/.exec(l.operand); return `${longForm(l.op).name} ${il ? '@0x' + parseInt(il[1], 16).toString(16).toUpperCase() : l.operand}`; };
const ourText = (l: Line) => (l.name ? `${l.name}: ` : '') + (isBr(l.op) ? brText(l) : l.op.kind === 'u8' || l.op.kind === 'u16' ? `${l.op.name} ${l.operand.replace(/^V_/, '')}` : `${l.op.name}${l.operand ? ' ' + l.operand : ''}`);
// A core patch's own run counter ("ldsfld F; ldc.i4.1; add; stsfld F" on a field another entry of the same file adds) has no
// token in any build; the patcher counts its applications itself, so the bump is dropped and the spec says so.
function dropCounterBumps(lines: Line[], fields: Set<string>): { lines: Line[]; dropped: string[] } {
  const fieldOf = (operand: string) => operand.slice(operand.lastIndexOf('::') + 2).trim();
  const isOne = (l: Line) => l.op.name === 'ldc.i4.1' || ((l.op.name === 'ldc.i4.s' || l.op.name === 'ldc.i4') && l.operand === '1');
  const out: Line[] = []; const dropped: string[] = []; const rename = new Map<string, string>();
  for (let i = 0; i < lines.length; i++) {
    const l = lines[i];
    if (l.op.name === 'ldsfld' && i + 3 < lines.length && fields.has(fieldOf(l.operand)) && isOne(lines[i + 1]) && lines[i + 2].op.name === 'add' && lines[i + 3].op.name === 'stsfld' && lines[i + 3].operand === l.operand && lines[i + 3].label == null) {
      dropped.push(fieldOf(l.operand));
      const names = [l, lines[i + 1], lines[i + 2], lines[i + 3]].map((x) => x.name).filter((n): n is string => !!n);
      if (names.length && i + 4 < lines.length) { const next = lines[i + 4]; if (next.name) names.forEach((n) => rename.set(n, next.name!)); else { next.name = names[0]; names.slice(1).forEach((n) => rename.set(n, names[0])); } }
      else if (names.length) { out.push(l, lines[i + 1], lines[i + 2], lines[i + 3]); dropped.pop(); continue; } // a labelled bump at the very end: kept (no line to move the label to)
      i += 3; continue;
    }
    out.push(l);
  }
  for (const l of out) if (isBr(l.op) && rename.has(l.operand)) l.operand = rename.get(l.operand)!;
  return { lines: out, dropped };
}
// Stack depth change of one of our lines; calls by their signature text.
function effect(l: Line): [number, number] {
  const k = memberKind(l.op);
  if (k === 'method' && l.op.name !== 'ldftn' && l.op.name !== 'ldvirtftn') { const r = parseMember('method', l.operand); const pop = r.params + (r.instance && l.op.name !== 'newobj' ? 1 : 0); return [pop, l.op.name === 'newobj' || r.ret !== 'void' ? 1 : 0]; }
  return [l.op.pop, l.op.push];
}
function align(find: Line[], replace: Block, body: Body): Edit[] {
  const findAt = new Map(find.filter((l) => l.label != null).map((l) => [l.label!, l]));
  const bodyAt = new Map(body.ins.map((x) => [x.at, x]));
  const lastFind = find[find.length - 1]; const after = lastFind.label != null ? lastFind.label + bodyAt.get(lastFind.label)!.size : -1;
  // One of our labels written on an EA line that stays ("FIX_HOME_OK:  IL_006c:  callvirt ...", or on the line before it)
  // names that offset: our branches to it become branches to the EA instruction. A label on an EA line that changes stays
  // ours (the line is emitted with the label).
  const isUnchanged = (r: Line) => r.label != null && findAt.has(r.label) && sameOp(bodyAt.get(r.label)!, r) && (!isBr(r.op) ? r.operand === findAt.get(r.label)!.operand : true);
  const nameAt = new Map<string, number>(); for (const r of replace) if (r.name && isUnchanged(r)) nameAt.set(r.name, r.label!);
  if (replace.tail && after >= 0) nameAt.set(replace.tail, after);
  for (const r of replace) if (r.label == null && isBr(r.op) && nameAt.has(r.operand)) r.operand = `IL_${nameAt.get(r.operand)!.toString(16).padStart(4, '0')}`;
  const edits: Edit[] = []; let ours: Line[] = []; let prev = -1;
  const flush = (at: number, dropped: number[], entry: 'fallthrough' | 'incoming') => {
    if (!ours.length && !dropped.length) return;
    const x = bodyAt.get(at); if (!x && at !== body.code.length) throw new Error(`no instruction at 0x${at.toString(16)}`);
    let depth = 0, max = 0; for (const l of ours) { const [pop, push] = effect(l); depth += push - pop; if (depth > max) max = depth; }
    edits.push({ at, mode: dropped.length ? 'replace' : 'insert', entry: dropped.length ? 'incoming' : entry, expect: dropped.length ? dropped.map((d) => bodyAt.get(d)!.op.name) : x ? [x.op.name] : [], il: ours.map(ourText), maxStack: max, drop: dropped.length });
    ours = [];
  };
  const droppedBetween = (from: number, to: number) => [...findAt.keys()].filter((o) => o > from && o < to && !replace.some((r) => r.label === o)).sort((p, q) => p - q);
  for (const r of replace) {
    if (r.label == null) { ours.push(r); continue; }
    const f = findAt.get(r.label); if (!f) throw new Error(`label IL_${r.label.toString(16)} not in Find`);
    const unchanged = sameOp(bodyAt.get(r.label)!, r) && (!isBr(r.op) ? r.operand === f.operand : true);
    const dropped = droppedBetween(prev, r.label);
    if (unchanged) { const at = dropped.length ? dropped[0] : r.label; flush(at, dropped, 'fallthrough'); }
    else {
      // A labelled line with another opcode replaces the EA instruction at that label (the Replace text is the whole
      // truth: an EA line that stays is listed again, unlabelled). Before 2026-11-02 the EA instruction was kept as well:
      // b68's "IL_0096: brfalse.s" came out as brfalse + EA's brtrue.s on one stack value (smoke 21 crashed in the transform).
      ours.push(r); const at = dropped.length ? dropped[0] : r.label; flush(at, [...dropped, r.label], 'incoming'); ours = [];
    }
    prev = r.label;
  }
  const tail = droppedBetween(prev, after + 1);
  if (ours.length || tail.length) { if (after < 0) throw new Error('our lines after the block need a labelled EA line before them'); flush(tail.length ? tail[0] : after, tail, 'fallthrough'); }
  return edits;
}

// ---------- apply a spec to a body (the offline copy of the native patcher, docs/506 §2 steps 3-5) ----------
type Resolved = { edit: Edit; ins: { op: Op; operand: string; label?: string }[] };
function assembleLine(text: string): { op: Op; operand: string; label?: string } {
  let label: string | undefined; let t = text.trim();
  const lm = /^([A-Za-z_]\w*):\s*(.*)$/.exec(t); if (lm && !BY_NAME.has(lm[1])) { label = lm[1]; t = lm[2]; }
  const m = /^(\S+)(?:\s+(.*))?$/.exec(t)!; const op = BY_NAME.get(m[1]); if (!op) throw new Error(`unknown opcode ${m[1]}`);
  return { op: longForm(op), operand: (m[2] ?? '').trim(), label };
}
function apply(body: Body, edits: Edit[], tok: (ref: MemberRef) => number | null, addMaxStack: number): { code: Buffer; full: Buffer; missing: string[] } {
  const missing: string[] = [];
  const res: Resolved[] = edits.map((e) => ({ edit: e, ins: e.il.map(assembleLine) }));
  // check: at is an instruction start with the expected opcodes
  const idx = new Map(body.ins.map((x, i) => [x.at, i]));
  for (const { edit } of res) {
    const i = idx.get(edit.at); if (i == null && edit.at !== body.code.length) throw new Error(`edit at 0x${edit.at.toString(16)} is not an instruction start`);
    edit.expect.forEach((n, k) => { if (i == null || body.ins[i + k]?.op.name !== n) throw new Error(`edit at 0x${edit.at.toString(16)}: expected ${edit.expect.join(' ')}`); });
  }
  // the new sequence: items are original instructions or ours; size first (every branch long), then offsets
  type Item = { kind: 'orig'; x: Ins; size: number; off: number } | { kind: 'ours'; r: Resolved; k: number; size: number; off: number };
  const items: Item[] = []; const byAt = new Map(res.map((r) => [r.edit.at, r]));
  for (let i = 0; i < body.ins.length; i++) {
    const x = body.ins[i], r = byAt.get(x.at);
    if (r) { r.ins.forEach((y, k) => items.push({ kind: 'ours', r, k, size: 1 + (y.op.code > 0xff ? 1 : 0) + (y.op.kind === 'switch' ? 4 + 4 * splitTop(y.operand.replace(/[()]/g, '')).length : SIZE[y.op.kind]), off: 0 })); if (r.edit.drop) { i += r.edit.drop - 1; continue; } }
    const lo = longForm(x.op);
    items.push({ kind: 'orig', x, size: x.op.kind === 'switch' ? x.size : 1 + (lo.code > 0xff ? 1 : 0) + SIZE[lo.kind], off: 0 });
  }
  const end = byAt.get(body.code.length); if (end) end.ins.forEach((y, k) => items.push({ kind: 'ours', r: end, k, size: 1 + (y.op.code > 0xff ? 1 : 0) + SIZE[y.op.kind], off: 0 }));
  let off = 0; for (const it of items) { it.off = off; off += it.size; } const newLen = off;
  // old offset -> new offset: an original instruction's new place; an edit's "at" per its entry (a fallthrough insert's own
  // branch to "@at" means the original instruction after the block, as in the core patches' `brtrue.s IL_007d`); the end stays the end
  const origOff = new Map<number, number>(); for (const it of items) if (it.kind === 'orig') origOff.set(it.x.at, it.off);
  const oursOff = new Map<Resolved, number>(); for (const it of items) if (it.kind === 'ours' && it.k === 0) oursOff.set(it.r, it.off);
  const map = (old: number, from?: Resolved): number => {
    if (old === body.code.length) return newLen;
    const r = byAt.get(old);
    if (r && (r.edit.entry === 'incoming' || r.edit.mode === 'replace')) return oursOff.get(r)!;
    if (r && r.edit.mode === 'insert') return origOff.get(old)!;
    const o = origOff.get(old); if (o == null) throw new Error(`branch target 0x${old.toString(16)} is inside a replaced range or not an instruction start`);
    return o;
  };
  const labels = new Map<string, number>(); for (const it of items) if (it.kind === 'ours' && it.r.ins[it.k].label) labels.set(it.r.ins[it.k].label!, it.off);
  const out = Buffer.alloc(newLen);
  for (const it of items) {
    let p = it.off; const op = it.kind === 'orig' ? (it.x.op.kind === 'switch' ? it.x.op : longForm(it.x.op)) : it.r.ins[it.k].op;
    if (op.code > 0xff) { out[p++] = 0xfe; out[p++] = op.code & 0xff; } else out[p++] = op.code;
    if (it.kind === 'orig') {
      const x = it.x;
      switch (x.op.kind) {
        case 'none': break;
        case 'i8': out.writeInt8(x.v, p); break; case 'u8': out[p] = x.v; break; case 'u16': out.writeUInt16LE(x.v, p); break;
        case 'i32': case 'tok': out.writeInt32LE(x.v, p); break;
        case 'f32': case 'i64': case 'f64': x.raw!.copy(out, p); break;
        case 'br8': case 'br32': out.writeInt32LE(map(x.v) - (p + 4), p); break;
        case 'switch': { const n = x.targets!.length; out.writeUInt32LE(n, p); const base = p + 4 + 4 * n; x.targets!.forEach((t, k) => out.writeInt32LE(map(t) - base, p + 4 + 4 * k)); break; }
      }
    } else {
      const y = it.r.ins[it.k], kind = memberKind(y.op);
      switch (y.op.kind) {
        case 'none': break;
        case 'i8': out.writeInt8(parseInt(y.operand, 10), p); break; case 'u8': out[p] = parseInt(y.operand, 10); break; case 'u16': out.writeUInt16LE(parseInt(y.operand, 10), p); break;
        case 'i32': out.writeInt32LE(parseInt(y.operand, 10), p); break;
        case 'i64': out.writeBigInt64LE(BigInt(y.operand), p); break; case 'f32': out.writeFloatLE(parseFloat(y.operand), p); break; case 'f64': out.writeDoubleLE(parseFloat(y.operand), p); break;
        case 'tok': { if (kind === 'other') throw new Error(`${y.op.name}: operand kind not supported in a spec`); const t = tok(parseMember(kind, y.operand)); if (t == null) { missing.push(y.operand); break; } out.writeInt32LE(t, p); break; }
        case 'br32': { const tgt = y.operand.startsWith('@') ? map(parseInt(y.operand.slice(1), 16), it.r) : labels.get(y.operand); if (tgt == null) throw new Error(`branch to ${y.operand}: unknown label`); out.writeInt32LE(tgt - (p + 4), p); break; }
        case 'switch': { const ts = splitTop(y.operand.replace(/[()]/g, '')); out.writeUInt32LE(ts.length, p); const base = p + 4 + 4 * ts.length; ts.forEach((s, k) => out.writeInt32LE((s.startsWith('@') ? map(parseInt(s.slice(1), 16), it.r) : labels.get(s)!) - base, p + 4 + 4 * k)); break; }
      }
    }
  }
  // the whole body: fat header, code, one fat exception section with every clause moved
  const clauses = body.clauses.map((c) => ({ ...c, tryOff: map(c.tryOff), tryLen: map(c.tryOff + c.tryLen) - map(c.tryOff), hOff: map(c.hOff), hLen: map(c.hOff + c.hLen) - map(c.hOff), x: c.flags === 1 ? map(c.x) : c.x }));
  const ehSize = clauses.length ? 4 + 24 * clauses.length : 0, codeEnd = 12 + newLen, ehStart = (codeEnd + 3) & ~3;
  const full = Buffer.alloc(ehStart + ehSize);
  full.writeUInt16LE(0x3003 | (clauses.length ? 0x08 : 0) | (body.initLocals ? 0x10 : 0), 0); full.writeUInt16LE(body.maxStack + addMaxStack, 2); full.writeUInt32LE(newLen, 4); full.writeUInt32LE(body.localsTok, 8);
  out.copy(full, 12);
  if (clauses.length) { full[ehStart] = 0x41; full.writeUIntLE(ehSize, ehStart + 1, 3); clauses.forEach((c, i) => { const q = ehStart + 4 + 24 * i; [c.flags, c.tryOff, c.tryLen, c.hOff, c.hLen, c.x].forEach((v, k) => full.writeUInt32LE(v >>> 0, q + 4 * k)); }); }
  return { code: out, full, missing };
}

// ---------- conversion ----------
type Build = { label: string; dir: string; asms: Map<string, Asm> };
function loadBuilds(): Build[] {
  const ea = join(ROOT, 'game-copy/assemblies'), sim = join(ROOT, 'game-copy/simler90-242/dll');
  const builds: Build[] = [{ label: 'ea-1.67', dir: ea, asms: new Map() }];
  if (existsSync(sim)) builds.push({ label: 'simler90-242', dir: sim, asms: new Map() });
  return builds;
}
function asmOf(b: Build, name: string, ea: Build): Asm | null {
  const got = b.asms.get(name); if (got) return got;
  const f = join(b.dir, `${name}.dll`); const a = existsSync(f) ? loadStandalone(f, name) : b === ea ? null : asmOf(ea, name, ea);
  if (a) b.asms.set(name, a); return a;
}
function g6Hash(dll: string, id: string): string {
  try { const out = execFileSync(process.execPath, [join(ROOT, 'tools/autshadow-il-hashes.ts'), '--hash-file', dll, id], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }); const l = out.split(/\r?\n/).find((x) => x.startsWith(id + ' ')); return l ? l.slice(id.length + 1).trim() : ''; } catch { return ''; }
}
const idOf = (file: string, n: number, total: number) => { const m = /_b(\d+)_(.+)\.ps1$/.exec(file); const base = m ? `b${m[1]}-${m[2].replace(/_/g, '-')}` : basename(file, '.ps1'); return total > 1 ? `${base}-${n + 1}` : base; };

type Row = { file: string; entry: string; asm: string; method: string; status: string; builds: string; selfCheck: string; spec: string };
function convert(): Row[] {
  const builds = loadBuilds(), ea = builds[0], base = builds[builds.length - 1];
  const entries = readPatchFiles(join(CORE, 'patches'), args('--only'));
  const rows: Row[] = []; const specs = new Map<string, { spec: any; rows: Row[]; edits: Edit[]; dropped: string[] }>();
  const coreAsms = new Map<string, Asm | null>();
  const coreAsm = (name: string) => { if (!coreAsms.has(name)) { const f = join(CORE, 'out', `${name}.dll`); coreAsms.set(name, existsSync(f) ? loadStandalone(f, name) : null); } return coreAsms.get(name)!; };
  const perFile = new Map<string, Entry[]>(); for (const e of entries) (perFile.get(e.file) ?? perFile.set(e.file, []).get(e.file)!).push(e);
  // The spec against each build: tokens, hashes, the offline application and the comparison with the core build's own body.
  const checkSpec = (spec: any, edits: Edit[], dropped: string[], m: Method, asmName: string): { okBuilds: string[]; checks: string[] } => {
    spec.builds = {};
    const maxAdd = Math.max(0, ...edits.map((x) => x.maxStack));
    const okBuilds: string[] = []; const checks: string[] = [];
    for (const b of builds) {
      const ba = asmOf(b, asmName, ea); if (!ba) continue;
      const bm = ba.methods.find((x) => x.id === m.id); const bb = bm ? bodyOf(bm) : null;
      if (!bm || !bb) { checks.push(`${b.label}: method missing`); continue; }
      let applied; try { applied = apply(bb, edits, (ref) => tokenFor(ba, ref), maxAdd); } catch (err) { checks.push(`${b.label}: ${(err as Error).message}`); continue; }
      if (applied.missing.length) { checks.push(`${b.label}: no token for ${applied.missing.join(' ; ')}`); continue; }
      const tokens: Record<string, string> = {};
      for (const ed of edits) for (const line of ed.il) { const al = assembleLine(line); const k = memberKind(al.op); if (al.op.kind === 'tok' && k !== 'other') tokens[al.operand] = '0x' + (tokenFor(ba, parseMember(k, al.operand))! >>> 0).toString(16).padStart(8, '0'); }
      spec.builds[b.label] = { size: bb.code.length, sha256: sha(bb.code), tokens, patchedSha256: sha(applied.code) };
      okBuilds.push(b.label);
      if (b === ea) spec.ilHashG6 = g6Hash(join(b.dir, `${asmName}.dll`), m.id);
      // self-check: the patched shape against the core build's own body of the method
      const ca = coreAsm(asmName); const cm = ca?.methods.find((x) => x.id === m.id);
      if (!cm) { checks.push(`${b.label}: core build has no ${m.id}`); continue; }
      const ours = ilShapeBody(ba, applied.full, 0).split('\n'), core = ilShape(cm).split('\n');
      let d = -1; for (let i = 0; i < Math.max(ours.length, core.length); i++) if (ours[i] !== core[i]) { d = i; break; }
      if (d < 0) { checks.push(`${b.label}: equal to the core build`); continue; }
      // other core patches in the same method move everything: then the weaker check, our inserted run of instructions
      // (opcodes and tokens; branch targets are instruction indices and differ) must appear in the core body
      const mask = (l: string) => l.replace(/^(2b|2c|2d|2e|2f|3[0-9a-f]|4[0-4]|dd|de) .*$/, '$1 *');
      let coreM = core.map(mask);
      if (dropped.length) { // "ldsfld F; ldc.i4.1; add; stsfld F" (7e, 17, 58, 80) on the same field: the core has it, the spec does not
        const o: string[] = []; for (let i = 0; i < coreM.length; i++) { if (coreM[i].startsWith('7e ') && coreM[i + 1] === '20 1' && coreM[i + 2].startsWith('58') && coreM[i + 3] === '80 ' + coreM[i].slice(3)) { i += 3; continue; } o.push(coreM[i]); } coreM = o;
      }
      const found = edits.every((ed) => { const run = runShape(ba, bb, ed).map(mask); return coreM.some((_, i) => run.every((l, k) => coreM[i + k] === l)); });
      checks.push(found ? `${b.label}: ${dropped.length ? `the core build has the counter bump the spec drops` : `the core build's method has other patches too`}; our instructions are in it (branch targets not compared)` : `${b.label}: differs from the core build at line ${d} (${ours[d] ?? 'end'} | ${core[d] ?? 'end'}) and our instructions are not in it`);
    }
    return { okBuilds, checks };
  };
  for (const [file, list] of perFile) {
    list.forEach((e) => {
      const row: Row = { file, entry: e.name, asm: e.asm, method: '', status: '', builds: '', selfCheck: '', spec: '' }; rows.push(row);
      if (!GAME_ASMS.has(e.asm)) { row.status = 'skip: not a game assembly'; return; }
      if (e.addsMembers) { row.status = 'needs helper: adds members (.method/.field)'; return; }
      if (/^\s*\.(maxstack|locals)\b/m.test(e.find) || /^\s*\.(maxstack|locals)\b/m.test(e.replace)) { row.status = 'whole method: the patch rewrites the header (max stack / locals); a ts3fixes wrap, not a spec'; return; }
      try {
        const find = parseBlock(e.find), parsed = parseBlock(e.replace);
        const fields = new Set(list.flatMap((x) => [...x.replace.matchAll(/^\s*\.field\s+[^\n]*?\b(\w+)\s*$/gm)].map((mm) => mm[1])));
        const { lines: replace, dropped } = dropCounterBumps(parsed, fields);
        const a = asmOf(base, e.asm, ea); if (!a) { row.status = `skip: no ${e.asm}.dll in ${base.label}`; return; }
        const hits = locate(a, find);
        if (hits.length !== 1) { row.status = hits.length ? `multi-method: ${hits.length} bodies match (${hits.slice(0, 3).map((h) => h.m.id).join(' | ')})` : 'no method match'; return; }
        const { m, body } = hits[0]; row.method = m.id;
        const edits = align(find, replace, body);
        if (!edits.length) { row.status = 'no change (Replace equals Find)'; return; }
        const key = `${e.asm}|${m.id}`; const prior = specs.get(`${file}|${key}`);
        if (prior) { // a second entry on the same method: merge the edits into that spec
          prior.edits.push(...edits); prior.edits.sort((p, q) => p.at - q.at); prior.spec.edits = prior.edits.map(edSpec); prior.spec.source.entries.push(e.name);
          prior.dropped.push(...dropped); if (prior.dropped.length) prior.spec.source.droppedCounters = prior.dropped;
          const chk = checkSpec(prior.spec, prior.edits, prior.dropped, m, e.asm); // the whole spec again, with this entry's edits in it
          row.builds = chk.okBuilds.join(' '); row.selfCheck = chk.checks.join(' / '); prior.rows.push(row); row.spec = prior.spec.id;
          row.status = chk.okBuilds.length ? 'converted (merged)' : 'unresolved (merged): no build resolves every token'; return;
        }
        const spec: any = { id: '', switch: '', counter: null, assembly: e.asm, method: m.id, ilHashG6: '', builds: {}, edits: edits.map(edSpec), source: { file, entries: [e.name], ...(dropped.length ? { droppedCounters: dropped } : {}) } };
        const chk = checkSpec(spec, edits, dropped, m, e.asm); const okBuilds = chk.okBuilds, checks = chk.checks;
        row.builds = okBuilds.join(' '); row.selfCheck = checks.join(' / ');
        if (!okBuilds.length) { row.status = 'unresolved: no build resolves every token'; return; }
        row.status = dropped.length ? `converted (counter ${dropped.join(', ')} dropped)` : 'converted'; specs.set(`${file}|${key}`, { spec, rows: [row], edits, dropped });
      } catch (err) { row.status = `error: ${(err as Error).message}`; }
    });
    // ids: the file's name (b89-fixup-no-inventory), numbered only when the file yields several specs
    const mine = [...specs.entries()].filter(([k]) => k.startsWith(`${file}|`)).map(([, v]) => v);
    mine.forEach((v, i) => { const id = idOf(file, i, mine.length); v.spec.id = id; v.spec.switch = `ilp_${id.replace(/^b\d+-/, '').replace(/-/g, '')}`; for (const r of v.rows) r.spec = id; });
  }
  if (WRITE) {
    mkdirSync(SPECS, { recursive: true });
    for (const { spec } of specs.values()) writeFileSync(join(SPECS, `${spec.id}.json`), JSON.stringify(spec, null, 2) + '\n');
  }
  return rows;
}
// The shape lines of one edit's instructions alone: the edit applied to an empty body with the same locals and tokens.
function runShape(a: Asm, like: Body, ed: Edit): string[] {
  const empty: Body = { code: Buffer.alloc(0), maxStack: like.maxStack, localsTok: like.localsTok, initLocals: like.initLocals, clauses: [], ins: [] };
  const here = new Set(ed.il.map((l) => /^(\w+): /.exec(l)?.[1]).filter((x): x is string => !!x));
  const il = ed.il.map((l) => l.replace(/@0x[0-9A-Fa-f]+/g, '@0x0').replace(/^((?:\w+: )?(?:br|brtrue|brfalse|beq|bne\.un|bge|bgt|ble|blt|bge\.un|bgt\.un|ble\.un|blt\.un|leave)) (?!@)(\S+)$/, (m, a, b) => (here.has(b) ? m : `${a} @0x0`)));
  const r = apply(empty, [{ ...ed, at: 0, mode: 'insert', entry: 'fallthrough', expect: [], il, drop: 0 }], (ref) => tokenFor(a, ref), 0);
  return ilShapeBody(a, r.full, 0).split('\n').slice(like.localsTok ? 1 : 0);
}
const edSpec = (e: Edit) => ({ at: '0x' + e.at.toString(16).toUpperCase(), mode: e.mode, entry: e.entry, expect: e.expect, maxStack: e.maxStack, il: e.il });

// ---------- self-check (synthetic bodies) ----------
function selfCheck(): void {
  // body: 0 ldarg.0 | 1 brfalse.s -> 5 | 3 ldc.i4.1 | 4 ret | 5 ldc.i4.0 | 6 ret ; try 0-4 catch 5-7 (synthetic, class token 0)
  const code = Buffer.from([0x02, 0x2c, 0x02, 0x17, 0x2a, 0x16, 0x2a]);
  const body: Body = { code, maxStack: 1, localsTok: 0, initLocals: true, clauses: [{ flags: 0, tryOff: 0, tryLen: 5, hOff: 5, hLen: 2, x: 0x01000001 }], ins: decode(code) };
  // insert 130 nops before offset 3 (fall-through entry): the short branch at 1 to 5 must become long and still land on the ldc.i4.0
  const edit: Edit = { at: 3, mode: 'insert', entry: 'fallthrough', expect: ['ldc.i4.1'], il: Array(130).fill('nop'), maxStack: 0, drop: 0 };
  const r = apply(body, [edit], () => null, 0);
  const ins = decode(r.code); const br = ins[1];
  if (br.op.name !== 'brfalse' || ins[br.v === undefined ? -1 : ins.findIndex((x) => x.at === br.v)]?.op.name !== 'ldc.i4.0') throw new Error(`self-check: branch not relocated (${br.op.name} -> ${br.v})`);
  if (r.code.length !== 7 + 130 + 3) throw new Error(`self-check: length ${r.code.length}`);
  const nb = readBody(r.full, 0); const c = nb.clauses[0];
  if (c.tryOff !== 0 || c.tryLen !== 5 + 130 + 3 || c.hOff !== 5 + 130 + 3 || c.hLen !== 2) throw new Error(`self-check: clause ${JSON.stringify(c)}`);
  // incoming entry: branches to "at" land on our first instruction
  const e2: Edit = { at: 5, mode: 'insert', entry: 'incoming', expect: ['ldc.i4.0'], il: ['nop', 'nop'], maxStack: 0, drop: 0 };
  const r2 = decode(apply(body, [e2], () => null, 0).code); if (r2[1].v !== 5 + 3 || r2[r2.findIndex((x) => x.at === r2[1].v)].op.name !== 'nop') throw new Error('self-check: incoming entry');
  // fallthrough entry: our own branch to "at" lands on the original instruction after the block, not on our first one (b69)
  const e3: Edit = { at: 5, mode: 'insert', entry: 'fallthrough', expect: ['ldc.i4.0'], il: ['ldarg.0', 'brtrue @0x5', 'nop'], maxStack: 1, drop: 0 };
  const r3f = decode(apply(body, [e3], () => null, 0).code); const b3 = r3f.find((x) => x.op.name === 'brtrue')!;
  if (r3f[r3f.findIndex((x) => x.at === b3.v)].op.name !== 'ldc.i4.0' || b3.v <= b3.at) throw new Error('self-check: fallthrough entry, own branch to at');
  // alignment of a text patch (the docs/506 B89 shape) against this body
  const find = parseBlock('IL_0001:  brfalse.s  IL_0005\n\nIL_0003:  ldc.i4.1\nIL_0004:  ret');
  const rep = parseBlock('IL_0001:  brfalse.s  IL_0005\n// ours\nldarg.0\nbrfalse    IL_0005\nIL_0003:  ldc.i4.1\nIL_0004:  ret');
  const ed = align(find, rep, body);
  if (ed.length !== 1 || ed[0].at !== 3 || ed[0].mode !== 'insert' || ed[0].entry !== 'fallthrough' || ed[0].il.join(';') !== 'ldarg.0;brfalse @0x5' || ed[0].maxStack !== 1) throw new Error(`self-check: align ${JSON.stringify(ed)}`);
  // a replaced EA instruction
  const lb = parseBlock('IL_0001:  brfalse.s  IL_0005\nbrtrue.s   L_A\nnop\nL_A:\nldsfld     int32 Ns.T::sCount\nldc.i4.1\nadd\nstsfld     int32 Ns.T::sCount\nL_B: nop\nIL_0003:  ldc.i4.1\nIL_0004:  ret');
  if (lb[1].operand !== 'L_A' || lb[3].name !== 'L_A' || lb[7].name !== 'L_B' || lb.length !== 10) throw new Error('self-check: label lines');
  const dc = dropCounterBumps(lb, new Set(['sCount']));
  if (dc.dropped.join() !== 'sCount' || dc.lines.length !== 6 || dc.lines[3].name !== 'L_B' || dc.lines[1].operand !== 'L_B') throw new Error(`self-check: counter bump: ${dc.lines.map(ourText).join(' | ')}`);
  const edL = align(find, dc.lines, body); if (edL.length !== 1 || edL[0].il.join(' | ') !== 'brtrue @0x5 | nop | L_B: nop' && edL[0].il.join(' | ') !== 'brtrue L_B | nop | L_B: nop') throw new Error(`self-check: label text: ${edL[0].il.join(' | ')}`);
  const rL = decode(apply(body, edL, () => null, 0).code); if (rL.map((x) => x.op.name).join(' ') !== 'ldarg.0 brfalse brtrue nop nop ldc.i4.1 ret ldc.i4.0 ret' || rL[2].v !== rL[4].at) throw new Error(`self-check: label apply: ${rL.map((x) => x.op.name + (isBr(x.op) ? '>' + x.v : '')).join(' ')}`);
  const tb = parseBlock('IL_0001:  brfalse.s  IL_0005\nIL_0003:  ldc.i4.1\nIL_0004:  ret\nbrtrue.s L_END\nnop\nL_END:'); if (tb.tail !== 'L_END') throw new Error('self-check: tail label');
  const edT = align(find, tb, body); if (edT.length !== 1 || edT[0].at !== 5 || edT[0].il[0] !== 'brtrue @0x5') throw new Error(`self-check: tail label target: ${JSON.stringify(edT)}`);
  const ed2 = align(find, parseBlock('IL_0001:  brfalse.s  IL_0005\nldc.i4.2\nIL_0004:  ret'), body);
  if (ed2.length !== 1 || ed2[0].mode !== 'replace' || ed2[0].at !== 3 || ed2[0].expect.join() !== 'ldc.i4.1' || ed2[0].il.join() !== 'ldc.i4.2') throw new Error(`self-check: replace ${JSON.stringify(ed2)}`);
  const r3 = decode(apply(body, ed2, () => null, 0).code); if (r3.map((x) => x.op.name).join(' ') !== 'ldarg.0 brfalse ldc.i4.2 ret ldc.i4.0 ret') throw new Error('self-check: replace apply');
  // member keys: ildasm text and the reader's rendering agree
  const k1 = parseMember('method', 'instance class [SimIFace]Sims3.SimIFace.CAS.SimOutfit Sims3.Gameplay.CAS.SimDescriptionCore::GetOutfit(valuetype [SimIFace]Sims3.SimIFace.CAS.OutfitCategories, int32)').key;
  const k2 = keyOfTokenText('method', 'Sims3.Gameplay.CAS.SimDescriptionCore::GetOutfit(Sims3.SimIFace.CAS.OutfitCategories,int)');
  if (k1 !== k2) throw new Error(`self-check: keys ${k1} | ${k2}`);
  const k3 = parseMember('method', 'instance class [mscorlib]System.Collections.Generic.List`1<!!0> Sims3.Gameplay.Inventory::FindAll<class Sims3.Gameplay.ActorSystems.TraitChip>(bool)').key;
  const k4 = keyOfTokenText('method', 'Sims3.Gameplay.Inventory::FindAll`1(bool):System.Collections.Generic.List`1<!!0><Sims3.Gameplay.ActorSystems.TraitChip>');
  if (k3 !== k4) throw new Error(`self-check: generic keys ${k3} | ${k4}`);
  console.log('ilpatch-convert self-check ok');
}

// ---------- --emit-native: the specs as a C table for ts3native (docs/526) ----------
// native/ts3native/ilpatch-specs.h: the opcode table (code, operand kind), then per spec the method's names, per build the
// body size, SHA-256 and patched SHA-256 with the tokens our lines use, and the edits with our instructions encoded for
// the native relocator (ilpatch.cpp). Our data only: no EA bytes. A spec whose lines need an operand kind the native
// table does not carry (i64, r4, r8, switch) is refused.
const NATIVE_KIND: Record<Kind, number> = { none: 0, i8: 1, u8: 2, u16: 3, i32: 4, i64: 5, f32: 6, f64: 7, br8: 8, br32: 9, tok: 10, switch: 11 };
const NK_BR_ORIG = 12, NK_BR_OURS = 13;  // our branches: to an original offset (v = the offset) or to one of our lines (v = edit index << 16 | line index)
function emitNative(out: string): void {
  const files = readdirSync(SPECS).filter((f) => f.endsWith('.json')).sort();
  const hex = (s: string) => s.match(/../g)!.map((b) => '0x' + b).join(', ');
  const cstr = (s: string) => JSON.stringify(s);
  const L: string[] = [
    '// Generated by `node tools/ilpatch-convert.ts --emit-native` from mods/ilpatch/specs/*.json (docs/506 §1, docs/526). Do not edit.',
    '// Our data only: method names, body hashes per build, the tokens our lines use, our instructions. No EA bytes.',
    'static const IlpOp kIlpOps[] = {',
    ...OPS.map((o) => `    {0x${o.code.toString(16).padStart(4, '0')}, ${NATIVE_KIND[o.kind]}},  // ${o.name}`),
    '};',
  ];
  const specs: string[] = [];
  files.forEach((f, si) => {
    const s = JSON.parse(readFileSync(join(SPECS, f), 'utf8'));
    const type = String(s.method).split('::')[0], name = String(s.method).split('::')[1].replace(/\(.*$/, '');
    const nested = type.includes('/'), ns = nested ? '' : type.includes('.') ? type.slice(0, type.lastIndexOf('.')) : '', cls = nested ? type.slice(type.lastIndexOf('/') + 1) : type.slice(type.lastIndexOf('.') + 1);
    const tokKeys: string[] = [];
    const tokIdx = (k: string) => { let i = tokKeys.indexOf(k); if (i < 0) { i = tokKeys.length; tokKeys.push(k); } return i; };
    const edits: { at: number; mode: number; entry: number; expect: number[]; maxStack: number; drop: number; ins: { op: number; kind: number; v: number }[] }[] = [];
    const labels = new Map<string, number>(); // our label -> (edit index << 16) | line index: a branch may cross edits
    (s.edits as any[]).forEach((e, ei) => (e.il as string[]).forEach((line, i) => { const al = assembleLine(line); if (al.label) labels.set(al.label, (ei << 16) | i); }));
    for (const e of s.edits) {
      const lines = (e.il as string[]).map(assembleLine);
      const ins = lines.map((l) => {
        const k = l.op.kind;
        if (k === 'none') return { op: l.op.code, kind: 0, v: 0 };
        if (k === 'i8' || k === 'u8' || k === 'u16' || k === 'i32') return { op: l.op.code, kind: NATIVE_KIND[k], v: parseInt(l.operand, 10) >>> 0 };
        if (k === 'tok') { if (memberKind(l.op) === 'other') throw new Error(`${f}: ${l.op.name}: operand kind not supported`); return { op: l.op.code, kind: NATIVE_KIND.tok, v: tokIdx(l.operand) }; }
        if (k === 'br32') { if (l.operand.startsWith('@')) return { op: l.op.code, kind: NK_BR_ORIG, v: parseInt(l.operand.slice(1), 16) }; const t = labels.get(l.operand); if (t == null) throw new Error(`${f}: branch to ${l.operand}: unknown label`); return { op: l.op.code, kind: NK_BR_OURS, v: t }; }
        throw new Error(`${f}: ${l.op.name}: operand kind ${k} is not in the native table`);
      });
      const expect = (e.expect as string[]).map((n) => { const o = BY_NAME.get(n); if (!o) throw new Error(`${f}: expect ${n}`); return o.code; });
      edits.push({ at: parseInt(e.at, 16), mode: e.mode === 'replace' ? 1 : 0, entry: e.entry === 'incoming' ? 1 : 0, expect, maxStack: e.maxStack, drop: e.mode === 'replace' ? expect.length : 0, ins });
    }
    const builds = Object.entries(s.builds as Record<string, any>);
    const p = `ilp${si}`;
    edits.forEach((e, ei) => {
      L.push(`static const WORD ${p}_expect${ei}[] = {${e.expect.length ? e.expect.map((c) => '0x' + c.toString(16)).join(', ') : '0'}};`);
      L.push(`static const IlpIns ${p}_ins${ei}[] = {${e.ins.length ? e.ins.map((x) => `{0x${x.op.toString(16)}, ${x.kind}, 0, 0x${x.v.toString(16)}}`).join(', ') : '{0, 0, 0, 0}'}};`);  // a pure deletion has no lines: one unused placeholder (nIns stays 0)
    });
    L.push(`static const IlpEdit ${p}_edits[] = {${edits.map((e, ei) => `{0x${e.at.toString(16)}, ${e.mode}, ${e.entry}, ${e.expect.length}, ${e.ins.length}, ${e.maxStack}, ${e.drop}, ${p}_expect${ei}, ${p}_ins${ei}}`).join(', ')}};`);
    builds.forEach(([label, b], bi) => {
      const toks = tokKeys.map((k) => { const t = b.tokens[k]; if (!t) throw new Error(`${f}: ${label}: no token for ${k}`); return t; });
      L.push(`static const DWORD ${p}_tok${bi}[] = {${toks.length ? toks.join(', ') : '0'}};  // ${label}: ${tokKeys.map((k) => k.replace(/^.*::/, '').replace(/\(.*$/, '')).join(', ') || '(no tokens)'}`);
    });
    L.push(`static const IlpBuild ${p}_builds[] = {${builds.map(([label, b], bi) => `{${cstr(label)}, ${b.size}, {${hex(b.sha256)}}, {${hex(b.patchedSha256)}}, ${p}_tok${bi}, ${tokKeys.length}}`).join(',\n    ')}};`);
    specs.push(`    {${cstr(s.id)}, L${cstr(s.switch)}, ${cstr(ns)}, ${cstr(cls)}, ${cstr(name)}, ${cstr(s.method)}, ${builds.length}, ${edits.length}, ${p}_builds, ${p}_edits},`);
  });
  L.push('static const IlpSpec kIlpSpecs[] = {', ...specs, '};', `static const int kIlpSpecN = ${files.length};`, '');
  writeFileSync(out, L.join('\n'));
  console.log(`${out}: ${files.length} specs (${files.map((f) => basename(f, '.json')).join(', ')}), ${OPS.length} opcodes`);
}

if (process.argv.includes('--self-check')) selfCheck();
else if (process.argv.includes('--emit-native')) { const o = arg('--emit-native', '') ?? ''; emitNative(!o || o.startsWith('--') ? join(ROOT, 'native/ts3native/ilpatch-specs.h') : resolve(o)); }
else {
  const rows = convert();
  const csv = arg('--csv', WRITE ? join(ROOT, 'docs/data/ilpatch-convert.csv') : '');
  const q = (s: string) => `"${String(s).replace(/"/g, '""')}"`;
  if (csv) writeFileSync(csv, ['file,entry,assembly,method,status,builds,selfCheck,spec', ...rows.map((r) => [r.file, r.entry, r.asm, r.method, r.status, r.builds, r.selfCheck, r.spec].map(q).join(','))].join('\n') + '\n');
  const tally = new Map<string, number>(); for (const r of rows) { const k = r.status.split(':')[0]; tally.set(k, (tally.get(k) ?? 0) + 1); }
  for (const r of rows) console.log(`${r.file} | ${r.entry} | ${r.method || '-'} | ${r.status}${r.builds ? ` | builds ${r.builds}` : ''}${r.selfCheck ? ` | ${r.selfCheck}` : ''}`);
  console.log(`${rows.length} entries: ${[...tally].map(([k, v]) => `${k} ${v}`).join(', ')}${csv ? `; census ${csv}` : ''}${WRITE ? `; specs in ${SPECS}` : ''}`);
}
