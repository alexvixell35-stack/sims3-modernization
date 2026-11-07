// Household route fails per --ab slice (issue #304, queue kd-household-ab.json). Reads only; never talks to the game.
//   node tools/harness/kd-household-ab.ts <tag> <job's ts3native.log> [--smoke]     --self-check
// Reads probe-out/profile/<tag>-ab.json, <tag>-ab-fixes.jsonl, <tag>-ab-native.jsonl. "routefail:" lines have no time stamp, but
// routefaillog_routefail counts exactly those lines and household lines are never dropped, so the counter at each switch splits the log.
// Per slice: fails, distinct (Sim, interaction, goal) = repeats of one loop folded, fails with a KindDeadlines cut on the line,
// household plans, fails per plan, top Sim / lot share. Pairs: ABBA slices 1-2, 3-4, ...; the slice is the unit (one failed intent
// gives several lines and loops span seconds, so counts are not Poisson): mean paired difference, exact sign-flip p, off-first and
// on-first means. --smoke: exit 1 unless the arm split is proven (both arms, cuts only in on slices, lines match the counter, plans seen).
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = process.env.TS3_REPO ?? join(import.meta.dirname, '..', '..');
type Snap = { i: number; c: Record<string, number> };
type Sl = { on: boolean; warmup?: boolean };
export type Row = { i: number; arm: string; fails: number; distinct: number; cutLines: number; kdCut: number; plans: number; perPlan: number | null; topSim: number; topLot: number };

const share = (m: Map<string, number>, n: number) => (n ? Math.max(0, ...m.values()) / n : 0);
const bump = (m: Map<string, number>, k: string) => m.set(k, (m.get(k) ?? 0) + 1);

