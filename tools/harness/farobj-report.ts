// docs/358 FarLight timing: lot pulse cost per A/B arm and distance band. Reads <tag>-ab-fixes.jsonl (profile-world.ts
// --ab farobjlight with farobj + farobjpulseprobe on in both arms: one `patches` snapshot at every slice switch, which
// is also the per-arm switch read-back: a slice whose snapshot does not show the three switches as expected is a problem) and
// prints, per arm, the ms per wall second of Lot.UpdateReactions bodies by docs/322 band (nearest viewer to the lot's
// edge: <50, 50-150, 150-400, 400-1000, >=1000 m), plus pulses skipped per second. The probe times every 16th body, so
// its sums are multiplied by 16. Slice 0 is warm-up. Counters are cumulative ints, so deltas per slice.
// Ramp: the records and their dwell stay through an off slice (the pass runs with farobj alone), so an on slice skips
// again from its first boundary pass; the FarLight lot count at the end of each slice is printed, and an on slice that
// ends with none is a problem.
//   node tools/harness/farobj-report.ts <probe-out/profile/<tag>-ab-fixes.jsonl> [...]
//   node tools/harness/farobj-report.ts --self-check
import { readFileSync } from 'node:fs';
import { parse } from './loopcost-report.ts';

type Snap = { i: number; s: number; c: Record<string, number | boolean> };
export type Arm = { secs: number; slices: number; v: Record<string, number> };
export const BANDS = ['<50 m', '50-150 m', '150-400 m', '400-1000 m', '>=1000 m'];
const SAMPLE = 16;
const KEYS = [...BANDS.flatMap((_, b) => [`farobjProbeUsB${b}`, `farobjProbeNB${b}`]), 'farobjProbeYielded', 'farobjSkipReact', 'farobjSkipZombie',
  'farobjSkipDoor', 'farobjLight', 'farobjGoNear', 'farobjCatchUp', 'farobjHhPulseSkip', 'farobjErrors'];

// arm of the slice that ends at snapshot k = farobjlight as read in snapshot k
export function perArm(snaps: Snap[]): { on: Arm; off: Arm; problems: string[]; slices: string[] } {
  const arms = { on: { secs: 0, slices: 0, v: {} as Record<string, number> }, off: { secs: 0, slices: 0, v: {} as Record<string, number> } };
  const problems: string[] = [], slices: string[] = [];
  for (let k = 2; k < snaps.length; k++) {  // k = 1 closes the warm-up slice 0
    const a = snaps[k - 1], b = snaps[k], st = b.c.farobjlight;
    if (typeof st !== 'boolean') { problems.push(`snapshot ${b.i}: farobjlight state missing (old agent?)`); continue; }
    if (b.c.farobj !== true || b.c.farobjpulseprobe !== true) { problems.push(`snapshot ${b.i}: farobj / farobjpulseprobe not on`); continue; }
    const lightLots = b.c.farobjByState2;
    slices.push(`slice ${k - 1}: ${Math.round(a.s)}-${Math.round(b.s)} s, farobj true, farobjpulseprobe true, farobjlight ${st}, FarLight lots at its end ${typeof lightLots === 'number' ? lightLots : '?'}`);
    if (st && lightLots === 0) problems.push(`snapshot ${b.i}: on slice ended with no FarLight lot (ramp: records lost or dwell not met)`);
    const arm = st ? arms.on : arms.off;
    arm.secs += b.s - a.s; arm.slices++;
    for (const key of KEYS) {
      const x = a.c[key], y = b.c[key];
      if (typeof x === 'number' && typeof y === 'number') arm.v[key] = (arm.v[key] ?? 0) + (y - x);
    }
  }
  if (!arms.on.slices || !arms.off.slices) problems.push(`counted slices: on ${arms.on.slices}, off ${arms.off.slices}`);
  return { ...arms, problems, slices };
}

