// Round 10 C3 (docs/207 §16): hot-method selection for the method compiler. Joins three things by "Class.Method":
//   1. the sampler ranking, docs/data/compile-candidates.csv (tools/compile-candidates.ts: self ms/s per world, yield class;
//      a new sampler run, ours or a contributor's, goes through that tool first and lands here by the same file),
//   2. the compile list the host runs today (kJitList in native/monohost/monohost-jit.cpp, plus the B6 accessor fixtures of
//      tools/jit-corpus.ts),
//   3. a count-mode run of the host (MONOHOST_JITCOUNT=1: `jitcalls` and `jitexit` rows), which says how often a compiled
//      body leaves through an exit; a body whose hot path exits costs more than EA's call (§15.3), so it is not a candidate
//      until the exit's template exists.
// Output: docs/data/jit-hot.csv (one row per sampled method) and a summary with the coverage at each threshold.
//   node tools/jit-hot.ts [--count <monohost.exe.log>] [--threshold 1.0] [--csv docs/data/jit-hot.csv] [--write]
//   node tools/jit-hot.ts --emit-native <dir with the EA dlls> [--csv docs/data/jit-hot.csv] [--out native/ts3native/monojit-list.h]: the
//      [MonoJit] built-in list, the compile rows of the csv with size and SHA-256 of every overload's IL body (docs/527 §4.2); overloads
//      whose signature is in docs/data/binder-bodies.csv (EA native bodies, never run as mint in game) are left out (docs/537 §20.2 C2c);
//      --alt <dll>: a core mod's copy of an assembly (simler90 UPDATE242): its Y-row bodies whose token-resolved IL equals EA's join kJitYRows (docs/537 §31.1)
//   node tools/jit-hot.ts --coverage <monohost.exe.log> [--list native/ts3native/monojit-list.h]: docs/data/jit-call-edges.csv, the edges a [MonoJit] Direct build
//      takes lazily with that list (callee's lazy body from the log's jitlazy rows, a count run with MONOHOST_JITLAZY=1)
//   node tools/jit-hot.ts --game-count <ts3native.log> [--out <host-style.log>] [--method Class.Method]: the last complete `jitcount: begin` .. `end` block of a
//      game count run ([MonoJit] Count=1; cumulative counters, so only the last block counts), as monohost-style lines that --count
//      and tools/jit-exit-census.ts --census read unchanged; prints the bails by reason and the top exit sites, and the bail census
//      (docs/537 §23): the block's `jitcallee` rows ranked by callee (bails per run, share, EA native body or managed, sites) [--top 30]
//   node tools/jit-hot.ts --self-check
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { ACCESSORS } from './jit-corpus.ts';
import { ilShapeBody, loadStandalone, rva, type Asm } from './managed-callgraph.ts';

const ROOT = resolve(import.meta.dirname, '..');
type Row = { frame: string; key: string; cls: string; worlds: number; samples: number; ms: number; il: number; asm: string };
// calls = entries from the interpreter (a lazy call into the body is not one); rets = `ret*` exits, the normal end of a non-lazy
// body, one per execution; exits = the others, the ones that cost. Executions = max(calls, rets).
export type Count = { calls: number; rets: number; exits: number; topOp: string; topN: number };

export function keyOf(frame: string): string {  // "ns.Class.Method" -> "Class.Method"; "Enumerator..ctor" keeps its ".ctor"
  const p = frame.split('.');
  let m = p.pop()!;
  if ((m === 'ctor' || m === 'cctor') && p[p.length - 1] === '') { p.pop(); m = '.' + m; }
  return `${p.pop() ?? ''}.${m}`;
}

export function csvFields(line: string): string[] {  // the quoted CSV compile-candidates.ts writes; no embedded quotes
  const out: string[] = [];
  const re = /"([^"]*)"|([^,]+)/g;
  for (let m; (m = re.exec(line)); ) out.push(m[1] ?? m[2]);
  return out;
}

export function ranking(csv: string): Row[] {  // self rows, mean ms/s over the worlds present (a world without the row counts 0)
  const lines = csv.split(/\r?\n/).filter(l => l.trim());
  const worlds = new Set<string>();
  const by = new Map<string, Row>();
  for (const l of lines.slice(1)) {
    const f = csvFields(l);
    if (f[1] !== 'self') continue;
    worlds.add(f[0]);
    const r = by.get(f[2]) ?? { frame: f[2], key: keyOf(f[2]), cls: f[3], worlds: 0, samples: 0, ms: 0, il: +f[9] || 0, asm: f[7] };
    r.worlds++; r.samples += +f[4]; r.ms += +f[6];
    by.set(f[2], r);
  }
  const n = Math.max(1, worlds.size);
  return [...by.values()].map(r => ({ ...r, ms: r.ms / n })).sort((a, b) => b.ms - a.ms);
}

export function compileList(cpp: string): Set<string> {  // kJitList's names, the host's own test methods left out
  const m = /kJitList\[\] = \{([\s\S]*?)\};/.exec(cpp);
  const out = new Set<string>();
  if (m) for (const s of m[1].matchAll(/"([^"]+)"/g)) if (!/^(HostTest|Lz\w*)\./.test(s[1])) out.add(s[1]);
  return out;
}

