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
//      [MonoJit] built-in list, the compile rows of the csv with size and SHA-256 of every overload's IL body (docs/527 §4.2)
//   node tools/jit-hot.ts --coverage <monohost.exe.log> [--list native/ts3native/monojit-list.h]: docs/data/jit-call-edges.csv, the edges a [MonoJit] Direct build
//      takes lazily with that list (callee's lazy body from the log's jitlazy rows, a count run with MONOHOST_JITLAZY=1)
//   node tools/jit-hot.ts --self-check
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { ACCESSORS } from './jit-corpus.ts';
import { loadStandalone, rva, type Asm } from './managed-callgraph.ts';

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
];
export const execs = (c: Count): number => Math.max(c.calls, c.rets);
export type Verdict = { row: Row; listed: string; count?: Count; verdict: string };
export function judge(rows: Row[], listed: Set<string>, acc: Set<string>, cnt: Map<string, Count>, threshold: number): Verdict[] {
  return rows.map(row => {
    const where = listed.has(row.key) ? 'kJitList' : acc.has(row.key) ? 'accessor' : '';
    const count = cnt.get(row.key);
    let verdict: string;
    if (row.cls === 'Y') verdict = 'yields: stays interpreted';
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
// every overload of frame "ns.Class.Method" (Class.Method = key) with an IL body
export function rowsOf(a: Asm, frame: string, key: string): NativeRow[] {
  const dot = key.indexOf('.'), cls = key.slice(0, dot), name = key.slice(dot + 1), ns = frame.slice(0, -key.length - 1);
  const out: NativeRow[] = [];
  for (const t of a.types) if (t.name === cls && t.ns === ns)  // a nested type's frame has no namespace, so "Enumerator" is several types
    for (const m of t.methods) if (m.name === name && m.rva) out.push(ilRow(key, ilCode(a.buf, rva(a, m.rva))));
  return out.filter((r, i) => out.findIndex(x => x.sha === r.sha) === i);
}
export function nativeHeader(rows: NativeRow[], csv: string): string {
  const row = (r: NativeRow) => `    {"${r.key}", ${r.size}, {${r.sha.match(/../g)!.map(h => '0x' + h).join(', ')}}},`;
  return [`// Generated by node tools/jit-hot.ts --emit-native from the compile rows of ${csv}, EA's TS3 1.67 assemblies. Names, sizes and hashes only.`,
    'static const JitRow kJitRows[] = {', ...rows.map(row), '};', 'static const int kJitRowN = (int)(sizeof(kJitRows) / sizeof(kJitRows[0]));', ''].join('\r\n');
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
  const h = nativeHeader([ilRow('A.B', Buffer.from([1]))], 'x.csv');
  ok(h.includes('{"A.B", 1, {0x4b, 0xf5, ') && h.includes('kJitRowN'), 'native row: the SHA-256 of the IL');
  const fake = { sections: [{ va: 0x1000, size: 16, raw: 0 }], buf: tiny, types: [{ name: 'C', ns: 'N', methods: [{ name: 'M', rva: 0x1001 }, { name: 'M', rva: 0 }, { name: 'X', rva: 0x1001 }] }] } as unknown as Asm;
  ok(rowsOf(fake, 'N.C.M', 'C.M').length === 1 && rowsOf(fake, 'O.C.M', 'C.M').length === 0, 'overloads with a body, in the namespace of the frame');
  const ed = edgeRows('h\nN.A,N.B,call,100,0,yes,6,1,0,no,no,no,direct,x,y\nN.A,N.C,callvirt,50,0,yes,6,1,0,no,no,no,class guard,x,y\nN.Z,N.B,call,7,0,yes,6,1,0,no,no,no,direct,x,y\nN.A,N.D,call,3,0,yes,6,1,0,no,no,no,direct,x,y\n');
  const cv = coverage(ed, new Set(['A', 'B', 'C', 'D']), lazyRows('jitlazy leaf N.B\njitlazy chain N.C\njitlazy chain N.D\njitlazy leaf N.D\n'));
  ok(cv.total === 160 && cv.leaf.length === 1 && cv.leaf[0].calls === 100 && cv.chain.length === 2, 'coverage: leaf direct, chain and guard, an unlisted caller left out, chain wins over a leaf instantiation');
  ok(nameListed('System.Math.Min', new Set(['Math.Min'])) && !nameListed('System.XMath.Min', new Set(['Math.Min'])), 'a key matches at a dot');
  console.log(`jit-hot self-check: ${k} checks, ok`);
}

if (process.argv[1] && /jit-hot\.ts$/.test(process.argv[1])) {
  const arg = (k: string) => { const i = process.argv.indexOf(k); return i < 0 ? undefined : process.argv[i + 1]; };
  if (process.argv.includes('--self-check')) selfCheck();
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
    const hot = readFileSync(resolve(ROOT, csv), 'utf8').split(/\r?\n/).slice(1).filter(l => l.trim()).map(csvFields).filter(f => f[13].startsWith('compile'));
    const asms = new Map<string, Asm>(), rows: NativeRow[] = [];
    for (const f of hot) {
      const r = f[8].split('+').flatMap(name => {  // an Enumerator frame merges assemblies
        if (!asms.has(name)) asms.set(name, loadStandalone(join(dir, name + '.dll'), name));
        return rowsOf(asms.get(name)!, f[2], f[1]);
      });
      if (!r.length) throw new Error(`no IL body for ${f[2]} in ${f[8]}`);
      rows.push(...r);
      console.log(`${f[1].padEnd(60)} ${r.length} overload(s), IL ${r.map(x => x.size).join(' ')}`);
    }
    writeFileSync(resolve(ROOT, out), nativeHeader(rows, csv));
    console.log(`wrote ${out}: ${hot.length} methods, ${rows.length} bodies`);
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
