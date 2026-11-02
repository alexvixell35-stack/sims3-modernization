// docs/220 (contributor research round 2, task 2): managed facts for EA's binder native bodies, input for
// tools/binder-bodies.py. For each hash the binder registers (0xE3E570, list from binder-bodies.py --hashes): the bound method
// (managed-callgraph.ts bindKeys), static flag, parameter and return kinds, the fields its managed IL writes (index fw/sw:
// the IL-proxy of docs/212), the [AutShadow] worker reach label, and the instance field layout of every type a body can
// reach through `this` or a parameter (two field hops deep).
// Usage: node tools/binder-bodies.ts --asm game-copy/assemblies [--index game-copy/analysis/callgraph/index.json]
//          --hashes <file> [--typed <autonomy-virtual-closure.ts --edges file>] --out <facts.json>
//        node tools/binder-bodies.ts --self-check --asm game-copy/assemblies
// Field layout = Mono 1.2 mono_class_layout_fields with this build's 4-byte object header (docs/130 D18): parent fields first;
// auto-layout classes outside mscorlib place reference fields first (gc-aware layout); structs and sequential classes keep
// declaration order; explicit layout uses the FieldLayout offsets. --layout decl|gc|gcsort picks a variant for the
// docs/220 §2 check against the native stores (gcsort = gc-aware with the non-reference fields sorted by alignment).
import { readFileSync, writeFileSync } from 'node:fs';
import { bindKeys, defRoots, methodIl, resolveFieldToken, resolveMethodToken, typeModel, type Field, type Method, type TypeDef } from './managed-callgraph.ts';

const arg = (n: string, d: string) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : d; };
const ASM = arg('--asm', 'game-copy/assemblies');
const LAYOUT = arg('--layout', 'gcsort');
const tm = typeModel(ASM, []);

// ---------- kinds and sizes ----------
const SCALAR: Record<string, [number, number]> = { bool: [1, 1], byte: [1, 1], sbyte: [1, 1], char: [2, 2], short: [2, 2], ushort: [2, 2],
  int: [4, 4], uint: [4, 4], float: [4, 4], long: [8, 8], ulong: [8, 8], double: [8, 8] };
