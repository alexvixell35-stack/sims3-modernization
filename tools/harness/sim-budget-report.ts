// Sim-thread budget (docs/160): where each ms of the sim thread goes per second of play, one table per world, from one
// sim-budget.json run (profile-world.ts with the stutter profiler, both stack samplers and [HitchAttr]). Reads only.
//   node tools/harness/sim-budget-report.ts [<tag> ...]   default: newest probe-out/profile/sim-budget-*-{bp,ip,sv}-* run per world
//   node tools/harness/sim-budget-report.ts --self-check  fixture + every bucket check on the newest real run with managed samples
// Set TS3_REPO=<main checkout> when running from a git worktree.
// Buckets (exclusive; per second row, first 60 s dropped as load work like hitch-attr-report.ts, paused seconds dropped):
//   route   = stutter-profile `route` (Route_PlanLocked on the sim thread) - rtPlanWait          [measured]
//   waits   = rtLock + rtOtherLock + rtJob + rtPlanWait ([HitchAttr])                          [measured]
//   gc      = gcMark + fin                                                                        [measured]
//   anim    = animSacs + animPose + locomotion + publish - the steering-key share of the lock waits [measured, split inferred]
//   ICs / Mono = the scheduler stages (tick, TaskObjects, OneShot, ThreadedObjects) - route - other waits - gcForcedMs,
//             split by the managed samples whose leaf is a (non-route) internal call               [split inferred]
//   other   = row wall - all of the above (catch-up overhead, remote-call pump, frame sync, untimed) [measured as remainder]
// Contributors: Mono = stall-split.ts owner category, ICs = leaf IC name (non-plan Route ICs included), route = script caller of
// the plan IC (Route_Plan/PlanFromPoint/Replan/ReplanFromPosition; managed
// samples); waits = /native blockKey deltas (probe-out/queue/<tag>-native.json); other = leaf frame of simstack samples
// outside every timed stage. Contributor ms/s = bucket ms/s x its share of that bucket's samples or wait us.
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { keyName } from './hitch-attr-report.ts';
import { category, isPlan, ownFrame, worldOf } from './stall-split.ts';

const ROOT = process.env.TS3_REPO ?? join(import.meta.dirname, '..', '..');
const PROFILE = join(ROOT, 'probe-out', 'profile'), QUEUE = join(ROOT, 'probe-out', 'queue');
const SKIP_MS = 60_000, HITCH_MS = 300;
// +0x918 wait keys taken from Locomotion_UpdateAgent (steering / avoidance, docs/81-84, 137): their waits sit in `locomotion`
export const STEER = new Set(['0x0085fe10', '0x00846d21', '0x0085b50c', '0x0082f0b0', '0x0080e5b0']);
// every timed stage and the Mono runtime/interpreter (docs/20): a simstack sample with none of these is "other"
const TIMED_FN = /FUN_00(d80bc0|d7fc60|d74800|d7f8b0|571ad0|571b90|83ced0|593590|e4a050|e697f0|82c6e0)\+/i;
const inMono = (frame: string) => { const a = parseInt(frame, 16); return a >= 0xD70000 && a < 0xEF0000; };
export const BUCKETS = ['Mono script', 'ICs (native under script)', 'route sync plans', '+0x918 / job waits', 'GC + finalizers', 'anim + locomotion + publish', 'other (untimed, sync, idle)'] as const;
type B = typeof BUCKETS[number];
type Row = Record<string, number>;
export type Run = { tag: string; rows: Row[]; hitches: Row[]; managed: string[][]; simstack: string[][]; native?: { before?: Row; after?: Row }; ics: Set<string> };

