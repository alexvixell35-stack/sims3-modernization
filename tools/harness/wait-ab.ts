// Sim-thread waits on the async route job, split per A/B side, from the counter snapshots profile-world.ts --ab takes at
// every switch (<tag>-ab-native.jsonl, probe P0 blockKey/blockN/blockUs + async totals). Answers "where did the saved
// route time go" (PLAN item 6, ip-ar-ab 2026-10-18: plans moved off the sim thread, stutter unchanged).
// In-load repro loops (profile-world.ts --repro with a loop) write the same files: one slice per firing, slice 0 = lead-in.
//   node tools/harness/wait-ab.ts <tag> [...]      (tag as in probe-out/profile/<tag>-ab.json)
//   node tools/harness/wait-ab.ts --self-check
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const DIR = join(resolve(import.meta.dirname, '..', '..'), 'probe-out', 'profile');
type Snap = { i: number; s: number; c: Record<string, number> };
const LABEL: Record<number, string> = {
  0x2000003: 'destroy (closure #4)', 0x62EF7A: 'gate 0 transform', 0x62F782: 'gate 12 Contains', 0x62FB74: 'gate 12 (F-F)',
  0x631814: 'gate 12 front door (F-C)', 0x631F40: 'gate 12 (F-G)', 0x760BFF: 'gate 10 goal step', 0x833593: 'UpdateSimFootprint',
  0x85FFC7: 'plan tail', 0x85FE10: 'steering S2 site', 0x846D21: 'avoidance steering', 0x83156D: 'route-to-object check',
};
const TOTALS = ['asyncPumpUs', 'asyncUs', 'blockOther'];

// per side: summed counter deltas over the counted slices (slice 0 = warm-up) and their seconds
export function split(slices: { from: number; on: boolean }[], snaps: Snap[]) {
  const side = { on: { secs: 0, keys: new Map<number, number>(), tot: {} as Record<string, number> },
    off: { secs: 0, keys: new Map<number, number>(), tot: {} as Record<string, number> } };
  for (let i = 1; i < slices.length; i++) {
    const a = snaps.find(x => x.i === i), b = snaps.find(x => x.i === i + 1);
    if (!a || !b) continue;
    const sd = side[slices[i].on ? 'on' : 'off'];
    sd.secs += b.s - a.s;
    for (let k = 0; k < 32; k++) {
      const key = b.c[`blockKey_${k}`];
      if (!key) continue;
      const us = (b.c[`blockUs_${k}`] ?? 0) - (a.c[`blockUs_${k}`] ?? 0);
      sd.keys.set(key, (sd.keys.get(key) ?? 0) + us);
    }
    for (const t of TOTALS) sd.tot[t] = (sd.tot[t] ?? 0) + (b.c[t] ?? 0) - (a.c[t] ?? 0);
  }
  return side;
}

// a rerun with the same tag before profile-world truncated the file (2026-10-20) appended a second run: <tag>-ab.json holds
// only the last run's slices, so keep the snapshots from the last i = 0 on
export function lastRun(snaps: Snap[]) {
  const from = snaps.map(x => x.i).lastIndexOf(0);
  return from > 0 ? snaps.slice(from) : snaps;
}

// mean CPU clock per side from <tag>-machine.jsonl (machine-load.ts: pAvg, else perf; a row covers the ~30 s before t):
// the on and off slices of one run can sit at different clocks on a laptop (2026-11-03: ~5% apart), which moves every ms/s
export function clockSides(slices: { from: number; on: boolean }[], t0: number, rows: { t: number; perf?: number; pAvg?: number }[]) {
  const r = { on: [] as number[], off: [] as number[] };
  for (const m of rows) {
    const v = m.pAvg ?? m.perf, at = (m.t - 15000 - t0) / 1000;
    const i = slices.findLastIndex(x => x.from <= at);
    if (v == null || i < 1 || (i === slices.length - 1 && at > slices[i].from + (slices[1].from - slices[0].from))) continue;  // slice 0 = warm-up
    r[slices[i].on ? 'on' : 'off'].push(v);
  }
  const mean = (a: number[]) => a.length ? a.reduce((x, y) => x + y, 0) / a.length : null;
  return { on: mean(r.on), off: mean(r.off), n: [r.on.length, r.off.length] };
}

