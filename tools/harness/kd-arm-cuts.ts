// KindDeadlines cuts per KdExemptSeen arm, from ts3native.log (docs/249, household-scope proof):
//   node tools/harness/kd-arm-cuts.ts <ts3native.log> [--expect-on-zero] [--ceil-ms N] [--cam]     --self-check
// --cam: KdCutCamRecheck per arm from "KdCutCamRecheck switch now N" and "kd cam recheck:" lines; exit 1 on a hold in an off arm, a would-hold in an on arm, or an arm with none.
// The arm comes from the "KdExemptSeen switch now N" line (logged at the first job after a flip); a cut line is a
// "routefail job: ... cut (KindDeadlines" line, "seen" = its trailing flag, household = a follower a "gohere start" line named as an
// active-household Sim. --expect-on-zero: exit 1 when an on arm (mode 1) has a cut of a seen or household Sim (the household rule).
// A "cut (KdSeenCeilMs" line is a ceiling cut (docs/197 §7): one with deadline >= --ceil-ms (default 10000, the household ceiling) is allowed
// for anyone (key .../ceil), one below it (key .../ceil-low) only for a line flagged "seen npc" or "unseen", never for a household follower.
import { readFileSync } from 'node:fs';
import assert from 'node:assert/strict';

export type ArmCuts = Record<string, Record<string, number>>;
export function armCuts(lines: string[], ceilMs = 10000): { flips: number[]; arms: ArmCuts } {
  const house = new Set<string>(), arms: ArmCuts = { '0': {}, '1': {} }, flips: number[] = [];
  for (const l of lines) { const m = l.match(/gohere start: .* follower ([0-9A-F]+), household \S+ active/); if (m) house.add(m[1]); }
  let mode = -1;
  for (const l of lines) {
    const f = l.match(/KdExemptSeen switch now (\d)/);
    if (f) { mode = Number(f[1]); flips.push(mode); continue; }
    const cut = l.match(/routefail job:.*cut \((KindDeadlines|KdSeenCeilMs)/);
    if (mode < 0 || !cut) continue;
    const id = l.match(/follower ([0-9A-F]+)/)?.[1] ?? '';
    const scope = l.trim().match(/, (seen npc|seen|unseen)$/)?.[1].replace(' ', '') ?? 'notseen';
    const tail = cut[1] === 'KindDeadlines' ? '' : Number(l.match(/deadline (\d+) ms/)?.[1] ?? 0) < ceilMs ? '/ceil-low' : '/ceil';
    const key = `${house.has(id) ? 'household' : 'other'}/${scope}${tail}`;
    arms[mode][key] = (arms[mode][key] ?? 0) + 1;
  }
  return { flips, arms };
}
// the household rule: with the switch on, no time cut of a seen or household Sim; ceilings as in the header
const violates = (k: string): boolean => {
  if (k.endsWith('/ceil')) return false;
  if (k.endsWith('/ceil-low')) return k.startsWith('household/') || !/\/(seennpc|unseen)\//.test(k);
  return k.startsWith('household/') || k.endsWith('/seen') || k.endsWith('/seennpc');
};
export const onArmViolations = (a: ArmCuts): number => Object.entries(a['1']).filter(([k]) => violates(k)).reduce((n, [, v]) => n + v, 0);

export function camArms(lines: string[]): Record<string, Record<string, number>> {
  const a: Record<string, Record<string, number>> = { '0': {}, '1': {} };
  let mode = -1;
  for (const l of lines) {
    const f = l.match(/KdCutCamRecheck switch now (\d)/);
    if (f) { mode = Number(f[1]); continue; }
    const c = l.match(/kd cam recheck: route \S+ (held|would hold)/);
    if (c && mode >= 0) a[mode][c[1]] = (a[mode][c[1]] ?? 0) + 1;
  }
  return a;
}
export const camBad = (a: Record<string, Record<string, number>>): boolean => !a['0']['would hold'] || !a['1'].held || !!a['0'].held || !!a['1']['would hold'];

if (process.argv.includes('--self-check')) {
  const log = ['gohere start: cmd 1 X follower AA, household H active selected', 'KdExemptSeen switch now 0 (cuts so far)',
    'routefail job: route 1, cut (KindDeadlines, mask 2), kind own Plan deadline 50 ms, follower AA, plan, seen',
    'routefail job: route 2, cut (KindDeadlines, mask 2), kind own Plan deadline 50 ms, follower BB, plan', 'KdExemptSeen switch now 1 (x)',
    'routefail job: route 3, cut (KindDeadlines, mask 2), kind own Plan deadline 50 ms, follower BB, plan'];
  const r = armCuts(log);
  assert.deepEqual(r.flips, [0, 1]);
  assert.deepEqual(r.arms['0'], { 'household/seen': 1, 'other/notseen': 1 });
  assert.deepEqual(r.arms['1'], { 'other/notseen': 1 });
  assert.equal(onArmViolations(r.arms), 0, 'an unseen townie cut on an on arm is the pass');
  log.push('routefail job: route 4, cut (KindDeadlines, mask 2), kind own Plan deadline 50 ms, follower AA, plan, seen');
  assert.equal(onArmViolations(armCuts(log).arms), 1, 'a household cut on an on arm is a fail');
  const ceil = (id: string, ms: number, flag: string) => `2026 routefail job: route 5, cut (KdSeenCeilMs, mask 2), kind own Plan deadline ${ms} ms, follower ${id}, plan, ${flag}`;
  const on = (...l: string[]) => onArmViolations(armCuts(['gohere start: cmd 1 X follower AA, household H active selected', 'KdExemptSeen switch now 1', ...l]).arms);
  assert.equal(on(ceil('BB', 2000, 'seen npc')), 0, 'a seen NPC cut at its 2 s ceiling is allowed');
  assert.equal(on(ceil('AA', 10000, 'seen')), 0, 'a household runaway at the 10 s ceiling is allowed');
  assert.equal(on(ceil('AA', 2000, 'seen')), 1, 'a household cut below the 10 s ceiling fails');
  assert.equal(on(ceil('AA', 2000, 'seen npc')), 1, 'a household follower flagged as an NPC fails');
  assert.equal(on(ceil('BB', 2000, 'scope unknown')), 1, 'unknown = household: below the 10 s ceiling fails');
  assert.equal(on('routefail job: route 6, cut (KindDeadlines, mask 2), kind own Plan deadline 250 ms, follower BB, plan, seen npc'), 1, 'a seen NPC is exempt from the kind deadlines');
  assert.equal(onArmViolations(armCuts(['KdExemptSeen switch now 1', ceil('BB', 100, 'seen')], 50).arms), 0, '--ceil-ms: a test-lowered household ceiling');
  const cam = camArms(['KdCutCamRecheck switch now 0 (checks 0)', 'kd cam recheck: route 1 would hold, in view (switch off), job', 'KdCutCamRecheck switch now 1 (checks 1)', 'x kd cam recheck: route 2 held, in view, job']);
  assert.deepEqual(cam, { '0': { 'would hold': 1 }, '1': { held: 1 } });
  assert.equal(camBad(cam), false);
  assert.equal(camBad(camArms(['KdCutCamRecheck switch now 1', 'kd cam recheck: route 2 held, in view'])), true, 'an arm with no window proves nothing');
  console.log('kd-arm-cuts self-check: PASS');
} else if (process.argv[2]) {
  const ci = process.argv.indexOf('--ceil-ms');
  const r = armCuts(readFileSync(process.argv[2], 'latin1').split('\n'), ci > 0 ? Number(process.argv[ci + 1]) : 10000);
  console.log(`flips ${r.flips.join(',')}\noff arms (mode 0): ${JSON.stringify(r.arms['0'])}\non arms (mode 1): ${JSON.stringify(r.arms['1'])}`);
  if (process.argv.includes('--cam')) { const c = camArms(readFileSync(process.argv[2], 'latin1').split('\n')); console.log(`camera recheck: ${JSON.stringify(c)}`); if (camBad(c)) { console.log('FAIL: per-arm camera recheck'); process.exit(1); } }
  if (process.argv.includes('--expect-on-zero') && onArmViolations(r.arms)) { console.log(`FAIL: ${onArmViolations(r.arms)} seen or household cuts on an on arm`); process.exit(1); }
}