export const OTHER_TIMERS = ['svcUpdate', 'preLoco', 'pump', 'other7694'];
const n = (r: Row, k: string) => Number(r[k]) || 0;
// one second row or one hitch frame -> exclusive ms per bucket; script = Mono + ICs before the sample split
export function split(r: Row, wall: number, steerShare: number) {
  const waits = n(r, 'rtLock') + n(r, 'rtOtherLock') + n(r, 'rtJob') + n(r, 'rtPlanWait');
  const route = Math.max(0, n(r, 'route') - n(r, 'rtPlanWait'));
  const gc = n(r, 'gcMark') + n(r, 'fin');
  const animRaw = n(r, 'animSacs') + n(r, 'animPose') + n(r, 'locomotion') + n(r, 'publish');
  const sched = n(r, 'script') + n(r, 'taskObjects') + n(r, 'oneShot') + n(r, 'threaded');
  // waits the script stages cannot hold sat inside locomotion, whatever the run-wide key share says (else counted twice)
  const keyed = (n(r, 'rtLock') + n(r, 'rtOtherLock')) * steerShare;
  const waitsLoco = Math.min(animRaw, Math.max(keyed, waits - Math.max(0, sched - route - n(r, 'gcForcedMs'))));
  const script = Math.max(0, sched - route - (waits - waitsLoco) - n(r, 'gcForcedMs'));
  const anim = animRaw - waitsLoco;
  return { script, route, waits, gc, anim, other: Math.max(0, wall - script - route - waits - gc - anim) };
}
type Parts = ReturnType<typeof split>;
const top = (m: Map<string, number>, total: number, ms: number, k = 5) =>
  [...m].sort((a, b) => b[1] - a[1]).slice(0, k).map(([name, c]) => `${name} ${(ms * c / total).toFixed(1)}`).join('; ');
const count = (m: Map<string, number>, k: string, v = 1) => m.set(k, (m.get(k) ?? 0) + v);

