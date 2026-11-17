// GC measure for A/B runs (docs/584 §6): collections (Boehm marks, GC_gc_no) per hour and live-set growth per hour, from the
// ts3native [MemDiag] samples ("diag: mem #N ... gcHeap= gcFree= gcN= t=", tools/mem-report.ts parses them).
//   live set = gcHeap - gcFree (MB) at the samples taken right after a forced collection (ev=gc-after); with fewer than two of
//   those, every sample is used and the row says "upper bound" (heap in use then includes garbage not yet collected).
//   rates are per wall hour of the log's sample clock (t, seconds since init); give --sim-hours to add per-sim-hour rates.
// Usage:
//   node tools/harness/gc-rate.ts <ts3native.log> [--sim-hours H]           one run
//   node tools/harness/gc-rate.ts <off.log> <on.log> [--sim-hours H,H]      an A/B pair (two loads): both rows and on - off
//   node tools/harness/gc-rate.ts --self-check
// gcHeap / gcFree / gcN are read from Boehm's globals (memdiag.cpp, docs/66 §6.1) and still flagged '?' (unvalidated) in the log.
// Reads only.
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { parseLog, type Sample } from '../mem-report.ts';

export type GcRow = { samples: number; hours: number; marks: number; marksPerHour: number; heapMbPerHour: number; liveFrom: 'gc-after' | 'all (upper bound)'; liveFirst: number; liveLast: number; liveMbPerHour: number; simHours?: number; marksPerSimHour?: number; liveMbPerSimHour?: number };

function slope(xs: number[], ys: number[]): number {
  const n = xs.length;
  if (n < 2) return 0;
  const mx = xs.reduce((a, b) => a + b, 0) / n, my = ys.reduce((a, b) => a + b, 0) / n;
  let num = 0, den = 0;
  for (let i = 0; i < n; i++) { num += (xs[i] - mx) * (ys[i] - my); den += (xs[i] - mx) ** 2; }
  return den > 0 ? num / den : 0;
}

export function gcRate(samples: Sample[], simHours?: number): GcRow | null {
  const ok = samples.filter((s) => s.t != null && typeof s.v.gcHeap === 'number' && typeof s.v.gcFree === 'number' && typeof s.v.gcN === 'number');
  if (ok.length < 2) return null;
  ok.sort((a, b) => a.t! - b.t!);
  const first = ok[0], last = ok[ok.length - 1];
  const hours = (last.t! - first.t!) / 3600;
  if (hours <= 0) return null;
  const marks = (last.v.gcN as number) - (first.v.gcN as number);
  const after = ok.filter((s) => s.ev === 'gc-after');
  const live = after.length >= 2 ? after : ok;
  const xs = live.map((s) => s.t! / 3600), ys = live.map((s) => (s.v.gcHeap as number) - (s.v.gcFree as number));
  const liveMbPerHour = slope(xs, ys);
  const row: GcRow = {
    samples: ok.length, hours, marks, marksPerHour: marks / hours,
    heapMbPerHour: ((last.v.gcHeap as number) - (first.v.gcHeap as number)) / hours,
    liveFrom: after.length >= 2 ? 'gc-after' : 'all (upper bound)', liveFirst: ys[0], liveLast: ys[ys.length - 1], liveMbPerHour,
  };
  if (simHours && simHours > 0) { row.simHours = simHours; row.marksPerSimHour = marks / simHours; row.liveMbPerSimHour = liveMbPerHour * hours / simHours; }
  return row;
}

const f1 = (x: number) => (Math.round(x * 10) / 10).toFixed(1);
function print(label: string, r: GcRow | null) {
  if (!r) { console.log(`${label}: no usable diag: mem samples (needs [MemDiag] with gcHeap, gcFree, gcN)`); return; }
  console.log(`${label}: ${f1(r.hours)} h, ${r.samples} samples, marks ${r.marks} (${f1(r.marksPerHour)}/h), heap ${f1(r.heapMbPerHour)} MB/h, live ${f1(r.liveFirst)} -> ${f1(r.liveLast)} MB (${f1(r.liveMbPerHour)} MB/h, ${r.liveFrom})`
    + (r.simHours ? `; per sim-hour: marks ${f1(r.marksPerSimHour!)}, live ${f1(r.liveMbPerSimHour!)} MB` : ''));
}

function selfCheck() {
  const line = (n: number, t: number, heap: number, free: number, gcN: number, ev?: string) =>
    `diag: mem #${n} mark${ev ? ` ev=${ev}` : ''} priv=0.0 gcHeap=${heap}? gcFree=${free}? gcN=${gcN}? t=${t}`;
  const text = ['ts3native 0.36.0 2026-11-15 00:00:00',
    line(1, 0, 300, 100, 10), line(2, 1800, 310, 100, 20, 'gc-before'), line(3, 1810, 310, 150, 21, 'gc-after'),
    line(4, 3600, 330, 100, 40), line(5, 5400, 340, 120, 50, 'gc-before'), line(6, 5410, 340, 150, 51, 'gc-after'), line(7, 7200, 350, 100, 70)].join('\n');
  const r = gcRate(parseLog(text).samples, 4)!;
  assert.equal(r.samples, 7);
  assert.equal(r.hours, 2);
  assert.equal(r.marks, 60);
  assert.equal(r.marksPerHour, 30);
  assert.equal(r.heapMbPerHour, 25);
  assert.equal(r.liveFrom, 'gc-after');
  assert.equal(r.liveFirst, 160);
  assert.equal(r.liveLast, 190);
  assert.ok(Math.abs(r.liveMbPerHour - 30 / ((5410 - 1810) / 3600)) < 1e-9);
  assert.equal(r.marksPerSimHour, 15);
  // no gc-after pair: every sample, flagged as an upper bound
  const r2 = gcRate(parseLog([line(1, 0, 300, 100, 1), line(2, 3600, 320, 100, 5)].join('\n')).samples)!;
  assert.equal(r2.liveFrom, 'all (upper bound)');
  assert.equal(r2.liveMbPerHour, 20);
  assert.equal(gcRate(parseLog(line(1, 0, 1, 1, 1)).samples), null);
  console.log('gc-rate self-check ok');
}

if (process.argv[1] && import.meta.url === new URL(`file://${process.argv[1]}`).href) {
  const argv = process.argv.slice(2);
  if (argv.includes('--self-check')) selfCheck();
  else {
    const si = argv.indexOf('--sim-hours');
    const sim = si >= 0 ? argv[si + 1].split(',').map(Number) : [];
    const logs = argv.filter((a, i) => !a.startsWith('--') && (si < 0 || i !== si + 1));
    if (logs.length < 1 || logs.length > 2) { console.error('usage: gc-rate.ts <log> [<on log>] [--sim-hours H[,H]] | --self-check'); process.exit(2); }
    const rows = logs.map((l, i) => gcRate(parseLog(readFileSync(l, 'latin1')).samples, sim[i]));
    rows.forEach((r, i) => print(logs.length === 2 ? (i === 0 ? 'off' : 'on') : 'run', r));
    if (rows.length === 2 && rows[0] && rows[1]) {
      const [a, b] = rows;
      console.log(`on - off: marks ${f1(b.marksPerHour - a.marksPerHour)}/h, live ${f1(b.liveMbPerHour - a.liveMbPerHour)} MB/h`
        + (a.marksPerSimHour != null && b.marksPerSimHour != null ? `, per sim-hour marks ${f1(b.marksPerSimHour - a.marksPerSimHour)}` : ''));
    }
  }
}
