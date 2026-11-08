// docs/537 §17: per-method timing of the [MonoJit] compile list in the host. MONOHOST_JITBENCH=1 (monohost-jitgate.cpp): self ns per call,
// EA's interpreter vs compiled, over whole corpus passes, pinned, MONOHOST_JITBENCH_REPS passes per arm with the arm order swapped each
// pass. Median and spread (interquartile) are over the passes. The ms/s are MODELLED, not measured: no game run has calls per method, so a
// method's gain is its sampled self time (docs/data/jit-hot.csv, ms_s_mean: samples of the v0.2.4 and 2026-10-28 builds, docs/207 §1) x
// (1 - new / old), and calls/s is that self time / old ns. A method that shares its key with several bodies weighs them by host self time.
//   node tools/jit-bench.ts --run [--game-copy <dir>] [--reps 9] [--no-write]    bench + two call-path runs, writes docs/data/jit-bench.csv
//   node tools/jit-bench.ts --report <jit-bench.txt> [--calls <off.log> <on.log>] [--game-copy <dir>] [--no-write]
//   node tools/jit-bench.ts --self-check
// Not for CI or a loaded machine (the numbers); the self-check is. kind in the csv: body (ns), method (modelled ms/s), call (direct calls, bench cases), total.
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { gameCopy, listRows, OUT, runHost, type Row } from './jit-coverage.ts';
import { csvFields } from './jit-hot.ts';

const ROOT = resolve(import.meta.dirname, '..');
export const CSV = resolve(ROOT, 'docs/data/jit-bench.csv');
const arg = (n: string) => { const i = process.argv.indexOf(n); return i > 0 ? process.argv[i + 1] : undefined; };