// a routefail: line's fields; null = not one (routefail job / retry / summary lines are not counted by [687])
export function parseLine(l: string) {
  const m = l.match(/routefail: (.*?) (0x[0-9A-F]+) \S+(?: active)?, ([^,]*), .*?, goal ([^,]+,[^,]+,[^,]+), at .*?, lot (0x[0-9A-F]+)/);
  return m ? { sim: m[2], ia: m[3], goal: m[4], lot: m[5], cut: /cut \(KindDeadlines/.test(l) } : null;
}

export function sliceRows(lines: string[], slices: Sl[], fixes: Snap[], native: Snap[]): Row[] {
  const parsed = lines.map(parseLine).filter(p => p !== null);
  const at = (i: number) => fixes.find(s => s.i === i)?.c;
  return slices.map((sl, i) => {  // the counter starts at 0 with the game, like the log: its value is the line's index, also past lines from before the window
    const from = at(i)?.routefaillog_routefail ?? 0, nx = at(i + 1), to = nx ? nx.routefaillog_routefail : parsed.length;
    const part = parsed.slice(from, to), keys = new Set<string>(), sims = new Map<string, number>(), lots = new Map<string, number>();
    for (const p of part) { keys.add(`${p.sim}|${p.ia}|${p.goal}`); bump(sims, p.sim); bump(lots, p.lot); }
    const d = (c0?: Record<string, number>, c1?: Record<string, number>, k = '') => c0 && c1 && k in c1 ? c1[k] - (c0[k] ?? 0) : 0;
    const plans = d(at(i), nx, 'routefaillog_household_plans');
    const n0 = native.find(s => s.i === i)?.c, n1 = native.find(s => s.i === i + 1)?.c;
    return { i, arm: sl.warmup ? 'warm' : sl.on ? 'on' : 'off', fails: part.length, distinct: keys.size, cutLines: part.filter(p => p.cut).length,
      kdCut: d(n0, n1, 'kdCut_0'), plans, perPlan: plans ? part.length / plans : null, topSim: share(sims, part.length), topLot: share(lots, part.length) };
  });
}

// exact sign-flip p (two-sided) of the mean difference
export function signFlip(d: number[]): number {
  const obs = Math.abs(d.reduce((a, b) => a + b, 0)); let hit = 0;
  const n = 1 << d.length;
  for (let k = 0; k < n; k++) { let t = 0; d.forEach((v, i) => { t += k >> i & 1 ? v : -v; }); if (Math.abs(t) >= obs - 1e-9) hit++; }
  return hit / n;
}
// counted slices in pairs (1,2), (3,4), ...: the last slice without a partner, or either side missing, is left out
export function pairs(rows: Row[], val: (r: Row) => number | null) {
  const out: { on: number; off: number; onFirst: boolean }[] = [], c = rows.filter(r => r.arm !== 'warm');
  for (let i = 0; i + 1 < c.length; i += 2) {
    const [a, b] = [c[i], c[i + 1]], on = a.arm === 'on' ? a : b, off = a.arm === 'on' ? b : a, x = val(on), y = val(off);
    if (a.arm !== b.arm && x !== null && y !== null) out.push({ on: x, off: y, onFirst: a.arm === 'on' });
  }
  return out;
}
const mean = (a: number[]) => a.length ? a.reduce((s, v) => s + v, 0) / a.length : NaN;
export function summary(name: string, rows: Row[], val: (r: Row) => number | null): string {
  const p = pairs(rows, val), d = p.map(x => x.on - x.off), sumOn = p.reduce((s, x) => s + x.on, 0), sumOff = p.reduce((s, x) => s + x.off, 0);
  if (!p.length) return `${name}: no pairs`;
  const of = p.filter(x => !x.onFirst).map(x => x.on - x.off), nf = p.filter(x => x.onFirst).map(x => x.on - x.off);
  return `${name}: ${p.length} pairs, on ${+sumOn.toFixed(3)} off ${+sumOff.toFixed(3)} (${sumOff ? `${((sumOn / sumOff - 1) * 100).toFixed(1)}%` : 'n/a'}), on higher in ${d.filter(v => v > 0).length}, mean diff ${mean(d).toFixed(3)}, sign-flip p ${signFlip(d).toFixed(3)}, off-first mean ${mean(of).toFixed(3)} on-first mean ${mean(nf).toFixed(3)}`;
}
// the proof the smoke job must give
export function smokeProblems(rows: Row[], linesSeen: number, counterDelta: number): string[] {
  const bad: string[] = [], on = rows.filter(r => r.arm === 'on'), off = rows.filter(r => r.arm === 'off');
  if (on.length < 2 || off.length < 2) bad.push(`arms: on ${on.length} off ${off.length} slices`);
  const cOn = on.reduce((s, r) => s + r.kdCut, 0), cOff = off.reduce((s, r) => s + r.kdCut, 0);
  if (!cOn) bad.push('no kdCut_0 in on slices: the arm split is not proven');
  if (cOff > cOn * 0.02) bad.push(`kdCut_0 off ${cOff} vs on ${cOn}: the switch does not split`);
  if (linesSeen !== counterDelta) bad.push(`routefail lines ${linesSeen} != counter ${counterDelta}: the log cannot be split`);
  if (!rows.some(r => r.plans > 0)) bad.push('no routefaillog_household_plans: ts3fixes / ts3agent not from this branch');
  return bad;
}

function selfCheck() {
  const l = (s: string, g: string, cut = false) => `ts3fixes: routefail: A B ${s} Adult active, Fridge_EatLeftovers, Plan async FailedBecauseNoPathToDestination blocker None, goal ${g}, at 1.0,2.0,3.0, lot 0x10 room 1, 250 ms, seen, route 0000ABCD${cut ? ', cut (KindDeadlines, mask 2)' : ''}`;
  assert.deepEqual(parseLine(l('0x1', '4.0,5.0,6.0')), { sim: '0x1', ia: 'Fridge_EatLeftovers', goal: '4.0,5.0,6.0', lot: '0x10', cut: false });
  assert.equal(parseLine('ts3fixes: routefail job: route 1, cut (KindDeadlines, mask 2)'), null);
  const slices: Sl[] = [{ on: true, warmup: true }, { on: false }, { on: true }, { on: true }, { on: false }];
  const lines = [l('0x1', '1,1,1'), l('0x1', '1,1,1'), l('0x2', '2,2,2'), l('0x1', '3,3,3', true), l('0x1', '3,3,3', true), l('0x2', '4,4,4'), l('0x2', '5,5,5'), 'junk', l('0x3', '6,6,6')];
  const fixes = [0, 1, 2, 3, 4].map((i, k) => ({ i, c: { routefaillog_routefail: [0, 2, 3, 5, 8][k], routefaillog_household_plans: 10 * k } }));
  const native = [0, 1, 2, 3, 4, 5].map((i, k) => ({ i, c: { kdCut_0: [0, 3, 3, 8, 12, 12][k] } }));
  const r = sliceRows(lines, slices, fixes, native);
  assert.deepEqual(r.map(x => x.fails), [2, 1, 2, 3, 0]);
  assert.deepEqual(sliceRows(['x', l('0x9', '9,9,9'), ...lines], slices, fixes.map(s => ({ i: s.i, c: { ...s.c, routefaillog_routefail: s.c.routefaillog_routefail + 1 } })), native).map(x => x.fails), [2, 1, 2, 3, 0], 'lines from before the window');
  assert.deepEqual([r[0].distinct, r[2].distinct, r[2].cutLines, r[1].arm, r[2].plans, r[2].perPlan, r[1].kdCut, r[2].kdCut], [1, 1, 2, 'off', 10, 0.2, 0, 5]);
  assert.equal(signFlip([1, 1, 1]), 0.25);
  const p = pairs(r, x => x.fails);
  assert.deepEqual(p, [{ on: 2, off: 1, onFirst: false }, { on: 3, off: 0, onFirst: true }]);
  assert.ok(summary('fails', r, x => x.fails).includes('2 pairs, on 5 off 1'));
  assert.deepEqual(smokeProblems(r, 8, 8), []);
  assert.equal(smokeProblems(r, 7, 8).length, 1);
  assert.equal(smokeProblems(r.map(x => ({ ...x, kdCut: 0 })), 8, 8).length, 1);
  assert.equal(smokeProblems(r.map(x => ({ ...x, kdCut: 9 })), 8, 8).length, 1);
  assert.equal(smokeProblems(r.map(x => ({ ...x, plans: 0 })), 8, 8).length, 1);
  // Dalexlol's 9 pairs, issue #304
  assert.equal(signFlip([-38, 237, 51, 69, 137, 44, 66, 108, 102]).toFixed(4), '0.0078');
  console.log('kd-household-ab self-check: PASS');
}

if (resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.includes('--self-check')) selfCheck();
  else {
    const [tag, log] = args.filter(a => !a.startsWith('--'));
    if (!tag || !log) { console.error('usage: node tools/harness/kd-household-ab.ts <tag> <ts3native.log> [--smoke] | --self-check'); process.exit(2); }
    const pre = join(ROOT, 'probe-out', 'profile', tag);
    const jsonl = (f: string): Snap[] => readFileSync(f, 'utf8').split('\n').filter(Boolean).map(l => JSON.parse(l));
    const slices: Sl[] = JSON.parse(readFileSync(`${pre}-ab.json`, 'utf8')).slices;
    const fixes = jsonl(`${pre}-ab-fixes.jsonl`), lines = readFileSync(log, 'latin1').split('\n');
    const rows = sliceRows(lines, slices, fixes, jsonl(`${pre}-ab-native.jsonl`));
    console.log('| slice | arm | fails | distinct | fails with cut | kdCut_0 | household plans | fails/plan | top Sim | top lot |\n|---|---|---|---|---|---|---|---|---|---|');
    for (const r of rows) console.log(`| ${r.i} | ${r.arm} | ${r.fails} | ${r.distinct} | ${r.cutLines} | ${r.kdCut} | ${r.plans} | ${r.perPlan?.toFixed(4) ?? '?'} | ${(r.topSim * 100).toFixed(0)}% | ${(r.topLot * 100).toFixed(0)}% |`);
    console.log(summary('fails', rows, r => r.fails));
    console.log(summary('distinct', rows, r => r.distinct));
    console.log(summary('fails/plan', rows, r => r.perPlan));
    if (args.includes('--smoke')) {
      // the last slice has no end snapshot: the log must hold at least what the last snapshot counted
      const seen = lines.filter(l => parseLine(l)).length, counted = fixes.at(-1)?.c.routefaillog_routefail ?? 0;
      const bad = smokeProblems(rows, Math.min(seen, counted), counted);
      console.log(bad.length ? `FAIL: ${bad.join('; ')}` : 'PASS: arm split proven');
      if (bad.length) process.exit(1);
    }
  }
}
