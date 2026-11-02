// docs/214: first-time Mono runtime work reachable from autonomy scoring after a world has loaded and played a while
// (transform, class init, vtable, generic instance, delegate, thunk, image load: what the [AutShadow] worker must not hit,
// docs/206 §8 X11). Two inputs:
//   static  closure of the docs/75 entry list (docs/data/autonomy-entry-list.csv rows with a resolvable definition, family
//           member, base path or scoring function; the unresolved Definition.Test buckets are left to the live runs) over
//           the docs/78 index, the same edges as managed-callgraph.ts closure() (delegate creation not followed), plus per
//           reached method its class-init triggers (static field access, newobj, static call into a type with a .cctor),
//           delegate creations (ldftn) and generic members;
//   live    tools/frida/cold-paths.js files: every cold-path entry with the interpreter frames at its first hit, and the
//           end snapshot (classes with vtable / init done, transformed methods).
// Usage: node tools/autonomy-cold-paths.ts [--live a-coldpaths.json,b-coldpaths.json] [--warm-min 5] [--depth 12] [--row-depth 3]
//        [--index game-copy/analysis/callgraph/index.json] [--asm game-copy/assemblies] > docs/data/autonomy-cold-paths.csv
//        (summary to stderr)
//        node tools/autonomy-cold-paths.ts --self-check
import { readFileSync } from 'node:fs';
import assert from 'node:assert/strict';

type Rec = { k: string; a: number; f: number; c: number[]; sr: number[]; sw: number[] };
type Ix = { kinds: string[]; methods: Rec[]; fields: string[] };
type Ev = { kind: string; name: string; interp: boolean; firstMs: number; count: number; stack: string[] };
type Live = { file: string; minutes: number; events: Ev[]; snapshot: { classes: [string, number, number][]; transformed: string[] } | null };
export type Row = { method_or_type: string; kind: string; reached_from: string; rare_branch_condition: string; confidence: string };