export function analyze(r: Run) {
  const t0 = r.rows[0]?.t ?? 0;
  let prev = NaN, wallMs = 0, iters = 0;
  const kept: { row: Row; dt: number }[] = [];
  for (const row of r.rows) {
    const dt = row.t - prev; prev = row.t;
    if (!(dt > 0 && dt < 5000) || !row.iters || row.t - t0 < SKIP_MS) continue; // first row, gaps, paused, load
    kept.push({ row, dt }); wallMs += dt; iters += row.iters;
  }
  // waits by key over the whole /native window
  const keys = new Map<string, number>();
  const a = r.native?.after ?? {}, b = r.native?.before ?? {};
  const beforeUs = new Map<number, number>();
  for (let i = 0; i < 32; i++) if (b[`blockKey_${i}`]) beforeUs.set(b[`blockKey_${i}`], n(b, `blockUs_${i}`));
  for (let i = 0; i < 32; i++) {
    const key = a[`blockKey_${i}`];
    if (!key) continue;
    const us = n(a, `blockUs_${i}`) - (beforeUs.get(key) ?? 0);
    if (us > 0) count(keys, '0x' + (key >>> 0).toString(16).padStart(8, '0'), us);
  }
  const keyUs = [...keys.values()].reduce((s, x) => s + x, 0);
  const steerShare = keyUs ? [...keys].filter(([k]) => STEER.has(k)).reduce((s, [, x]) => s + x, 0) / keyUs : 0;
  const sum: Parts = { script: 0, route: 0, waits: 0, gc: 0, anim: 0, other: 0 };
  const gcParts = new Map<string, number>(), animParts = new Map<string, number>();
  for (const { row, dt } of kept) {
    const p = split(row, dt, steerShare);
    for (const k of Object.keys(sum) as (keyof Parts)[]) sum[k] += p[k];
    count(gcParts, 'GC step mark', n(row, 'gcMark') - n(row, 'gcForcedMs')); count(gcParts, 'forced GC (alloc)', n(row, 'gcForcedMs')); count(gcParts, 'finalizers', n(row, 'fin'));
    for (const s of ['animSacs', 'animPose', 'locomotion', 'publish']) count(animParts, s, n(row, s));
  }
  const perS = (x: number) => wallMs ? x * 1000 / wallMs : 0;
  // the four "other"-bucket timers (docs/28 §0.3): ms/s each, only when the profile has the columns (older runs: none).
  // Diagnostic: they overlap the other buckets (publish, route waits, lot add inside the pump), so they do not sum to "other".
  const otherTimers: Record<string, number> = {};
  if (kept.length && OTHER_TIMERS.every(k => k in kept[0].row)) for (const k of OTHER_TIMERS) otherTimers[k] = perS(kept.reduce((s, { row }) => s + n(row, k), 0));
  // managed samples: route ICs, other ICs, Mono
  const ic = new Map<string, number>(), mono = new Map<string, number>(), route = new Map<string, number>();
  let icN = 0, monoN = 0, routeN = 0;
  for (const s of r.managed) {
    if (!s.length) continue;
    if (isPlan(s)) { routeN++; count(route, `${s[0].replace(/^ScriptCore\.Route\./, '')} <- ${ownFrame(s.slice(1))}`); }
    else if (r.ics.has(s[0])) { icN++; count(ic, s[0].replace(/^ScriptCore\./, '')); }
    else { monoN++; count(mono, category(s)); }
  }
  const icShare = icN + monoN ? icN / (icN + monoN) : 0;
  // simstack: leaf of samples outside every timed stage
  const other = new Map<string, number>();
  let otherN = 0;
  for (const s of r.simstack) if (s.length && !s.some(f => TIMED_FN.test(f) || inMono(f))) { otherN++; count(other, s[0].replace(/^0x[0-9a-f]+ /i, '').replace(/\+0x[0-9a-f]+$/i, '')); }
  const ms: Record<B, number> = {
    'Mono script': perS(sum.script) * (1 - icShare), 'ICs (native under script)': perS(sum.script) * icShare,
    'route sync plans': perS(sum.route), '+0x918 / job waits': perS(sum.waits), 'GC + finalizers': perS(sum.gc),
    'anim + locomotion + publish': perS(sum.anim), 'other (untimed, sync, idle)': perS(sum.other),
  };
  const sumOf = (m: Map<string, number>) => [...m.values()].reduce((s, x) => s + x, 0);
  const contrib: Record<B, string> = {
    'Mono script': monoN ? top(mono, monoN, ms['Mono script']) : '(no managed samples)',
    'ICs (native under script)': icN ? top(ic, icN, ms['ICs (native under script)']) : '(no managed samples)',
    'route sync plans': routeN ? top(route, routeN, ms['route sync plans']) : '(no route samples)',
    '+0x918 / job waits': keyUs ? (sum.waits ? '' : '[HitchAttr] off in this run (rt* = 0): key split only, as % of keyed wait: ') + top(new Map([...keys].map(([k, v]) => [keyName(k), v])), keyUs, sum.waits ? ms['+0x918 / job waits'] : 100) : '(no /native blockKey)',
    'GC + finalizers': top(gcParts, sumOf(gcParts) || 1, ms['GC + finalizers'], 3),
    'anim + locomotion + publish': top(animParts, sumOf(animParts) || 1, ms['anim + locomotion + publish'], 4) + (steerShare ? ` (minus steering waits, ${(steerShare * 100).toFixed(0)}% of keyed wait)` : ''),
    'other (untimed, sync, idle)': otherN ? top(other, otherN, ms['other (untimed, sync, idle)']) : '(no simstack samples)',
  };
  // hitch frames >= 300 ms: the bucket with the most ms in that frame (Mono and ICs stay one bucket: no per-frame samples)
  const hs = r.hitches.filter(h => h.wallMs >= HITCH_MS && h.t - t0 >= SKIP_MS);
  const led = new Map<string, number>();
  for (const h of hs) {
    const p = split(h, h.wallMs, STEER.has(String(h.rtMaxKey)) ? 1 : 0);
    const best = (Object.entries(p) as [keyof Parts, number][]).sort((x, y) => y[1] - x[1])[0][0];
    count(led, best);
  }
  const ledOf: Record<B, string> = Object.fromEntries(BUCKETS.map(b => [b, ''])) as Record<B, string>;
  const pct = (k: keyof Parts) => hs.length ? `${((led.get(k) ?? 0) * 100 / hs.length).toFixed(0)}%` : '-';
  ledOf['Mono script'] = `${pct('script')} (Mono + ICs)`; ledOf['ICs (native under script)'] = 'in Mono row';
  ledOf['route sync plans'] = pct('route'); ledOf['+0x918 / job waits'] = pct('waits'); ledOf['GC + finalizers'] = pct('gc');
  ledOf['anim + locomotion + publish'] = pct('anim'); ledOf['other (untimed, sync, idle)'] = pct('other');
  const waitMs = ms['+0x918 / job waits'] + perS(kept.reduce((s, { row }) => s + n(row, 'publish') + n(row, 'other7694'), 0));
  return { tag: r.tag, world: worldOf(r.tag), otherTimers, secs: kept.length, wallMs, its: perS(iters), hitches: hs.length, ms, contrib, ledOf,
    samples: { managed: r.managed.length, routeN, icN, monoN, simstack: r.simstack.length, otherN }, native: keyUs > 0,
    who: who(r.native, r.rows.length > 1 ? (r.rows[r.rows.length - 1].t - r.rows[0].t) / 1000 : 0), waitMs };
}

