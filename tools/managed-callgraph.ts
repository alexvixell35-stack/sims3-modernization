// PLAN gap (12) steps 12a + 12b: managed call graph of the script DLLs from .NET metadata and IL, plus leaf tags.
// Usage:
//   node tools/managed-callgraph.ts build [--asm game-copy/assemblies] [--out game-copy/analysis/callgraph]
//   node tools/managed-callgraph.ts query <root>... [--depth 12] [--delegates] [--index <out>/index.json] [--ic docs/data/ic-safety.csv]
//   node tools/managed-callgraph.ts def <DefinitionType> [--depth 12] ...   (roots: the type's Test overloads + nearest CalculateScore)
//   query/def: --cut <prefix,prefix> skips calls into hand-verified guarded methods; --delegates follows delegate creation;
//              --ics lists every native binding reached (verdict, binding, depth)
//   node tools/managed-callgraph.ts callers <method> [--index ...]
//   node tools/managed-callgraph.ts subtypes <Type>... [--asm ...]   (every derived class, abstract flagged)
//   node tools/managed-callgraph.ts --self-check [--asm ...]
//   --mods <dir> (docs/127): mod assemblies loaded after the game ones for the class hierarchy; with build, their methods are indexed after the game's (docs/141 §8).
// Library (docs/127): virtualTargets / familyTargets (overrides per receiver class), receiverTypes (receiver static type
// per call site from the IL), instanceTypeOf, onChain, isA; closure(..., ovHops) follows overrides near the roots.
// A root or method is a method id ("Ns.Type+Nested::Name(params)") or a prefix of one ("Ns.Type::Name" = all overloads).
// Reads ECMA-335 metadata tables and IL bodies directly (no decompiler, no npm deps). Method ids carry the full
// parameter list, so overloads are distinct; generic methods carry `n arity; a duplicate id (conversion operators) gets ":ret".
// Edge kinds: call (direct), cv (callvirt to a non-virtual, sealed or final target), virt (unresolved virtual: declared
// target), iface (interface: declared target), ifaceN (interface with 1-4 implementing classes, all final: one edge per implementation), newobj,
// dlg (ldftn/ldvirtftn = delegate creation, not followed unless --delegates), dinv (delegate Invoke), cons (constrained. callvirt).
import { readFileSync, readdirSync, writeFileSync, mkdirSync, existsSync } from 'node:fs';
import { join } from 'node:path';

const arg = (n: string, d: string) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : d; };
const ASM_DIR = arg('--asm', 'game-copy/assemblies');
const OUT = arg('--out', 'game-copy/analysis/callgraph');
const INDEX = arg('--index', join(OUT, 'index.json'));
const IC_CSV = arg('--ic', 'docs/data/ic-safety.csv');
const DEPTH = Number(arg('--depth', '12'));
// Hand-verified guards (docs/73 §2.1 style): calls into methods with these id prefixes are not followed.
const CUT = arg('--cut', '').split(',').filter(Boolean);

// ---------- PE + metadata reader ----------
export type Asm = {
  name: string; buf: Buffer; mvid: string; sections: { va: number; size: number; raw: number }[];
  strOff: number; blobOff: number; guidOff: number; usOff: number; rows: number[]; tables: number[][][];
  types: TypeDef[]; methods: Method[]; fields: Field[];
};
export type TypeDef = { asm: Asm; row: number; ns: string; name: string; full: string; flags: number; extendsCi: number; methods: Method[]; fields: Field[]; enclosing?: TypeDef; ifaces: number[]; base?: TypeDef | null };
export type Method = { gi: number; asm: Asm; row: number; type: TypeDef; name: string; flags: number; impl: number; rva: number; sigBlob: number; params: string; ret: string; arity: number; id: string };
export type Field = { asm: Asm; row: number; type: TypeDef; name: string; flags: number; sigBlob: number; id: string };

// Column specs: number = fixed bytes; 's' string, 'g' guid, 'b' blob; 't<n>' simple index into table n; 'c<name>' coded index.
const CODED: Record<string, number[]> = {
  TypeDefOrRef: [2, 1, 0x1b], HasConstant: [4, 8, 0x17],
  HasCustomAttribute: [6, 4, 1, 2, 8, 9, 10, 0, 0x0e, 0x17, 0x14, 0x11, 0x1a, 0x1b, 0x20, 0x23, 0x26, 0x27, 0x28, 0x2a, 0x2c, 0x2b],
  HasFieldMarshal: [4, 8], HasDeclSecurity: [2, 6, 0x20], MemberRefParent: [2, 1, 0x1a, 6, 0x1b], HasSemantics: [0x14, 0x17],
  MethodDefOrRef: [6, 0x0a], MemberForwarded: [4, 6], Implementation: [0x26, 0x23, 0x27], CustomAttributeType: [-1, -1, 6, 0x0a, -1],
  ResolutionScope: [0, 0x1a, 0x23, 1], TypeOrMethodDef: [2, 6],
};
const SCHEMA: (number | string)[][] = [];
const S = (t: number, ...c: (number | string)[]) => { SCHEMA[t] = c; };
S(0x00, 2, 's', 'g', 'g', 'g'); S(0x01, 'cResolutionScope', 's', 's'); S(0x02, 4, 's', 's', 'cTypeDefOrRef', 't4', 't6');
S(0x03, 't4'); S(0x04, 2, 's', 'b'); S(0x05, 't6'); S(0x06, 4, 2, 2, 's', 'b', 't8'); S(0x07, 't8'); S(0x08, 2, 2, 's');
S(0x09, 't2', 'cTypeDefOrRef'); S(0x0a, 'cMemberRefParent', 's', 'b'); S(0x0b, 2, 'cHasConstant', 'b');
S(0x0c, 'cHasCustomAttribute', 'cCustomAttributeType', 'b'); S(0x0d, 'cHasFieldMarshal', 'b'); S(0x0e, 2, 'cHasDeclSecurity', 'b');
S(0x0f, 2, 4, 't2'); S(0x10, 4, 't4'); S(0x11, 'b'); S(0x12, 't2', 't20'); S(0x13, 't20'); S(0x14, 2, 's', 'cTypeDefOrRef');
S(0x15, 't2', 't23'); S(0x16, 't23'); S(0x17, 2, 's', 'b'); S(0x18, 2, 't6', 'cHasSemantics'); S(0x19, 't2', 'cMethodDefOrRef', 'cMethodDefOrRef');
S(0x1a, 's'); S(0x1b, 'b'); S(0x1c, 2, 'cMemberForwarded', 's', 't26'); S(0x1d, 4, 't4'); S(0x1e, 4, 4); S(0x1f, 4);
S(0x20, 4, 2, 2, 2, 2, 4, 'b', 's', 's'); S(0x21, 4); S(0x22, 4, 4, 4); S(0x23, 2, 2, 2, 2, 4, 'b', 's', 's', 'b'); S(0x24, 4, 't35');
S(0x25, 4, 4, 4, 't35'); S(0x26, 4, 's', 'b'); S(0x27, 4, 4, 's', 's', 'cImplementation'); S(0x28, 4, 4, 's', 'cImplementation');
S(0x29, 't2', 't2'); S(0x2a, 2, 2, 'cTypeOrMethodDef', 's'); S(0x2b, 'cMethodDefOrRef', 'b'); S(0x2c, 't42', 'cTypeDefOrRef');

const tagBits = (n: number) => Math.ceil(Math.log2(n));
function decodeCi(kind: string, v: number): [number, number] {
  const tabs = CODED[kind]; const b = tagBits(tabs.length);
  return [tabs[v & ((1 << b) - 1)], v >>> b];
}

function loadAsm(path: string, name: string): Asm {
  const buf = readFileSync(path);
  const pe = buf.readUInt32LE(0x3c);
  if (buf.readUInt32LE(pe) !== 0x4550) throw new Error(`${name}: not PE`);
  const nsec = buf.readUInt16LE(pe + 6), optSize = buf.readUInt16LE(pe + 20), opt = pe + 24;
  const ddir = opt + (buf.readUInt16LE(opt) === 0x20b ? 112 : 96);
  const sections = [];
  for (let i = 0, s = opt + optSize; i < nsec; i++, s += 40) sections.push({ va: buf.readUInt32LE(s + 12), size: Math.max(buf.readUInt32LE(s + 8), buf.readUInt32LE(s + 16)), raw: buf.readUInt32LE(s + 20) });
  const asm = { name, buf, sections } as Asm;
  const cli = rva(asm, buf.readUInt32LE(ddir + 14 * 8));
  const md = rva(asm, buf.readUInt32LE(cli + 8));
  if (buf.readUInt32LE(md) !== 0x424a5342) throw new Error(`${name}: no metadata`);
  let p = md + 16 + buf.readUInt32LE(md + 12) + 2;
  const nstreams = buf.readUInt16LE(p); p += 2;
  const streams: Record<string, number> = {};
  for (let i = 0; i < nstreams; i++) {
    const off = buf.readUInt32LE(p); p += 8;
    let e = p; while (buf[e]) e++;
    streams[buf.toString('latin1', p, e)] = md + off;
    p = (e + 4) & ~3;
  }
  if (streams['#-'] !== undefined) throw new Error(`${name}: uncompressed (#-) metadata not supported`);
  asm.strOff = streams['#Strings']; asm.blobOff = streams['#Blob']; asm.guidOff = streams['#GUID']; asm.usOff = streams['#US'] ?? 0;
  const t = streams['#~'];
  const heap = buf[t + 6];
  const valid = buf.readBigUInt64LE(t + 8);
  const rows: number[] = new Array(64).fill(0);
  p = t + 24;
  for (let i = 0; i < 64; i++) if ((valid >> BigInt(i)) & 1n) { rows[i] = buf.readUInt32LE(p); p += 4; }
  asm.rows = rows;
  const size = (c: number | string): number => {
    if (typeof c === 'number') return c;
    if (c === 's') return heap & 1 ? 4 : 2;
    if (c === 'g') return heap & 2 ? 4 : 2;
    if (c === 'b') return heap & 4 ? 4 : 2;
    if (c[0] === 't') return rows[+c.slice(1)] < 0x10000 ? 2 : 4;
    const tabs = CODED[c.slice(1)];
    const max = Math.max(...tabs.map((x) => (x < 0 ? 0 : rows[x])));
    return max < 1 << (16 - tagBits(tabs.length)) ? 2 : 4;
  };
  asm.tables = [];
  for (let ti = 0; ti < 64; ti++) {
    if (!rows[ti]) continue;
    const spec = SCHEMA[ti];
    if (!spec) throw new Error(`${name}: unknown table 0x${ti.toString(16)}`);
    const sizes = spec.map(size);
    const out: number[][] = [];
    for (let r = 0; r < rows[ti]; r++) {
      const row: number[] = [];
      for (const s of sizes) { row.push(s === 1 ? buf[p] : s === 2 ? buf.readUInt16LE(p) : buf.readUInt32LE(p)); p += s; }
      out.push(row);
    }
    asm.tables[ti] = out;
  }
  const mod = asm.tables[0][0];
  asm.mvid = buf.subarray(asm.guidOff + (mod[2] - 1) * 16, asm.guidOff + mod[2] * 16).toString('hex');
  return asm;
}
function rva(asm: Asm, r: number): number {
  for (const s of asm.sections) if (r >= s.va && r < s.va + s.size) return r - s.va + s.raw;
  throw new Error(`${asm.name}: rva 0x${r.toString(16)} outside sections`);
}
const str = (a: Asm, i: number) => { const s = a.strOff + i; let e = s; while (a.buf[e]) e++; return a.buf.toString('utf8', s, e); };
const tbl = (a: Asm, t: number) => a.tables[t] ?? [];

// Compressed unsigned int (II.23.2). Returns [value, bytes used].
export function cuint(b: Buffer, p: number): [number, number] {
  const x = b[p];
  if ((x & 0x80) === 0) return [x, 1];
  if ((x & 0xc0) === 0x80) return [((x & 0x3f) << 8) | b[p + 1], 2];
  return [((x & 0x1f) * 0x1000000) + (b[p + 1] << 16) + (b[p + 2] << 8) + b[p + 3], 4];
}
const blob = (a: Asm, i: number): [number, number] => { const [len, n] = cuint(a.buf, a.blobOff + i); return [a.blobOff + i + n, len]; };

// ---------- global model ----------
const asms: Asm[] = [];
const typeByFull = new Map<string, TypeDef>();
const methods: Method[] = [];

