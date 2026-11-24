// docs/330 our-loop cost per A/B arm: reads <tag>-ab-fixes.jsonl (profile-world.ts --ab deadrefcadence,viewerpushdelta: one
// `patches` snapshot at every slice switch, state and counters at the end of the slice before) and prints, per arm (off = old
// cadence, on = new), the ms per wall second of deadrefsweep (deadrefsweep_total_ms) and of the viewer push (viewerpush_ms),
// plus calls, items and sends per second. Slice 0 is warm-up (not counted). Counters are cumulative ints, so deltas per slice.
//   node tools/harness/loopcost-report.ts [--arm sw1,sw2] <probe-out/profile/<tag>-ab-fixes.jsonl> [...]
//   --arm: the switches that name the arm (all must agree; default deadrefcadence,viewerpushdelta). docs/334 LC-1: --arm deadrefexact.
//   node tools/harness/loopcost-report.ts --self-check
import { readFileSync } from 'node:fs';

type Snap = { i: number; s: number; c: Record<string, number | boolean> };
export type ArmSum = { secs: number; slices: number; v: Record<string, number> };
const KEYS = ['deadrefsweep_total_ms', 'deadrefsweep_passes', 'deadrefsweep_slices', 'deadrefcadence_snapshots', 'deadrefcadence_desc_skipped',
  'deadrefcadence_sim_skipped', 'viewerpush_ms', 'viewerpush_wakes', 'viewerpush_table_sends', 'viewerpush_held_sends', 'viewerpush_actor_walks',
  'viewerpush_actors_walked', 'viewerpush_walks_skipped', 'deadrefexact_partitions', 'deadrefexact_shared_lists', 'deadrefexact_rereads', ...Array.from({ length: 15 }, (_, p) => `deadrefsweep_p${p + 1}_ms`)];

export function parse(text: string): Snap[] { return text.split(/\r?\n/).filter(l => l.trim()).map(l => JSON.parse(l) as Snap); }

// arm of the slice that ends at snapshot k = the switch state read in snapshot k (set at the previous switch)
export const DEFAULT_ARM = ['deadrefcadence', 'viewerpushdelta'];
export function perArm(snaps: Snap[], armKeys: string[] = DEFAULT_ARM): { on: ArmSum; off: ArmSum; problems: string[] } {
  const arms = { on: { secs: 0, slices: 0, v: {} as Record<string, number> }, off: { secs: 0, slices: 0, v: {} as Record<string, number> } };
  const problems: string[] = [];
  for (let k = 2; k < snaps.length; k++) {  // k = 1 closes the warm-up slice 0
    const a = snaps[k - 1], b = snaps[k];
    const st = armKeys.map(k => b.c[k]);
    if (st.some(v => typeof v !== 'boolean')) { problems.push(`snapshot ${b.i}: switch state missing (old agent?)`); continue; }
    if (st.some(v => v !== st[0])) { problems.push(`snapshot ${b.i}: ${armKeys.map((k, j) => `${k} ${st[j]}`).join(' but ')}`); continue; }
    const arm = st[0] ? arms.on : arms.off;
    arm.secs += b.s - a.s; arm.slices++;
    for (const key of KEYS) {
      const x = a.c[key], y = b.c[key];
      if (typeof x === 'number' && typeof y === 'number') arm.v[key] = (arm.v[key] ?? 0) + (y - x);
    }
  }
  if (!arms.on.slices || !arms.off.slices) problems.push(`counted slices: on ${arms.on.slices}, off ${arms.off.slices}`);
  return { ...arms, problems };
}

