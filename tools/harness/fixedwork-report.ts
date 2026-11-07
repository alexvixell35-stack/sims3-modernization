// X4 fixed-work bench report (docs/244): reads <tag>-ab-fixedwork.jsonl written by profile-world.ts --fixedwork (one line per
// agent fixed_work call: slice i, arm on, usPer100 = microseconds per 100 rounds of one fixed managed loop, sum, resUs, warm,
// totalUs) and prints one line: time per arm, same-arm slice spread (the noise floor), on vs off, the tool's own cost.
//   node tools/harness/fixedwork-report.ts <probe-out prefix>-ab-fixedwork.jsonl [--tol 1]   verdict: exit 1 on FAIL
//   node tools/harness/fixedwork-report.ts --self-check
// --tol <pct>: the same-arm spread limit of the measurement smoke (default 1): above it X4 is dropped (audit row X4).
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { parseArgs } from 'node:util';

export type Row = { i: number; s: number; on: boolean; warmup?: boolean; us: number; sum: number; resUs: number; warm?: boolean; totalUs: number };
export type Arm = { slices: number[]; median: number; spread: number };
export type Summary = { off: Arm; on: Arm; deltaPct: number | null; noisePct: number; costPct: number | null; calls: number; problems: string[]; line: string };

const median = (a: number[]): number => { const s = [...a].sort((x, y) => x - y); const m = s.length >> 1; return s.length ? (s.length % 2 ? s[m] : (s[m - 1] + s[m]) / 2) : NaN; };
const pct = (x: number): string => `${x >= 0 ? '+' : ''}${x.toFixed(2)}%`;

export function parseRows(text: string): Row[] {
  return text.split(/\r?\n/).filter(l => l.trim()).map(l => JSON.parse(l) as Row);
}

// secs: counted sim seconds of the run (for the cost line); omitted = no cost figure
export function summarize(rows: Row[], tolPct = 1, secs?: number): Summary {
  const problems: string[] = [];
  const used = rows.filter(r => !r.warm && !r.warmup);
  const bySlice = (on: boolean): Arm => {
    const per = new Map<number, number[]>();
    for (const r of used) if (r.on === on) per.set(r.i, [...(per.get(r.i) ?? []), r.us]);
    const slices = [...per.values()].map(median);
    const med = median(slices);
    return { slices, median: med, spread: slices.length > 1 ? 100 * (Math.max(...slices) - Math.min(...slices)) / med : NaN };
  };
  const off = bySlice(false), on = bySlice(true);
  if (off.slices.length < 2 || on.slices.length < 2) problems.push(`needs 2 counted slices per arm, have off ${off.slices.length}, on ${on.slices.length}`);
  const sums = new Set(rows.map(r => r.sum));
  if (sums.size > 1) problems.push(`checksum differs between calls (${[...sums].join(', ')}): the loop is not fixed work`);
  const res = Math.max(0, ...rows.map(r => r.resUs));
  if (res > 1000) problems.push(`clock step ${res} us: too coarse for the loop`);
  const noise = Math.max(off.spread || 0, on.spread || 0);
  if (noise > tolPct) problems.push(`same-arm spread ${noise.toFixed(2)}% > ${tolPct}%`);
  const delta = off.slices.length && on.slices.length ? 100 * (on.median - off.median) / off.median : null;
  const cost = secs && secs > 0 ? 100 * rows.reduce((a, r) => a + r.totalUs, 0) / 1e6 / secs : null;
  const arm = (n: string, a: Arm) => `${n} ${a.median.toFixed(1)} us/100 rounds (${a.slices.length} slices, spread ${Number.isFinite(a.spread) ? a.spread.toFixed(2) + '%' : 'n/a'})`;
  const line = `fixed-work: ${arm('off', off)}, ${arm('on', on)}${delta === null ? '' : `, on vs off ${pct(delta)}`}; noise floor ${noise.toFixed(2)}%; ${rows.length} calls${cost === null ? '' : `, tool cost ${cost.toFixed(3)}% of run time`}; clock step ${res} us${problems.length ? `; PROBLEMS: ${problems.join('; ')}` : ''}`;
  return { off, on, deltaPct: delta, noisePct: noise, costPct: cost, calls: rows.length, problems, line };
}

if (import.meta.filename === resolve(process.argv[1] ?? '')) {
  const { values, positionals } = parseArgs({ allowPositionals: true, options: { 'self-check': { type: 'boolean' }, tol: { type: 'string', default: '1' }, secs: { type: 'string' } } });
  if (values['self-check']) {
    const mk = (i: number, on: boolean, us: number[], extra: Partial<Row> = {}): Row[] => us.map((u, k) => ({ i, s: i * 60 + k, on, us: u, sum: 777, resUs: 1, totalUs: 20000, ...extra }));
    // ABBA: slice 0 warm-up (dropped), then off, on, on, off. on = 2% faster, same-arm slices within 0.3%
    const rows = [...mk(0, true, [900, 900], { warmup: true }), ...mk(1, false, [100, 100.2]), ...mk(2, true, [98, 98.1]), ...mk(3, true, [98.2, 98.2]), ...mk(4, false, [100.1, 100.3]),
      { i: 1, s: 1, on: false, us: 9999, sum: 777, resUs: 1, totalUs: 99999, warm: true }];
    const ok = summarize(rows, 1, 300);
    console.assert(ok.problems.length === 0, `ok case: ${ok.problems}`);
    console.assert(Math.abs(ok.deltaPct! + 2) < 0.3, `delta ${ok.deltaPct}`);
    console.assert(ok.noisePct < 0.3, `noise ${ok.noisePct}`);
    console.assert(ok.calls === 11 && Math.abs(ok.costPct! - 100 * (10 * 20000 + 99999) / 1e6 / 300) < 1e-9, `cost ${ok.costPct}`);
    // same-arm slices 5% apart: smoke fails
    const noisy = summarize([...mk(1, false, [100]), ...mk(2, false, [105]), ...mk(3, true, [100]), ...mk(4, true, [100.2])], 1);
    console.assert(noisy.problems.some(p => p.includes('spread')), 'noisy case flags the spread');
    // checksum drift, coarse clock, too few slices
    console.assert(summarize([...rows, { i: 2, s: 1, on: true, us: 98, sum: 778, resUs: 1, totalUs: 1 }]).problems.some(p => p.includes('checksum')), 'checksum');
    console.assert(summarize(mk(1, false, [1]).concat(mk(2, true, [1]), mk(3, true, [1]), mk(4, false, [1]).map(r => ({ ...r, resUs: 15600 })))).problems.some(p => p.includes('clock step')), 'clock');
    console.assert(summarize(mk(1, false, [1]).concat(mk(2, true, [1]))).problems.some(p => p.includes('2 counted slices')), 'few slices');
    console.assert(parseRows('{"i":1,"s":2,"on":true,"us":3,"sum":4,"resUs":1,"totalUs":5}\n').length === 1, 'parse');
    if (process.exitCode) process.exit(1);
    console.log('fixedwork-report self-check ok');
  } else {
    if (!positionals[0]) { console.error('usage: node tools/harness/fixedwork-report.ts <tag>-ab-fixedwork.jsonl [--tol 1] [--secs <run seconds>] | --self-check'); process.exit(2); }
    const r = summarize(parseRows(readFileSync(positionals[0], 'utf8')), Number(values.tol), values.secs ? Number(values.secs) : undefined);
    console.log(r.line);
    if (r.problems.length) process.exit(1);
  }
}