const arg = (n: string, d: string) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : d; };
const STATIC = 0x10;
const bare = (k: string) => k.replace(/\(.*$/s, '').replace(/`\d+$/, ''); // index key -> "Ns.Type::Name" (live key form)
const typeOf = (k: string) => k.slice(0, k.indexOf('::'));

// Entry-list rows -> root method indices, with the label each root reports as reached_from.
export function entryRoots(ix: Ix, rows: Record<string, string>[], defRoots: (t: string) => number[]): { roots: Map<number, string>; skipped: string[] } {
  const roots = new Map<number, string>(), skipped: string[] = [];
  const add = (i: number, label: string) => { if (!roots.has(i)) roots.set(i, label); };
  for (const r of rows) {
    const d = r.definition_type;
    let m: RegExpMatchArray | null;
    if ((m = d.match(/^(?:base path|scoring function):? (\w+)\.(\w+)$/))) {
      const re = new RegExp(`[.+]${m[1]}::${m[2]}[(\`]`);
      const hit = ix.methods.flatMap((x, i) => (re.test(x.k) ? [i] : []));
      if (hit.length) hit.forEach((i) => add(i, `${r.kind} ${m![1]}.${m![2]}`)); else skipped.push(d);
      continue;
    }
    const names = [...d.split(' | '), ...(r.concrete_types || '').split(' | ')].map((s) => s.replace(/ \(target .*$/, '').trim())
      .filter((s) => /^Sims3\.[\w.+`]+$/.test(s));
    let any = false;
    for (const t of new Set(names)) { try { const rs = defRoots(t); rs.forEach((i) => add(i, t)); any ||= rs.length > 0; } catch { /* not a type here */ } }
    if (!any) skipped.push(d);
  }
  return { roots, skipped };
}

// Multi-source BFS: depth and first root per reached method (closure() edges: every kind except delegate creation).
export function reach(ix: Ix, roots: Map<number, string>, depth: number): Map<number, { d: number; root: string }> {
  const out = new Map<number, { d: number; root: string }>();
  let q = [...roots.keys()];
  q.forEach((r) => out.set(r, { d: 0, root: roots.get(r)! }));
  for (let d = 0; d < depth && q.length; d++) {
    const next: number[] = [];
    for (const n of q) {
      const c = ix.methods[n].c;
      for (let k = 0; k < c.length; k += 2) {
        if (ix.kinds[c[k + 1]] === 'dlg' || out.has(c[k])) continue;
        out.set(c[k], { d: d + 1, root: out.get(n)!.root });
        next.push(c[k]);
      }
    }
    q = next;
  }
  return out;
}

// Static candidates: types whose .cctor a reached method can trigger, delegate targets created, generic members reached.
export function staticCandidates(ix: Ix, reached: Map<number, { d: number; root: string }>) {
  const cctor = new Set(ix.methods.filter((m) => m.k.endsWith('::.cctor()')).map((m) => typeOf(m.k)));
  const types = new Map<string, { d: number; root: string; via: string }>(), dlg = new Map<string, { d: number; root: string; via: string }>();
  const keep = (map: typeof types, key: string, d: number, root: string, via: string) => { const o = map.get(key); if (!o || d < o.d) map.set(key, { d, root, via }); };
  for (const [n, { d, root }] of reached) {
    const r = ix.methods[n], via = bare(r.k);
    for (const fi of [...r.sr, ...r.sw]) { const t = typeOf(ix.fields[fi]); if (cctor.has(t)) keep(types, t, d, root, via); }
    for (let k = 0; k < r.c.length; k += 2) {
      const t = ix.methods[r.c[k]], kind = ix.kinds[r.c[k + 1]];
      if (kind === 'dlg') keep(dlg, bare(t.k), d, root, via);
      else if ((kind === 'newobj' || t.f & STATIC) && cctor.has(typeOf(t.k))) keep(types, typeOf(t.k), d, root, via);
    }
  }
  const generic = [...reached].filter(([n]) => ix.methods[n].k.includes('`')).length;
  return { types, dlg, generic, cctorTypes: cctor.size };
}

const SCORING = [/^Sims3\.Gameplay\.Autonomy\.Autonomy::/, /Definition[^:]*::(Test|CalculateScore)$/, /::\w*ScoringFunction$/, /::CalculateScore\w*$/];
export const scoringFrame = (stack: string[], rootKeys: Set<string>) => stack.findIndex((f) => rootKeys.has(f) || SCORING.some((re) => re.test(f)));
const liveKind = (e: Ev) => (e.kind === 'transform' && /\binvoke_|::Invoke$|::BeginInvoke$/.test(e.name) ? 'delegate'
  : (e.kind === 'transform' || e.kind === 'vtable') && e.name.includes('`') ? 'generic' : e.kind);

export function rows(live: Live[], warmMin: number, rootKeys: Set<string>, st: ReturnType<typeof staticCandidates> | null,
  reached: Map<number, { d: number; root: string }> | null, ix: Ix | null, rowDepth: number): Row[] {
  const out: Row[] = [], seen = new Set<string>();
  // live: first hits after the warm-up minute with a scoring frame on the same thread's interpreter stack
  for (const L of live) for (const e of L.events) {
    if (!e.interp || e.firstMs < warmMin * 60000) continue;
    const at = scoringFrame(e.stack, rootKeys);
    if (at < 0) continue;
    const kind = liveKind(e), key = `${kind}|${e.name}`;
    if (seen.has(key)) continue;
    seen.add(key);
    const updateOnly = /^Sims3\.Gameplay\.Autonomy\.Autonomy::Update(Orig)?$/.test(e.stack[at]); // ts3fixes renames the original Update
    out.push({ method_or_type: e.name, kind, reached_from: e.stack[at],
      rare_branch_condition: `first hit at min ${(e.firstMs / 60000).toFixed(1)} of ${L.minutes} (${e.count}x); innermost frame ${e.stack[0] ?? '?'}`,
      confidence: updateOnly ? 'verified (live, Autonomy.Update frame only: may be outside scoring)' : 'verified (live, scoring frame on the stack)' });
  }
  if (!st || !reached || !ix) return out;
  const snaps = live.map((L) => L.snapshot).filter((s): s is NonNullable<Live['snapshot']> => !!s);
  const inited = snaps.map((s) => new Set(s.classes.filter((c) => c[2]).map((c) => c[0])));
  const transformed = snaps.map((s) => new Set(s.transformed));
  const minutes = live.map((L) => L.minutes).join('/');
  const coldIn = (sets: Set<string>[], k: string) => sets.filter((s) => !s.has(k)).length;
  for (const [t, v] of st.types) {
    if (seen.has(`cctor|${t}`)) continue;
    const cold = coldIn(inited, t);
    if (snaps.length && !cold) continue; // initialised by the end of every live run
    out.push({ method_or_type: t, kind: 'cctor', reached_from: v.root,
      rare_branch_condition: `${snaps.length ? `class not initialised at the end of ${cold} of ${snaps.length} runs (${minutes} min)` : 'no live run'}; trigger in ${v.via} at depth ${v.d}`,
      confidence: snaps.length ? 'verified (static reach) + live (not run in play)' : 'static (reach only)' });
  }
  for (const [n, { d, root }] of reached) {
    if (d > rowDepth) continue;
    const k = bare(ix.methods[n].k);
    if (seen.has(`transform|${k}`) || !snaps.length || coldIn(transformed, k) < snaps.length) continue; // ran in some run
    seen.add(`transform|${k}`);
    out.push({ method_or_type: k, kind: k.includes('`') ? 'generic' : 'transform', reached_from: root,
      rare_branch_condition: `never transformed in ${snaps.length} runs (${minutes} min); depth ${d} from the root`, confidence: 'verified (static reach) + live (not run in play)' });
  }
  for (const [m, v] of st.dlg) {
    if (!snaps.length || coldIn(transformed, m) < snaps.length) continue;
    out.push({ method_or_type: m, kind: 'delegate', reached_from: v.root,
      rare_branch_condition: `delegate created in ${v.via} (depth ${v.d}); target never transformed in ${snaps.length} runs`, confidence: 'verified (static reach) + live (not run in play)' });
  }
  return out;
}

const csvCell = (s: string) => (/[",\n]/.test(s) ? `"${s.replace(/"/g, '""')}"` : s);
export const toCsv = (rs: Row[]) => ['method_or_type,kind,reached_from,rare_branch_condition,confidence',
  ...rs.map((r) => [r.method_or_type, r.kind, r.reached_from, r.rare_branch_condition, r.confidence].map(csvCell).join(','))].join('\n') + '\n';

function selfCheck() {
  const ix: Ix = { kinds: ['call', 'newobj', 'dlg'], fields: ['A.S::x'], methods: [
    { k: 'A.Def::Test(int)', a: 0, f: 0, c: [1, 0, 2, 1, 4, 2], sr: [], sw: [] },
    { k: 'A.Help::Rare()', a: 0, f: STATIC, c: [], sr: [0], sw: [] },
    { k: 'A.Obj::.ctor()', a: 0, f: 0, c: [], sr: [], sw: [] },
    { k: 'A.S::.cctor()', a: 0, f: STATIC, c: [], sr: [], sw: [] },
    { k: 'A.Cb::Fire()', a: 0, f: 0, c: [], sr: [], sw: [] },
  ] };
  const roots = new Map([[0, 'A.Def']]);
  const r = reach(ix, roots, 12);
  assert.deepEqual([...r.keys()].sort(), [0, 1, 2]); // the delegate target is not followed
  const st = staticCandidates(ix, r);
  assert.deepEqual([...st.types.keys()], ['A.S']); assert.deepEqual([...st.dlg.keys()], ['A.Cb::Fire']);
  const live: Live[] = [{ file: 'x', minutes: 30, snapshot: { classes: [['A.S', 1, 0]], transformed: ['A.Def::Test', 'A.Obj::.ctor'] }, events: [
    { kind: 'cctor', name: 'A.Late', interp: true, firstMs: 9 * 60000, count: 1, stack: ['A.Help::Rare', 'A.Def::Test'] },
    { kind: 'transform', name: 'A.Early', interp: true, firstMs: 60000, count: 1, stack: ['A.Def::Test'] },
    { kind: 'vtable', name: 'A.NoScore', interp: true, firstMs: 9 * 60000, count: 1, stack: ['A.Ui::Click'] }] }];
  const out = rows(live, 5, new Set(['A.Def::Test']), st, r, ix, 3);
  assert.deepEqual(out.map((x) => `${x.kind} ${x.method_or_type}`), ['cctor A.Late', 'cctor A.S', 'transform A.Help::Rare', 'delegate A.Cb::Fire']);
  assert.equal(out[0].reached_from, 'A.Def::Test');
  assert.ok(toCsv(out).startsWith('method_or_type,kind,reached_from,rare_branch_condition,confidence\n'));
  console.log('autonomy-cold-paths self-check ok');
}

if (/autonomy-cold-paths\.ts$/.test(process.argv[1] ?? '')) {
  if (process.argv.includes('--self-check')) selfCheck();
  else {
    const { defRoots } = await import('./managed-callgraph.ts');
    const ix: Ix = JSON.parse(readFileSync(arg('--index', 'game-copy/analysis/callgraph/index.json'), 'utf8'));
    const lines = readFileSync('docs/data/autonomy-entry-list.csv', 'utf8').trim().split('\n');
    const head = lines[0].split(','), parse = (l: string) => { const o: string[] = []; let cur = '', q = false;
      for (const ch of l) { if (ch === '"') q = !q; else if (ch === ',' && !q) { o.push(cur); cur = ''; } else cur += ch; } o.push(cur); return o; };
    const entry = lines.slice(1).map((l) => Object.fromEntries(parse(l).map((v, i) => [head[i], v])));
    const { roots, skipped } = entryRoots(ix, entry, (t) => defRoots(ix as any, t));
    const reached = reach(ix, roots, Number(arg('--depth', '12')));
    const st = staticCandidates(ix, reached);
    const live: Live[] = arg('--live', '').split(',').filter(Boolean).map((f) => ({ file: f, ...JSON.parse(readFileSync(f, 'utf8')) }));
    const rootKeys = new Set([...roots.keys()].map((i) => bare(ix.methods[i].k)));
    const out = rows(live, Number(arg('--warm-min', '5')), rootKeys, st, reached, ix, Number(arg('--row-depth', '3')));
    process.stdout.write(toCsv(out));
    const by: Record<string, number> = {}; out.forEach((r) => (by[r.kind] = (by[r.kind] ?? 0) + 1));
    console.error(JSON.stringify({ roots: roots.size, skippedRows: skipped.length, reached: reached.size, cctorTriggers: st.types.size,
      delegates: st.dlg.size, genericReached: st.generic, liveRuns: live.length, rows: out.length, byKind: by }));
  }
}
