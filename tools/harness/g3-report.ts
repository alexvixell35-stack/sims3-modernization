// G3 whole-stack cost (docs/276 §5.2, docs/222 §11.5) from a rewind-grouped ABBA queue (tools/harness/queues/g3a-rewind-1116.json).
// Reads only; never talks to the game.
//   node tools/harness/g3-report.ts probe-out/queue/<queue>-<date>.jsonl [...] [--json]
//   node tools/harness/g3-report.ts --self-check
// G3a (runtime part): jobs <world>-<name>-off-N / -on-N; pair N = the off and on job of the same N (ABBA puts them next to each
// other). A pair counts only when both rows ran from the same snapshot (snapshot epoch: a new one starts at every row whose
// start is not a rewind, i.e. the group's fresh load or a refused/failed rewind that reloaded), neither row is noise "high",
// and both left a seconds.csv. A rerun row (<id>-rerun, queue.ts noisyRerun) counts instead of its noisy original when it is
// in the same epoch as its partner. Per pair: on - off of iterations per wall second (primary: the sim runs flat out, a saving
// shows as more iterations, docs/222 §11.5 fixed-work note) and of sim ms/s (arm-ms.ts MS_COLS sum, secondary); mean, sd and
// n over the valid pairs per world. G3b (installed-only part, not switchable at runtime): counted calls x a measured unit cost
// from the on rows' /native dump: guard thunks (autshadow guardSim x GUARD_NS); the kAutLive pass-through hooks have no call
// counter yet and are listed as not counted.
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadRun, MS_COLS } from './arm-ms.ts';
import { GUARD_NS } from './mono-cost-report.ts';

// [measured, docs/139 CallBench table] one interpreted static call + return in EA's interpreter, stock entries, 14.34 ns: the unit of
// one pass-through frame (kAutLive B14 / push / enqueue / choose hooks); a hook with more arguments costs more [inferred lower bound]
export const FRAME_NS = 14.3;
export const PAIR_MIN = 6; // docs/276 §5.2: >= 6 valid pairs per world
const JOB_RE = /^(.*)-(off|on)-(\d+)(-rerun)?$/;
type Row = { job: string; profileTag?: string; start?: string; noise?: string; rerunOf?: string; nativeFile?: string; run?: any; verdict?: string[] };
type Metric = { itps: number; msps: number; secs: number };
export type Pair = { name: string; n: number; off?: Row; on?: Row; why: string; d?: { itps: number; msps: number } };