export const median = (x: number[]) => { const s = [...x].sort((a, b) => a - b), n = s.length; return !n ? NaN : n % 2 ? s[(n - 1) / 2] : (s[n / 2 - 1] + s[n / 2]) / 2; };
export const spread = (x: number[]) => { const s = [...x].sort((a, b) => a - b), n = s.length; return s[Math.floor(n * 0.75)] - s[Math.floor(n * 0.25)]; };  // interquartile: a GC pause in one pass stays out
export type Rep = { arm: number; rep: number; m: number; calls: number; cycles: number };
export type Bench = { hz: number; floor: number; reps: Rep[]; sha: Map<number, string> };
export function parseBench(text: string): Bench {
  const b: Bench = { hz: 0, floor: 0, reps: [], sha: new Map() };
  let m = -1;
  for (const l of text.split(/\r?\n/)) {
    const f = l.split(' ');
    if (f[0] === 'R') b.reps.push({ arm: +f[1], rep: +f[2], m: +f[3], calls: +f[4], cycles: +f[5] });
    else if (f[0] === 'H') { b.hz = +f[1]; b.floor = +f[2]; }
    else if (f[0] === 'B') m = +f[1];
    else if (f[0] === 'I') b.sha.set(m, createHash('sha256').update(Buffer.from(f[1] ?? '', 'hex')).digest('hex'));
  }
  return b;
}
export type BodyTime = { row: Row; oldNs: number; oldSpread: number; newNs: number; newSpread: number; calls: number; reps: number };
// one IL body = the sum of its runtime methods per pass; the stamps' own floor comes off both arms
export function bodyTimes(b: Bench, rows: Row[]): BodyTime[] {
  const out: BodyTime[] = [], ns = 1e9 / b.hz;
  for (const r of rows) {
    const ms = new Set([...b.sha].filter(([, s]) => s === r.sha).map(([m]) => m));
    const of = (arm: number) => {
      const by = Map.groupBy(b.reps.filter(x => x.arm === arm && ms.has(x.m)), x => x.rep);
      return [...by.values()].map(v => { const c = v.reduce((s, x) => s + x.calls, 0); return { c, ns: (v.reduce((s, x) => s + x.cycles, 0) / c - b.floor) * ns }; });
    };
    const o = of(0), n = of(1);
    if (!o.length || !n.length) continue;
    out.push({ row: r, oldNs: median(o.map(x => x.ns)), oldSpread: spread(o.map(x => x.ns)), newNs: median(n.map(x => x.ns)), newSpread: spread(n.map(x => x.ns)), calls: median(n.map(x => x.c)), reps: Math.min(o.length, n.length) });
  }
  return out;
}
// below this many host calls per pass a ratio means nothing (get_Value: entered once, by reflection): the method gets no modelled gain
export const MIN_CALLS = 16;
export type Model = { key: string; selfMs: number; oldNs: number; newNs: number; ratio: number; gainMs: number; callsPerS: number; calls: number };
export function model(t: BodyTime[], selfMs: Map<string, number>): Model[] {
  const out: Model[] = [];
  for (const [key, ts] of Map.groupBy(t, x => x.row.key)) {
    const w = ts.map(x => x.calls * x.oldNs), sum = w.reduce((s, x) => s + x, 0);
    const oldNs = ts.reduce((s, x, i) => s + x.oldNs * w[i], 0) / sum, newNs = ts.reduce((s, x, i) => s + x.newNs * w[i], 0) / sum;
    const m = selfMs.get(key) ?? 0, ratio = oldNs > 0 ? newNs / oldNs : 1;
    const calls = ts.reduce((s, x) => s + x.calls, 0);
    out.push({ key, selfMs: m, oldNs, newNs, ratio, gainMs: calls < MIN_CALLS ? 0 : m * (1 - ratio), callsPerS: oldNs > 0 ? m * 1e6 / oldNs : 0, calls });
  }
  return out.sort((a, b) => b.gainMs - a.gainMs);
}
export const selfTimes = (csv: string) => new Map(csv.split(/\r?\n/).slice(1).filter(l => l.trim()).map(csvFields).map(f => [f[1], +f[6]] as [string, number]));
// the host's fixed bench cases (HostTest.JitBench) with lazy frames off and on: what a direct compiled call saves over EA's call path
export const CALL_CASES = ['JitLz 64', 'JitLzChain 64', 'JitLzVirt 64', 'JitLzAlloc 64'];
export function callRows(offLog: string, onLog: string) {
  const row = (log: string, name: string) => log.match(new RegExp(`jitbench case\\s+\\d+ ${name}.*?interp\\s+([\\d.]+) ns\\s+compiled\\s+([\\d.]+) ns`));
  return CALL_CASES.flatMap(c => {
    const a = row(offLog, c), b = row(onLog, c), t = row(offLog, `${c.replace(' 64', 'Plain 64')}`);
    return a && b ? [{ name: c, interpNs: t ? +t[1] : +a[1], offNs: +a[2], onNs: +b[2] }] : [];
  });
}
const q = (s: string | number) => `"${s}"`, n1 = (x: number) => x.toFixed(1);
export function csvText(t: BodyTime[], m: Model[], c: ReturnType<typeof callRows>, meta: string): string {
  const lines = ['kind,method,sha8,old_ns,old_spread_ns,new_ns,new_spread_ns,reps,host_calls_per_pass,game_self_ms_s,implied_game_calls_s,modelled_gain_ms_s,note'];
  for (const x of t) lines.push(['body', x.row.key, x.row.sha.slice(0, 8), n1(x.oldNs), n1(x.oldSpread), n1(x.newNs), n1(x.newSpread), x.reps, x.calls, '', '', '', ''].map(q).join(','));
  for (const x of m) lines.push(['method', x.key, '', n1(x.oldNs), '', n1(x.newNs), '', '', '', x.selfMs.toFixed(2), Math.round(x.callsPerS), x.gainMs.toFixed(2), x.calls < MIN_CALLS ? `not modelled: ${x.calls} host calls per pass` : 'modelled'].map(q).join(','));
  for (const x of c) lines.push(['call', x.name, '', n1(x.offNs), '', n1(x.onNs), '', '', '', '', '', '', `ns per bench iteration (per 64 / step), old = compiled with EA's call path, new = lazy direct calls, interpreted twin ${n1(x.interpNs)}`].map(q).join(','));
  const sum = m.reduce((s, x) => s + x.gainMs, 0), self = m.filter(x => x.calls >= MIN_CALLS).reduce((s, x) => s + x.selfMs, 0);
  lines.push(['total', 'all methods', '', '', '', '', '', '', '', self.toFixed(2), '', sum.toFixed(2), `modelled, not measured; ${meta}`].map(q).join(','));
  return lines.join('\n') + '\n';
}