function report(tag: string) {
  const { slices, t0 } = JSON.parse(readFileSync(join(DIR, `${tag}-ab.json`), 'utf8'));
  const all: Snap[] = readFileSync(join(DIR, `${tag}-ab-native.jsonl`), 'utf8').trim().split('\n').map(l => JSON.parse(l));
  const snaps = lastRun(all);
  if (snaps.length < all.length) console.log(`${tag}: ${all.length - snaps.length} snapshots of an earlier run with the same tag skipped`);
  const s = split(slices, snaps);
  const msps = (us: number, secs: number) => (us / 1000 / (secs || 1)).toFixed(1);
  console.log(`${tag}: on ${s.on.secs.toFixed(0)} s, off ${s.off.secs.toFixed(0)} s (sim-thread wait ms/s, on vs off)`);
  const keys = [...new Set([...s.on.keys.keys(), ...s.off.keys.keys()])]
    .sort((x, y) => (s.on.keys.get(y) ?? 0) + (s.off.keys.get(y) ?? 0) - (s.on.keys.get(x) ?? 0) - (s.off.keys.get(x) ?? 0));
  let ton = 0, toff = 0;
  for (const k of keys) {
    const on = s.on.keys.get(k) ?? 0, off = s.off.keys.get(k) ?? 0;
    ton += on; toff += off;
    console.log(`  0x${k.toString(16).toUpperCase().padStart(7, '0')} ${(LABEL[k] ?? '').padEnd(26)} ${msps(on, s.on.secs).padStart(7)} ${msps(off, s.off.secs).padStart(7)}`);
  }
  console.log(`  ${'all keyed waits'.padEnd(34)} ${msps(ton, s.on.secs).padStart(7)} ${msps(toff, s.off.secs).padStart(7)}`);
  // waits whose key found no free slot (count only, no time): a side with many more of them lacks a key's time (docs/51 §218)
  const per = (n: number, secs: number) => (n / (secs || 1) * 60).toFixed(1);
  console.log(`  ${'untimed waits/min (blockOther)'.padEnd(34)} ${per(s.on.tot.blockOther ?? 0, s.on.secs).padStart(7)} ${per(s.off.tot.blockOther ?? 0, s.off.secs).padStart(7)}`);
  console.log(`  ${'pump (asyncPumpUs)'.padEnd(34)} ${msps(s.on.tot.asyncPumpUs ?? 0, s.on.secs).padStart(7)} ${msps(s.off.tot.asyncPumpUs ?? 0, s.off.secs).padStart(7)}`);
  console.log(`  ${'worker job time (asyncUs)'.padEnd(34)} ${msps(s.on.tot.asyncUs ?? 0, s.on.secs).padStart(7)} ${msps(s.off.tot.asyncUs ?? 0, s.off.secs).padStart(7)}`);
  const mf = join(DIR, `${tag}-machine.jsonl`);
  if (t0 && existsSync(mf)) {
    const k = clockSides(slices, t0, readFileSync(mf, 'utf8').trim().split('\n').map(l => JSON.parse(l)));
    const f = (v: number | null) => (v == null ? '-' : v.toFixed(0) + '%');
    console.log(`  ${'CPU clock (machine samples)'.padEnd(34)} ${f(k.on).padStart(7)} ${f(k.off).padStart(7)}   (${k.n[0]} / ${k.n[1]} samples)`);
  }
}

if (process.argv[2] === '--self-check') {
  const c = (us: number, pump: number) => ({ blockKey_0: 0x62EF7A, blockUs_0: us, asyncPumpUs: pump, asyncUs: 0, blockOther: 0 });
  const slices = [{ from: 0, on: true }, { from: 10, on: false }, { from: 20, on: true }];
  const snaps = [{ i: 1, s: 10, c: c(1000, 0) }, { i: 2, s: 20, c: c(3000, 500) }, { i: 3, s: 30, c: c(9000, 600) }];
  const s = split(slices, snaps);
  assert.equal(s.off.keys.get(0x62EF7A), 2000); assert.equal(s.on.keys.get(0x62EF7A), 6000);
  assert.equal(s.off.tot.asyncPumpUs, 500); assert.equal(s.on.secs, 10);
  const old = [{ i: 0, s: 0, c: c(0, 0) }, { i: 1, s: 10, c: c(5, 0) }];
  assert.deepEqual(lastRun([...old, { i: 0, s: 0, c: c(0, 0) }, ...snaps]), [{ i: 0, s: 0, c: c(0, 0) }, ...snaps]);
  assert.deepEqual(lastRun(snaps), snaps);
  const ck = clockSides([{ from: 0, on: true }, { from: 100, on: false }, { from: 200, on: true }], 1e6,
    [{ t: 1e6 + 50e3, perf: 90 }, { t: 1e6 + 145e3, pAvg: 150, perf: 1 }, { t: 1e6 + 175e3, perf: 130 }, { t: 1e6 + 245e3, perf: 120 }, { t: 1e6 + 900e3, perf: 5 }]);
  assert.deepEqual(ck, { on: 120, off: 140, n: [1, 2] });
  console.log('wait-ab self-check ok');
} else for (const t of process.argv.slice(2)) report(t);