// snapshot epoch of each row (file order): a row that did not start from a rewind starts a new snapshot
export function epochs(rows: Row[]): number[] {
  let e = -1;
  return rows.map(r => (/^rewind \(/.test(r.start ?? '') ? e : ++e));
}
const isFail = (r: Row) => (r.verdict ?? []).some(v => /^FAIL/.test(v));
// pairs per <name> and N with the reason a pair does not count ('' = counts)
export function pairRows(rows: Row[], metric: (r: Row) => Metric | null): Pair[] {
  const ep = epochs(rows), at = new Map<Row, number>(rows.map((r, i) => [r, ep[i]]));
  const slots = new Map<string, { name: string; n: number; off: Row[]; on: Row[] }>();
  for (const r of rows) {
    const m = JOB_RE.exec(r.job ?? ''); if (!m) continue;
    const k = `${m[1]}#${m[3]}`, s = slots.get(k) ?? { name: m[1], n: Number(m[3]), off: [], on: [] };
    s[m[2] as 'off' | 'on'].push(r); slots.set(k, s);
  }
  const out: Pair[] = [];
  for (const s of slots.values()) {
    // a row counts when it is not noisy and not a FAIL; the newest counting row wins (a rerun after its original)
    const pick = (rs: Row[]) => rs.filter(r => r.noise !== 'high' && !isFail(r)).at(-1);
    const off = pick(s.off), on = pick(s.on);
    let why = '';
    if (!off || !on) why = `${!off ? 'off' : 'on'} arm missing, noisy or FAIL`;
    else if (at.get(off) !== at.get(on)) why = 'arms from different snapshots';
    const mOff = off && metric(off), mOn = on && metric(on);
    if (!why && (!mOff || !mOn)) why = 'no seconds.csv';
    out.push({ name: s.name, n: s.n, off, on, why, d: !why ? { itps: mOn!.itps - mOff!.itps, msps: mOn!.msps - mOff!.msps } : undefined });
  }
  return out.sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : a.n - b.n);
}
export function meanSd(xs: number[]): { mean: number; sd: number; n: number } {
  const n = xs.length, mean = n ? xs.reduce((s, x) => s + x, 0) / n : NaN;
  return { mean, sd: n > 1 ? Math.sqrt(xs.reduce((s, x) => s + (x - mean) ** 2, 0) / (n - 1)) : NaN, n };
}
const sg = (v: number, d = 1) => (Number.isNaN(v) ? 'n/a' : `${v >= 0 ? '+' : ''}${v.toFixed(d)}`);
export function g3aLines(pairs: Pair[]): string[] {
  const L: string[] = [];
  for (const name of [...new Set(pairs.map(p => p.name))]) {
    const ps = pairs.filter(p => p.name === name), ok = ps.filter(p => !p.why);
    const it = meanSd(ok.map(p => p.d!.itps)), ms = meanSd(ok.map(p => p.d!.msps));
    L.push(`${name}: ${ok.length} of ${ps.length} pairs valid${ok.length < PAIR_MIN ? ` (below ${PAIR_MIN}: no verdict)` : ''}; on - off it/s ${sg(it.mean, 2)} sd ${sg(it.sd, 2).replace('+', '')}, sim ms/s ${sg(ms.mean)} sd ${sg(ms.sd).replace('+', '')}`);
    for (const p of ps) L.push(`  pair ${p.n}: ${p.why ? `void (${p.why})` : `it/s ${sg(p.d!.itps, 2)}, ms/s ${sg(p.d!.msps)}`}`);
  }
  return L;
}
// G3b from one /native dump {before, after} over `secs` seconds of play
export function g3bLines(nat: { before?: any; after?: any } | undefined, secs: number): string[] {
  const d = (k: string) => { const a = nat?.after?.autshadow?.[k], b = nat?.before?.autshadow?.[k]; return typeof a === 'number' ? a - (typeof b === 'number' ? b : 0) : undefined; };
  const g = d('guardSim');
  return [
    `guard thunks (installed by [AutShadow] Guard): ${g === undefined ? 'n/a (no autshadow block)' : `${(g / secs / 1000).toFixed(1)}k calls/s x ${GUARD_NS} ns = ${(g * GUARD_NS / 1e6 / secs).toFixed(2)} ms/s`} [inferred: counted calls x selftest ns]`,
    `kAutLive pass-through frames (B14, push, enqueue, choose): not counted (no call counter; ${FRAME_NS} ns each, docs/139 CallBench) [open]`,
  ];
}

const jsonl = (f: string): Row[] => readFileSync(f, 'utf8').split('\n').filter(Boolean).flatMap(l => { try { return [JSON.parse(l)]; } catch { return []; } });
function metricOf(r: Row): Metric | null {
  if (!r.profileTag) return null;
  let arms; try { arms = loadRun(r.profileTag); } catch { return null; }
  const a = arms.all; if (!a?.secs) return null;
  return { itps: a.iters / a.secs, msps: MS_COLS.reduce((t, c) => t + a.ms[c] / a.secs, 0), secs: a.secs };
}

