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
//   node tools/jit-hot.ts --self-check
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { ACCESSORS } from './jit-corpus.ts';

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
  console.log(`jit-hot self-check: ${k} checks, ok`);
}

if (process.argv[1] && /jit-hot\.ts$/.test(process.argv[1])) {
  const arg = (k: string) => { const i = process.argv.indexOf(k); return i < 0 ? undefined : process.argv[i + 1]; };
  if (process.argv.includes('--self-check')) selfCheck();
  else {
    const threshold = +(arg('--threshold') ?? 1);
    const rows = ranking(readFileSync(resolve(ROOT, 'docs/data/compile-candidates.csv'), 'utf8'));
    const listed = compileList(readFileSync(resolve(ROOT, 'native/monohost/monohost-jit.cpp'), 'utf8'));
    const acc = new Set(ACCESSORS.map(a => a[3]));
    const cl = arg('--count');
    const cnt = cl ? counts(readFileSync(cl, 'utf8')) : new Map<string, Count>();
    const v = judge(rows, listed, acc, cnt, threshold);
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