const rate = (a: ArmSum, k: string) => a.secs > 0 ? (a.v[k] ?? 0) / a.secs : NaN;
export function line(on: ArmSum, off: ArmSum): string {
  const f = (x: number, d = 2) => Number.isFinite(x) ? x.toFixed(d) : '-';
  const ms = (a: ArmSum) => rate(a, 'deadrefsweep_total_ms') + rate(a, 'viewerpush_ms');
  return [
    `ours ms/s off ${f(ms(off))} on ${f(ms(on))} (saved ${f(ms(off) - ms(on))})`,
    `deadrefsweep ms/s off ${f(rate(off, 'deadrefsweep_total_ms'))} on ${f(rate(on, 'deadrefsweep_total_ms'))}, passes/min off ${f(rate(off, 'deadrefsweep_passes') * 60)} on ${f(rate(on, 'deadrefsweep_passes') * 60)}`,
    `viewer push ms/s off ${f(rate(off, 'viewerpush_ms'))} on ${f(rate(on, 'viewerpush_ms'))}, table sends/s off ${f(rate(off, 'viewerpush_table_sends'))} on ${f(rate(on, 'viewerpush_table_sends'))}, Actors walks/s off ${f(rate(off, 'viewerpush_actor_walks'))} on ${f(rate(on, 'viewerpush_actor_walks'))}`,
    `phase ms/s on: ${Array.from({ length: 15 }, (_, p) => `p${p + 1} ${f(rate(on, `deadrefsweep_p${p + 1}_ms`))}`).join(' ')}`,
    `phase ms/s off: ${Array.from({ length: 15 }, (_, p) => `p${p + 1} ${f(rate(off, `deadrefsweep_p${p + 1}_ms`))}`).join(' ')}`,
    `deadrefexact partitions/min off ${f(rate(off, 'deadrefexact_partitions') * 60)} on ${f(rate(on, 'deadrefexact_partitions') * 60)}, shared lists/s on ${f(rate(on, 'deadrefexact_shared_lists'))}`,
    `slices on ${on.slices} (${f(on.secs, 0)} s), off ${off.slices} (${f(off.secs, 0)} s)`,
  ].join('\n');
}

function selfCheck(): void {
  const mk = (i: number, s: number, on: boolean, dr: number, vp: number) => ({ i, s, c: { deadrefcadence: on, viewerpushdelta: on, deadrefsweep_total_ms: dr, viewerpush_ms: vp } });
  // warm-up on (0-300 s), then off, on, on, off; the old cadence costs 20 + 10 ms/s, the new 5 + 2
  const snaps = [mk(0, 0, true, 0, 0), mk(1, 300, true, 1500, 600), mk(2, 600, false, 1500 + 6000, 600 + 3000),
    mk(3, 900, true, 7500 + 1500, 3600 + 600), mk(4, 1200, true, 9000 + 1500, 4200 + 600), mk(5, 1500, false, 10500 + 6000, 4800 + 3000)];
  const r = perArm(snaps);
  const ok = r.problems.length === 0 && r.off.slices === 2 && r.on.slices === 2 && Math.abs(rate(r.off, 'deadrefsweep_total_ms') - 20) < 1e-9
    && Math.abs(rate(r.on, 'viewerpush_ms') - 2) < 1e-9 && /saved 23\.00/.test(line(r.on, r.off));
  const bad = perArm([mk(0, 0, true, 0, 0), mk(1, 300, true, 1, 1), { i: 2, s: 600, c: { deadrefcadence: true, viewerpushdelta: false } }]);
  let ok2 = bad.problems.some(p => /viewerpushdelta false/.test(p)) && bad.problems.some(p => /counted slices/.test(p));
  // --arm deadrefexact: arm by that switch alone (cadence stays on in both arms)
  const ex = (i: number, s: number, on: boolean, dr: number) => ({ i, s, c: { deadrefcadence: true, viewerpushdelta: true, deadrefexact: on, deadrefsweep_total_ms: dr, deadrefexact_partitions: on ? i : 0 } });
  const r3 = perArm([ex(0, 0, false, 0), ex(1, 300, false, 300), ex(2, 600, true, 600), ex(3, 900, false, 1200)], ['deadrefexact']);
  const ok3 = r3.problems.length === 0 && r3.on.slices === 1 && r3.off.slices === 1 && Math.abs(rate(r3.on, 'deadrefsweep_total_ms') - 1) < 1e-9
    && Math.abs(rate(r3.off, 'deadrefsweep_total_ms') - 2) < 1e-9 && perArm([ex(0, 0, true, 0), ex(1, 300, true, 1), { i: 2, s: 600, c: {} }], ['deadrefexact']).problems.some(p => /missing/.test(p));
  ok2 = ok2 && ok3;
  console.log(ok && ok2 ? 'loopcost-report self-check: PASS' : 'loopcost-report self-check: FAIL');
  if (!(ok && ok2)) process.exit(1);
}

if (import.meta.main) {
  const args = process.argv.slice(2);
  if (args[0] === '--self-check' || !args.length) selfCheck();
  else {
    const ai = args.indexOf('--arm'), armKeys = ai >= 0 ? args.splice(ai, 2)[1].split(',') : DEFAULT_ARM;
    for (const f of args) {
      const r = perArm(parse(readFileSync(f, 'utf8')), armKeys);
      console.log(`== ${f} (arm: ${armKeys.join(',')})\n${line(r.on, r.off)}${r.problems.length ? `\nproblems: ${r.problems.join('; ')}` : ''}`);
    }
  }
}