export type Kind = 'scalar' | 'native' | 'string' | 'array' | 'byref' | 'class' | 'struct' | 'object' | 'generic' | 'unknown';
const strip = (t: string) => t.replace(/<.*>$/, ''); // List`1<X> -> List`1 (generic definition)
function typeDef(t: string): TypeDef | undefined { return tm.typeByFull.get(t) ?? tm.typeByFull.get(strip(t)); }
function isA(td: TypeDef, base: string): boolean { for (let c: TypeDef | undefined = td; c; c = tm.baseOf(c)) if (c.full === base) return true; return false; }
export function kindOf(t: string): Kind {
  if (t.endsWith('&')) return 'byref';
  if (t.endsWith('*') || t === 'System.IntPtr' || t === 'System.UIntPtr' || t === 'IntPtr' || t === 'UIntPtr') return 'native';
  if (/\[,*\]$/.test(t)) return 'array';
  if (SCALAR[t]) return 'scalar';
  if (t === 'string' || t === 'System.String') return 'string';
  if (t === 'object' || t === 'System.Object') return 'object';
  if (t.startsWith('!')) return 'generic';
  const td = typeDef(t);
  if (!td) return 'unknown';
  if (isA(td, 'System.Enum')) return 'scalar';
  if (isA(td, 'System.ValueType') && td.full !== 'System.ValueType') return 'struct';
  return 'class';
}
type Lay = { kind: Kind; size: number | null; align: number; fields: { name: string; t: string; kind: Kind; off: number | null; size: number | null }[] };
const lays = new Map<string, Lay>();
function sizeOf(t: string): [number | null, number] { // [size, align] of a field or stack value of type t
  const k = kindOf(t);
  if (k === 'scalar') { const s = SCALAR[t] ?? enumSize(t); return [s[0], s[1]]; }
  if (k === 'struct') { const l = layout(t); return [l.size, l.align]; }
  if (k === 'generic' || k === 'unknown') return [null, 4];
  return [4, 4];
}
function enumSize(t: string): [number, number] {
  const td = typeDef(t)!; const v = td.fields.find((f) => f.name === 'value__');
  return (v && SCALAR[tm.fieldType(v)]) || [4, 4];
}
const hasRefs = (t: string, seen = new Set<string>()): boolean => {
  const k = kindOf(t);
  if (k === 'struct') { if (seen.has(t)) return false; seen.add(t); return layout(t).fields.some((f) => hasRefs(f.t, seen)); }
  return k === 'class' || k === 'string' || k === 'array' || k === 'object';
};
export function layout(t: string): Lay {
  const key = t;
  const got = lays.get(key); if (got) return got;
  const kind = kindOf(t);
  const lay: Lay = { kind, size: null, align: 4, fields: [] };
  lays.set(key, lay);
  const td = typeDef(t);
  if (!td || (kind !== 'class' && kind !== 'struct')) return lay;
  const struct = kind === 'struct';
  const chain: TypeDef[] = []; for (let c: TypeDef | undefined = td; c && c.full !== 'System.Object' && c.full !== 'System.ValueType'; c = tm.baseOf(c)) chain.unshift(c);
  let real = struct ? 0 : 4; let minAlign = 1;
  for (const c of struct ? [td] : chain) {
    const inst = c.fields.filter((f: Field) => !(f.flags & 0x10));
    const lk = c.flags & 0x18; // 0 auto, 8 sequential, 0x10 explicit
    const cl = c.asm.tables[0x0f]?.find((r) => r[2] === c.row);
    const pack = cl ? cl[0] : 0;
    if (lk === 0x10) {
      for (const f of inst) {
        const fl = c.asm.tables[0x10]?.find((r) => r[1] === f.row);
        const [sz, al] = sizeOf(tm.fieldType(f));
        const off = fl ? fl[0] + (struct ? 0 : 4) : null;
        lay.fields.push({ name: f.name, t: tm.fieldType(f), kind: kindOf(tm.fieldType(f)), off, size: sz });
        if (off !== null && sz !== null) real = Math.max(real, off + sz);
        minAlign = Math.max(minAlign, al);
      }
      if (cl && cl[1]) real = Math.max(real, cl[1] + (struct ? 0 : 4));
      continue;
    }
    const gc = LAYOUT !== 'decl' && lk === 0 && !struct && c.asm.name !== 'mscorlib';
    let order = inst;
    if (gc) {
      // Mono's gc-aware pass 0 takes MONO_TYPE_IS_POINTER fields: references plus IntPtr and pointers (structs go to pass 1)
      const ptr = (f: Field) => ['class', 'string', 'array', 'object', 'native'].includes(kindOf(tm.fieldType(f)));
      const refs = inst.filter(ptr), rest = inst.filter((f) => !ptr(f));
      if (LAYOUT === 'gcsort') rest.sort((x, y) => sizeOf(tm.fieldType(y))[1] - sizeOf(tm.fieldType(x))[1]);
      order = [...refs, ...rest];
    }
    for (const f of order) {
      const ft = tm.fieldType(f);
      let [sz, al] = sizeOf(ft);
      if (pack) al = Math.min(al, pack);
      if (kindOf(ft) === 'struct' && hasRefs(ft)) al = 4;
      if (sz === null || Number.isNaN(real)) { lay.fields.push({ name: f.name, t: ft, kind: kindOf(ft), off: null, size: sz }); real = NaN; continue; }
      const off = (real + al - 1) & ~(al - 1);
      lay.fields.push({ name: f.name, t: ft, kind: kindOf(ft), off, size: sz });
      real = off + sz; minAlign = Math.max(minAlign, al);
    }
  }
  lay.align = struct ? minAlign : 4;
  lay.size = Number.isNaN(real) ? null : struct ? ((real + minAlign - 1) & ~(minAlign - 1)) || 1 : real;
  return lay;
}