export function counts(log: string): Map<string, Count> {  // by "Class.Method"; several runtime methods of one name add up
  const name = new Map<number, string>(), out = new Map<string, Count>();
  for (const l of log.split(/\r?\n/)) {
    const c = /^jitcalls (\d+) (\d+) \d+ \d+ (.+)$/.exec(l);
    if (c) {
      const k = keyOf(c[3]); name.set(+c[1], k);
      const r = out.get(k) ?? { calls: 0, rets: 0, exits: 0, topOp: '', topN: 0 };
      r.calls += +c[2]; out.set(k, r);
    }
  }
  for (const l of log.split(/\r?\n/)) {
    const e = /^jitexit (\d+) [0-9A-F]+ (\S+) (?:op|slow) (\d+)$/.exec(l);
    if (!e) continue;
    const k = name.get(+e[1]);
    if (!k) continue;
    const r = out.get(k)!;
    if (/^ret/.test(e[2])) { r.rets += +e[3]; continue; }
    r.exits += +e[3];
    if (+e[3] > r.topN) r.topN = +e[3], r.topOp = e[2];
  }
  return out;
}

// Lazy callees (docs/537 §8): below the threshold or not sampled, listed so a hot row's call site has a lazy body to enter. Same exit rule as any row; only the threshold is waived.
export const LAZY_ROWS: [frame: string, asm: string][] = [
  ['System.Math.Min', 'mscorlib'], ['System.Single.IsNaN', 'mscorlib'],
  ['System.Collections.Generic.IEquatableOfTEqualityComparer`1.Equals', 'mscorlib'], ['System.UInt64.Equals', 'mscorlib'], ['System.Int32.Equals', 'mscorlib'], ['System.Int32.GetHashCode', 'mscorlib'],
  ['Enumerator.VerifyState', 'mscorlib'], ['Enumerator.CurrentSlot', 'mscorlib'],
  ['System.Collections.Generic.Dictionary`2.ContainsKey', 'mscorlib'], ['System.Collections.Generic.List`1.get_Count', 'mscorlib'], ['System.Collections.Generic.List`1.get_Capacity', 'mscorlib'],
  // handoff C2 (docs/data/monojit-exits-handoff.md): callees of the call exits and the newobj.vt struct ctors; ObjectComponent.get_BaseType overrides, one row per class
  ['ScriptCore.ScriptProxy.get_ObjectId', 'ScriptCore'], ['Sims3.SimIFace.ObjectGuid..ctor', 'SimIFace'],
  ['Sims3.Gameplay.Abstracts.ScriptObject.get_Proxy', 'Sims3GameplaySystems'], ['Sims3.Gameplay.ObjectComponents.ItemComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.PortalComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.DonatableComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.ObjectOwnerComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.RoutingComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.MaintenanceComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.InteractionsDisabledComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.BoobyTrapComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.CleanableComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.PartComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.PetSurfaceComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.LootComponent.get_BaseType', 'Sims3GameplaySystems'], ['DoorRouteThroughComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.RepairableComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.CatHuntingComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.TombObjectStaticComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.TombObjectDynamicComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.SandsOfUnderstandingComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.ResortStaffedObjectComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.SittableComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.HarvestableComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.PlantableComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.UpgradableComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.AdventureComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.NameComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.AdventureFXComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.ScienceComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.ComeAndSeeComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.Utilities.SimClock.ElapsedTime', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.Utilities.SimClock.get_CurrentTicks', 'Sims3GameplaySystems'], ['Sims3.Gameplay.Utilities.SimClock.ConvertFromTicks', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.SyphonableComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.InventoryComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.FertilizerComponent.get_BaseType', 'Sims3GameplaySystems'], ['StairsRouteThroughComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.Opportunities.OpportunitySetupObjectComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.SculptureComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.Autonomy.OverlayComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.ObjectComponents.TreasureComponent.get_BaseType', 'Sims3GameplaySystems'],
  ['Sims3.Gameplay.Actors.WorldBuilderDeathComponent.get_BaseType', 'Sims3GameplaySystems'], ['System.Collections.Generic.KeyValuePair`2..ctor', 'mscorlib'],
  ['Enumerator..ctor', 'mscorlib'],
];
// docs/537 §31 (M2): class-Y rows admitted by hand. The yield is reached only through virtual targets the compiled body never
// enters (unlisted overrides: the call site exits to EA's callvirt, the method keeps EA's frame), checked per row in the doc and by
// the host corpus (Acc row + the yield task). Their verdict starts with "opt-in Y", not "compile": --emit-native writes them to
// kJitYRows, which the game compiles only with [MonoJit] YRows=1 (default 0).
export const Y_ADMIT = new Map<string, string>([
  ['Motive.UpdateMotiveWithDecay', 'yields only via BabySwing.InSwingPosture / WalkerClassic.InWalkerPosture GetPostureMotiveDecayUpdate (callvirt exit)'],
]);
export const Y_VERDICT = 'opt-in Y ([MonoJit] YRows=1): hand-checked, docs/537 §31';
export const execs = (c: Count): number => Math.max(c.calls, c.rets);
export type Verdict = { row: Row; listed: string; count?: Count; verdict: string };
export function judge(rows: Row[], listed: Set<string>, acc: Set<string>, cnt: Map<string, Count>, threshold: number): Verdict[] {
  return rows.map(row => {
    const where = listed.has(row.key) ? 'kJitList' : acc.has(row.key) ? 'accessor' : '';
    const count = cnt.get(row.key);
    let verdict: string;
    if (row.cls === 'Y' && Y_ADMIT.has(row.key)) verdict = Y_VERDICT;
    else if (row.cls === 'Y') verdict = 'yields: stays interpreted';
    else if (row.cls === '?') verdict = 'not in the index (mod or unmatched frame)';
    else if (row.ms < threshold) verdict = 'below threshold';
    else if (count && execs(count) && count.exits / execs(count) > 0.1) verdict = `template first: ${count.topOp}, ${(count.exits / execs(count)).toFixed(1)} exits per execution`;
    else if (row.cls === 'D') verdict = 'compile (delegate path: the Invoke target decides)';
    else verdict = where ? 'compile' : 'compile (new: not in the host list yet)';
    return { row, listed: where, count, verdict };
  });
}