function typeRefName(a: Asm, row: number): string {
  const r = tbl(a, 1)[row - 1];
  const [st, si] = decodeCi('ResolutionScope', r[0]);
  const nm = str(a, r[1]), ns = str(a, r[2]);
  if (st === 1 && si) return `${typeRefName(a, si)}+${nm}`;
  return ns ? `${ns}.${nm}` : nm;
}
function tdorName(a: Asm, t: number, i: number): string {
  if (t === 2) return a.types[i - 1].full;
  if (t === 1) return typeRefName(a, i);
  const [p] = blob(a, tbl(a, 0x1b)[i - 1][0]);
  return sigType(a, p)[0];
}
const PRIM: Record<number, string> = { 1: 'void', 2: 'bool', 3: 'char', 4: 'sbyte', 5: 'byte', 6: 'short', 7: 'ushort', 8: 'int', 9: 'uint', 0x0a: 'long', 0x0b: 'ulong', 0x0c: 'float', 0x0d: 'double', 0x0e: 'string', 0x16: 'typedref', 0x18: 'IntPtr', 0x19: 'UIntPtr', 0x1c: 'object' };
// Type in a signature blob to a name string. Returns [name, next offset].
function sigType(a: Asm, p: number): [string, number] {
  const b = a.buf; const e = b[p++];
  if (PRIM[e]) return [PRIM[e], p];
  switch (e) {
    case 0x0f: { const [t, q] = sigType(a, p); return [t + '*', q]; }
    case 0x10: { const [t, q] = sigType(a, p); return [t + '&', q]; }
    case 0x11: case 0x12: { const [v, n] = cuint(b, p); return [tdorName(a, [2, 1, 0x1b][v & 3], v >>> 2), p + n]; }
    case 0x13: case 0x1e: { const [v, n] = cuint(b, p); return [(e === 0x13 ? '!' : '!!') + v, p + n]; }
    case 0x14: {
      const [t, q0] = sigType(a, p); let q = q0;
      const [rank, n1] = cuint(b, q); q += n1;
      for (let k = 0; k < 2; k++) { const [cnt, n2] = cuint(b, q); q += n2; for (let i = 0; i < cnt; i++) q += cuint(b, q)[1]; }
      return [`${t}[${','.repeat(Math.max(0, rank - 1))}]`, q];
    }
    case 0x15: {
      const [g, q0] = sigType(a, p); let q = q0;
      const [cnt, n] = cuint(b, q); q += n;
      const args: string[] = [];
      for (let i = 0; i < cnt; i++) { const [t, q2] = sigType(a, q); args.push(t); q = q2; }
      return [`${g}<${args.join(',')}>`, q];
    }
    case 0x1b: { const r = methodSig(a, p); return [`fnptr(${r.params})`, r.end]; }
    case 0x1d: { const [t, q] = sigType(a, p); return [t + '[]', q]; }
    case 0x1f: case 0x20: { const n = cuint(b, p)[1]; return sigType(a, p + n); } // custom modifiers: dropped
    case 0x41: case 0x45: return sigType(a, p); // sentinel, pinned
  }
  throw new Error(`${a.name}: sig element 0x${e.toString(16)} at ${p - 1}`);
}
function methodSig(a: Asm, p: number): { params: string; ret: string; arity: number; end: number } {
  const b = a.buf; const cc = b[p++]; let arity = 0;
  if (cc & 0x10) { const [g, n] = cuint(b, p); arity = g; p += n; }
  const [cnt, n] = cuint(b, p); p += n;
  const [ret, q] = sigType(a, p); p = q;
  const ps: string[] = [];
  for (let i = 0; i < cnt; i++) { if (b[p] === 0x41) p++; const [t, q2] = sigType(a, p); ps.push(t); p = q2; }
  return { params: ps.join(','), ret, arity, end: p };
}
function fieldSigType(a: Asm, blobIdx: number): string { const [p] = blob(a, blobIdx); return sigType(a, p + 1)[0]; }

// --mods <dir>: mod assemblies (docs/127) loaded after the game ones, so game method ids keep the index's numbering.
// Used for the class hierarchy (overridesOf); in index.json only when build ran with --mods (docs/141 §8: route-plan-callers over\n// mods; keep that index apart from the game-only one). A mod's own copy of an EA assembly is skipped.
const MOD_DIR = arg('--mods', '');
export const modAsms = new Set<string>();
function loadAll(dir: string, skipped?: string[]): void {
  for (const f of readdirSync(dir).filter((n) => n.endsWith('.dll')).sort()) {
    try { asms.push(loadAsm(join(dir, f), f.replace(/\.dll$/, ''))); } catch (e) { if (!skipped) throw e; skipped.push(`${f}: ${(e as Error).message}`); }
  }
  const ea = new Set(asms.map((a) => a.name));
  if (MOD_DIR) for (const f of readdirSync(MOD_DIR).filter((n) => n.endsWith('.dll') && !ea.has(n.replace(/\.dll$/, ''))).sort()) {
    const a = loadAsm(join(MOD_DIR, f), f.replace(/\.dll$/, '')); asms.push(a); modAsms.add(a.name);
  }
  for (const a of asms) {
    indexAsm(a, methods);
    for (const t of a.types) if (!typeByFull.has(t.full)) typeByFull.set(t.full, t);
  }
}
// Types, methods (gi = position in sink), fields and method ids of one assembly, from its own metadata only.
function indexAsm(a: Asm, sink: Method[]): void {
  {
    const tds = tbl(a, 2), mds = tbl(a, 6), fds = tbl(a, 4);
    a.types = tds.map((r, i) => ({ asm: a, row: i + 1, ns: str(a, r[2]), name: str(a, r[1]), full: '', flags: r[0], extendsCi: r[3], methods: [], fields: [], ifaces: [] }));
    for (const r of tbl(a, 0x29)) a.types[r[0] - 1].enclosing = a.types[r[1] - 1];
    const full = (t: TypeDef): string => t.full || (t.full = t.enclosing ? `${full(t.enclosing)}+${t.name}` : t.ns ? `${t.ns}.${t.name}` : t.name);
    a.types.forEach(full);
    for (const r of tbl(a, 9)) a.types[r[0] - 1].ifaces.push(r[1]);
    a.methods = []; a.fields = [];
    tds.forEach((r, i) => {
      const t = a.types[i];
      const mEnd = i + 1 < tds.length ? tds[i + 1][5] : mds.length + 1;
      for (let m = r[5]; m < mEnd; m++) {
        const md = mds[m - 1];
        const [bp] = blob(a, md[4]);
        const sig = methodSig(a, bp);
        const meth: Method = { gi: sink.length, asm: a, row: m, type: t, name: str(a, md[3]), flags: md[2], impl: md[1], rva: md[0], sigBlob: md[4], params: sig.params, ret: sig.ret, arity: sig.arity, id: '' };
        sink.push(meth); t.methods.push(meth); a.methods[m - 1] = meth;
      }
      const fEnd = i + 1 < tds.length ? tds[i + 1][4] : fds.length + 1;
      for (let f = r[4]; f < fEnd; f++) {
        const fd = fds[f - 1];
        const fld: Field = { asm: a, row: f, type: t, name: str(a, fd[1]), flags: fd[0], sigBlob: fd[2], id: `${t.full}::${str(a, fd[1])}` };
        t.fields.push(fld); a.fields[f - 1] = fld;
      }
    });
  }
  for (const t of a.types) {
    const seen = new Map<string, Method[]>();
    for (const m of t.methods) {
      m.id = `${t.full}::${m.name}${m.arity ? '`' + m.arity : ''}(${m.params})`;
      seen.set(m.id, [...(seen.get(m.id) ?? []), m]);
    }
    for (const [, ms] of seen) if (ms.length > 1) for (const m of ms) m.id += `:${m.ret}`;
  }
}

// ---------- resolution ----------
// TypeDef/TypeRef/TypeSpec to its TypeDef; a generic instance resolves to its generic type (arrays, vars: none).
function resolveTdor(a: Asm, t: number, i: number): TypeDef | undefined {
  if (t === 2) return a.types[i - 1];
  if (t === 1) return typeByFull.get(typeRefName(a, i));
  const [p] = blob(a, tbl(a, 0x1b)[i - 1][0]);
  if (a.buf[p] !== 0x15) return undefined;
  const [v] = cuint(a.buf, p + 2);
  return resolveTdor(a, [2, 1, 0x1b][v & 3], v >>> 2);
}
function baseOf(t: TypeDef): TypeDef | undefined {
  if (t.base === undefined) {
    const [bt, bi] = decodeCi('TypeDefOrRef', t.extendsCi);
    t.base = bi ? resolveTdor(t.asm, bt, bi) ?? null : null;
  }
  return t.base ?? undefined;
}
const isIface = (t: TypeDef) => (t.flags & 0x20) !== 0;
const isSealed = (t: TypeDef) => (t.flags & 0x100) !== 0;
const isDelegate = (t: TypeDef) => baseOf(t)?.full === 'System.MulticastDelegate';
const VIRT = 0x40, FINAL = 0x20, ABSTRACT = 0x400, IC_FLAG = 0x1000, PINVOKE = 0x2000; // ScriptCore declares its ICs as extern PInvokeImpl