// ---------- self-check ----------
if (process.argv.includes('--self-check')) {
  const off = (t: string, f: string) => layout(t).fields.find((x) => x.name === f)?.off;
  const want: [string, string, number][] = [
    // offsets the native bodies store to (docs/220 §2): PriorityQueue.Add/SetCapacity 0xE0D450/0xE0D290, Curve.Fx 0xE0AD21
    ['Sims3.Collections.PriorityQueue', 'mItems', 4], ['Sims3.Collections.PriorityQueue', 'mCapacity', 8], ['Sims3.Collections.PriorityQueue', 'mCount', 12],
    ['Sims3.Gameplay.Utilities.Curve', 'm_x', 0x14],
  ];
  const bad = want.filter(([t, f, o]) => off(t, f) !== o);
  if (kindOf('Sims3.SimIFace.Vector3') !== 'struct' || kindOf('string') !== 'string' || kindOf('float[]') !== 'array' || kindOf('int&') !== 'byref') bad.push(['kinds', '', 0]);
  if (layout('Sims3.SimIFace.Vector3').size !== 12) bad.push(['Vector3 size', '', 12]);
  if (bad.length) { console.log('self-check FAILED', JSON.stringify(bad.map(([t, f, o]) => [t, f, o, off(t, f)]))); process.exit(1); }
  console.log(`self-check ok: ${want.length} native store offsets match the ${LAYOUT} layout; Vector3 12 bytes`);
}

// ---------- facts ----------
// --hashes: one registered hash per line (hex, first column); facts are keyed by the 8-digit lowercase hash. Fifth column
// (binder-bodies.py): the globals the body's init fills, `<global>:<kind>:<IL offset>` comma-separated.
const hx = (h: string) => parseInt(h, 16).toString(16).padStart(8, '0');
// init global -> the token at that IL offset of the bound method's own IL (the offset points just past the opcode).
// ok = the opcode fits the init helper's kind (method/icall: call, callvirt, newobj, ldftn; field: ld/st[s]fld[a];
// string: ldstr; class: any other token opcode).
const OPK: Record<number, string> = { 0x28: 'method', 0x6f: 'method', 0x73: 'method', 0xfe06: 'method', 0xfe07: 'method', 0x7b: 'field',
  0x7c: 'field', 0x7d: 'field', 0x7e: 'field', 0x7f: 'field', 0x80: 'field', 0x72: 'string' };
