// Round 3 B5 (docs/207 §9): the never-yielding Sims3Gameplay* methods among the top 100 of compile-candidates.csv (mean self
// ms/s over the three worlds) that monohost cannot run without a game world, what each reads from the world, and the
// smallest monohost fixture that would run it. Static, from the managed call graph (docs/78) and docs/data/ic-safety.csv.
//   node tools/offline-accessors.ts [--index game-copy/analysis/callgraph/index.json] [--depth 3] [--top 100] [--write]
//   node tools/offline-accessors.ts --self-check
// needs = instance fields read in the closure (by declaring type), static fields read, EA internal calls reached (verdict from
// ic-safety.csv: each needs a stub with a recorded answer offline), virtual/interface edges (receiver class decides the body).
// fixture_effort: S = hand-built objects only (no EA IC, no virtual edge, <= 8 fields); M = up to 3 EA ICs with recorded
// answers or virtual edges with a known receiver; L = more ICs, an UNSAFE/MAIN IC, or a closure over 60 methods.
// --write: docs/data/offline-accessors.csv. Last line `needs review: ...`.
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { closure, icVerdicts, ownTags, type Index } from './managed-callgraph.ts';

const arg = (n: string, d: string) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : d; };
const ROOT = resolve(import.meta.dirname, '..');
const csvSplit = (l: string) => (l.match(/("([^"]|"")*"|[^,]*)(,|$)/g) ?? []).map(c => c.replace(/,$/, '').replace(/^"|"$/g, '').replace(/""/g, '"'));
const q = (s: string) => /[",]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s;

export type Cand = { frame: string; assembly: string; ms: number; cls: string; il: number };
// mean self ms/s over the worlds a frame appears in, divided by 3 (absent world = 0), top N by it
export function topCandidates(csv: string, top: number): Cand[] {
  const [h, ...ls] = csv.trim().split(/\r?\n/).map(csvSplit), ix = (k: string) => h.indexOf(k), agg = new Map<string, Cand>();
  for (const r of ls) {
    if (r[ix('kind')] !== 'self') continue;
    const f = r[ix('frame')], c = agg.get(f) ?? { frame: f, assembly: r[ix('assembly')], ms: 0, cls: r[ix('class')], il: Number(r[ix('il_bytes')]) };
    c.ms += Number(r[ix('ms_s')]) / 3; agg.set(f, c);
  }
  return [...agg.values()].sort((a, b) => b.ms - a.ms).slice(0, top);
}

export type Need = { fields: string[]; statics: string[]; eaIcs: [string, string][]; monoIcs: string[]; virt: string[]; size: number; cctors: string[] };
export function needsOf(ix: Index, roots: number[], depth: number, ics: Map<string, string>, withCctors = true): Need {
  const c = closure(ix, roots, depth, ics), fields = new Set<string>(), statics = new Set<string>(), ea = new Map<string, string>(), mono = new Set<string>(), virt = new Set<string>();
  for (const n of c.reached.keys()) {
    const r = ix.methods[n];
    r.fr.forEach(f => fields.add(ix.fields[f])); r.sr.forEach(f => statics.add(ix.fields[f]));
    for (const [tag, d] of ownTags(ix, n, ics)) if (r.ic && d === r.ic) (tag === 'MONO_IC' ? mono.add(d) : ea.set(d, tag));
    for (let k = 0; k < r.c.length; k += 2) { const kind = ix.kinds[r.c[k + 1]]; if (kind === 'virt' || kind === 'iface') virt.add(ix.methods[r.c[k]].k); }
  }
  // static constructors that run before the first access: the method's own class and each class whose statics it reads, with
  // the EA ICs their closure reaches (a fixture runs them under lenient IC stubs, or sets the statics after they ran)
  const keyIx = new Map(ix.methods.map((r, i) => [r.k, i])), cctors: string[] = [];
  for (const ty of !withCctors ? [] : new Set([...roots.map(i => ix.methods[i].k.split('::')[0]), ...[...statics].map(f => f.split('::')[0])])) {
    const ci = keyIx.get(`${ty}::.cctor()`);
    if (ci === undefined) continue;
    const cn = needsOf(ix, [ci], depth, ics, false);
    cctors.push(`${ty}${cn.eaIcs.length ? ` (${cn.eaIcs.length} EA ICs)` : ''}`);
  }
  return { fields: [...fields].sort(), statics: [...statics].sort(), eaIcs: [...ea].sort(), monoIcs: [...mono].sort(), virt: [...virt].sort(), size: c.size, cctors: cctors.sort() };
}

export function effort(n: Need): 'S' | 'M' | 'L' {
  if (n.eaIcs.length > 3 || n.eaIcs.some(([, v]) => v === 'UNSAFE' || v === 'MAIN') || n.size > 60) return 'L';
  if (n.eaIcs.length || n.virt.length || n.fields.length > 8 || n.cctors.some(c => c.includes('EA ICs'))) return 'M';
  return 'S';
}
// one-line fixture: objects with the fields set, statics initialised, IC stubs with recorded answers
export function fixture(n: Need): string {
  const types = [...new Set(n.fields.map(f => f.split('::')[0].split('.').at(-1)))];
  return [types.length ? `build ${types.join(', ')} with ${n.fields.length} field(s) set` : 'no instance state',
    n.statics.length ? `${n.statics.length} static(s) set by reflection${n.cctors.length ? ` after ${n.cctors.length} .cctor(s)` : ''}` : '', n.eaIcs.length ? `stub ${n.eaIcs.map(([i]) => i.split('::').at(-1)).join(', ')} with recorded answers` : '',
    n.virt.length ? `pick the receiver class for ${n.virt.length} virtual call(s)` : ''].filter(Boolean).join('; ');
}

const short = (s: string) => s.replace(/^Sims3\.(Gameplay|SimIFace|UI)\./, '').replace(/\([^)]*\)/g, '');
function rowsFor(ix: Index, cands: Cand[], depth: number, ics: Map<string, string>) {
  const byName = new Map<string, number[]>(), byShort = new Map<string, Set<string>>();
  ix.methods.forEach((r, i) => {
    const k = r.k.replace(/\(.*$/, '').replace(/`\d+$/, '').replace('::', '.'); byName.set(k, [...(byName.get(k) ?? []), i]);
    const sk = k.replace(/^.*[.+]([^.+]+\.[^.]+)$/, '$1'); byShort.set(sk, (byShort.get(sk) ?? new Set()).add(k));
  });
  // the sampler drops the namespace and outer types of nested classes ("Members.GetActorsOfType" = Household+Members): unique short name only
  const full = (f: string) => byName.has(f) ? f : [...(byShort.get(f) ?? [])].length === 1 ? [...byShort.get(f)!][0] : f;
  return cands.map(c => {
    const roots = byName.get(full(c.frame)) ?? byName.get(full(c.frame.replace(/`\d+/g, ''))) ?? [];
    return { c, roots, n: roots.length ? needsOf(ix, roots, depth, ics) : null };
  });
}