export function summary(v: Verdict[], thresholds: number[]): string[] {
  const total = v.reduce((s, x) => s + x.row.ms, 0);
  const n = v.filter(x => x.row.cls === 'N' || x.row.cls === 'D');
  const nMs = n.reduce((s, x) => s + x.row.ms, 0);
  const out = [`sampled self time ${total.toFixed(1)} ms/s over ${v.length} methods; never-yielding (N + D) ${nMs.toFixed(1)} ms/s over ${n.length}`];
  for (const t of thresholds) {
    const pick = n.filter(x => x.row.ms >= t);
    const ms = pick.reduce((s, x) => s + x.row.ms, 0);
    const vetoed = pick.filter(x => x.verdict.startsWith('template first'));
    const vMs = vetoed.reduce((s, x) => s + x.row.ms, 0);
    out.push(`threshold ${t.toFixed(1)} ms/s: ${pick.length} methods, ${ms.toFixed(1)} ms/s (${(100 * ms / nMs).toFixed(0)}% of N); ${vetoed.length} of them (${vMs.toFixed(1)} ms/s) held back by an exit`);
  }
  const byOp = new Map<string, { ms: number; n: number }>();
  for (const x of n) if (x.verdict.startsWith('template first')) {
    const op = x.count!.topOp, r = byOp.get(op) ?? { ms: 0, n: 0 };
    r.ms += x.row.ms; r.n++; byOp.set(op, r);
  }
  for (const [op, r] of [...byOp].sort((a, b) => b[1].ms - a[1].ms)) out.push(`  held back by ${op.padEnd(12)} ${r.ms.toFixed(1).padStart(5)} ms/s in ${r.n} methods`);
  return out;
}

// The IL of a method body at p (II.25.4.2): the bytes the game hashes through mono_method_get_header's code and code size
export function ilCode(b: Buffer, p: number): Buffer {
  if ((b[p] & 3) === 2) return b.subarray(p + 1, p + 1 + (b[p] >> 2));
  return b.subarray(p + (b.readUInt16LE(p) >> 12) * 4, p + (b.readUInt16LE(p) >> 12) * 4 + b.readUInt32LE(p + 4));
}
export type NativeRow = { key: string; size: number; sha: string };
const ilRow = (key: string, il: Buffer): NativeRow => ({ key, size: il.length, sha: createHash('sha256').update(il).digest('hex') });
// docs/537 §20.2 C2c: managed signatures EA binds to a native body (docs/data/binder-bodies.csv): the interpreter calls that body
// at RuntimeMethod+0x24, so a compiled mint of these overloads never runs in game (I-475-4)
// docs/537 §24: overloads left off the list on purpose: the two long Array.Copy overloads (no game caller known, never compiled by
// the corpus, so their IL edges could not pass the coverage bar; ClauseLazy needs only the int ones)
export const LEFT_OUT = new Set(['System.Array::Copy(System.Array,long,System.Array,long,long)', 'System.Array::Copy(System.Array,System.Array,long)']);
export const binderSigs = (csv: string) => new Set(csv.split(/\r?\n/).slice(1).filter(l => l.trim()).map(l => csvFields(l)[4]));
// every overload of frame "ns.Class.Method" (Class.Method = key) with an IL body, minus the binder-bound ones (`bound`, by signature)
export function rowsOf(a: Asm, frame: string, key: string, bound: Set<string> = new Set()): NativeRow[] {
  const dot = key.indexOf('.'), cls = key.slice(0, dot), name = key.slice(dot + 1), ns = frame.slice(0, -key.length - 1);
  const out: NativeRow[] = [];
  for (const t of a.types) if (t.name === cls && t.ns === ns)  // a nested type's frame has no namespace, so "Enumerator" is several types
    for (const m of t.methods) if (m.name === name && m.rva && !bound.has(m.id)) out.push(ilRow(key, ilCode(a.buf, rva(a, m.rva))));
  return out.filter((r, i) => out.findIndex(x => x.sha === r.sha) === i);
}
// docs/537 §31.1: the bodies of `alt` (a core mod's rebuild of the same assembly) whose IL differs from EA's only in token numbers:
// same token-resolved shape (header, code, clauses) as one of EA's overloads; a changed body never joins
export function shapeTwins(ea: Asm, alt: Asm, frame: string, key: string): NativeRow[] {
  const dot = key.indexOf('.'), cls = key.slice(0, dot), name = key.slice(dot + 1), ns = frame.slice(0, -key.length - 1);
  const bodies = (a: Asm) => a.types.filter(t => t.name === cls && t.ns === ns).flatMap(t => t.methods.filter(m => m.name === name && m.rva)).map(m => rva(a, m.rva));
  const eaShapes = new Set(bodies(ea).map(p => ilShapeBody(ea, ea.buf, p)));
  const eaShas = new Set(bodies(ea).map(p => ilRow(key, ilCode(ea.buf, p)).sha));
  return bodies(alt).filter(p => eaShapes.has(ilShapeBody(alt, alt.buf, p))).map(p => ilRow(key, ilCode(alt.buf, p))).filter(r => !eaShas.has(r.sha));
}
export function nativeHeader(rows: NativeRow[], csv: string, yRows: NativeRow[] = []): string {
  const row = (r: NativeRow) => `    {"${r.key}", ${r.size}, {${r.sha.match(/../g)!.map(h => '0x' + h).join(', ')}}},`;
  return [`// Generated by node tools/jit-hot.ts --emit-native from the compile rows of ${csv}, EA's TS3 1.67 assemblies. Names, sizes and hashes only.`,
    'static const JitRow kJitRows[] = {', ...rows.map(row), '};', 'static const int kJitRowN = (int)(sizeof(kJitRows) / sizeof(kJitRows[0]));',
    '// docs/537 §31: the hand-checked class-Y rows (Y_ADMIT), compiled only with [MonoJit] YRows=1',
    'static const JitRow kJitYRows[] = {', ...yRows.map(row), '};', 'static const int kJitYRowN = (int)(sizeof(kJitYRows) / sizeof(kJitYRows[0]));', ''].join('\r\n');
}