function selfCheck() {
  const ok = (c: boolean, m: string) => { if (!c) { console.error(`FAIL ${m}`); process.exit(1); } };
  ok(median([3, 1, 2]) === 2 && median([1, 2, 3, 4]) === 2.5 && spread([1, 5, 2, 3, 4]) === 2, 'median, spread');
  const il = Buffer.from([0x2a]), sha = createHash('sha256').update(il).digest('hex');
  const text = `R 0 0 1 10 1000\nR 0 1 1 10 1200\nR 1 0 1 10 400\nR 1 1 1 10 400\nR 1 0 2 10 400\nH 1000000000 10\nB 1 0 3 0 1 X.Y\nI 2a\nB 2 0 3 0 1 Q.R\nI 00\n`;
  const b = parseBench(text);
  ok(b.hz === 1e9 && b.floor === 10 && b.reps.length === 5 && b.sha.get(1) === sha, 'parse');
  const t = bodyTimes(b, [{ key: 'X.Y', size: 1, sha }, { key: 'A.B', size: 1, sha: 'none' }]);
  ok(t.length === 1 && t[0].oldNs === 100 && t[0].oldSpread === 20 && t[0].newNs === 30, 'body times: median, floor off, one row without data dropped');
  const mo = model([{ ...t[0], calls: 1000 }], new Map([['X.Y', 10]]));
  ok(model([{ ...t[0], calls: 2 }], new Map([['X.Y', 10]]))[0].gainMs === 0, 'too few calls: no gain');
  ok(Math.abs(mo[0].gainMs - 7) < 1e-9 && Math.abs(mo[0].callsPerS - 1e5) < 1e-6, 'model: 10 ms/s at 100 -> 30 ns is 7 ms/s, 100,000 calls/s');
  const w = model([{ ...t[0], calls: 500, oldNs: 100, newNs: 50 }, { ...t[0], calls: 500, oldNs: 300, newNs: 300 }], new Map([['X.Y', 8]]));
  ok(Math.abs(w[0].ratio - (50 * 100 + 300 * 300) / (100 * 100 + 300 * 300)) < 1e-9, 'model weighs by host self time');
  const log = 'jitbench case 23 JitLz 64 (compiled, direct calls to leaves) interp    39.0 ns  compiled    39.5 ns  speedup 1.00x  saved\njitbench case 24 JitLzPlain 64 (interpreted twin) interp 56.2 ns  compiled 56.0 ns  speedup 1x saved\n';
  const c = callRows(log, log.replace('39.5', '9.5'));
  ok(c.length === 1 && c[0].offNs === 39.5 && c[0].onNs === 9.5 && c[0].interpNs === 56.2, 'call rows');
  const csv = csvText(t, mo, c, 'x');
  ok(csv.includes('"total"') && csv.includes('modelled, not measured') && csvFields(csv.split('\n')[1])[0] === 'body', 'csv');
  if (existsSync(CSV)) ok(/modelled, not measured/.test(readFileSync(CSV, 'utf8')), 'the checked-in csv keeps its label');
  console.log('jit-bench self-check OK');
}

if (import.meta.main) {
  if (process.argv.includes('--self-check')) selfCheck();
  else {
    const gc = gameCopy(), reps = arg('--reps') ?? '9';
    let text: string, offLog = '', onLog = '';
    if (process.argv.includes('--run')) {
      const r = runHost({ MONOHOST_JIT: 'on', MONOHOST_JITBENCH: '1', MONOHOST_JITBENCH_REPS: reps, MONOHOST_MINTCALL: 'off' }, gc, 560);
      console.log(r.line);
      console.log((r.log.match(/jit bench: .*/) ?? ['no jit bench line'])[0]);
      text = readFileSync(join(OUT, 'jit-bench.txt'), 'latin1');
      offLog = runHost({ MONOHOST_JIT: 'on', MONOHOST_JITCORPUS: '0', MONOHOST_MINTCALL: 'off', MONOHOST_JITLAZY: '0' }, gc, 280, false).log;
      onLog = runHost({ MONOHOST_JIT: 'on', MONOHOST_JITCORPUS: '0', MONOHOST_MINTCALL: 'off', MONOHOST_JITLAZY: '1' }, gc, 280, false).log;
    } else {
      const p = arg('--report');
      if (!p) throw new Error('--run, --report <jit-bench.txt> or --self-check');
      text = readFileSync(p, 'latin1');
      const i = process.argv.indexOf('--calls');
      if (i > 0) { offLog = readFileSync(process.argv[i + 1], 'latin1'); onLog = readFileSync(process.argv[i + 2], 'latin1'); }
    }
    const rows = listRows(readFileSync(resolve(ROOT, 'native/ts3native/monojit-list.h'), 'utf8'));
    const b = parseBench(text), t = bodyTimes(b, rows);
    const self = selfTimes(readFileSync(resolve(ROOT, 'docs/data/jit-hot.csv'), 'utf8')), m = model(t, self), c = callRows(offLog, onLog);
    const noData = rows.filter(r => !t.some(x => x.row === r));
    const meta = `self ms/s from docs/data/jit-hot.csv (sampler runs of the v0.2.4 and 2026-10-28 builds, docs/207 §1), host Direct=0, tsc ${(b.hz / 1e9).toFixed(2)} GHz, stamp floor ${(b.floor * 1e9 / b.hz).toFixed(1)} ns`;
    if (!process.argv.includes('--no-write')) writeFileSync(CSV, csvText(t, m, c, meta));
    for (const x of m) console.log(`${x.key.padEnd(56)} self ${x.selfMs.toFixed(2).padStart(6)} old ${n1(x.oldNs).padStart(7)} new ${n1(x.newNs).padStart(7)} x${x.ratio.toFixed(2)} gain ${x.gainMs.toFixed(2)}`);
    for (const x of c) console.log(`${x.name}: EA call ${n1(x.offNs)} lazy ${n1(x.onNs)} interp twin ${n1(x.interpNs)} ns per iteration`);
    console.log(`${t.length} of ${rows.length} bodies timed; no data: ${noData.map(r => `${r.key} ${r.sha.slice(0, 8)}`).join(', ') || 'none'}`);
    console.log(`total modelled gain ${m.reduce((s, x) => s + x.gainMs, 0).toFixed(1)} ms/s of ${m.filter(x => x.calls >= MIN_CALLS).reduce((s, x) => s + x.selfMs, 0).toFixed(1)} sampled (host only, not measured in game); ${meta}`);
  }
}