function selfCheck() {
  const R = (job: string, start: string, x: Partial<Row> = {}): Row => ({ job, start, profileTag: job, ...x });
  const rows = [R('bp-g3a-off-1', 'fresh'), R('bp-g3a-on-1', 'rewind (2100 ms)'), R('bp-g3a-on-2', 'rewind (2000 ms)', { noise: 'high' }),
    R('bp-g3a-off-2', 'rewind (2050 ms)'), R('bp-g3a-off-3', 'rewind refused -> fresh (the bridge server refused op 3)'), R('bp-g3a-on-3', 'rewind (1900 ms)'),
    R('bp-g3a-on-4', 'rewind (2000 ms)'), R('bp-g3a-off-4', 'rewind (2000 ms)', { verdict: ['FAIL: script errors 1'] }),
    R('ip-g3a-off-1', 'fresh'), R('ip-g3a-on-1', 'rewind (2500 ms)'), R('bp-g3a-on-2-rerun', 'fresh', { rerunOf: 'bp-g3a-on-2' }),
    R('ip-g3a-on-5', 'rewind (2500 ms)'), R('ip-g3a-off-5', 'rewind (2500 ms)')];
  assert.deepEqual(epochs(rows), [0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 3]);
  const met: Record<string, Metric> = { 'bp-g3a-off-1': { itps: 50, msps: 200, secs: 180 }, 'bp-g3a-on-1': { itps: 51, msps: 198, secs: 180 },
    'bp-g3a-off-3': { itps: 49, msps: 201, secs: 180 }, 'bp-g3a-on-3': { itps: 50.5, msps: 199, secs: 180 }, 'ip-g3a-off-1': { itps: 30, msps: 300, secs: 180 },
    'bp-g3a-off-2': { itps: 50, msps: 200, secs: 180 }, 'bp-g3a-on-2-rerun': { itps: 52, msps: 190, secs: 180 }, 'ip-g3a-on-5': { itps: 31, msps: 299, secs: 180 }, 'ip-g3a-off-5': { itps: 30, msps: 300, secs: 180 } };
  const ps = pairRows(rows, r => met[r.job] ?? null), why = Object.fromEntries(ps.map(p => [`${p.name}#${p.n}`, p.why]));
  assert.deepEqual(why, { 'bp-g3a#1': '', 'bp-g3a#2': 'arms from different snapshots', 'bp-g3a#3': '', 'bp-g3a#4': 'off arm missing, noisy or FAIL',
    'ip-g3a#1': 'no seconds.csv', 'ip-g3a#5': '' }, JSON.stringify(why));
  const p1 = ps.find(p => p.name === 'bp-g3a' && p.n === 1)!;
  assert.equal(p1.d!.itps, 1); assert.equal(p1.d!.msps, -2);
  // the noisy on-2 is replaced by its rerun, which loaded fresh (epoch 3): not the off-2 snapshot (epoch 0), so the pair is void
  assert.equal(ps.find(p => p.name === 'bp-g3a' && p.n === 2)!.on!.job, 'bp-g3a-on-2-rerun');
  const m = meanSd([1, 1.5]); assert.equal(m.mean, 1.25); assert.ok(Math.abs(m.sd - 0.35355) < 1e-4); assert.ok(Number.isNaN(meanSd([2]).sd));
  const L = g3aLines(ps);
  assert.equal(L[0], 'bp-g3a: 2 of 4 pairs valid (below 6: no verdict); on - off it/s +1.25 sd 0.35, sim ms/s -2.0 sd 0.0');
  assert.equal(L[2], '  pair 2: void (arms from different snapshots)');
  assert.ok(L.includes('ip-g3a: 1 of 2 pairs valid (below 6: no verdict); on - off it/s +1.00 sd n/a, sim ms/s -1.0 sd n/a'), L.join('\n'));
  // G3b on the 11-16 pilot numbers (bp-rewind-pilot-a: guardSim 33,785,053 over a 5 min profile): ~1.9 ms/s, docs/222's 1.5-2.1
  const b = g3bLines({ before: { autshadow: { guardSim: 0 } }, after: { autshadow: { guardSim: 33785053 } } }, 300);
  assert.equal(b[0], 'guard thunks (installed by [AutShadow] Guard): 112.6k calls/s x 17 ns = 1.91 ms/s [inferred: counted calls x selftest ns]');
  assert.match(g3bLines(undefined, 300)[0], /n\/a \(no autshadow block\)/);
  assert.equal(JOB_RE.exec('bp-g3a-on-12-rerun')?.[3], '12'); assert.equal(JOB_RE.exec('bp-g3a-office'), null);
  console.log('g3-report self-check ok');
}

if (resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.includes('--self-check')) selfCheck();
  else {
    const files = args.filter(a => !a.startsWith('--'));
    if (!files.length || files.some(f => !existsSync(f))) { console.error('usage: node tools/harness/g3-report.ts <queue results .jsonl> [...] [--json] | --self-check'); process.exit(1); }
    const rows = files.flatMap(jsonl).filter(r => r.job);
    const pairs = pairRows(rows, metricOf);
    if (args.includes('--json')) { console.log(JSON.stringify(pairs.map(p => ({ name: p.name, n: p.n, why: p.why, d: p.d })), null, 1)); process.exit(0); }
    console.log('G3a, runtime part (paired on - off, same snapshot):'); for (const l of g3aLines(pairs)) console.log(`- ${l}`);
    console.log('G3b, installed-only part (the last on row with a /native dump per world):');
    for (const name of [...new Set(pairs.map(p => p.name))]) {
      const on = pairs.filter(p => p.name === name && p.on?.nativeFile && existsSync(p.on.nativeFile)).map(p => p.on!);
      const r = on.at(-1);
      if (!r) { console.log(`- ${name}: no /native dump`); continue; }
      const secs = 60 * Number(r.run?.profile?.minutes ?? 4); // shortcut: profile minutes as the counter window (/native has no clock), as mono-cost-report.ts
      for (const l of g3bLines(JSON.parse(readFileSync(r.nativeFile!, 'utf8')), secs)) console.log(`- ${name} (${r.job}): ${l}`);
    }
  }
}