// the static coverage of docs/537 §5: an edge is lazy if the caller is listed and the callee has a lazy body; DirectLeaf=1 takes direct sites of a leaf callee only
export type EdgeRow = { caller: string; callee: string; calls: number; direct: boolean; guard: boolean };
export const nameListed = (full: string, keys: Set<string>) => [...keys].some(k => full === k || full.endsWith('.' + k));
export function coverage(edges: EdgeRow[], keys: Set<string>, lazy: Map<string, string>) {
  const total = edges.reduce((s, e) => s + e.calls, 0), out = { total, edges: edges.length, leaf: [] as EdgeRow[], chain: [] as EdgeRow[] };
  for (const e of edges) {
    if (!nameListed(e.caller, keys) || !nameListed(e.callee, keys) || !lazy.has(e.callee)) continue;
    if (e.direct && lazy.get(e.callee) === 'leaf') out.leaf.push(e); else if (e.direct || e.guard) out.chain.push(e);
  }
  return out;
}
export const listKeys = (h: string) => new Set([...h.matchAll(/\{"([^"]+)"/g)].map(m => m[1]));
export function lazyRows(log: string) {
  const m = new Map<string, string>();
  for (const l of log.split(/\r?\n/)) { const r = /^jitlazy (leaf|chain) (.+)$/.exec(l); if (r && m.get(r[2]) !== 'chain') m.set(r[2], r[1]); }
  return m;
}
export function edgeRows(csv: string): EdgeRow[] {
  return csv.split(/\r?\n/).slice(1).filter(l => l.trim()).map(csvFields).map(f => ({ caller: f[0], callee: f[1], calls: +f[3], direct: f[12] === 'direct', guard: f[12] === 'class guard' }));
}

// the last block with an end line (a block cut by a crash has none); null when no block ended
export function gameCount(log: string): { lines: string[]; callee: string[]; head: string; leftOut: number } | null {
  const l = log.split(/\r?\n/);
  let e = l.length - 1;
  while (e >= 0 && !l[e].startsWith('jitcount: end ')) e--;
  let b = e - 1;
  while (b >= 0 && !l[b].startsWith('jitcount: begin ')) b--;
  if (e < 0 || b < 0) return null;
  const body = l.slice(b + 1, e);
  return { lines: body.filter(x => /^jit(bail|new|mat|calls|exit) /.test(x)), callee: body.filter(x => x.startsWith('jitcallee ')), head: l[b], leftOut: +(/(\d+) left out/.exec(l[e])?.[1] ?? 0) };
}
// "jitcallee <caller> <ip> <op> <lazy|normal> <target> native=<0|1|?> bound=<row|0> nolazy=<n>" (monojit.cpp JcnReportTo), one row
// per guard site and target; target "(other)" = the bails on targets past the site's 8 named rows (native unknown)
export const CALLEE_RE = /^jitcallee (\S+) (-?\d+) ([0-9A-F]+) (lazy|normal) (\S+) native=([01?]) bound=(\d+) nolazy=(\d+)$/;
export type CalleeRow = { callee: string; native: string; bails: number; sites: number; top: string; topN: number };
export function calleeRanking(rows: string[]): { rows: CalleeRow[]; total: number; bad: number } {
  const m = new Map<string, CalleeRow>();
  let total = 0, bad = 0;
  for (const x of rows) {
    const r = CALLEE_RE.exec(x);
    if (!r) { bad++; continue; }
    const n = +r[8], key = r[5] + ' ' + r[6], site = `${r[1]}@${r[2]}`;
    total += n;
    const c = m.get(key) ?? { callee: r[5], native: r[6], bails: 0, sites: 0, top: site, topN: 0 };
    c.bails += n, c.sites++;
    if (n > c.topN) c.top = site, c.topN = n;
    m.set(key, c);
  }
  return { rows: [...m.values()].sort((a, b) => b.bails - a.bails), total, bad };
}
// docs/537 §31 (M2 gate): one compiled method's exits from count lines (host or --game-count): executions (max of entries and ret
// exits), exits by opcode, exit sites listed / taken, and the call-exit sites (call, callvirt, con.call, newobj) taken
export function methodExits(lines: string[], key: string) {
  const idx = new Set<string>();
  let calls = 0;
  for (const l of lines) { const c = /^jitcalls (\d+) (\d+) \d+ \d+ (.+)$/.exec(l); if (c && keyOf(c[3]) === key) idx.add(c[1]), calls += +c[2]; }
  const byOp = new Map<string, number>();
  let rets = 0, exits = 0, sites = 0, taken = 0, callSites = 0, callTaken = 0;
  for (const l of lines) {
    const e = /^jitexit (\d+) [0-9A-F]+ (\S+) (?:op|slow) (\d+)$/.exec(l);
    if (!e || !idx.has(e[1])) continue;
    const n = +e[3], call = /^(call|callvirt|con\.call|con\.callvirt|newobj)$/.test(e[2]);
    sites++; if (n) taken++;
    if (call) { callSites++; if (n) callTaken++; }
    if (/^ret/.test(e[2])) { rets += n; continue; }
    exits += n; byOp.set(e[2], (byOp.get(e[2]) ?? 0) + n);
  }
  const execs = Math.max(calls, rets);
  return { found: idx.size > 0, calls, rets, execs, exits, perExec: execs ? exits / execs : 0, sites, taken, callSites, callTaken, byOp };
}
export function bailsByReason(lines: string[]): Map<string, number> {  // "jitbail <caller> <word> <op> <lazy|normal> <guard|direct> reason=n ..."
  const m = new Map<string, number>();
  for (const x of lines.filter(y => y.startsWith('jitbail ')))
    for (const r of x.split(' ').slice(6).join(' ').matchAll(/(?:^| )([^=]+?)=(\d+)/g)) m.set(r[1], (m.get(r[1]) ?? 0) + +r[2]);
  return m;
}

function selfCheck() {
  let k = 0;
  const ok = (c: boolean, what: string) => { k++; if (!c) { console.error('self-check failed:', what); process.exit(1); } };
  ok(keyOf('System.Collections.Generic.Dictionary`2.GetPrev') === 'Dictionary`2.GetPrev', 'key of a generic');
  ok(keyOf('Enumerator.MoveNext') === 'Enumerator.MoveNext', 'key of a nested type frame');
  ok(csvFields('"bp","self","A.B","N","36","0.04","15.4","mscorlib","1","106","x",""').length === 12, 'csv fields');
  const rows = ranking('world,kind,frame\n"bp","self","A.B","N","36","0.04","15.4","m","1","10","",""\n"ip","self","A.B","N","4","0.01","1.6","m","1","10","",""\n"ip","root","A.B","N","4","0.01","1.6","m","1","10","",""');
  ok(rows.length === 1 && Math.abs(rows[0].ms - 8.5) < 1e-9 && rows[0].samples === 40, 'mean over the worlds present');
  ok(compileList('static const char* const kJitList[] = {\n "A.B", "HostTest.X", "LzBox.Get", "C.D",\n};').size === 2, 'compile list without the host tests');
  const c = counts('jitcalls 3 100 1 1 N.A.B\njitexit 3 EC callvirt op 40\njitexit 3 6 ret op 120\njitexit 9 6 ret op 7');
  ok(c.get('A.B')!.exits === 40 && c.get('A.B')!.rets === 120 && c.get('A.B')!.topOp === 'callvirt' && c.size === 1, 'counts by method: ret is the normal end');
  ok(keyOf('Enumerator..ctor') === 'Enumerator..ctor' && keyOf('A.B..cctor') === 'B..cctor', 'constructor keys');
  const v = judge(rows, new Set(['A.B']), new Set(), c, 1);
  ok(v[0].verdict === 'template first: callvirt, 0.3 exits per execution', 'an exiting body is held back');
  ok(judge(rows, new Set(), new Set(), new Map(), 1)[0].verdict === 'compile (new: not in the host list yet)', 'a new candidate');
  ok(judge(rows, new Set(), new Set(), new Map(), 10)[0].verdict === 'below threshold', 'threshold');
  ok(summary(v, [1]).some(l => /held back by callvirt\s+8\.5 ms\/s in 1 methods/.test(l)), 'held-back ms/s by exit op');
  const tiny = Buffer.from([0xFF, (3 << 2) | 2, 1, 2, 3, 0xFF]);
  ok(ilCode(tiny, 1).equals(Buffer.from([1, 2, 3])), 'tiny header');
  const fat = Buffer.alloc(16); fat.writeUInt16LE(0x3013, 0); fat.writeUInt32LE(2, 4); fat[12] = 7; fat[13] = 8;
  ok(ilCode(fat, 0).equals(Buffer.from([7, 8])), 'fat header: code after the 12 bytes');
  const h = nativeHeader([ilRow('A.B', Buffer.from([1]))], 'x.csv', [ilRow('C.D', Buffer.from([2]))]);
  ok(h.includes('{"A.B", 1, {0x4b, 0xf5, ') && h.includes('kJitRowN'), 'native row: the SHA-256 of the IL');
  ok(h.indexOf('{"C.D", 1,') > h.indexOf('kJitYRows[] = {') && h.indexOf('{"A.B"') < h.indexOf('kJitYRows[]') && h.includes('kJitYRowN'), 'Y rows in their own table');
  const yr = [{ ...rows[0], key: 'Motive.UpdateMotiveWithDecay', cls: 'Y' }, { ...rows[0], key: 'X.Other', cls: 'Y' }];
  const yv = judge(yr, new Set(), new Set(), new Map(), 1);
  ok(yv[0].verdict === Y_VERDICT && !yv[0].verdict.startsWith('compile') && yv[1].verdict === 'yields: stays interpreted', 'Y_ADMIT: only the hand-checked Y row, never a compile row');
  const fake = { sections: [{ va: 0x1000, size: 16, raw: 0 }], buf: tiny, types: [{ name: 'C', ns: 'N', methods: [{ name: 'M', rva: 0x1001 }, { name: 'M', rva: 0 }, { name: 'X', rva: 0x1001 }] }] } as unknown as Asm;
  ok(rowsOf(fake, 'N.C.M', 'C.M').length === 1 && rowsOf(fake, 'O.C.M', 'C.M').length === 0, 'overloads with a body, in the namespace of the frame');
  const two = { ...fake, buf: Buffer.from([0x0A, 7, 8, 0x06, 9]), types: [{ name: 'C', ns: 'N', methods: [{ name: 'M', rva: 0x1000, id: 'N.C::M(int)' }, { name: 'M', rva: 0x1000, id: 'N.C::M(long)' }, { name: 'M', rva: 0x1003, id: 'N.C::M(byte)' }] }] } as unknown as Asm;
  const bs = binderSigs('index,address,adapter,hash,managed_signature,class\n8,0xD9B500,0xD9B540,03235aef,"N.C::M(long)",reader\n9,0x1,0x2,0a,"N.C::M(byte)",reader\n');
  ok(bs.size === 2 && bs.has('N.C::M(long)') && !bs.has('N.C::M(int)'), 'binder signatures from the csv (quoted, commas inside)');
  ok(rowsOf(two, 'N.C.M', 'C.M').length === 2 && rowsOf(two, 'N.C.M', 'C.M', bs).length === 1, 'C2c: a bound-only body is left out, a body shared with an unbound overload stays');
  ok(rowsOf(two, 'N.C.M', 'C.M', new Set(['N.C::M(int)', 'N.C::M(long)', 'N.C::M(byte)'])).length === 0, 'C2c: every overload bound = no row');
  const ed = edgeRows('h\nN.A,N.B,call,100,0,yes,6,1,0,no,no,no,direct,x,y\nN.A,N.C,callvirt,50,0,yes,6,1,0,no,no,no,class guard,x,y\nN.Z,N.B,call,7,0,yes,6,1,0,no,no,no,direct,x,y\nN.A,N.D,call,3,0,yes,6,1,0,no,no,no,direct,x,y\n');
  const cv = coverage(ed, new Set(['A', 'B', 'C', 'D']), lazyRows('jitlazy leaf N.B\njitlazy chain N.C\njitlazy chain N.D\njitlazy leaf N.D\n'));
  ok(cv.total === 160 && cv.leaf.length === 1 && cv.leaf[0].calls === 100 && cv.chain.length === 2, 'coverage: leaf direct, chain and guard, an unlisted caller left out, chain wins over a leaf instantiation');
  ok(nameListed('System.Math.Min', new Set(['Math.Min'])) && !nameListed('System.XMath.Min', new Set(['Math.Min'])), 'a key matches at a dot');
  const gc = gameCount('x\njitcount: begin interval #1, 1 methods\njitexit 3 EA call op 5\njitcount: end interval #1, 1 lines, 0 left out (cap 6000)\n' +
    'jitcount: begin save #2, 1 methods\njitbail N.A.B 0 EA normal direct no lazy body=2 ForceJIT=1\njitexit 3 EA call op 9\nother\njitcount: end save #2, 2 lines, 4 left out (cap 6000)\n' +
    'jitcount: begin exit #3\njitexit 3 EA call op 99\n');
  ok(gc !== null && gc.lines.length === 2 && gc.lines[1] === 'jitexit 3 EA call op 9' && gc.leftOut === 4 && gc.head.includes('#2'), 'game count: the last complete block, a torn block ignored');
  const br = bailsByReason(gc!.lines);
  ok(br.get('no lazy body') === 2 && br.get('ForceJIT') === 1 && br.size === 2, 'bails by reason, reasons with spaces');
  ok(gameCount('jitcount: begin exit #1\njitexit 3 EA call op 1\n') === null, 'no complete block');
  const me = methodExits(['jitcalls 7 10 500 90 Sims3.Gameplay.Autonomy.Motive.UpdateMotiveWithDecay', 'jitcalls 8 99 1 1 N.A.B', 'jitexit 7 1A callvirt op 30',
    'jitexit 7 2B call slow 0', 'jitexit 7 3C ret op 12', 'jitexit 7 4D endfinally op 5', 'jitexit 8 1A callvirt op 1000'], 'Motive.UpdateMotiveWithDecay');
  ok(me.found && me.execs === 12 && me.exits === 35 && me.sites === 4 && me.taken === 3 && me.callSites === 2 && me.callTaken === 1 && me.byOp.get('callvirt') === 30, 'methodExits: one method, rets are executions, call sites taken');
  ok(!methodExits(['jitexit 7 1A callvirt op 30'], 'X.Y').found, 'methodExits: no jitcalls row, not found');
  // bail census rows: JcnReportTo's format strings filled in (no game run has written one yet)
  const cl = ['jitcallee System.Collections.Generic.IEquatableOfTEqualityComparer`1.GetHashCode 4 ED lazy System.String.GetHashCode native=1 bound=0 nolazy=6760000',
    'jitcallee System.Collections.Generic.IEquatableOfTEqualityComparer`1.Equals 17 ED normal System.String.Equals native=1 bound=0 nolazy=1760000',
    'jitcallee System.Collections.Generic.Dictionary`2.GetPrev 59 EC lazy (other) native=? bound=0 nolazy=10',
    'jitcallee System.Collections.Generic.Dictionary`2.GetPrev 24 EC lazy Sims3.Foo.Comparer.GetHashCode native=0 bound=0 nolazy=90',
    'jitcallee System.Collections.Generic.Dictionary`2.GetPrev 55 EC lazy System.String.GetHashCode native=1 bound=1 nolazy=40'];
  ok(cl.every(x => CALLEE_RE.test(x)), 'census row: match');
  ok(!CALLEE_RE.test('jitbail Ns.Cls.Meth 0 EA normal guard no lazy body=2') && !CALLEE_RE.test('jitcallee A.B 4 ED lazy C.D native=1 bound=0') &&
    !CALLEE_RE.test('jitcount: end interval #1, 1 lines, 0 left out (cap 6000)'), 'census row: no match');
  const gcc = gameCount('jitcount: begin exit #1\n' + cl.join('\n') + '\njitexit 3 EA call op 1\njitcount: end exit #1, 6 lines, 0 left out (cap 6000)\n');
  ok(gcc !== null && gcc.callee.length === 5 && gcc.lines.length === 1, 'census rows kept apart from the host-style lines');
  const rk = calleeRanking([...gcc!.callee, 'jitcallee broken']);
  ok(rk.total === 8520140 && rk.bad === 1 && rk.rows.length === 4 && rk.rows[0].callee === 'System.String.GetHashCode' && rk.rows[0].bails === 6760040 &&
    rk.rows[0].sites === 2 && rk.rows[0].top.endsWith('GetHashCode@4') && rk.rows[3].callee === '(other)' && rk.rows[3].native === '?', 'census ranking by callee');
  console.log(`jit-hot self-check: ${k} checks, ok`);
}

if (process.argv[1] && /jit-hot\.ts$/.test(process.argv[1])) {
  const arg = (k: string) => { const i = process.argv.indexOf(k); return i < 0 ? undefined : process.argv[i + 1]; };
  if (process.argv.includes('--self-check')) selfCheck();
  else if (process.argv.includes('--game-count')) {
    const g = gameCount(readFileSync(arg('--game-count')!, 'utf8'));
    if (!g) { console.error('no complete jitcount block (begin .. end) in the log'); process.exit(1); }
    console.log(`${g.head}: ${g.lines.length} lines`);
    if (g.leftOut) console.warn(`WARN ${g.leftOut} lines left out by the game's cap: the census undercounts`);
    for (const [r, n] of [...bailsByReason(g.lines)].sort((a, b) => b[1] - a[1])) console.log(`  bail ${r.padEnd(18)} ${n}`);
    const mk = arg('--method');
    if (mk) {
      const m = methodExits(g.lines, mk);
      if (!m.found) console.log(`method ${mk}: not compiled in this block (no jitcalls row)`);
      else {
        console.log(`method ${mk}: ${m.execs} executions (${m.calls} entries, ${m.rets} ret exits), ${m.exits} other exits = ${m.perExec.toFixed(2)} per execution`);
        console.log(`  exit sites ${m.taken} of ${m.sites} taken; call-exit sites (call, callvirt, con.call, newobj) ${m.callTaken} of ${m.callSites} taken`);
        for (const [op, n] of [...m.byOp].sort((a, b) => b[1] - a[1]).slice(0, 8)) console.log(`  ${op.padEnd(12)} ${n} (${m.execs ? (n / m.execs).toFixed(2) : '0'} per execution)`);
      }
    }
    const ex = g.lines.filter(x => x.startsWith('jitexit ')).map(x => x.split(' ')).sort((a, b) => +b[5] - +a[5]).slice(0, 15);
    for (const f of ex) console.log(`  exit ${f[5].padStart(10)} meth ${f[1]} ${f[2]} ${f[3]} ${f[4]}`);
    const rk = calleeRanking(g.callee);
    if (g.callee.length) {
      const nat = rk.rows.filter(r => r.native === '1').reduce((a, r) => a + r.bails, 0), pct = (n: number) => (100 * n / rk.total).toFixed(1).padStart(5) + '%';
      console.log(`bail census (docs/537 §23): ${rk.total} guard "no lazy body" bails, ${rk.rows.length} callees, EA native bodies ${pct(nat).trim()}${rk.bad ? `, ${rk.bad} rows unreadable` : ''}`);
      console.log('  rank      bails  share native sites callee (top site)');
      rk.rows.slice(0, +(arg('--top') ?? 30)).forEach((r, i) =>
        console.log(`  ${String(i + 1).padStart(4)} ${String(r.bails).padStart(10)} ${pct(r.bails)} ${(r.native === '1' ? 'yes' : r.native === '0' ? 'no' : '?').padEnd(6)} ${String(r.sites).padStart(5)} ${r.callee} (${r.top})`));
    } else console.log('no jitcallee rows (needs [MonoJit] Count=1 with Direct=1 DirectLeaf=0, ts3native 0.36.250+)');
    const out = arg('--out');
    if (out) { writeFileSync(out, g.lines.join('\n') + '\n'); console.log(`wrote ${out}`); }
  }
  else if (process.argv.includes('--coverage')) {
    const keys = listKeys(readFileSync(resolve(ROOT, arg('--list') ?? 'native/ts3native/monojit-list.h'), 'utf8'));
    const c = coverage(edgeRows(readFileSync(resolve(ROOT, 'docs/data/jit-call-edges.csv'), 'utf8')), keys, lazyRows(readFileSync(arg('--coverage')!, 'utf8')));
    const sum = (a: EdgeRow[]) => a.reduce((s, e) => s + e.calls, 0), pct = (n: number) => (100 * n / c.total).toFixed(2) + '%';
    console.log(`${keys.size} listed names; ${c.edges} edges, ${c.total} calls`);
    console.log(`DirectLeaf=1: ${c.leaf.length} edges, ${sum(c.leaf)} calls, ${pct(sum(c.leaf))}`);
    console.log(`DirectLeaf=0: ${c.leaf.length + c.chain.length} edges, ${sum(c.leaf) + sum(c.chain)} calls, ${pct(sum(c.leaf) + sum(c.chain))}`);
    for (const e of [...c.leaf, ...c.chain].sort((a, b) => b.calls - a.calls)) console.log(`  ${String(e.calls).padStart(9)} ${c.leaf.includes(e) ? 'leaf ' : 'chain'} ${e.caller} -> ${e.callee}`);
  } else if (process.argv.includes('--emit-native')) {
    const dir = arg('--emit-native')!, csv = arg('--csv') ?? 'docs/data/jit-hot.csv', out = arg('--out') ?? 'native/ts3native/monojit-list.h';
    const hot = readFileSync(resolve(ROOT, csv), 'utf8').split(/\r?\n/).slice(1).filter(l => l.trim()).map(csvFields).filter(f => f[13].startsWith('compile') || f[13] === Y_VERDICT);
    const asms = new Map<string, Asm>(), rows: NativeRow[] = [], yRows: NativeRow[] = [];
    const bound = binderSigs(readFileSync(resolve(ROOT, 'docs/data/binder-bodies.csv'), 'utf8'));
    let methods = 0, dropped = 0;
    const alt = arg('--alt');
    for (const f of hot) {
      const of = (b?: Set<string>) => f[8].split('+').flatMap(name => {  // an Enumerator frame merges assemblies
        if (!asms.has(name)) asms.set(name, loadStandalone(join(dir, name + '.dll'), name));
        return rowsOf(asms.get(name)!, f[2], f[1], b);
      });
      const all = of(LEFT_OUT), r = of(new Set([...bound, ...LEFT_OUT]));
      if (!all.length) throw new Error(`no IL body for ${f[2]} in ${f[8]}`);
      dropped += all.length - r.length;
      if (all.length > r.length) console.log(`${f[1].padEnd(60)} binder: native body, ${all.length - r.length} of ${all.length} bodies left out (docs/537 §20.2)`);
      if (!r.length) continue;
      methods++;
      (f[13] === Y_VERDICT ? yRows : rows).push(...r);
      if (f[13] === Y_VERDICT && alt) {
        const tw = shapeTwins(asms.get(f[8])!, loadStandalone(alt, f[8]), f[2], f[1]);
        yRows.push(...tw);
        console.log(`${f[1].padEnd(60)} --alt: ${tw.length} token-renumbered twin(s)`);
      }
      console.log(`${f[1].padEnd(60)} ${r.length} overload(s), IL ${r.map(x => x.size).join(' ')}`);
    }
    writeFileSync(resolve(ROOT, out), nativeHeader(rows, csv, yRows));
    console.log(`wrote ${out}: ${methods} methods, ${rows.length} bodies + ${yRows.length} YRows bodies (${dropped} binder-bound bodies left out)`);
  } else {
    const threshold = +(arg('--threshold') ?? 1);
    const rows = ranking(readFileSync(resolve(ROOT, 'docs/data/compile-candidates.csv'), 'utf8'));
    const listed = compileList(readFileSync(resolve(ROOT, 'native/monohost/monohost-jit.cpp'), 'utf8'));
    const acc = new Set(ACCESSORS.map(a => a[3]));
    const cl = arg('--count');
    const cnt = cl ? counts(readFileSync(cl, 'utf8')) : new Map<string, Count>();
    const lazy = new Map(LAZY_ROWS.map(([frame, asm]) => [keyOf(frame), { frame, key: keyOf(frame), cls: 'N', worlds: 0, samples: 0, ms: 0, il: 0, asm }]));
    const v = [...judge(rows.filter(r => !lazy.has(r.key) || r.ms >= threshold), listed, acc, cnt, threshold), ...judge([...lazy.values()].map(l => rows.find(r => r.key === l.key && r.ms < threshold) ?? l).filter(l => !rows.some(r => r.key === l.key && r.ms >= threshold)), listed, acc, cnt, 0)];
    for (const l of summary(v, [2, 1, 0.5])) console.log(l);
    const csv = ['rank,method,frame,class,worlds,samples,ms_s_mean,il_bytes,assembly,host_list,host_executions,host_exits,top_exit,verdict',
      ...v.map((x, i) => [i + 1, x.row.key, x.row.frame, x.row.cls, x.row.worlds, x.row.samples, x.row.ms.toFixed(2), x.row.il, x.row.asm, x.listed,
        x.count ? execs(x.count) : '', x.count?.exits ?? '', x.count?.topOp ?? '', x.verdict].map(f => `"${f}"`).join(','))].join('\n') + '\n';
    const out = arg('--csv') ?? 'docs/data/jit-hot.csv';
    if (process.argv.includes('--write')) { writeFileSync(resolve(ROOT, out), csv); console.log(`wrote ${out}: ${v.length} rows`); }
    else for (const x of v.filter(y => y.verdict.startsWith('compile') || y.verdict.startsWith('template')).slice(0, 40))
      console.log(`${x.row.ms.toFixed(1).padStart(5)} ${x.row.cls} ${x.row.key.padEnd(52)} ${x.listed.padEnd(9)} ${x.verdict}`);
  }
}