function selfCheck() {
  let k = 0;
  const t = (c: unknown, m: string) => { console.assert(c, m); if (!c) process.exitCode = 1; k++; };
  const cands = topCandidates(['world,kind,frame,class,samples,share_of_mono,ms_s,assembly,overloads,il_bytes,il_features,yield_path',
    'bp,self,A.B.Get,N,1,0,3.0,Sims3GameplaySystems,1,20,"call1, x",', 'ip,self,A.B.Get,N,1,0,1.5,Sims3GameplaySystems,1,20,call1,',
    'bp,self,C.D.Big,Y,1,0,6.0,mscorlib,1,99,call9,', 'bp,root,A.B.Get,N,1,0,9.0,Sims3GameplaySystems,1,20,call1,'].join('\n'), 5);
  t(cands.length === 2 && cands[0].frame === 'C.D.Big' && cands[1].ms === 1.5 && cands[1].cls === 'N', 'mean over 3 worlds, self rows only');
  // index: 0 = A.B::Get -> 1 (call) + 2 (virt); 1 = EA IC PROXY; 2 = virtual; 3 = mono icall
  const ix: Index = { version: 1, asms: [{ name: 'Sims3GameplaySystems', mvid: '' }, { name: 'mscorlib', mvid: '' }], kinds: ['call', 'cv', 'virt'], fields: ['A.B::mX', 'A.B::sY', 'E.F::mZ'], stats: {},
    methods: [{ k: 'A.B::Get()', a: 0, f: 0, c: [1, 0, 2, 2, 3, 0], fr: [0], fw: [], sr: [1], sw: [] }, { k: 'S.W::Q_Impl()', a: 0, f: 0, ic: 'S.W::Q_Impl', c: [], fr: [], fw: [], sr: [], sw: [] },
      { k: 'E.F::V()', a: 0, f: 0, c: [], fr: [2], fw: [], sr: [], sw: [] }, { k: 'System.Type::internal_from_handle()', a: 1, f: 0, ic: 'System.Type::internal_from_handle', c: [], fr: [], fw: [], sr: [], sw: [] },
      { k: 'A.B::.cctor()', a: 0, f: 0, c: [1, 0], fr: [], fw: [], sr: [], sw: [1] }] } as any;
  const n = needsOf(ix, [0], 3, new Map([['S.W::Q_Impl', 'PROXY']]));
  t(n.fields.join() === 'A.B::mX,E.F::mZ' && n.statics.join() === 'A.B::sY' && n.eaIcs[0]?.join() === 'S.W::Q_Impl,PROXY' && n.monoIcs.join() === 'System.Type::internal_from_handle' && n.virt.join() === 'E.F::V()' && n.cctors.join() === 'A.B (1 EA ICs)', 'needs: fields, statics, EA vs mono ICs, virtual, .cctor');
  t(effort(n) === 'M' && effort({ ...n, eaIcs: [], virt: [], cctors: [] }) === 'S' && effort({ ...n, eaIcs: [], virt: [] }) === 'M' && effort({ ...n, eaIcs: [['x', 'UNSAFE']] }) === 'L', 'effort classes');
  t(fixture(n) === 'build B, F with 2 field(s) set; 1 static(s) set by reflection after 1 .cctor(s); stub Q_Impl with recorded answers; pick the receiver class for 1 virtual call(s)', 'fixture text');
  ix.methods.push({ k: 'N.Outer+In::Go()', a: 0, f: 0, c: [], fr: [], fw: [], sr: [], sw: [] } as any);
  const r = rowsFor(ix, [{ frame: 'A.B.Get', assembly: 'x', ms: 1, cls: 'N', il: 1 }, { frame: 'In.Go', assembly: 'x', ms: 1, cls: 'N', il: 1 }], 3, new Map());
  t(r[0].roots.join() === '0' && r[1].roots.join() === '5', 'frame name to method id, nested class by short name');
  console.log(`offline-accessors self-check: ${k} checks${process.exitCode ? ', FAILED' : ', ok'}`);
}