export const bandMs = (a: Arm, b: number) => a.secs > 0 ? (a.v[`farobjProbeUsB${b}`] ?? 0) * SAMPLE / 1000 / a.secs : NaN;
export const totalMs = (a: Arm) => BANDS.reduce((s, _, b) => s + bandMs(a, b), 0);
export function lines(on: Arm, off: Arm): string {
  const f = (x: number, d = 2) => Number.isFinite(x) ? x.toFixed(d) : '-';
  const per = (a: Arm, k: string) => a.secs > 0 ? (a.v[k] ?? 0) / a.secs : NaN;
  return [
    `lot reaction pulses ms/s off ${f(totalMs(off))} on ${f(totalMs(on))} (saved ${f(totalMs(off) - totalMs(on))})`,
    ...BANDS.map((n, b) => `  band ${n}: off ${f(bandMs(off, b))} on ${f(bandMs(on, b))} ms/s, timed bodies/s off ${f(per(off, `farobjProbeNB${b}`))} on ${f(per(on, `farobjProbeNB${b}`))}`),
    `skipped per s (on arm): reactions ${f(per(on, 'farobjSkipReact'))}, zombies ${f(per(on, 'farobjSkipZombie'))}, door ${f(per(on, 'farobjSkipDoor'))}; off arm ${f(per(off, 'farobjSkipReact') + per(off, 'farobjSkipZombie') + per(off, 'farobjSkipDoor'))} (must be 0.00)`,
    `timed bodies that yielded (not counted): off ${off.v.farobjProbeYielded ?? 0}, on ${on.v.farobjProbeYielded ?? 0}; household skips ${(on.v.farobjHhPulseSkip ?? 0) + (off.v.farobjHhPulseSkip ?? 0)} (must be 0), errors ${(on.v.farobjErrors ?? 0) + (off.v.farobjErrors ?? 0)}`,
    `slices on ${on.slices} (${f(on.secs, 0)} s), off ${off.slices} (${f(off.secs, 0)} s)`,
  ].join('\n');
}

function selfCheck(): void {
  // far band costs 8 ms/s with the skip off and 0 with it on; the near band 4 ms/s in both; 300 s slices: warm-up, off, on, on, off
  let far = 0, near = 0, skip = 0;
  const mk = (i: number, s: number, light: boolean) => {
    if (i > 0) { near += 4 * 300 * 1000 / SAMPLE; if (!light) far += 8 * 300 * 1000 / SAMPLE; else skip += 3000; }
    return { i, s, c: { farobj: true, farobjpulseprobe: true, farobjlight: light, farobjByState2: 40, farobjProbeUsB0: near, farobjProbeUsB3: far, farobjProbeNB3: far / 50, farobjSkipReact: skip } };
  };
  const snaps = [mk(0, 0, false), mk(1, 300, false), mk(2, 600, false), mk(3, 900, true), mk(4, 1200, true), mk(5, 1500, false)];
  const r = perArm(parse(snaps.map(x => JSON.stringify(x)).join('\n')) as Snap[]);
  const near0 = (a: Arm) => Math.abs(bandMs(a, 0) - 4) < 1e-9;
  const ok = r.problems.length === 0 && r.on.slices === 2 && r.off.slices === 2 && near0(r.on) && near0(r.off) && Math.abs(bandMs(r.off, 3) - 8) < 1e-9
    && bandMs(r.on, 3) === 0 && /saved 8\.00/.test(lines(r.on, r.off)) && /reactions 10\.00/.test(lines(r.on, r.off));
  const bad = perArm([mk(0, 0, false), mk(1, 300, false), { i: 2, s: 600, c: { farobj: true, farobjpulseprobe: false, farobjlight: true } }, { i: 3, s: 900, c: {} }] as Snap[]);
  const ramp = perArm([mk(0, 0, false), mk(1, 300, false), { ...mk(2, 600, true), c: { ...mk(2, 600, true).c, farobjByState2: 0 } }, mk(3, 900, false)] as Snap[]);
  const ok3 = ramp.problems.some(p => /no FarLight lot/.test(p)) && r.slices.length === 4 && /^slice 1: 300-600 s, .*farobjlight false, FarLight lots at its end 40$/.test(r.slices[0]);
  const ok2 = ok3 && bad.problems.some(p => /not on/.test(p)) && bad.problems.some(p => /state missing/.test(p)) && bad.problems.some(p => /counted slices/.test(p));
  console.log(ok && ok2 ? 'farobj-report self-check: PASS' : 'farobj-report self-check: FAIL');
  if (!(ok && ok2)) process.exit(1);
}

if (import.meta.main) {
  const args = process.argv.slice(2);
  if (args.includes('--self-check')) selfCheck();
  else if (!args.length) { console.error('usage: node tools/harness/farobj-report.ts <tag>-ab-fixes.jsonl [...] | --self-check'); process.exit(2); }
  else for (const f of args) {
    const r = perArm(parse(readFileSync(f, 'utf8')) as Snap[]);
    console.log(`${f}\n${r.slices.join('\n')}\n${lines(r.on, r.off)}${r.problems.length ? `\nPROBLEMS: ${r.problems.join('; ')}` : ''}`);
    if (r.problems.length) process.exitCode = 1;
  }
}