const WANT: Record<string, string> = { method: 'method', icall: 'method', field: 'field', string: 'string', class: 'type' };
function initRefs(m: Method, col: string) {
  if (!col) return [];
  const il = methodIl(m.gi);
  return col.split(',').map((e) => {
    const [g, kind, o] = e.split(':'); const off = parseInt(o, 16);
    const x = il?.ins.find((i) => i.tok && i.next - 4 === off);
    const opk = x ? OPK[x.op] ?? 'type' : 'none';
    let target = '';
    if (x && opk === 'method') { const r = resolveMethodToken(il!.asm, x.tok) as { m?: Method; stub?: string }; target = r.m ? r.m.id : r.stub ?? ''; }
    else if (x && opk === 'field') target = resolveFieldToken(il!.asm, x.tok).id;
    else if (x) target = `token 0x${x.tok.toString(16)}`;
    return { g: parseInt(g, 16), kind, off, op: x ? x.op : -1, ok: kind === 'token' ? opk !== 'none' : opk === WANT[kind], target };
  });
}
if (process.argv.includes('--self-check')) {
  // Concat(string,string)'s init caches InternalAllocateStr's icall pointer from the call at IL 0x39 (binder-bodies.py pins the global)
  const m = bindKeys().find((k) => k.m.id === 'System.String::Concat(string,string)')?.m;
  const r = m && initRefs(m, '11EF494:icall:39')[0];
  if (!r || !r.ok || r.target !== 'System.String::InternalAllocateStr(int)') { console.log('self-check FAILED: init ref', JSON.stringify(r)); process.exit(1); }
  console.log('self-check ok: Concat init ref resolves to InternalAllocateStr');
  process.exit(0);
}
const rows = readFileSync(arg('--hashes', ''), 'utf8').split(/\r?\n/).map((l) => l.trim().split(/\s+/)).filter((c) => /^(0x)?[0-9a-f]+$/i.test(c[0] ?? ''));
const hashes = new Set(rows.map((c) => hx(c[0])));
const initCol = new Map(rows.map((c) => [hx(c[0]), c[4] ?? '']));
const keys = bindKeys().filter((k) => hashes.has(hx(k.hash)));
const T = await import('./autonomy-tier2.ts');
const ix = T.ix as unknown as { methods: { k: string; c: number[]; fw?: number[]; sw?: number[] }[]; fields: string[] };
// IL allocations a body's copies can target: the managed method builds its result in a string it allocated itself
const ALLOC = /^System\.String::(InternalAllocateStr|FastAllocateString)\(/;
const byId = new Map<string, number>(); ix.methods.forEach((r, i) => { if (!byId.has(r.k)) byId.set(r.k, i); });

// worker reach labels, most direct first (as tools/mod-method-diff.ts, docs/218): v0 = docs/122 pilot union; -typed / -cha = with
// overrides up to 5 hops (docs/127 receiver-typed edges if --typed, else class hierarchy); tier2 = a docs/126 ready or new-rows
// candidate's closure; tier2-blocked = only through blocked candidates.
const RANK = ['v0', 'v0-typed', 'v0-cha', 'tier2', 'tier2-typed', 'tier2-cha', 'tier2-blocked', 'tier2-blocked-cha'];
const reach = new Map<string, string>();
const put = (k: string, l: string) => { const o = reach.get(k); if (!o || RANK.indexOf(l) < RANK.indexOf(o)) reach.set(k, l); };
const mark = (ids: number[], l: string) => { for (const i of ids) put(T.ix.methods[i].k, l); };
mark(T.pilot.ids, 'v0');
for (const t of T.inV0) mark(T.closure(defRoots(T.ix as never, t), t, 5).ids, 'v0-cha');
for (const t of T.IMPL) mark(T.closure(T.starts(`${t}::HowMuchItWantsSimToCome`), undefined, 5).ids, 'v0-cha');
for (const c of T.cands) {
  if (!c.roots.length) continue;
  const r = T.closure(c.roots, c.own), ok = /^(ready|new-rows)$/.test(T.classify(c, r).verdict);
  mark(r.ids, ok ? 'tier2' : 'tier2-blocked'); mark(T.closure(c.roots, c.own, 5).ids, ok ? 'tier2-cha' : 'tier2-blocked-cha');
}
const typed = arg('--typed', '');
if (typed) for (const m of JSON.parse(readFileSync(typed, 'utf8')).methods as { k: string; groups: string[] }[])
  put(m.k, m.groups.some((g) => g === 'v0' || g === 'v0-implementor') ? 'v0-typed' : 'tier2-typed');

const split = (s: string): string[] => { const o: string[] = []; let d = 0, st = 0; for (let i = 0; i < s.length; i++) { const c = s[i]; if (c === '<') d++; else if (c === '>') d--; else if (c === ',' && !d) { o.push(s.slice(st, i)); st = i + 1; } } if (s) o.push(s.slice(st)); return o; };
const want = new Set<string>();
const methods: Record<string, unknown> = {};
for (const k of keys) {
  const m = k.m; const r = byId.get(m.id); const rec = r === undefined ? undefined : ix.methods[r];
  const params = split(m.params).map((t) => ({ t, kind: kindOf(t) }));
  const self = m.flags & 0x10 ? null : { t: m.type.full, kind: kindOf(m.type.full) };
  for (const p of [...params, ...(self ? [self] : [])]) want.add(p.t.replace(/&$/, '').replace(/\[,*\]$/, ''));
  methods[hx(k.hash)] = { id: m.id, key: k.key, static: !self, self, params, ret: { t: m.ret, kind: kindOf(m.ret) },
    il_fw: (rec?.fw ?? []).map((f) => ix.fields[f]), il_sw: (rec?.sw ?? []).map((f) => ix.fields[f]),
    il_alloc_str: !!rec && rec.c.some((t, i) => i % 2 === 0 && ALLOC.test(ix.methods[t].k)), reach: reach.get(m.id) ?? 'no',
    init: initRefs(m, initCol.get(hx(k.hash)) ?? '') };
}
const types: Record<string, Lay> = {};
let frontier = [...want];
for (let depth = 0; depth < 3 && frontier.length; depth++) {
  const next: string[] = [];
  for (const t of frontier) {
    if (types[t]) continue;
    const l = layout(t); types[t] = l;
    for (const f of l.fields) for (const u of [f.t.replace(/&$/, '').replace(/\[,*\]$/, '')]) if (!types[u] && (kindOf(u) === 'class' || kindOf(u) === 'struct')) next.push(u);
  }
  frontier = next;
}
writeFileSync(arg('--out', 'binder-facts.json'), JSON.stringify({ layout: LAYOUT, methods, types }));
console.error(`${keys.length} of ${hashes.size} hashes bound, ${Object.keys(types).length} types, reach ${[...new Set(Object.values(methods).map((x) => (x as { reach: string }).reach))].join('/')}`);