// docs/160 §Results m1122: who owns the thread, from the [UnseenProbe] Bands counters in the same run (docs/322; disjoint sites
// loco + pose + Sim task + autonomy). Rows: household (+ selected + unknown scope), NPC near (bands whose upper edge <= FAR_M),
// NPC far (lower edge >= FAR_M), no position; non-Sim = 1000 - their sum. Window = the /native before/after pair, i.e. the whole
// profile (first 60 s included), while the buckets drop the first 60 s [inferred: small mismatch].
export const FAR_M = 150;
export function who(nat: Run['native'], seconds: number) {
  const a = nat?.after ?? {}, b = nat?.before ?? {};
  const edges = n(a, 'ubEdges');
  if (!edges || !(n(a, 'ubReady') & 32) || !(seconds > 0)) return undefined;
  const em = Array.from({ length: edges }, (_, i) => n(a, `ubEdgeM_${i}`));
  const row = (r: number) => [0, 4, 5, 6].reduce((s, site) => s + n(a, `ubUs_${site}_${r}`) - n(b, `ubUs_${site}_${r}`), 0) / 1000 / seconds;
  let near = 0, far = 0, mixed = 0;
  for (let r = 0; r <= edges; r++) {
    const lo = r ? em[r - 1] : 0, hi = r < edges ? em[r] : Infinity;
    if (hi <= FAR_M) near += row(r); else if (lo >= FAR_M) far += row(r); else mixed += row(r); // a band across FAR_M: shown apart
  }
  const household = row(7), noPos = row(8), sims = household + near + far + mixed + noPos;
  return { household, near, far, mixed, noPos, sims, nonSim: 1000 - sims, seconds };
}

export function report(x: ReturnType<typeof analyze>) {
  const total = BUCKETS.reduce((s, b) => s + x.ms[b], 0);
  return [`### ${x.world.toUpperCase()}: ${x.tag}`, '',
    `${x.secs} s kept (first 60 s and paused seconds dropped), ${x.its.toFixed(1)} it/s, ${x.hitches} frames >= ${HITCH_MS} ms (${(x.hitches * 3600 / Math.max(x.secs, 1)).toFixed(0)}/h); managed samples ${x.samples.managed} (route ${x.samples.routeN}, IC ${x.samples.icN}, Mono ${x.samples.monoN}), simstack ${x.samples.simstack} (other ${x.samples.otherN}); /native waits by key: ${x.native ? 'yes' : 'MISSING'}`, '',
    '| Bucket | ms/s | Share of sim-thread time | Frames >= 300 ms it leads | Top 5 contributors (ms/s) |', '|---|---|---|---|---|',
    ...BUCKETS.map(b => `| ${b} | ${x.ms[b].toFixed(1)} | ${(x.ms[b] * 100 / (total || 1)).toFixed(1)}% | ${x.ledOf[b]} | ${x.contrib[b]} |`),
    `| **sum** | ${total.toFixed(1)} | 100% | | |`,
    ...(Object.keys(x.otherTimers).length ? ['', `"other" timers (ms/s, overlap the buckets): ${Object.entries(x.otherTimers).map(([k, v]) => `${k} ${v.toFixed(1)}`).join(', ')}`] : []),
    '', `wait / idle (route-job waits + publish sync + IdleSimulationCycle 0x7694B0): ${x.waitMs.toFixed(1)} ms/s; busy ${(1000 - x.waitMs).toFixed(1)} ms/s`,
    ...(x.who ? ['', `Who (ms/s over ${x.who.seconds.toFixed(0)} s, [UnseenProbe] Bands loco + pose + Sim task + autonomy; near < ${FAR_M} m, far >= ${FAR_M} m):`, '',
      '| Owner | ms/s | Share of sim-thread time |', '|---|---|---|',
      ...([['household + selected + unknown scope', x.who.household], [`NPC near (< ${FAR_M} m)`, x.who.near], [`NPC far (>= ${FAR_M} m)`, x.who.far],
        ...(x.who.mixed ? [[`NPC in a band across ${FAR_M} m`, x.who.mixed]] : []), ['NPC no position', x.who.noPos],
        ['non-Sim + unattributed (1000 - the rows above)', x.who.nonSim]] as [string, number][]).map(([k, v]) => `| ${k} | ${v.toFixed(1)} | ${(v / 10).toFixed(1)}% |`)]
      : ['', 'Who: no [UnseenProbe] Bands counters with the task sites in this run'])].join('\n');
}