function findMethod(t: TypeDef | undefined, name: string, params: string, ret: string, arity: number): Method | undefined {
  for (let c = t, hops = 0; c && hops < 40; c = baseOf(c), hops++) {
    const m = c.methods.find((x) => x.name === name && x.params === params && x.ret === ret && x.arity === arity);
    if (m) return m;
  }
  return undefined;
}
const stubs = new Map<string, number>(); // unresolved external targets (array accessors, missing refs)
type Target = { m?: Method; stub?: string };
export function resolveMethodToken(a: Asm, tok: number): Target {
  const tt = tok >>> 24, ti = tok & 0xffffff;
  if (tt === 0x06) return { m: a.methods[ti - 1] };
  if (tt === 0x2b) { const [mt, mi] = decodeCi('MethodDefOrRef', tbl(a, 0x2b)[ti - 1][0]); return resolveMethodToken(a, ((mt === 6 ? 0x06 : 0x0a) << 24) | mi); }
  if (tt !== 0x0a) return { stub: `?token 0x${tok.toString(16)}` };
  const r = tbl(a, 0x0a)[ti - 1];
  const [pt, pi] = decodeCi('MemberRefParent', r[0]);
  const name = str(a, r[1]);
  if (pt === 6) return { m: a.methods[pi - 1] };
  const [bp] = blob(a, r[2]);
  const sig = methodSig(a, bp);
  const owner = pt === 2 || pt === 1 || pt === 0x1b ? tdorName(a, pt, pi) : `module:${pi}`;
  const t = pt === 0x1a ? undefined : resolveTdor(a, pt, pi);
  const m = findMethod(t, name, sig.params, sig.ret, sig.arity);
  return m ? { m } : { stub: `${owner}::${name}${sig.arity ? '`' + sig.arity : ''}(${sig.params})` };
}
export function resolveFieldToken(a: Asm, tok: number): { id: string; type: string } {
  const tt = tok >>> 24, ti = tok & 0xffffff;
  if (tt === 0x04) { const f = a.fields[ti - 1]; return { id: f.id, type: fieldSigType(a, f.sigBlob) }; }
  const r = tbl(a, 0x0a)[ti - 1];
  const [pt, pi] = decodeCi('MemberRefParent', r[0]);
  const t = resolveTdor(a, pt, pi);
  return { id: `${t?.full ?? tdorName(a, pt, pi)}::${str(a, r[1])}`, type: fieldSigType(a, r[2]) };
}

// Interface method -> its implementations when at most 4 non-abstract classes declare the interface and every
// implementation is final (no subclass can override it). Cheap class hierarchy analysis; 12d does the rest.
let implementors: Map<TypeDef, TypeDef[]> | undefined;
function ifaceImpls(m: Method): Method[] | undefined {
  if (!implementors) {
    implementors = new Map();
    for (const t of asms.flatMap((a) => a.types)) {
      // Abstract classes count: their subclasses inherit the interface without listing it (NRaas AlarmOption : IAlarmOption,
      // docs/141 §8); an abstract (non-final) implementation makes the site plain `iface` below.
      if (isIface(t)) continue;
      for (const ci of t.ifaces) { const [it, ii] = decodeCi('TypeDefOrRef', ci); const i = resolveTdor(t.asm, it, ii); if (i) implementors.set(i, [...(implementors.get(i) ?? []), t]); }
    }
  }
  const im = implementors.get(m.type);
  if (!im || im.length > 4) return undefined;
  const out: Method[] = [];
  for (const c of im) {
    let impl: Method | undefined;
    for (const r of tbl(c.asm, 0x19)) if (r[0] === c.row) { // explicit MethodImpl
      const [dt, di] = decodeCi('MethodDefOrRef', r[2]);
      if (resolveMethodToken(c.asm, ((dt === 6 ? 0x06 : 0x0a) << 24) | di).m === m) { const [bt, bi] = decodeCi('MethodDefOrRef', r[1]); impl = resolveMethodToken(c.asm, ((bt === 6 ? 0x06 : 0x0a) << 24) | bi).m; }
    }
    impl ??= findMethod(c, m.name, m.params, m.ret, m.arity);
    if (!impl || (impl.flags & VIRT && !(impl.flags & FINAL) && !isSealed(c))) return undefined;
    out.push(impl);
  }
  return out;
}

// ---------- IL ----------
// Operand size per opcode; 'T' = 4-byte token, 'S' = switch. Unlisted single-byte opcodes take no operand.
const OP1: Record<number, number | string> = {};
for (const o of [0x0e, 0x0f, 0x10, 0x11, 0x12, 0x13, 0x1f, 0xde]) OP1[o] = 1;
for (let o = 0x2b; o <= 0x37; o++) OP1[o] = 1;
for (let o = 0x38; o <= 0x44; o++) OP1[o] = 4;
OP1[0x20] = 4; OP1[0x21] = 8; OP1[0x22] = 4; OP1[0x23] = 8; OP1[0xdd] = 4; OP1[0x45] = 'S';
for (const o of [0x27, 0x28, 0x29, 0x6f, 0x70, 0x71, 0x72, 0x73, 0x74, 0x75, 0x79, 0x7b, 0x7c, 0x7d, 0x7e, 0x7f, 0x80, 0x81, 0x8c, 0x8d, 0x8f, 0xa3, 0xa4, 0xa5, 0xc2, 0xc6, 0xd0]) OP1[o] = 'T';
const OP2: Record<number, number | string> = { 0x06: 'T', 0x07: 'T', 0x09: 2, 0x0a: 2, 0x0b: 2, 0x0c: 2, 0x0d: 2, 0x0e: 2, 0x12: 1, 0x15: 'T', 0x16: 'T', 0x19: 1, 0x1c: 'T' };
export type Ins = { op: number; tok: number; at: number };
// Decode an IL byte range into instructions that carry a token (the only ones the graph needs); throws on a bad stream.
export function decodeIl(b: Buffer, start: number, len: number): Ins[] {
  const out: Ins[] = []; let p = start; const end = start + len;
  while (p < end) {
    const at = p; let op = b[p++]; let sz: number | string | undefined;
    if (op === 0xfe) { op = 0xfe00 | b[p++]; sz = OP2[op & 0xff]; } else sz = OP1[op];
    if (sz === 'T') { out.push({ op, tok: b.readUInt32LE(p), at }); p += 4; } else if (sz === 'S') { p += 4 + 4 * b.readUInt32LE(p); } else if (sz) p += sz as number;
  }
  if (p !== end) throw new Error(`IL overrun at ${p} (end ${end})`);
  return out;
}
function body(m: Method): Ins[] {
  if (!m.rva || m.impl & IC_FLAG || m.flags & (ABSTRACT | PINVOKE)) return [];
  const b = m.asm.buf; const p = rva(m.asm, m.rva);
  if ((b[p] & 3) === 2) return decodeIl(b, p + 1, b[p] >> 2);
  const hdr = (b.readUInt16LE(p) >> 12) * 4;
  return decodeIl(b, p + hdr, b.readUInt32LE(p + 4));
}

// ---------- per-method IL shape (tools/mod-method-diff.ts) ----------
// One assembly outside the global model (own methods array), to compare two builds of the same module.
export function loadStandalone(path: string, name: string): Asm { const a = loadAsm(path, name); indexAsm(a, []); return a; }
// A token as text from the assembly's own metadata (no cross-assembly resolution): two builds that name the same member
// give the same text whatever their token numbers.
function tokenText(a: Asm, tok: number): string {
  const tt = tok >>> 24, ti = tok & 0xffffff;
  if (tt === 0x06) return a.methods[ti - 1].id;
  if (tt === 0x04) return a.fields[ti - 1].id;
  if (tt === 0x01 || tt === 0x02 || tt === 0x1b) return tdorName(a, tt, ti);
  if (tt === 0x0a) {
    const r = tbl(a, 0x0a)[ti - 1]; const [pt, pi] = decodeCi('MemberRefParent', r[0]);
    const owner = pt === 6 ? a.methods[pi - 1].id : pt === 0x1a ? `module:${pi}` : tdorName(a, pt, pi);
    const [bp] = blob(a, r[2]);
    if ((a.buf[bp] & 0x0f) === 6) return `${owner}::${str(a, r[1])}:${sigType(a, bp + 1)[0]}`;
    const s = methodSig(a, bp);
    return `${owner}::${str(a, r[1])}${s.arity ? '`' + s.arity : ''}(${s.params}):${s.ret}`;
  }
  if (tt === 0x2b) {
    const r = tbl(a, 0x2b)[ti - 1]; const [mt, mi] = decodeCi('MethodDefOrRef', r[0]);
    const [bp] = blob(a, r[1]); let p = bp + 1; const [n, k] = cuint(a.buf, p); p += k; const g: string[] = [];
    for (let i = 0; i < n; i++) { const [t, q] = sigType(a, p); g.push(t); p = q; }
    return `${tokenText(a, ((mt === 6 ? 0x06 : 0x0a) << 24) | mi)}<${g.join(',')}>`;
  }
  if (tt === 0x70) { const [len, n] = cuint(a.buf, a.usOff + ti); const s = a.usOff + ti + n; return JSON.stringify(a.buf.toString('utf16le', s, s + (len & ~1))); }
  if (tt === 0x11) { const [bp] = blob(a, tbl(a, 0x11)[ti - 1][0]); const s = methodSig(a, bp); return `sig(${s.params}):${s.ret}`; }
  return `?${tok.toString(16)}`;
}
// Normalized IL of one body: short and macro opcode forms folded into the long form (br.s -> br, ldloc.0 -> ldloc 0,
// ldc.i4.s -> ldc.i4), branch and switch targets and exception clauses as instruction indices, tokens as tokenText,
// locals as their types. Max stack and the init-locals flag are left out. No body: 'native' (IC / PInvoke) or 'abstract'.
export function ilShape(m: Method): string {
  if (m.impl & IC_FLAG || m.flags & PINVOKE) return 'native';
  if (m.flags & ABSTRACT || !m.rva) return 'abstract';
  const a = m.asm, b = a.buf, p0 = rva(a, m.rva), out: string[] = [];
  let start: number, len: number, more = false;
  if ((b[p0] & 3) === 2) { start = p0 + 1; len = b[p0] >> 2; }
  else {
    more = (b[p0] & 0x08) !== 0; start = p0 + (b.readUInt16LE(p0) >> 12) * 4; len = b.readUInt32LE(p0 + 4);
    const lt = b.readUInt32LE(p0 + 8);
    if (lt) {
      const [lp] = blob(a, tbl(a, 0x11)[(lt & 0xffffff) - 1][0]); let q = lp + 1; const [n, k] = cuint(b, q); q += k; const ls: string[] = [];
      for (let i = 0; i < n; i++) { const [t, q2] = sigType(a, q); ls.push(t); q = q2; }
      out.push(`locals ${ls.join(',')}`);
    }
  }
  const { ins } = decodeAll(b, start, len);
  const idx = new Map(ins.map((x, i) => [x.at, i]));
  const tgt = (off: number) => off === len ? ins.length : idx.get(off) ?? `@${off}`;
  for (const x of ins) {
    let op = x.op, v: number | string = x.v; const at = start + x.at;
    if (op >= 0x2b && op <= 0x37) { op += 0x0d; v = tgt(x.next + x.v); }
    else if ((op >= 0x38 && op <= 0x44) || op === 0xdd || op === 0xde) { if (op === 0xde) op = 0xdd; v = tgt(x.next + x.v); }
    else if (op >= 0x02 && op <= 0x05) { v = op - 0x02; op = 0xfe09; }
    else if (op >= 0x06 && op <= 0x09) { v = op - 0x06; op = 0xfe0c; }
    else if (op >= 0x0a && op <= 0x0d) { v = op - 0x0a; op = 0xfe0e; }
    else if (op >= 0x0e && op <= 0x13) op = [0xfe09, 0xfe0a, 0xfe0b, 0xfe0c, 0xfe0d, 0xfe0e][op - 0x0e];
    else if (op >= 0x15 && op <= 0x1e) { v = op - 0x16; op = 0x20; }
    else if (op === 0x1f) { v = b.readInt8(at + 1); op = 0x20; }
    else if (op === 0x21 || op === 0x23) v = b.readBigUInt64LE(at + 1).toString(16);
    else if (op === 0x22) v = b.readUInt32LE(at + 1).toString(16);
    else if (op === 0x45) { const n = b.readUInt32LE(at + 1), base = x.next; v = Array.from({ length: n }, (_, k) => tgt(base + b.readInt32LE(at + 5 + 4 * k))).join(','); }
    else if (x.tok) v = tokenText(a, x.tok);
    out.push(`${op.toString(16)} ${v}`);
  }
  for (let p = (start + len + 3) & ~3; more; ) {
    const kind = b[p], fat = (kind & 0x40) !== 0, size = fat ? b.readUIntLE(p + 1, 3) : b[p + 1];
    more = (kind & 0x80) !== 0;
    if (kind & 0x01) for (let c = p + 4; c + (fat ? 24 : 12) <= p + size; c += fat ? 24 : 12) {
      const f = fat ? b.readUInt32LE(c) : b.readUInt16LE(c);
      const [to, tl, ho, hl, x] = fat ? [b.readUInt32LE(c + 4), b.readUInt32LE(c + 8), b.readUInt32LE(c + 12), b.readUInt32LE(c + 16), b.readUInt32LE(c + 20)]
        : [b.readUInt16LE(c + 2), b[c + 4], b.readUInt16LE(c + 5), b[c + 7], b.readUInt32LE(c + 8)];
      out.push(`eh ${f} ${tgt(to)}-${tgt(to + tl)} ${tgt(ho)}-${tgt(ho + hl)} ${f === 0 ? tokenText(a, x) : f === 1 ? tgt(x) : ''}`);
    }
    p += size;
  }
  return out.join('\n');
}

// ---------- receiver static type (docs/127) ----------
// C# emits callvirt to the original declaration, so the IL target says nothing about the receiver. Its static type is the
// type of the expression that pushed it: walk back from the callvirt over instructions with a known stack effect until the
// producer of the receiver slot (ldarg, ldloc, ldfld, ldsfld, call, castclass, isinst, unbox.any, newobj). Gives up (no
// type) at a branch target in between (stack merge), on any other opcode, or on a generic parameter type.
const STATIC_M = 0x10;
export type FullIns = { op: number; at: number; next: number; v: number; tok: number };
function decodeAll(b: Buffer, start: number, len: number): { ins: FullIns[]; targets: Set<number> } {
  const ins: FullIns[] = []; const targets = new Set<number>(); let p = start; const end = start + len;
  while (p < end) {
    const at = p; let op = b[p++]; let sz: number | string | undefined; let v = 0, tok = 0;
    if (op === 0xfe) { op = 0xfe00 | b[p++]; sz = OP2[op & 0xff]; } else sz = OP1[op];
    if (sz === 'T') { tok = b.readUInt32LE(p); p += 4; }
    else if (sz === 'S') { const n = b.readUInt32LE(p); const base = p + 4 + 4 * n; for (let k = 0; k < n; k++) targets.add(base + b.readInt32LE(p + 4 + 4 * k) - start); p = base; }
    else if (sz === 1) { v = (op >= 0x2b && op <= 0x37) || op === 0xde ? b.readInt8(p) : b[p]; p += 1; }
    else if (sz === 2) { v = b.readUInt16LE(p); p += 2; }
    else if (sz === 4) { v = b.readInt32LE(p); p += 4; }
    else if (sz) p += sz as number;
    if ((op >= 0x2b && op <= 0x44) || op === 0xdd || op === 0xde) targets.add(p + v - start);
    ins.push({ op, at: at - start, next: p - start, v, tok });
  }
  return { ins, targets };
}
// Split a rendered type list at top-level commas (generic arguments and fnptr params stay together).
const splitTop = (s: string): string[] => { if (!s) return []; const o: string[] = []; let d = 0, st = 0; for (let i = 0; i <= s.length; i++) { const ch = s[i]; if (ch === '<' || ch === '(' || ch === '[') d++; else if (ch === '>' || ch === ')' || ch === ']') d--; else if ((ch === ',' && !d) || i === s.length) { o.push(s.slice(st, i)); st = i + 1; } } return o; };
export type Recv = { type?: string; isThis: boolean; via?: string }; // via: producing method or field id
const recvCache = new Map<number, Map<number, Recv>>();
// Per caller method: declared target -> receiver static type (undefined if any site of that target is unknown).
export function receiverTypes(caller: number): Map<number, Recv> {
  if (!asms.length) loadAll(ASM_DIR);
  let out = recvCache.get(caller); if (out) return out;
  out = new Map(); recvCache.set(caller, out);
  for (const { target: tm, recv: r0 } of methodIl(caller)?.sites ?? []) {
    const exact = r0.via && !r0.via.includes('(') ? exactStaticFieldType(r0.via) : undefined;
    const res = exact ? { ...r0, type: exact } : r0;
    // ifaceN edges point at the implementations, so key those too.
    for (const g of [tm.gi, ...(isIface(tm.type) ? ifaceImpls(tm)?.map((x) => x.gi) ?? [] : [])]) {
      const prev = out.get(g);
      out.set(g, prev && (prev.type !== res.type || prev.isThis !== res.isThis || prev.via !== res.via) ? { isThis: false } : res); // sites disagree: unknown
    }
  }
  return out;
}
// Static fields whose every store is `newobj T; stsfld` (EA's `Singleton = new Definition()`) and whose address is never
// taken (no ldsflda) hold an exact T or null, so a receiver loaded from one has type T, not the field's declared type
// (docs/data/tier2-blockers.csv: `Terrain+PlayOnCarpetInternal::Singleton` is declared InteractionDefinition).
let sfldExact: Map<string, string | null> | undefined;
export function exactStaticFieldType(id: string): string | undefined {
  if (!sfldExact) {
    sfldExact = new Map();
    for (let i = 0; i < methods.length; i++) {
      const il = methodIl(i); if (!il) continue;
      il.ins.forEach((x, j) => {
        if (x.op !== 0x80 && x.op !== 0x7f) return;
        const f = resolveFieldToken(il.asm, x.tok).id, p = il.ins[j - 1];
        const t = x.op === 0x80 && p?.op === 0x73 && !il.targets.has(x.at) ? resolveMethodToken(il.asm, p.tok).m?.type.full : undefined;
        const prev = sfldExact!.get(f);
        sfldExact!.set(f, t && (prev === undefined || prev === t) ? t : null);
      });
    }
  }
  return sfldExact.get(id) ?? undefined;
}
// Per method: every IL instruction (at = offset in the body) and every instance call site (call/callvirt) with its
// receiver: j = index of the call in ins, src = index of the instruction that pushed the receiver (-1 = unknown).
// docs/192 (script-leak-scan.ts) matches collection mutators to the static or instance field they run on.
export type CallSite = { j: number; target: Method; recv: Recv; src: number };
export function methodIl(caller: number): { ins: FullIns[]; sites: CallSite[]; asm: Asm; targets: Set<number> } | undefined {
  if (!asms.length) loadAll(ASM_DIR);
  const m = methods[caller];
  if (!m || !m.rva || m.impl & IC_FLAG || m.flags & (ABSTRACT | PINVOKE)) return undefined;
  const a = m.asm, b = a.buf, p0 = rva(a, m.rva);
  let start: number, len: number, locals: string[] = [];
  if ((b[p0] & 3) === 2) { start = p0 + 1; len = b[p0] >> 2; }
  else {
    start = p0 + (b.readUInt16LE(p0) >> 12) * 4; len = b.readUInt32LE(p0 + 4);
    const lt = b.readUInt32LE(p0 + 8);
    if (lt) { const [lp] = blob(a, tbl(a, 0x11)[(lt & 0xffffff) - 1][0]); let q = lp + 1; const [n, k] = cuint(b, q); q += k; for (let i = 0; i < n; i++) { const [t, q2] = sigType(a, q); locals.push(t); q = q2; } }
  }
  let dec: ReturnType<typeof decodeAll>; try { dec = decodeAll(b, start, len); } catch { return undefined; }
  const params = splitTop(m.params);
  const inst = !(m.flags & STATIC_M);
  const argType = (i: number): [string | undefined, boolean] => inst ? (i === 0 ? [m.type.full, true] : [params[i - 1], false]) : [params[i], false];
  const nParams = (t: Method) => splitTop(t.params).length;
  const effect = (x: FullIns): [number, number, string | undefined, boolean, string?] | undefined => { // [pop, push, pushed type, is this, via]
    const op = x.op;
    if (op >= 0x02 && op <= 0x05) return [0, 1, ...argType(op - 0x02)];
    if (op === 0x0e || op === 0xfe09) return [0, 1, ...argType(x.v)];
    if (op >= 0x06 && op <= 0x09) return [0, 1, locals[op - 0x06], false];
    if (op === 0x11 || op === 0xfe0c) return [0, 1, locals[x.v], false];
    if ((op >= 0x14 && op <= 0x23) || op === 0x72 || op === 0xd0 || op === 0x0f || op === 0x12 || op === 0xfe0a || op === 0xfe0d || op === 0xfe06) return [0, 1, undefined, false];
    if (op === 0x7e || op === 0x7f) { const f = resolveFieldToken(a, x.tok); return [0, 1, op === 0x7e ? f.type : undefined, false, f.id]; }
    if (op === 0x7b || op === 0x7c) { const f = resolveFieldToken(a, x.tok); return [1, 1, op === 0x7b ? f.type : undefined, false, f.id]; }
    if (op === 0x74 || op === 0x75 || op === 0xa5) { const tt = x.tok >>> 24; return [1, 1, tdorName(a, tt === 0x02 ? 2 : tt === 0x01 ? 1 : 0x1b, x.tok & 0xffffff), false]; }
    if (op === 0x8c || op === 0x8e || (op >= 0x46 && op <= 0x50) || (op >= 0x67 && op <= 0x6e) || (op >= 0xb3 && op <= 0xba) || (op >= 0xd1 && op <= 0xd5) || op === 0x65 || op === 0x66) return [1, 1, undefined, false];
    if ((op >= 0x58 && op <= 0x64) || (op >= 0x90 && op <= 0x9a) || op === 0xa3 || op === 0x8f || (op >= 0xfe01 && op <= 0xfe05)) return [2, 1, undefined, false];
    if (op === 0x28 || op === 0x6f || op === 0x73) {
      const t = resolveMethodToken(a, x.tok).m; if (!t) return undefined;
      const np = nParams(t);
      if (op === 0x73) return [np, 1, t.type.full, false];
      return [np + (t.flags & STATIC_M ? 0 : 1), t.ret === 'void' ? 0 : 1, t.ret, false, t.id];
    }
    return undefined;
  };
  const sites: CallSite[] = [];
  for (let j = 0; j < dec.ins.length; j++) {
    const x = dec.ins[j]; if (x.op !== 0x6f && x.op !== 0x28) continue;
    const tm = resolveMethodToken(a, x.tok).m; if (!tm || tm.flags & STATIC_M) continue;
    let res: Recv = { isThis: false }, src = -1;
    let k = nParams(tm); // values above the receiver still to skip
    for (let i = j - 1; i >= 0; i--) {
      if (dec.targets.has(dec.ins[i + 1].at)) break; // something jumps into the sequence: stack merge
      const e = effect(dec.ins[i]); if (!e) break;
      const [po, pu, ty, isThis, via] = e;
      if (k < pu) { if (pu === 1 && k === 0) { src = i; if (ty && !/^!|&$/.test(ty)) res = { type: ty.replace(/<.*$/s, ''), isThis, via }; } break; }
      k = k - pu + po;
    }
    sites.push({ j, target: tm, recv: res, src });
  }
  return { ins: dec.ins, sites, asm: a, targets: dec.targets };
}

// ---------- build ----------
const KINDS = ['call', 'cv', 'virt', 'iface', 'ifaceN', 'newobj', 'dlg', 'dinv', 'cons'] as const;
const MUTATORS = /^(Add|AddRange|Remove|RemoveAt|RemoveAll|Clear|Insert|set_Item|Push|Pop|Enqueue|Dequeue|Sort|Reverse)$/;
type Rec = { k: string; a: number; f: number; ic?: string; c: number[]; fr: number[]; fw: number[]; sr: number[]; sw: number[]; sc?: number[] };
export type Index = { version: 1; asms: { name: string; mvid: string }[]; kinds: string[]; methods: Rec[]; fields: string[]; stats: Record<string, number> };

function build(): Index {
  loadAll(ASM_DIR);
  const fieldIx = new Map<string, number>(); const fields: string[] = [];
  const fx = (id: string) => { let i = fieldIx.get(id); if (i === undefined) { i = fields.length; fields.push(id); fieldIx.set(id, i); } return i; };
  const recs: Rec[] = methods.map((m) => ({ k: m.id, a: asms.indexOf(m.asm), f: m.flags, c: [], fr: [], fw: [], sr: [], sw: [] }));
  const stubIx = (s: string) => { let i = stubs.get(s); if (i === undefined) { i = recs.length; recs.push({ k: s, a: -1, f: 0, c: [], fr: [], fw: [], sr: [], sw: [] }); stubs.set(s, i); } return i; };
  const st: Record<string, number> = { methods: methods.length, withBody: 0, ics: 0, ilErrors: 0, sites: 0, unresolvedSites: 0 };
  for (const k of KINDS) st[k] = 0;
  for (const m of methods) {
    const r = recs[m.gi];
    if (m.impl & IC_FLAG || m.flags & PINVOKE) { r.ic = `${m.type.full}::${m.name}`; st.ics++; continue; }
    let ins: Ins[];
    try { ins = body(m); } catch { st.ilErrors++; continue; }
    if (ins.length || m.rva) st.withBody++;
    const edges = new Set<string>();
    const collFields: string[] = []; let collMut = false;
    let constrained = false;
    for (const x of ins) {
      if (x.op === 0xfe16) { constrained = true; continue; }
      const op = x.op;
      if (op === 0x28 || op === 0x6f || op === 0x73 || op === 0xfe06 || op === 0xfe07) {
        const tg = resolveMethodToken(m.asm, x.tok);
        let kind: string; let tgt = tg.m;
        if (!tgt) kind = op === 0x73 ? 'newobj' : op >= 0xfe00 ? 'dlg' : op === 0x6f ? 'virt' : 'call';
        else if (op === 0x73) kind = 'newobj';
        else if (op >= 0xfe00) kind = 'dlg';
        else if (op === 0x28) kind = 'call';
        else if (constrained) kind = 'cons';
        else if (isDelegate(tgt.type) && tgt.name === 'Invoke') kind = 'dinv';
        else if (isIface(tgt.type)) kind = ifaceImpls(tgt) ? 'ifaceN' : 'iface';
        else if (!(tgt.flags & VIRT) || tgt.flags & FINAL || isSealed(tgt.type)) kind = 'cv';
        else kind = 'virt';
        constrained = false;
        if (op === 0x28 || op === 0x6f) { st.sites++; if (['virt', 'iface', 'cons', 'dinv'].includes(kind) || !tgt) st.unresolvedSites++; }
        if (tgt && MUTATORS.test(tgt.name) && tgt.type.full.startsWith('System.Collections')) collMut = true;
        for (const ti of kind === 'ifaceN' ? ifaceImpls(tgt!)!.map((x) => x.gi) : [tgt ? tgt.gi : stubIx(tg.stub!)]) {
          const key = `${ti}:${kind}`;
          if (!edges.has(key)) { edges.add(key); r.c.push(ti, KINDS.indexOf(kind as never)); st[kind]++; }
        }
        continue;
      }
      constrained = false;
      if (op >= 0x7b && op <= 0x80) {
        const f = resolveFieldToken(m.asm, x.tok); const i = fx(f.id);
        const list = op === 0x7d ? r.fw : op === 0x80 ? r.sw : op <= 0x7c ? r.fr : r.sr;
        if (!list.includes(i)) list.push(i);
        if ((op === 0x7e || op === 0x7f) && /^System\.Collections\./.test(f.type)) collFields.push(f.id);
      }
    }
    if (collMut && collFields.length) r.sc = [...new Set(collFields)].map(fx);
  }
  st.stubs = stubs.size; st.fields = fields.length;
  return { version: 1, asms: asms.map((a) => ({ name: a.name, mvid: a.mvid })), kinds: [...KINDS], methods: recs, fields, stats: st };
}

// ---------- 12b leaf rules + query ----------
const SEV = ['YIELD', 'MAIN', 'UNSAFE', 'IC?', 'EVENT', 'STATIC_WRITE', 'STATIC_COLL', 'DELEGATE', 'VIRTUAL', 'UNRESOLVED', 'PROXY', 'LAZY_CACHE', 'MONO_IC', 'SAFE'];
const YIELD_IC = /::(Simulator_SleepImpl|Simulator_SetYieldingDisabledImpl|SACS_AcquireDriverImpl|GameUtils_WaitFor\w*|GameUtils_StartupYield|OnlineFeaturesHost_WaitForDownload)$/;
const YIELD_M = /^(Sims3\.SimIFace\.Simulator::(Sleep|Yield\w*|set_YieldingDisabled)\(|Sims3\.SimIFace\.StateMachineClient::RequestState\()/;
// Compiler-generated lazy caches (cached anonymous delegates, string-switch maps): idempotent writes, own low tag.
const LAZY = /::<>9__CachedAnonymousMethodDelegate|^<PrivateImplementationDetails>/;
const EVENT_M = /\.EventTracker::(SendEvent|SendEvents|RaiseEvent)\w*\(/;

function parseCsv(text: string): string[][] {
  const rows: string[][] = []; let row: string[] = [], cur = '', q = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (q) { if (ch === '"') { if (text[i + 1] === '"') { cur += '"'; i++; } else q = false; } else cur += ch; continue; }
    if (ch === '"') q = true; else if (ch === ',') { row.push(cur); cur = ''; } else if (ch === '\n') { row.push(cur.replace(/\r$/, '')); rows.push(row); row = []; cur = ''; } else cur += ch;
  }
  if (cur || row.length) { row.push(cur); rows.push(row); }
  return rows;
}
export function icVerdicts(): Map<string, string> {
  const rows = parseCsv(readFileSync(IC_CSV, 'utf8'));
  const h = rows[0]; const ci = h.indexOf('ic'), vi = h.indexOf('verdict');
  const m = new Map(rows.slice(1).filter((r) => r[ci]).map((r) => [r[ci], r[vi]]));
  // By method name too, for bindings the csv names under another class (UTFConnector* twins, Sims3.UI.LocalizedStringService).
  const byName = new Map<string, string>();
  for (const [k, v] of m) { const n = '*::' + k.split('::')[1]; byName.set(n, byName.has(n) && byName.get(n) !== v ? 'IC?' : v); }
  for (const [k, v] of byName) m.set(k, v);
  return m;
}
function icTag(ix: Index, r: Rec, ics: Map<string, string>): string {
  return ics.get(r.ic!) ?? (ix.asms[r.a].name === 'mscorlib' ? 'MONO_IC' : ics.get('*::' + r.ic!.split('::')[1]) ?? 'IC?');
}
// Own tags of one method (not its callees): [tag, detail][].
export function ownTags(ix: Index, i: number, ics: Map<string, string>): [string, string][] {
  const r = ix.methods[i]; const out: [string, string][] = [];
  if (r.ic) { out.push([icTag(ix, r, ics), r.ic]); if (YIELD_IC.test(r.ic)) out.push(['YIELD', r.ic]); }
  if (YIELD_M.test(r.k)) out.push(['YIELD', r.k]);
  if (EVENT_M.test(r.k)) out.push(['EVENT', r.k]);
  if (r.a < 0) out.push(['UNRESOLVED', r.k]);
  for (const f of r.sw) out.push([LAZY.test(ix.fields[f]) ? 'LAZY_CACHE' : 'STATIC_WRITE', ix.fields[f]]);
  for (const f of r.sc ?? []) out.push([LAZY.test(ix.fields[f]) ? 'LAZY_CACHE' : 'STATIC_COLL', ix.fields[f]]);
  return out;
}
type Hit = { tag: string; detail: string; path: number[]; edge?: string };
// ovHops (docs/127): virt/iface edges in methods fewer than ovHops calls from a root also enter every game override (CHA).
export function closure(ix: Index, roots: number[], depth: number, ics: Map<string, string>, delegates = false, cut: string[] = [], ovHops = 0) {
  const parent = new Map<number, number>(); const dist = new Map<number, number>();
  const hits = new Map<string, Hit>(); const counts = new Map<string, number>();
  const note = (h: Hit) => { counts.set(h.tag, (counts.get(h.tag) ?? 0) + 1); if (!hits.has(h.tag)) hits.set(h.tag, h); };
  const pathTo = (n: number) => { const p = [n]; while (parent.has(p[0])) p.unshift(parent.get(p[0])!); return p; };
  let q = roots.filter((r, i, a) => a.indexOf(r) === i);
  q.forEach((r) => dist.set(r, 0));
  for (let d = 0; q.length && d <= depth; d++) {
    const next: number[] = [];
    for (const n of q) {
      for (const [tag, detail] of ownTags(ix, n, ics)) note({ tag, detail, path: pathTo(n) });
      const c = ix.methods[n].c;
      for (let k = 0; k < c.length; k += 2) {
        const t = c[k], kind = ix.kinds[c[k + 1]];
        if (kind === 'virt' || kind === 'iface' || kind === 'cons') note({ tag: 'VIRTUAL', detail: `${kind} ${ix.methods[t].k}`, path: pathTo(n), edge: kind });
        if (kind === 'dinv') note({ tag: 'DELEGATE', detail: ix.methods[t].k, path: pathTo(n), edge: kind });
        if (kind === 'dlg' && !delegates) continue;
        const ts = d < ovHops && (kind === 'virt' || kind === 'iface') ? [t, ...virtualTargets(t).ea] : [t];
        for (const u of ts) {
          if (cut.some((c) => ix.methods[u].k.startsWith(c))) continue;
          if (d < depth && !dist.has(u)) { dist.set(u, d + 1); parent.set(u, n); next.push(u); }
        }
      }
    }
    q = next;
  }
  const tags = [...hits.values()].sort((x, y) => SEV.indexOf(x.tag) - SEV.indexOf(y.tag));
  return { size: dist.size, reached: dist, tags, counts, worst: tags[0]?.tag ?? 'SAFE' };
}
const loadIndex = (): Index => JSON.parse(readFileSync(INDEX, 'utf8'));
function lookup(ix: Index, pat: string): number[] {
  const exact = ix.methods.findIndex((r) => r.k === pat);
  if (exact >= 0) return [exact];
  const out: number[] = [];
  ix.methods.forEach((r, i) => { if (r.k.startsWith(pat + '(') || r.k.startsWith(pat + '`')) out.push(i); });
  return out;
}
// Roots for an interaction definition: own Test overloads (else nearest base's) + nearest CalculateScore up the base chain.
export function defRoots(ix: Index, type: string): number[] {
  if (!asms.length) loadAll(ASM_DIR);
  const t = typeByFull.get(type);
  if (!t) throw new Error(`no type ${type}`);
  const byId = new Map(ix.methods.map((r, i) => [r.k, i]));
  const roots: number[] = [];
  for (const name of ['Test', 'CalculateScore']) {
    for (let c: TypeDef | undefined = t; c; c = baseOf(c)) {
      const ms = c.methods.filter((m) => m.name === name && !(m.flags & ABSTRACT));
      if (ms.length) { roots.push(...ms.map((m) => byId.get(m.id)!)); break; }
    }
  }
  return roots;
}
function printClosure(ix: Index, roots: number[], label: string): string {
  const ics = icVerdicts();
  const r = closure(ix, roots, DEPTH, ics, process.argv.includes('--delegates'), CUT);
  const lines = [`${label}: worst ${r.worst}; closure ${r.size} methods (depth ${DEPTH}); roots ${roots.map((i) => ix.methods[i].k).join(' | ')}`];
  for (const h of r.tags) lines.push(`  ${h.tag} x${r.counts.get(h.tag)}: ${h.detail}\n    path: ${h.path.map((i) => ix.methods[i].k.replace(/\(.*$/s, '')).join(' > ')}${h.edge ? ` > [${h.edge}]` : ''}`);
  // --ics: every native binding in the closure with its docs/62 verdict and depth (closure step of docs/118).
  if (process.argv.includes('--ics')) for (const [n, d] of [...r.reached].filter(([n]) => ix.methods[n].ic)) lines.push(`  IC\t${icTag(ix, ix.methods[n], ics)}\t${ix.methods[n].ic}\tdepth ${d}`);
  return lines.join('\n');
}

// ---------- 12d: virtual dispatch by class hierarchy analysis (docs/103) ----------
// Every override (class virtual) or implementation (interface) of a declared call target, across all loaded assemblies.
// Generic bases: a subclass of Base<X> overrides with concrete params, so a declared signature that mentions a type
// variable matches by name and parameter count. shortcut: no generic argument substitution, upgrade if a false match shows up.
const NEWSLOT = 0x100;
let kids: Map<TypeDef, TypeDef[]> | undefined;
let explicitImpls: Map<TypeDef, [Method, Method][]> | undefined; // class -> [declared, body] from the MethodImpl table
function hierarchy(): void {
  kids = new Map(); explicitImpls = new Map();
  for (const t of asms.flatMap((a) => a.types)) { const b = baseOf(t); if (b) kids.set(b, [...(kids.get(b) ?? []), t]); }
  for (const a of asms) for (const r of tbl(a, 0x19)) {
    const tok = (ci: number) => { const [k, i] = decodeCi('MethodDefOrRef', ci); return resolveMethodToken(a, ((k === 6 ? 0x06 : 0x0a) << 24) | i).m; };
    const body = tok(r[1]), decl = tok(r[2]); const t = a.types[r[0] - 1];
    if (body && decl) explicitImpls.set(t, [...(explicitImpls.get(t) ?? []), [decl, body]]);
  }
}
const sigEq = (d: Method, x: Method) => d.name === x.name && d.arity === x.arity &&
  ((d.params === x.params && d.ret === x.ret) || (/!/.test(d.params + d.ret) && d.params.split(',').length === x.params.split(',').length));
const ifaceClosure = (t: TypeDef, out = new Set<TypeDef>()): Set<TypeDef> => {
  for (const ci of t.ifaces) { const [k, i] = decodeCi('TypeDefOrRef', ci); const j = resolveTdor(t.asm, k, i); if (j && !out.has(j)) { out.add(j); ifaceClosure(j, out); } }
  return out;
};
export function overridesOf(m: Method): Method[] {
  if (!kids) hierarchy();
  if (isIface(m.type)) {
    const out = new Set<Method>();
    for (const c of asms.flatMap((a) => a.types)) {
      if (isIface(c) || !ifaceClosure(c).has(m.type)) continue;
      let impl: Method | undefined;
      for (let b: TypeDef | undefined = c; b && !impl; b = baseOf(b)) impl = explicitImpls!.get(b)?.find(([d]) => d === m)?.[1];
      for (let b: TypeDef | undefined = c; b && !impl; b = baseOf(b)) impl = b.methods.find((x) => x.flags & VIRT && sigEq(m, x));
      if (impl && !out.has(impl)) { out.add(impl); for (const o of overridesOf(impl)) out.add(o); }
    }
    return [...out];
  }
  if (!(m.flags & VIRT) || m.flags & FINAL || isSealed(m.type)) return [];
  const out: Method[] = [];
  const walk = (t: TypeDef) => {
    for (const c of kids!.get(t) ?? []) {
      const x = c.methods.find((y) => y.flags & VIRT && sigEq(m, y));
      if (x && x.flags & NEWSLOT) continue; // hides the slot: its subtree overrides x, not m
      if (x) out.push(x);
      walk(c);
    }
  };
  walk(m.type);
  return out;
}
// Index-position form of overridesOf (docs/125 addendum: rng-unit-paths.ts); the index must come from the same --asm.
export function overrideIds(gi: number): number[] {
  if (!asms.length) loadAll(ASM_DIR);
  const m = methods[gi];
  return m ? overridesOf(m).map((o) => o.gi) : [];
}

// docs/127: dispatch targets of one virt/iface edge for a closure. Game overrides as index ids; mod overrides by id only
// (no body in the index). BCL declared targets (object/collection members) are not expanded, as in docs/103.
// self (docs/127 §2): the closure's root type (an interaction definition or meta object). A call from a method declared on
// self's base chain to a virtual also declared there is taken as a call on `this` and resolves to self's family (familyTargets)
// [inferred: receiver not tracked; verified for the generic base Test calling this.Test(actor, target)].
const vtCache = new Map<number, { ea: number[]; mod: string[] }>();
const chainOf = new Map<string, Set<TypeDef>>();
// Interaction instance class of a definition: the first generic argument up its base chain that derives from
// InteractionInstance (the TInteraction that CreateInstance news up). undefined if only generic parameters are found.
export function instanceTypeOf(type: string): string | undefined {
  if (!asms.length) loadAll(ASM_DIR);
  const isInst = (n: string) => { for (let c = typeByFull.get(n); c; c = baseOf(c)) if (c.full === 'Sims3.Gameplay.Interactions.InteractionInstance') return true; return false; };
  for (let c = typeByFull.get(type); c; c = baseOf(c)) {
    const [k, i] = decodeCi('TypeDefOrRef', c.extendsCi); if (k !== 0x1b || !i) continue; // generic instance = TypeSpec
    const n = tdorName(c.asm, k, i); const a = n.slice(n.indexOf('<') + 1, -1);
    let d = 0, st = 0; const args: string[] = [];
    for (let j = 0; j <= a.length; j++) { const ch = a[j]; if (ch === '<') d++; else if (ch === '>') d--; else if ((ch === ',' && !d) || j === a.length) { args.push(a.slice(st, j)); st = j + 1; } }
    const hit = args.find((x) => !x.includes('!') && isInst(x));
    if (hit) return hit;
  }
  return undefined;
}
// Is method gi declared on fam or one of fam's base classes?
export function onChain(fam: string, gi: number): boolean {
  if (!asms.length) loadAll(ASM_DIR);
  let ch = chainOf.get(fam);
  if (!ch) { ch = new Set(); for (let c = typeByFull.get(fam); c; c = baseOf(c)) ch.add(c); chainOf.set(fam, ch); }
  return !!methods[gi] && ch.has(methods[gi].type);
}
export function isA(type: string, base: string): boolean {
  if (!asms.length) loadAll(ASM_DIR);
  for (let c = typeByFull.get(type); c; c = baseOf(c)) if (c.full === base) return true;
  return false;
}
export function virtualTargets(decl: number, self?: string, caller?: number): { ea: number[]; mod: string[] } {
  if (!asms.length) loadAll(ASM_DIR);
  const m0 = methods[decl];
  if (self && m0 && caller !== undefined && !isIface(m0.type) && onChain(self, decl) && onChain(self, caller)) return { ea: familyTargets(decl, self) ?? [], mod: [] };
  let r = vtCache.get(decl);
  if (!r) {
    const m = methods[decl];
    const os = !m || BCL.has(m.asm.name) || modAsms.has(m.asm.name) ? [] : overridesOf(m);
    r = { ea: os.filter((o) => !modAsms.has(o.asm.name)).map((o) => o.gi), mod: os.filter((o) => modAsms.has(o.asm.name)).map((o) => `${o.asm.name}:${o.id}`) };
    vtCache.set(decl, r);
  }
  return r;
}
// Overrides a receiver of static type fam can dispatch to. Class: overrides declared in fam's subtree plus the nearest one
// on fam's base chain. Interface: overrides on classes that implement fam or have a base or subclass that does.
// undefined when fam is unknown (caller falls back to CHA).
const famCache = new Map<string, number[] | undefined>();
const implementsI = (t: TypeDef, i: TypeDef) => { for (let c: TypeDef | undefined = t; c; c = baseOf(c)) if (c === i || ifaceClosure(c).has(i)) return true; return false; };
export function familyTargets(decl: number, fam: string): number[] | undefined {
  if (!asms.length) loadAll(ASM_DIR);
  const key = `${decl}|${fam}`; if (famCache.has(key)) return famCache.get(key);
  const m = methods[decl], f = typeByFull.get(fam);
  let r: number[] | undefined;
  if (m && f && !BCL.has(m.asm.name) && !modAsms.has(m.asm.name)) {
    const all = overridesOf(m).filter((o) => !modAsms.has(o.asm.name));
    let out: Method[];
    if (isIface(f)) out = all.filter((o) => implementsI(o.type, f) || derivedTypes(o.type).some((d) => implementsI(d, f)));
    else {
      const sub = new Set(derivedTypes(f)); out = all.filter((o) => sub.has(o.type));
      for (let c = baseOf(f); c && c !== m.type; c = baseOf(c)) { const x = all.find((o) => o.type === c); if (x) { out.push(x); break; } }
    }
    r = out.map((o) => o.gi);
  }
  famCache.set(key, r);
  return r;
}
const derivedTypes = (t: TypeDef): TypeDef[] => { if (!kids) hierarchy(); return [t, ...(kids!.get(t) ?? []).flatMap(derivedTypes)]; };
export function subtypes(type: string): { full: string; asm: string; abstract: boolean }[] | null {
  if (!asms.length) loadAll(ASM_DIR);
  const t = typeByFull.get(type);
  return t ? derivedTypes(t).map((x) => ({ full: x.full, asm: x.asm.name, abstract: (x.flags & 0x80) !== 0 })) : null;
}

// tools/autshadow-il-hashes.ts: metadata helpers for token names
export { decodeCi, str as heapString, typeRefName, resolveTdor, blob };

// tools/offload-override-scan.ts: type model over one directory (game + mod assemblies; files sort game first, and the
// first type of a full name wins resolution). Unreadable assemblies are listed in `skipped`, not fatal.
export function typeModel(dir: string, skipped: string[]) {
  if (!asms.length) loadAll(dir, skipped);
  if (!kids) hierarchy();
  return { asms, typeByFull, baseOf, ifaceClosure, isIface, sigEq, VIRT, NEWSLOT, fieldType: (f: Field) => fieldSigType(f.asm, f.sigBlob) };
}

// Root groups for the routing study. Specs: method id prefix; type:<T> (its methods); derived:<T> (T and subclasses);
// name:<Method>/<param prefix> (any method of that name); callers:<method prefix> (methods calling it).
type Group = { name: string; depth: number; follow: boolean; roots: string[] };
export const ROUTING_GROUPS: Group[] = [
  { name: 'portal', depth: 12, follow: true, roots: ['Sims3.Gameplay.Core.LotManager::PortalLockedCallback', 'Sims3.Gameplay.Core.Lot::OnPortalPathPlan', 'Sims3.Gameplay.Actors.Sim::OnPortalPathPlan', 'Sims3.Gameplay.Situations.PrivacySituation::OnPortalPathPlan'] },
  { name: 'event68', depth: 2, follow: true, roots: ['Sims3.Gameplay.Core.Lot::OnLevelChanged', 'Sims3.Gameplay.Core.LotManager::OnLotLevelInfoChanged', 'Sims3.Gameplay.UI.HudModel::OnLotLevelInfoChange'] },
  { name: 'setup', depth: 4, follow: true, roots: ['name:AboutToPlanRouteCallback/Sims3.SimIFace.Route,string', 'name:AboutToPlanCallback/Sims3.SimIFace.Route,string', 'name:PostPlanRouteCallback/Sims3.SimIFace.Route,string', 'name:PostPlanDelegate/Sims3.SimIFace.Route,string', 'name:UpdateFollowersOnReplan/Sims3.SimIFace.Route,string', 'type:ScriptCore.Route', 'type:Sims3.SimIFace.Route'] },
  { name: 'component', depth: 0, follow: false, roots: ['derived:Sims3.Gameplay.ObjectComponents.RoutingComponent'] },
  { name: 'consumer', depth: 0, follow: false, roots: ['callers:ScriptCore.Route::Plan(', 'callers:ScriptCore.Route::PlanFromPoint(', 'callers:ScriptCore.Route::Replan(', 'callers:ScriptCore.Route::ReplanFromPoint(', 'callers:Sims3.SimIFace.Route::Plan(', 'callers:Sims3.SimIFace.Route::PlanFromPoint(', 'callers:Sims3.SimIFace.Route::Replan(', 'callers:Sims3.SimIFace.Route::ReplanFromPoint('] },
  { name: 'routeaction', depth: 0, follow: false, roots: ['derived:Sims3.Gameplay.Routing.RouteAction'] },
  { name: 'destination', depth: 0, follow: false, roots: ['derived:Sims3.SimIFace.RouteDestination'] },
];
const BCL = new Set(['mscorlib', 'System', 'System.Xml']);
type Site = { caller: number; decl: number; kind: string; groups: Set<string> };
export function dispatchSites(ix: Index, groups: Group[]): { sites: Site[]; reached: Map<string, number> } {
  const byPrefix = (p: string) => ix.methods.flatMap((r, i) => (r.k.startsWith(p) && r.a >= 0 ? [i] : []));
  const rootsOf = (s: string): number[] => {
    const [kind, rest] = /^(type|derived|name|callers):/.test(s) ? [s.slice(0, s.indexOf(':')), s.slice(s.indexOf(':') + 1)] : ['id', s];
    if (kind === 'id') return lookup(ix, s).length ? lookup(ix, s) : byPrefix(s);
    if (kind === 'type' || kind === 'derived') {
      const t = typeByFull.get(rest); if (!t) throw new Error(`no type ${rest}`);
      return (kind === 'type' ? [t] : derivedTypes(t)).flatMap((x) => x.methods.map((m) => m.gi));
    }
    if (kind === 'name') { const [n, pp] = rest.split('/'); return methods.filter((m) => m.name === n && m.params.startsWith(pp)).map((m) => m.gi); }
    if (kind === 'callers') { const ts = new Set(byPrefix(rest)); return ix.methods.flatMap((r, i) => { for (let k = 0; k < r.c.length; k += 2) if (ts.has(r.c[k])) return [i]; return []; }); }
    throw new Error(`bad root ${s}`);
  };
  const sites = new Map<string, Site>(); const reached = new Map<string, number>();
  for (const g of groups) {
    const seen = new Set<number>(); let q = [...new Set(g.roots.flatMap(rootsOf))];
    if (!q.length) throw new Error(`group ${g.name}: no roots`);
    for (let d = 0; q.length && d <= g.depth; d++) {
      const next: number[] = [];
      const push = (t: number) => { if (d < g.depth && !seen.has(t)) { seen.add(t); next.push(t); } };
      for (const n of q) {
        seen.add(n);
        const c = ix.methods[n].c;
        for (let k = 0; k < c.length; k += 2) {
          const t = c[k], kind = ix.kinds[c[k + 1]];
          if (kind === 'dlg' || kind === 'dinv') continue;
          if (kind === 'virt' || kind === 'iface' || kind === 'cons') {
            const key = `${n}>${t}`;
            const s = sites.get(key) ?? { caller: n, decl: t, kind, groups: new Set() }; s.groups.add(g.name); sites.set(key, s);
            if (g.follow && t < methods.length && !BCL.has(methods[t].asm.name)) for (const o of overridesOf(methods[t])) push(o.gi);
          }
          push(t);
        }
      }
      q = next;
    }
    reached.set(g.name, seen.size);
  }
  return { sites: [...sites.values()], reached };
}
// Routing-decision heuristic on the declared method; docs/103 §3 reviews every flagged row by hand.
const ROUTING_DECL = /::(IsAllowedThrough|PortalCost|get_PortalCost|DiscourageSimFromUsing|OtherSimCanIgnorePrivacy|OtherSimCanJoinSituation|SimIsInPrivacySituation|\w*Route\w*|\w*Routing\w*|\w*Portal\w*|\w*Destination\w*|\w*Goal\w*|\w*Path\w*|\w*Slot\w*|\w*Footprint\w*|\w*Obstacle\w*|\w*Lane\w*|\w*Door\w*|\w*Lock\w*|\w*Plan\w*|\w*Jig\w*|\w*Walkstyle\w*|\w*Posture\w*|\w*Avoid\w*|\w*Ignore\w*|CanSee\w*|\w*Allowed\w*)\(/;
// Hand review (docs/103 §3): [declared or override id prefix, y/n, note]. First match wins; overrides before declared.
// Categories: P portal allow/deny, C cost/options/forbidden list/footprints (search input), G goal/destination, R result handling.
const G = 'Sims3.Gameplay.';
const REVIEW: [string, 'y' | 'n', string][] = [
  // P: portal allow/deny (event 59 handlers, docs/92 §5)
  [`${G}Abstracts.NonAnimatedDoor::IsAllowedThrough(`, 'n', 'P: unreachable from Sim.OnPortalPathPlan (receiver tested is Door; NonAnimatedDoor derives from CommonDoor)'],
  [`${G}Objects.Door.SecretDoor::IsAllowedThrough(`, 'n', 'P: unreachable from Sim.OnPortalPathPlan (SecretDoor derives from NonAnimatedDoor, not Door)'],
  [`${G}Abstracts.CommonDoor::IsAllowedThrough(int,int,`, 'y', 'P: door lock rules; DoorSingleGender adds the gender rule, Turnstile tickets'],
  [`${G}Interfaces.IBoxStall::IsAllowedThrough(`, 'y', 'P: box stall gate'],
  [`${G}Interfaces.IHorseJump::IsAllowedThrough(`, 'y', 'P: horse jump'],
  [`${G}Interactions.InteractionInstance::get_LinkedInteractionInstance(`, 'y', 'P: HorseJump.IsAllowedThrough reads the linked interaction'],
  [`${G}Situations.PrivacySituation::OtherSimCanIgnorePrivacy(`, 'y', 'P: privacy lock (PrivacySituation.OnPortalPathPlan)'],
  [`${G}ActorSystems.Posture::Satisfaction(`, 'y', 'P: privacy overrides test Posture.Satisfies (BeingCarried, Standing, WalkingToddler)'],
  [`${G}ActorSystems.Posture::get_Container(`, 'y', 'P: WooHoo privacy reads the carrying Sim from Posture.Container'],
  [`${G}Services.Service::get_ServiceType(`, 'y', 'P: CommonDoor.IsAllowedThrough service rule (constant per service class)'],
  // C: search inputs set before the plan
  [`${G}Core.Lot::get_IsInPublicResidentialRoom(`, 'n', 'C: not a door; the forbidden-list site reads doors only'],
  [`${G}Abstracts.GameObject::get_IsInPublicResidentialRoom(`, 'y', 'C: SimRoutingComponent.AboutToPlanRouteCallback adds non-public doors to the forbidden-portal list'],
  [`${G}ObjectComponents.RoutingComponent::CreateRoute(`, 'y', 'C: route options and AGS per component class'],
  [`${G}Core.Terrain+GoHere::SetSpecialRouteOptions(`, 'y', 'C: swim/boat route options'],
  [`${G}Core.SocialJigTwoPerson::AddRouteOptionsToJig`, 'y', 'C: jig route options'],
  [`${G}ActorSystems.Posture::HasSlaveRouteObjects(`, 'y', 'C: horse rider/leader slave objects (ignore list, follower)'],
  [`${G}Interfaces.IBoat::IsSlaveRoute(`, 'y', 'C: boat slave route objects'],
  [`${G}Interfaces.IBoat::UsesTurnHelperFootprint(`, 'y', 'C: boat turn helper footprint'],
  [`${G}Core.DynamicRegionBase::UpdateFootprint(`, 'y', 'C: discourage/impassable region footprints in the routing world'],
  [`${G}Abstracts.ObjectWithFlexibleBuyModePlacement::UpdateGeoStateAndFootprint(`, 'y', 'C: object footprint state in the routing world'],
  // G: goal and destination choice
  [`${G}Abstracts.MarineLifeState::WanderRoutePrePlanning(`, 'y', 'G: fish/shark wander destination'],
  [`${G}InteractionsShared.IViewableBase::GetViewRouteTarget(`, 'y', 'G: view-object route target'],
  [`${G}Core.SocialJig::GetSlotForActor(`, 'y', 'G: jig slot per actor'],
  [`${G}Core.SocialJig::RouteToJigA(`, 'y', 'G: jig routing (destination and options)'],
  [`${G}Abstracts.Shell::GetRouteToShell(`, 'y', 'G: shell/rabbit hole entry route'],
  [`${G}Abstracts.CommonDoor::GetRoutingSlotsOnSide(`, 'y', 'G: door routing slots'],
  [`${G}ActorSystems.SimQueueBehindSlotLine::AlterRadialRangeDestination(`, 'y', 'G: queue line destination'],
  [`${G}Interfaces.IGameObject::get_SimLine(`, 'y', 'G: SimRoutingComponent.PreRouteCheckForLine picks the line spot'],
  [`${G}Objects.Counters.BarAdvanced::get_RoutingSlotBartender(`, 'y', 'G: bartender slot'],
  [`${G}Objects.Electronics.Stereo::get_RouteDistanceForUse(`, 'y', 'G: radius around the stereo'],
  [`${G}ObjectComponents.PartData::get_RouteTarget(`, 'y', 'G: part route target (bunk bed upper)'],
  [`${G}ObjectComponents.PartData::GetContainmentSlot(`, 'y', 'G: part slot'],
  [`${G}Objects.Vehicles.Vehicle::RouteToAndGetIn(`, 'y', 'G: vehicle entry route'],
  [`${G}Objects.HobbiesSkills.HorseJump::get_StartSlots(`, 'y', 'G: horse jump start slots'],
  [`${G}Objects.HobbiesSkills.HorseJump::get_EndSlots(`, 'y', 'G: horse jump end slots'],
  [`${G}ObjectComponents.PortalComponent::GetStartSlotsForApproachingSim(`, 'y', 'G: portal approach slots (route to the portal start)'],
  // R: result handling after the plan
  [`${G}ObjectComponents.RoutingComponent::DoRoute(`, 'y', 'R: follows or rejects the planned route'],
  [`${G}Abstracts.GameObject::DoRoute(`, 'y', 'R: car and marine life route handling'],
  [`${G}ObjectComponents.RoutingComponent::OnAboutToStartFollowingRoute(`, 'y', 'R: pre-follow edits (followers, vehicles)'],
  [`${G}Routing.RouteAction::PerformAction(`, 'y', 'R: route action per subpath; interrupt actions replan or fail'],
  [`${G}Routing.FollowRouteAction::ForceReplan(`, 'y', 'R: replan from the route action'],
  [`${G}ObjectComponents.PortalComponent::LockRoutingLane(`, 'y', 'R: lane lock failure stops or replans at the portal'],
  [`${G}ObjectComponents.PortalComponent::IsFullyBlocked(`, 'y', 'R: blocked portal at traversal'],
  [`${G}Objects.Vehicles.GoHereWithSituation+GoHereWithChildSituation::OnRouteReplan(`, 'y', 'R: follower replan (PostPlanCallback)'],
  // Single implementation: no dispatch to resolve
  ['Sims3.SimIFace.Route::', 'n', 'single implementation ScriptCore.Route'],
  ['Sims3.SimIFace.RouteDestination::', 'n', 'abstract API, ScriptCore bodies'],
];
const COLLAPSE = 40; // a declared method reviewed 'n' with more overrides than this gets one summary row
function reviewed(decl: string, ov: string, flagged: boolean): ['y' | 'n', string] {
  const hit = (ov && REVIEW.find(([p]) => ov.startsWith(p))) || REVIEW.find(([p]) => decl.startsWith(p));
  return hit ? [hit[1], hit[2]] : ['n', flagged ? 'routing-named, reviewed: no routing effect' : ''];
}
// Pack of an override: the install's OBJK script class names (--packs json: class -> pack dirs) of the override's
// outermost type and its subclasses. GameData = base game. Empty when no object class instantiates it (components, postures).
let packMap: Record<string, string[]> | undefined;
function packsOf(m: Method): string {
  if (!packMap) { const f = arg('--packs', ''); packMap = f ? JSON.parse(readFileSync(f, 'utf8')) : {}; }
  let t = m.type; while (t.enclosing) t = t.enclosing;
  const ps = new Set(derivedTypes(t).flatMap((x) => packMap![x.full] ?? []));
  if (ps.has('GameData')) return 'base';
  if (!ps.size && m.asm.name === 'Sims3StoreObjects') return 'store';
  return [...ps].sort((a, b) => a.localeCompare(b, 'en', { numeric: true })).join('/');
}
function runDispatch(): void {
  const ix = build();
  const { sites, reached } = dispatchSites(ix, ROUTING_GROUPS);
  const q = (s: string) => (/[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s);
  // One csv row per (declared method, override); the call site column names up to 3 callers and the count.
  const rows = ['call site,declared method,override,assembly,affects routing decision,note'];
  const st = { sites: sites.length, virt: 0, iface: 0, cons: 0, bclSites: 0, gameSites: 0, gameDeclared: 0, noOverride: 0, overrides: 0, flaggedDeclared: 0, yRows: 0 };
  const byDecl = new Map<number, Site[]>();
  for (const s of sites) { st[s.kind as 'virt' | 'iface' | 'cons']++; byDecl.set(s.decl, [...(byDecl.get(s.decl) ?? []), s]); }
  const flaggedOut: string[] = [], yOut: string[] = [];
  for (const [d, ss] of [...byDecl].sort((a, b) => ix.methods[a[0]].k.localeCompare(ix.methods[b[0]].k))) {
    const callers = [...new Set(ss.map((s) => ix.methods[s.caller].k))].sort();
    const site = `${callers.length}: ${callers.slice(0, 3).join('; ')}${callers.length > 3 ? '; ...' : ''}`;
    const g = [...new Set(ss.flatMap((s) => [...s.groups]))].join('+'), kinds = [...new Set(ss.map((s) => s.kind))].join('/');
    const decl = ix.methods[d].k, dm = d < methods.length ? methods[d] : undefined;
    if (!dm || BCL.has(dm.asm.name) || kinds === 'cons') {
      st.bclSites += ss.length; rows.push([site, decl, '(BCL or constrained: not listed)', dm?.asm.name ?? '?', 'n', `${kinds}; ${g}`].map(q).join(',')); continue;
    }
    st.gameSites += ss.length; st.gameDeclared++;
    const ovs = overridesOf(dm); const flagged = ROUTING_DECL.test(decl);
    if (!ovs.length) st.noOverride++;
    st.overrides += ovs.length;
    if (flagged) { st.flaggedDeclared++; flaggedOut.push(`flagged ${decl} sites=${callers.length} overrides=${ovs.length} groups=${g}`); }
    const [dyn, dnote] = reviewed(decl, '', flagged);
    if (ovs.length > COLLAPSE && dyn === 'n' && !REVIEW.some(([p, v]) => v === 'y' && ovs.some((o) => o.id.startsWith(p)))) {
      const asmN = [...new Set(ovs.map((o) => o.asm.name))].sort().join('/');
      rows.push([site, decl, `(${ovs.length} overrides, not listed)`, asmN, 'n', `${kinds}; ${g}${dnote ? '; ' + dnote : ''}`].map(q).join(','));
      continue;
    }
    for (const o of [...(dm.flags & ABSTRACT ? [] : [dm]), ...ovs]) {
      const [yn, note] = reviewed(decl, o === dm ? '' : o.id, flagged);
      if (yn === 'y') { st.yRows++; yOut.push(`y ${o.id} [${o.asm.name}] packs=${packsOf(o) || '-'} :: ${note}`); }
      const pk = packsOf(o);
      rows.push([site, decl, o === dm ? '(declared body)' : o.id, o.asm.name, yn, `${kinds}; ${g}${note ? '; ' + note : ''}${pk ? '; packs ' + pk : ''}`].map(q).join(','));
    }
  }
  const out = arg('--csv', '');
  if (out) writeFileSync(out, rows.join('\n') + '\n');
  console.log(JSON.stringify({ reached: Object.fromEntries(reached), ...st, rows: rows.length - 1 }));
  for (const l of [...flaggedOut, ...yOut]) console.log(l);
}

// ---------- self-check ----------
function selfCheck(): void {
  // Synthetic: compressed ints and an IL stream (ldarg.0; call tok; ldc.i4.s 5; switch(2); ldfld tok; ret).
  const c = Buffer.from([0x03, 0x80, 0x80, 0xc0, 0x00, 0x40, 0x00]);
  if (cuint(c, 0)[0] !== 3 || cuint(c, 1)[0] !== 0x80 || cuint(c, 3)[0] !== 0x4000) throw new Error('selfcheck: cuint');
  const il = Buffer.from([0x02, 0x28, 1, 0, 0, 6, 0x1f, 5, 0x45, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0x7b, 2, 0, 0, 4, 0xfe, 0x06, 3, 0, 0, 6, 0x2a]);
  const ins = decodeIl(il, 0, il.length);
  if (ins.length !== 3 || ins[0].tok !== 0x06000001 || ins[1].op !== 0x7b || ins[2].op !== 0xfe06) throw new Error('selfcheck: IL decode');
  if (!existsSync(ASM_DIR)) { console.log('self-check: synthetic ok; no assemblies at', ASM_DIR, '(real-data checks skipped)'); return; }
  // Real data.
  const ix = build();
  const ids = new Map(ix.methods.map((r, i) => [r.k, i]));
  const fail = (m: string) => { throw new Error('selfcheck: ' + m); };
  if (ix.stats.ilErrors) fail(`${ix.stats.ilErrors} IL decode errors`);
  const ics = icVerdicts();
  const icBound = new Set(ix.methods.filter((r) => r.ic).map((r) => r.ic!));
  const rows = [...ics.keys()].filter((k) => !k.startsWith("*::"));
  const missing = rows.filter((k) => !icBound.has(k));
  if (missing.length > rows.length * 0.02) fail(`${missing.length} ic-safety rows not bound to an IC method, e.g. ${missing.slice(0, 3)}`);
  const cs = lookup(ix, 'Sims3.Gameplay.Autonomy.Autonomy::CalculateScore');
  if (!cs.length) fail('Autonomy.CalculateScore not found');
  const callers = ix.methods.flatMap((r, i) => { for (let k = 0; k < r.c.length; k += 2) if (cs.includes(r.c[k])) return [i]; return []; });
  if (!callers.some((i) => ix.methods[i].k === 'Sims3.Gameplay.Autonomy.Autonomy::CalculateScoreAndAddToCandidates(Sims3.Gameplay.Autonomy.InteractionObjectPair)')) fail(`CalculateScore callers miss C1 (docs/59): ${callers.map((i) => ix.methods[i].k).slice(0, 5)}`);
  const sends = lookup(ix, 'Sims3.Gameplay.EventSystem.EventTracker::SendEvent');
  if (sends.length < 4) fail(`EventTracker.SendEvent overloads ${sends.length} < 4 (overloads not split?)`);
  const sleep = lookup(ix, 'Sims3.SimIFace.Simulator::Sleep');
  if (!sleep.length) fail('Simulator.Sleep missing');
  const sc = closure(ix, sleep, 4, ics);
  if (![...sc.reached.keys()].some((i) => ix.methods[i].ic === 'ScriptCore.Simulator::Simulator_SleepImpl')) fail('Simulator.Sleep does not reach Simulator_SleepImpl (interface resolution)');
  if (!ids.size) fail('no ids');
  // 12d: class hierarchy analysis across assemblies (docs/103).
  const ov = (id: string) => { const m = methods.find((x) => x.id === id); if (!m) fail(`no method ${id}`); return overridesOf(m!).map((o) => o.id); };
  const priv = ov('Sims3.Gameplay.Situations.PrivacySituation::OtherSimCanIgnorePrivacy(Sims3.Gameplay.Actors.Sim)');
  if (!priv.some((x) => x.startsWith('Sims3.Store.Objects.RoutineMachine+')) || !priv.some((x) => x.startsWith('Sims3.Gameplay.Situations.GenericPrivacySituation::'))) fail(`privacy overrides miss the store or generic one: ${priv.length}`);
  const door = ov('Sims3.Gameplay.Abstracts.CommonDoor::IsAllowedThrough(int,int,Sims3.Gameplay.Actors.Sim)');
  if (!door.some((x) => x.startsWith('Sims3.Store.Objects.Turnstile::')) || !door.some((x) => x.startsWith('Sims3.Gameplay.Abstracts.DoorSingleGender::'))) fail(`door overrides: ${door}`);
  const plan = ov('Sims3.SimIFace.Route::Plan()');
  if (plan.length !== 1 || plan[0] !== 'ScriptCore.Route::Plan()') fail(`Route.Plan implementations: ${plan}`);
  const box = ov('Sims3.Gameplay.Interfaces.IBoxStall::IsAllowedThrough(Sims3.Gameplay.Actors.Sim)');
  if (box.length !== 1 || !box[0].startsWith('Sims3.Gameplay.Objects.Pets.BoxStall::')) fail(`IBoxStall implementations: ${box}`);
  console.log(`  12d: privacy overrides ${priv.length}, door overrides ${door.length}, Route.Plan -> ScriptCore.Route, IBoxStall -> BoxStall`);
  // docs/127: a closure with ovHops enters override bodies the plain closure cannot see (door rule behind Sim.OnPortalPathPlan).
  const opp = lookup(ix, 'Sims3.Gameplay.Actors.Sim::OnPortalPathPlan');
  const has = (r: ReturnType<typeof closure>, p: string) => [...r.reached.keys()].some((i) => ix.methods[i].k.startsWith(p));
  const turn = 'Sims3.Store.Objects.Turnstile::IsAllowedThrough(';
  if (!opp.length || has(closure(ix, opp, 2, ics), turn) || !has(closure(ix, opp, 2, ics, false, [], 2), turn)) fail('ovHops closure does not add Turnstile.IsAllowedThrough to Sim.OnPortalPathPlan');
  const ots = lookup(ix, 'System.Object::ToString()');
  if (!ots.length || virtualTargets(ots[0]).ea.length) fail('System.Object::ToString missing or expanded (BCL targets stay closed)');
  // `this` rule: the generic base Test(ref parameters) calls this.Test(actor, target, ...): one target for a known definition.
  const gt = lookup(ix, 'Sims3.Gameplay.Interactions.InteractionDefinition`3::Test(!0,!1,bool,Sims3.SimIFace.GreyedOutTooltipCallback&)');
  const gc = lookup(ix, 'Sims3.Gameplay.Interactions.InteractionDefinition`3::Test(Sims3.Gameplay.Interactions.InteractionInstanceParameters&,Sims3.SimIFace.GreyedOutTooltipCallback&)');
  const self = virtualTargets(gt[0], 'Sims3.Gameplay.Core.VisitLot+Definition', gc[0]).ea.map((i) => ix.methods[i].k);
  if (gt.length !== 1 || gc.length !== 1 || !self.length || self.length > 5 || !self.some((x) => x.startsWith('Sims3.Gameplay.Core.VisitLot+Definition::Test('))) fail(`this rule: ${self}`); // the definition and its subclasses
  if (virtualTargets(gt[0]).ea.length < 1000) fail('generic Test overrides < 1000 without the this rule');
  // Receiver static type from the IL: GameObject.GetInUseAutonomyMultiplier reads this.InUse (ldarg.0);
  // CalculateScoreForObjectInteraction calls GetInUseAutonomyMultiplier on its IGameObject local (a stloc'd iop.Target).
  const gm = lookup(ix, 'Sims3.Gameplay.Abstracts.GameObject::GetInUseAutonomyMultiplier'), inUse = lookup(ix, 'Sims3.Gameplay.Abstracts.GameObject::get_InUse');
  const r1 = gm.length && inUse.length ? receiverTypes(gm[0]).get(inUse[0]) : undefined;
  if (!r1?.isThis || r1.type !== 'Sims3.Gameplay.Abstracts.GameObject') fail(`receiver of InUse in GetInUseAutonomyMultiplier: ${JSON.stringify(r1)}`);
  const csfo = lookup(ix, 'Sims3.Gameplay.Autonomy.Autonomy::CalculateScoreForObjectInteraction'); const igm = lookup(ix, 'Sims3.Gameplay.Interfaces.IGameObject::GetInUseAutonomyMultiplier');
  const r2 = csfo.length && igm.length ? csfo.map((c) => receiverTypes(c).get(igm[0])).find(Boolean) : undefined;
  if (r2?.type !== 'Sims3.Gameplay.Interfaces.IGameObject' || r2.isThis) fail(`receiver of GetInUseAutonomyMultiplier: ${JSON.stringify(r2)}`);
  // A receiver loaded from a singleton field takes the type of the only object stored there (exactStaticFieldType).
  const rug = lookup(ix, 'Sims3.Gameplay.Objects.Decorations.PlayOnRug+Definition::Test(Sims3.Gameplay.Interactions.InteractionInstanceParameters&,Sims3.SimIFace.GreyedOutTooltipCallback&)'), idt = lookup(ix, 'Sims3.Gameplay.Interactions.InteractionDefinition::Test(Sims3.Gameplay.Interactions.InteractionInstanceParameters&,Sims3.SimIFace.GreyedOutTooltipCallback&)');
  const r3 = rug.length && idt.length ? receiverTypes(rug[0]).get(idt[0]) : undefined;
  if (r3?.type !== 'Sims3.Gameplay.Core.Terrain+PlayOnCarpetInternal+Definition') fail(`receiver of the PlayOnCarpetInternal.Singleton Test call: ${JSON.stringify(r3)}`);
  const lotOv = familyTargets(inUse[0], 'Sims3.Gameplay.Core.Lot') ?? [];
  if (lotOv.length >= virtualTargets(inUse[0]).ea.length) fail('Lot family not narrower than all get_InUse overrides');
  console.log(`  docs/127: Sim.OnPortalPathPlan + overrides reaches Turnstile.IsAllowedThrough; BCL targets not expanded; this rule: generic Test -> ${self.length} VisitLot+Definition family Tests (of ${virtualTargets(gt[0]).ea.length}); receiver types: this.InUse, IGameObject local`);
  console.log(`self-check ok: ${ix.stats.methods} methods, ${ix.stats.ics} ICs (${rows.length - missing.length}/${rows.length} csv rows bound), CalculateScore callers ${callers.length}, SendEvent overloads ${sends.length}, Sleep reaches SleepImpl`);
  for (const i of callers.slice(0, 8)) console.log('  caller:', ix.methods[i].k);
}

// ---------- main ----------
const VALUE_FLAGS = new Set(['--asm', '--out', '--index', '--ic', '--depth', '--cut', '--csv', '--packs', '--mods']);
function positional(): string[] {
  const out: string[] = []; const a = process.argv.slice(3);
  for (let i = 0; i < a.length; i++) { if (VALUE_FLAGS.has(a[i])) i++; else if (!a[i].startsWith('--')) out.push(a[i]); }
  return out;
}
export function bindKeys(): { hash: string; id: string; key: string; m: Method }[] {
  // docs/122 S0: EA's native-method binder 0xD9A7B0 keys each method by the FNV-1 hash (lowercased bytes, 0x811C9DC5,
  // prime 0x1000193, multiply then xor) of "<ret> <Class>:<Method>(<arg>,...)" with Mono reflection type names
  // (0xD9A530). One entry per non-abstract method (the bindkeys command prints them); join with the 1,085 hashes 0xE3E570
  // registers (tools/binder-bodies.py).
  if (!asms.length) loadAll(ASM_DIR);
  const FULL: Record<string, string> = { void: 'Void', bool: 'Boolean', char: 'Char', sbyte: 'SByte', byte: 'Byte', short: 'Int16', ushort: 'UInt16', int: 'Int32', uint: 'UInt32', long: 'Int64', ulong: 'UInt64', float: 'Single', double: 'Double', string: 'String', object: 'Object', IntPtr: 'IntPtr', UIntPtr: 'UIntPtr', typedref: 'TypedReference' };
  const split = (s: string): string[] => { const o: string[] = []; let d = 0, st = 0; for (let i = 0; i < s.length; i++) { const c = s[i]; if (c === '<') d++; else if (c === '>') d--; else if (c === ',' && !d) { o.push(s.slice(st, i)); st = i + 1; } } if (s) o.push(s.slice(st)); return o; };
  const rname = (t: string): string => {
    const m = /^([^<\[&*]+)(?:<(.*)>)?((?:\[,*\]|\*)*)(&?)$/.exec(t);
    if (!m) return t;
    const base = FULL[m[1]] ? `System.${FULL[m[1]]}` : m[1];
    const gen = m[2] ? `[${split(m[2]).map(rname).join(',')}]` : '';
    return base + gen + m[3] + m[4];
  };
  const fnv = (s: string): string => { let h = 0x811c9dc5; for (const c of s.toLowerCase()) h = (Math.imul(h, 0x1000193) ^ c.charCodeAt(0)) >>> 0; return h.toString(16); };
  const out: { hash: string; id: string; key: string; m: Method }[] = [];
  for (const m of methods) {
    if (m.flags & ABSTRACT) continue;
    const key = `${rname(m.ret)} ${m.type.full}:${m.name}(${split(m.params).map(rname).join(',')})`;
    out.push({ hash: fnv(key), id: m.id, key, m });
  }
  return out;
}

// Imported as a library (mono-host-ics.ts): no CLI.
const cmd = /managed-callgraph\.ts$/.test(process.argv[1] ?? '') ? process.argv[2] : 'lib';
if (cmd === '--self-check' || (cmd !== 'lib' && process.argv.includes('--self-check'))) selfCheck();
else if (cmd === 'build') {
  const t0 = Date.now(); const ix = build();
  mkdirSync(OUT, { recursive: true });
  writeFileSync(join(OUT, 'index.json'), JSON.stringify(ix));
  const csv = ['method,assembly,ic,calls,virt_unresolved,instance_reads,instance_writes,static_reads,static_writes'];
  for (const r of ix.methods) {
    if (r.a < 0) continue;
    let v = 0; for (let k = 1; k < r.c.length; k += 2) if (['virt', 'iface', 'cons', 'dinv'].includes(ix.kinds[r.c[k]])) v++;
    csv.push([JSON.stringify(r.k), ix.asms[r.a].name, r.ic ?? '', r.c.length / 2, v, r.fr.length, r.fw.length, r.sr.length, r.sw.length].join(','));
  }
  writeFileSync(join(OUT, 'methods.csv'), csv.join('\n') + '\n');
  console.log(JSON.stringify(ix.stats), `${((Date.now() - t0) / 1000).toFixed(1)}s`);
} else if (cmd === 'query' || cmd === 'def') {
  const ix = loadIndex();
  for (const a of positional()) {
    const roots = cmd === 'def' ? defRoots(ix, a) : lookup(ix, a);
    if (!roots.length) { console.log(`${a}: no method`); continue; }
    console.log(printClosure(ix, roots, a));
  }
} else if (cmd === 'callers') {
  const ix = loadIndex(); const ts = new Set(lookup(ix, process.argv[3]));
  ix.methods.forEach((r, i) => { for (let k = 0; k < r.c.length; k += 2) if (ts.has(r.c[k])) { console.log(`${ix.kinds[r.c[k + 1]]}\t${r.k}`); break; } });
} else if (cmd === 'overrides') {
  build();
  for (const a of positional()) for (const m of methods.filter((x) => x.id === a || x.id.startsWith(a + '(') || x.id.startsWith(a + '`'))) {
    const os = overridesOf(m);
    console.log(`${m.id} [${m.asm.name}] ${os.length} overrides`);
    for (const o of os) console.log(`  ${o.id} [${o.asm.name}]`);
  }
} else if (cmd === 'subtypes') {
  // docs/126: every class deriving from a type (itself included), one line each: full name, assembly, abstract flag.
  for (const a of positional()) {
    const ts = subtypes(a);
    if (!ts) { console.log(`${a}: no type`); continue; }
    for (const x of ts) console.log(`${x.full}\t${x.asm}\t${x.abstract ? 'abstract' : ''}`);
  }
} else if (cmd === 'dispatch') runDispatch();
else if (cmd === 'hostdata') {
  // native/monohost input (docs/119 §10): "R <kind> <ret> <ic>" per extern (kind: v void, i 32-bit int/ref, l 64-bit int,
  // f float, d double, s struct) and "C <asm> <typedef row> <type>" per type with a .cctor in the game assemblies.
  loadAll(ASM_DIR);
  const PK: Record<string, string> = { void: 'v', long: 'l', ulong: 'l', float: 'f', double: 'd' };
  const kind = (ret: string): string => {
    if (PK[ret]) return PK[ret];
    const t = typeByFull.get(ret); const b = t && baseOf(t)?.full;
    if (b === 'System.Enum') return kind(fieldSigType(t!.asm, t!.fields.find((f) => f.name === 'value__')!.sigBlob));
    return b === 'System.ValueType' ? 's' : 'i';
  };
  const out: string[] = [];
  for (const m of methods) if (m.impl & IC_FLAG || m.flags & PINVOKE) out.push(`R ${kind(m.ret)} ${m.ret} ${m.type.full}::${m.name}`);
  for (const a of asms) if (!['mscorlib', 'System', 'System.Xml'].includes(a.name))
    for (const t of a.types) if (t.methods.some((m) => m.name === '.cctor')) out.push(`C ${a.name}.dll ${t.row} ${t.full}`);
  writeFileSync(arg('--out', 'game-copy/monohost/hostdata.txt'), out.join('\n') + '\n');
  console.log(`${out.filter((l) => l[0] === 'R').length} externs, ${out.filter((l) => l[0] === 'C').length} .cctor types`);
} else if (cmd === 'bindkeys') {
  for (const k of bindKeys()) console.log(`${k.hash}\t${k.id}\t${k.key}`);
} else if (cmd !== 'lib') console.log('usage: build | query <method>... | def <type>... | callers <method> | overrides <method>... | subtypes <type>... | dispatch [--csv out] | hostdata [--out f] | bindkeys | --self-check');
