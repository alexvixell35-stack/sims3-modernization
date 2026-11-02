// Per-arm sim-thread cost of an in-load A/B run, in ms per wall second (round 9 A1: "ms/s per arm, not it/s"). Reads only.
//   node tools/harness/arm-ms.ts <tag or tag prefix> [...]     one table per run: arm, seconds kept, it/s, sim busy ms/s and the
//       sim-budget-report.ts buckets (script = Mono + ICs, route sync, lock / job waits, GC + fin, anim + loco + publish), routes/s, route fails/s;
//       then on - off per column with +- 2 standard errors over the 1-minute slices (the slices are the samples, not the seconds)
//   node tools/harness/arm-ms.ts --self-check
// Inputs: probe-out/profile/<tag>-ab.json (the --ab slices: from in s, on, warmup) and <tag>-<secs>-seconds.csv (the stutter
// profiler's per-second rows, t in ms; the column sums are ms spent inside that second). Rows dropped as in sim-budget-report.ts:
// the first 60 s, gaps (dt outside 0-5 s), paused seconds (iters 0), and the warmup slice. Row i (second i of the profile)
// belongs to the last slice whose `from` is <= i, as hitch-ab.ts rowArms.
import { readdirSync, readFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { split } from './sim-budget-report.ts';

const PROFILE = join(process.cwd(), 'probe-out', 'profile');
const SKIP_MS = 60_000;
type Row = Record<string, number>;
type Slice = { from: number; on: boolean; warmup?: boolean };
// the sim-budget-report.ts buckets (exclusive ms per row; steerShare 0: no managed samples in an A/B run, so the steering share
// of the lock waits stays in `waits`), plus the route counters
type Parts = ReturnType<typeof split>;
const COLS: [string, (r: Row, p: Parts, dt: number) => number][] = [
  ['sim busy ms/s', (_r, p, dt) => dt - p.other],
  ['script (Mono + ICs) ms/s', (_r, p) => p.script],
  ['route sync ms/s', (_r, p) => p.route],
  ['lock / job waits ms/s', (_r, p) => p.waits],
  ['GC + fin ms/s', (_r, p) => p.gc],
  ['anim + loco + publish ms/s', (_r, p) => p.anim],
  ['routes/s', r => r.routes],
  ['route fails/s', r => r.routeFails],
];

function parseCsv(text: string): Row[] {
  const lines = text.split(/\r?\n/).filter(l => l.trim());
  const h = lines[0].split(',');
  return lines.slice(1).map(l => { const f = l.split(','); const r: Row = {}; h.forEach((k, i) => { r[k] = Number(f[i] ?? 0) || 0; }); return r; });
}

// per slice: wall seconds kept and the column sums; per arm: the pooled rate and the slice rates (for the error bars)
export function armTable(rows: Row[], slices: Slice[]): { arms: Record<'on' | 'off', { secs: number; itps: number; rates: number[]; sliceRates: number[][] }>; delta: { name: string; d: number; se: number }[] } {
  const perSlice = slices.map(() => ({ wall: 0, iters: 0, sums: COLS.map(() => 0) }));
  let prev = NaN, t0 = rows.length ? rows[0].t : 0;
  rows.forEach((row, ri) => {
    const dt = row.t - prev; prev = row.t;
    if (!(dt > 0 && dt < 5000) || !row.iters || row.t - t0 < SKIP_MS) return;
    // a slice's `from` is seconds since the profile start = the row index (one row per second; hitch-ab.ts rowArms does the same)
    let si = -1; for (let i = 0; i < slices.length; i++) if (slices[i].from <= ri) si = i;
    if (si < 0 || slices[si].warmup) return;
    const s = perSlice[si], p = split(row, dt, 0); s.wall += dt; s.iters += row.iters; COLS.forEach(([, f], i) => { s.sums[i] += f(row, p, dt); });
  });
  const arms = { on: { secs: 0, itps: 0, rates: COLS.map(() => 0), sliceRates: COLS.map(() => [] as number[]) }, off: { secs: 0, itps: 0, rates: COLS.map(() => 0), sliceRates: COLS.map(() => [] as number[]) } };
  const tot = { on: { wall: 0, iters: 0, sums: COLS.map(() => 0) }, off: { wall: 0, iters: 0, sums: COLS.map(() => 0) } };
  slices.forEach((sl, i) => {
    const s = perSlice[i]; if (s.wall < 10_000) return; // a slice with under 10 s kept says nothing
    const a = sl.on ? 'on' : 'off'; tot[a].wall += s.wall; tot[a].iters += s.iters;
    COLS.forEach((_, c) => { tot[a].sums[c] += s.sums[c]; arms[a].sliceRates[c].push(s.sums[c] / (s.wall / 1000)); });
  });
  for (const a of ['on', 'off'] as const) {
    const w = tot[a].wall / 1000; arms[a].secs = w; arms[a].itps = w ? tot[a].iters / w : 0;
    arms[a].rates = COLS.map((_, c) => w ? tot[a].sums[c] / w : 0);
  }
  const se = (xs: number[]) => { if (xs.length < 2) return NaN; const m = xs.reduce((p, q) => p + q, 0) / xs.length; return Math.sqrt(xs.reduce((p, q) => p + (q - m) ** 2, 0) / (xs.length - 1) / xs.length); };
  const delta = COLS.map(([name], c) => ({ name, d: arms.on.rates[c] - arms.off.rates[c], se: Math.hypot(se(arms.on.sliceRates[c]), se(arms.off.sliceRates[c])) }));
  return { arms, delta };
}

function report(tag: string): string {
  const files = readdirSync(PROFILE).filter(f => f.startsWith(tag));
  const abf = files.find(f => f.endsWith('-ab.json')), secf = files.find(f => /-\d+-seconds\.csv$/.test(f));
  if (!abf || !secf) return `${tag}: ${abf ? '' : 'no -ab.json '}${secf ? '' : 'no -seconds.csv'}`;
  const ab = JSON.parse(readFileSync(join(PROFILE, abf), 'utf8')) as { names: string[]; slices: Slice[] };
  const { arms, delta } = armTable(parseCsv(readFileSync(join(PROFILE, secf), 'utf8')), ab.slices);
  const f1 = (x: number) => Number.isFinite(x) ? x.toFixed(1) : 'n/a';
  const L = [`### ${abf.replace(/-ab\.json$/, '')} (ab: ${ab.names.join(',')}; ${ab.slices.filter(s => !s.warmup).length} slices, warmup dropped)`, '',
    `| arm | s kept | it/s | ${COLS.map(([n]) => n).join(' | ')} |`, `|---|---|---|${COLS.map(() => '---').join('|')}|`];
  for (const a of ['off', 'on'] as const) L.push(`| ${a} | ${arms[a].secs.toFixed(0)} | ${f1(arms[a].itps)} | ${arms[a].rates.map(f1).join(' | ')} |`);
  L.push(`| on - off (+- 2 SE over slices) | | ${f1(arms.on.itps - arms.off.itps)} | ${delta.map(d => `${d.d >= 0 ? '+' : ''}${f1(d.d)} +- ${f1(2 * d.se)}`).join(' | ')} |`);
  return L.join('\n');
}

function selfCheck() {
  // 4 slices of 100 s: off, on, off, on; the on arm spends 200 ms/s of script, the off arm 100; one paused second and the first 60 s dropped
  const slices: Slice[] = [{ from: 0, on: false, warmup: true }, { from: 100, on: false }, { from: 200, on: true }, { from: 300, on: false }, { from: 400, on: true }];
  const rows: Row[] = [];
  for (let t = 0; t < 500; t++) { const on = t >= 200 && t < 300 || t >= 400; rows.push({ t: t * 1000, iters: t === 250 ? 0 : 100, script: on ? 200 : 100, taskObjects: 0, oneShot: 0, threaded: 0, route: 10, rtLock: 1, rtOtherLock: 0, rtJob: 2, rtPlanWait: 0, animSacs: 0, animPose: 5, locomotion: 5, publish: 0, gcMark: 3, gcForcedMs: 0, fin: 0, routes: on ? 4 : 2, routeFails: 0 }); }
  const { arms, delta } = armTable(rows, slices);
  const near = (a: number, b: number, eps = 0.5) => Math.abs(a - b) < eps;
  if (!near(arms.off.secs, 200) || !near(arms.on.secs, 199, 1.5)) throw new Error(`self-check: seconds kept ${arms.off.secs} / ${arms.on.secs}`);
  // script = sched - route - waits - gcForced = 100 - 10 - 3 - 0; busy = script + route + waits + gc + anim = 87 + 10 + 3 + 3 + 10
  if (!near(arms.off.rates[1], 87) || !near(arms.on.rates[1], 187) || !near(arms.off.rates[0], 113)) throw new Error(`self-check: ms/s ${arms.off.rates} / ${arms.on.rates}`);
  if (!near(delta[1].d, 100) || !near(delta[6].d, 2) || !near(arms.on.itps, 100, 1)) throw new Error(`self-check: delta ${JSON.stringify(delta)}`);
  if (!(delta[0].se < 1)) throw new Error(`self-check: SE ${delta[0].se}`);
  console.log('arm-ms self-check ok');
}

if (process.argv[1] && /arm-ms\.ts$/.test(process.argv[1])) {
  const args = process.argv.slice(2);
  if (args.includes('--self-check')) selfCheck();
  else if (!args.length) console.log('usage: node tools/harness/arm-ms.ts <tag or tag prefix> [...] | --self-check');
  else {
    if (!existsSync(PROFILE)) throw new Error(`no ${PROFILE}`);
    const tags = new Set<string>();
    for (const a of args) for (const f of readdirSync(PROFILE)) if (f.startsWith(a) && f.endsWith('-ab.json')) tags.add(f.replace(/-ab\.json$/, ''));
    if (!tags.size) console.log(`no <tag>-ab.json under ${PROFILE} for ${args.join(' ')}`);
    for (const t of [...tags].sort()) console.log(report(t) + '\n');
  }
}