const jsonl = (f: string) => existsSync(f) ? readFileSync(f, 'utf8').split('\n').filter(Boolean).flatMap(l => { try { return [JSON.parse(l)]; } catch { return []; } }) : [];
const csvRows = (f: string): Row[] => {
  const [head, ...lines] = readFileSync(f, 'utf8').trim().split(/\r?\n/);
  const cols = head.split(',');
  return lines.map(l => Object.fromEntries(l.split(',').map((v, i) => [cols[i], Number(v)])));
};
let icCache: Set<string> | undefined;
const icNames = () => icCache ??= new Set(readFileSync(join(import.meta.dirname, '..', '..', 'docs', 'data', 'ic-safety.csv'), 'utf8').split('\n').slice(1).map(l => l.split(',')[0].replace('::', '.')).filter(Boolean));
const csvOf = (tag: string) => readdirSync(PROFILE).find(f => f.startsWith(`${tag}-`) && new RegExp(`^${tag.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}-\\d+-seconds\\.csv$`).test(f));
export function load(tag: string): Run {
  const csv = csvOf(tag);
  if (!csv) throw new Error(`no ${tag}-<secs>-seconds.csv in ${PROFILE}`);
  // ts3prof refused the run (e.g. a prologue it does not know): one row, no stages; say why instead of reporting 0 s
  const plog = join(PROFILE, csv.replace(/-seconds\.csv$/, '.log')), why = existsSync(plog) ? /NOT PROFILING: [^\r\n]*/.exec(readFileSync(plog, 'utf8'))?.[0] : undefined;
  if (why) throw new Error(`${tag}: ${why}`);
  const native = existsSync(join(QUEUE, `${tag}-native.json`)) ? JSON.parse(readFileSync(join(QUEUE, `${tag}-native.json`), 'utf8')) : undefined;
  return { tag, rows: csvRows(join(PROFILE, csv)), hitches: jsonl(join(PROFILE, csv.replace(/-seconds\.csv$/, '-hitches.jsonl'))),
    managed: jsonl(join(PROFILE, `${tag}-managed.jsonl`)), simstack: jsonl(join(PROFILE, `${tag}-simstack.jsonl`)).map((s: any) => s.stack ?? []),
    native, ics: icNames() };
}
const tagsBy = (re: RegExp) => readdirSync(PROFILE).flatMap(f => { const m = /^(.*)-\d+-seconds\.csv$/.exec(f); return m && re.test(m[1]) ? [{ tag: m[1], m: statSync(join(PROFILE, f)).mtimeMs }] : []; }).sort((a, b) => b.m - a.m);