if (resolve(process.argv[1] ?? '') === import.meta.filename) {
  if (process.argv.includes('--self-check')) selfCheck();
  else {
    const idxFile = arg('--index', 'game-copy/analysis/callgraph/index.json'), depth = Number(arg('--depth', '3')), top = Number(arg('--top', '100'));
    if (!existsSync(idxFile)) { console.error(`no ${idxFile}: node tools/managed-callgraph.ts build first (needs game-copy/assemblies)`); process.exit(2); }
    const ix: Index = JSON.parse(readFileSync(idxFile, 'utf8')), ics = icVerdicts();
    const cands = topCandidates(readFileSync(join(ROOT, 'docs', 'data', 'compile-candidates.csv'), 'utf8'), top)
      .filter(c => c.assembly.startsWith('Sims3Gameplay') && c.cls !== 'Y');
    const rows = rowsFor(ix, cands, depth, ics), miss = rows.filter(r => !r.n).map(r => r.c.frame);
    const out = ['method,assembly,ms_s,class,il_bytes,closure_methods,ea_ics,mono_ics,instance_fields,static_fields,virtual_calls,cctors,needs,fixture_effort,fixture'];
    for (const { c, n } of rows) {
      if (!n) continue;
      const needs = [n.fields.length && `${n.fields.length} fields`, n.statics.length && `${n.statics.length} statics`, n.eaIcs.length && `${n.eaIcs.length} EA ICs`, n.virt.length && `${n.virt.length} virtual`].filter(Boolean).join(', ') || 'nothing from the world';
      out.push([c.frame, c.assembly, c.ms.toFixed(2), c.cls, c.il, n.size, n.eaIcs.map(([i, v]) => `${short(i)}:${v}`).join('; '), n.monoIcs.map(short).join('; '),
        n.fields.map(short).join('; '), n.statics.map(short).join('; '), n.virt.map(short).join('; '), n.cctors.map(short).join('; '), needs, effort(n), fixture(n)].map(x => q(String(x))).join(','));
    }
    if (process.argv.includes('--write')) writeFileSync(join(ROOT, 'docs', 'data', 'offline-accessors.csv'), out.join('\n') + '\n');
    for (const l of out.slice(1, 16)) { const x = csvSplit(l); console.log(`${x[2].padStart(5)} ${x[13]} ${short(x[0])}: ${x[12]}${x[6] ? ` (${x[6]})` : ''}`); }
    const by = (e: string) => out.slice(1).filter(l => csvSplit(l)[13] === e).length;
    console.log(`${out.length - 1} methods: S ${by('S')}, M ${by('M')}, L ${by('L')}${process.argv.includes('--write') ? '; docs/data/offline-accessors.csv written' : ''}`);
    console.log(`needs review: ${miss.length ? `not in the index: ${miss.join(', ')}` : 'none'}`);
  }
}