function selfCheck() {
  // fixture: 61 s of load (dropped) + 2 rows of 1000 ms, one 400 ms hitch led by route
  const row = { iters: 30, script: 10, taskObjects: 80, oneShot: 0, threaded: 600, route: 100, rtPlanWait: 10, rtLock: 50, rtOtherLock: 0, rtJob: 0,
    gcMark: 4, gcForcedMs: 1, fin: 2, animSacs: 20, animPose: 30, locomotion: 40, publish: 10 };
  const fx: Run = { tag: 'sim-budget-fx-bp-x', ics: new Set(['ScriptCore.World.World_IsOnRoofImpl', 'ScriptCore.Route.Route_GetOption']),
    rows: [{ t: 0, iters: 1 }, { t: 61_000, ...row }, { t: 62_000, ...row }, { t: 63_000, ...row }],
    hitches: [{ t: 62_500, wallMs: 400, ...row, route: 300, script: 0, taskObjects: 0, threaded: 320, rtMaxKey: '0x00760bff' } as any, { t: 1000, wallMs: 900 }],
    managed: [[], ['ScriptCore.World.World_IsOnRoofImpl', 'X.Y'], ['A.B', 'Sims3.Gameplay.Autonomy.AutonomyManager.Simulate', 'ScriptCore.ScriptProxy.Simulate'], ['M.N'], ['ScriptCore.Route.Route_Plan', 'Sims3.Gameplay.Actors.SimRoutingComponent.DoRoute'], ['ScriptCore.Route.Route_GetOption', 'Q.R']],
    simstack: [['0x7700 ntdll.dll+0x1234'], ['0xe56514 TS3W.exe!FUN_00e53eb0+0x2664'], ['0x6f0000 TS3W.exe!FUN_006f0000+0x10', '0x83cf52 TS3W.exe!FUN_0083ced0+0x82']],
    native: { before: { blockKey_0: 0x846D21, blockUs_0: 1_000 }, after: { blockKey_0: 0x846D21, blockUs_0: 21_000, blockKey_1: 0x833593, blockUs_1: 20_000 } } };
  const x = analyze(fx);
  assert.equal(x.secs, 2); // 0 -> 61 s is a gap (dropped), 62 and 63 s are kept
  const close = (a: number, b: number, m: string) => assert.ok(Math.abs(a - b) < 1e-6, `${m}: ${a} vs ${b}`);
  // steering share 20000/40000 = 0.5 -> waitsLoco 25; route 90; waits 60; gc 6; anim 100 - 25 = 75; script 690 - 90 - 35 - 1 = 564
  close(x.ms['route sync plans'], 90, 'route'); close(x.ms['+0x918 / job waits'], 60, 'waits'); close(x.ms['GC + finalizers'], 6, 'gc');
  close(x.ms['anim + locomotion + publish'], 75, 'anim');
  close(x.ms['ICs (native under script)'], 564 / 2, 'IC = 2 of 4 non-plan samples (Route_GetOption is an IC)'); close(x.ms['Mono script'], 564 / 2, 'Mono');
  close(BUCKETS.reduce((s, b) => s + x.ms[b], 0), 1000, 'buckets sum to wall');
  // waits larger than the script stages sit in locomotion: nothing counted twice, other = wall - timed
  const w = split({ rtLock: 700, animSacs: 780, gcMark: 1 }, 1000, 0); close(w.waits + w.anim + w.gc + w.other, 1000, 'excess waits moved out of anim');
  assert.deepEqual(x.otherTimers, {}); // fixture rows carry no timer columns
  const xt = analyze({ ...fx, rows: fx.rows.map(r => ({ ...r, svcUpdate: 14, preLoco: 3, pump: 2, other7694: 1 })) });
  close(xt.otherTimers.svcUpdate, 14, 'svcUpdate ms/s'); assert.match(report(xt), /"other" timers \(ms\/s, overlap the buckets\): svcUpdate 14\.0, preLoco 3\.0/);
  assert.equal(x.hitches, 1); assert.equal(x.ledOf['route sync plans'], '100%');
  assert.match(x.contrib['+0x918 / job waits'], /steering avoidance A 30\.0; UpdateSimFootprint 30\.0/);
  assert.match(x.contrib['Mono script'], /autonomy search/); assert.match(x.contrib['route sync plans'], /Route_Plan <- Sims3\.Gameplay\.Actors\.SimRoutingComponent\.DoRoute/);
  assert.match(x.contrib['other (untimed, sync, idle)'], /^ntdll\.dll/); // Mono and locomotion samples are not "other"
  assert.match(report(x), /\| \*\*sum\*\* \| 1000\.0 \|/);
  assert.equal(x.who, undefined); assert.match(report(x), /Who: no \[UnseenProbe\] Bands/);
  // who: edges 50,150,400 -> rows 0,1 near, 2,3 far; household row 7, no position 8; site 1 (waits) is a part, never added
  const ub = { ubEdges: 3, ubEdgeM_0: 50, ubEdgeM_1: 150, ubEdgeM_2: 400, ubReady: 0x7f, ubUs_0_0: 10e6, ubUs_5_1: 20e6, ubUs_6_2: 30e6, ubUs_4_3: 40e6,
    ubUs_1_3: 99e6, ubUs_5_7: 50e6, ubUs_0_8: 1e6 };
  const wh = who({ before: { ubUs_0_0: 4e6 }, after: ub }, 100)!;
  close(wh.near, 260, 'near'); close(wh.far, 700, 'far'); close(wh.household, 500, 'household'); close(wh.noPos, 10, 'no position');
  close(wh.nonSim, 1000 - 1470, 'non-Sim'); assert.equal(who({ after: { ...ub, ubReady: 0x1f } }, 100), undefined); // task sites off
  close(who({ after: { ...ub, ubEdgeM_1: 200 } }, 100)!.mixed, 200, '50-200 m band straddles 150 m');
  console.log('fixture ok');
  // real data: the newest finished, profiled run with managed samples and stage columns (any queue); every bucket >= 0, sum =
  // wall. 2026-10-29: the newest such run was a [MonoThread] GcLock=1 job ts3prof had refused (1-row CSV) or one still running
  // (< 30 s kept): both are skipped, not failed (>= 61 s load + 30 s kept + header = 92 lines)
  if (!existsSync(PROFILE)) { console.log(`no ${PROFILE}: real-data check skipped (set TS3_REPO from a worktree)`); return console.log('self-check ok'); }
  const real = tagsBy(/(^|-)(bp|ip|sv)-/).find(({ tag }) => {
    if (!existsSync(join(PROFILE, `${tag}-managed.jsonl`)) || statSync(join(PROFILE, `${tag}-managed.jsonl`)).size === 0) return false;
    const lines = readFileSync(join(PROFILE, csvOf(tag)!), 'utf8').split(/\r?\n/);
    return lines[0].includes('locomotion') && lines.length >= 92 && !/NOT PROFILING/.test((() => { try { return readFileSync(join(PROFILE, csvOf(tag)!.replace(/-seconds\.csv$/, '.log')), 'utf8'); } catch { return ''; } })());
  });
  if (!real) { console.log('no real run with managed samples and stage columns: real-data check skipped'); return console.log('self-check ok'); }
  const y = analyze(load(real.tag));
  assert.ok(y.secs > 30, `${real.tag}: ${y.secs} s kept`);
  for (const b of BUCKETS) assert.ok(y.ms[b] >= 0, `${real.tag}: ${b} ${y.ms[b]}`);
  close(BUCKETS.reduce((s, b) => s + y.ms[b], 0), 1000, `${real.tag}: buckets sum to wall`);
  console.log(report(y));
  console.log(`real-data ok (${real.tag})\nself-check ok`);
}

if (resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url)) {
  const args = process.argv.slice(2);
  if (args.includes('--self-check')) selfCheck();
  else {
    if (!existsSync(PROFILE)) { console.error(`no ${PROFILE} (set TS3_REPO from a worktree)`); process.exit(2); }
    const tags = args.length ? args : ['bp', 'ip', 'sv'].flatMap(w => tagsBy(new RegExp(`^sim-budget-.*-${w}-`)).slice(0, 1).map(t => t.tag));
    if (!tags.length) { console.error(`no sim-budget-* runs in ${PROFILE} (run tools/harness/queues/sim-budget.json, or pass tags)`); process.exit(2); }
    console.log(tags.map(t => report(analyze(load(t)))).join('\n\n'));
  }
}
