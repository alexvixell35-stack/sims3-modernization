// Non-Sim split of the sim thread (docs/329): who owns the docs/160 "non-Sim + unattributed" share, per owner, distance band and
// far-lot class (a live / b lazy per-lot catch-up / c throttle / d engine). Reads only.
//   node tools/harness/nonsim-split-report.ts [<tag> ...]   default: newest probe-out/profile/nonsim-split-*-{bp,ip,sv}-* run per world
//   node tools/harness/nonsim-split-report.ts --provisional [<tag> ...]  no [Diag] NonSimSplit dump: managed-sample split only (m1122)
//   node tools/harness/nonsim-split-report.ts --self-check
// Set TS3_REPO=<checkout> to read another checkout's probe-out (the queue runs from the lane worktree).
// Inputs per tag: the sim-budget inputs (load() of sim-budget-report.ts: seconds.csv, managed.jsonl, <tag>-native.json with the
// docs/322 ub* counters) and the last "nonsim" dump in probe-out/queue/<tag>-<HHMM>-ts3native.log.
// Rows (ms per wall second, the whole thread = 1000):
//   Sim rows        = docs/322 band sites loco + pose + Sim task + autonomy (as docs/160 "Who")                    [measured]
//   owner rows      = NonSimSplit class rows of kind object / lot / simdrv / delegate / task / invoke, per band    [measured]
//   lot sub-owners  = a lot row split by the managed samples under Lot.Simulate (reactions, alarms by callback...)  [split inferred]
//   scheduler rest  = the four scheduler stages - every seam run (sim + autonomy + owner rows)                     [measured remainder]
//   engine          = anim + locomotion + publish - the Sim loco / pose sites; GC step + finalizers; svcUpdate, preLoco, pump [measured]
//   other           = 1000 - all of the above (untimed loop work, pacing; overlaps clamp here)                     [remainder]
// Windows: the dump and the ub counters span the whole profile (first 60 s included); the csv rows drop the first 60 s
// (sim-budget-report.ts), as in docs/160 "Who" (small mismatch, docs/329 blind spot 1).
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { load, type Run } from './sim-budget-report.ts';
import { worldOf } from './stall-split.ts';

const ROOT = process.env.TS3_REPO ?? join(import.meta.dirname, '..', '..');
const PROFILE = join(ROOT, 'probe-out', 'profile'), QUEUE = join(ROOT, 'probe-out', 'queue');
export const FAR_M = 150, SKIP_MS = 60_000;
const NS_ROWS = 10, HH = 7, NONE = 8, GLOB = 9; // nonsim.cpp: bands 0-6, hh, none, glob
type Row = Record<string, number>;
export type Cls = { kind: string; runs: number; us: number[]; name: string };
export type Dump = { secs: number; head: string; classes: Cls[]; outside: Record<string, number[]> };

// ---- the last dump in a ts3native.log ----
export function parseDump(log: string): Dump | undefined {
  const lines = log.split(/\r?\n/);
  const at = lines.map((l, i) => /^nonsim split: /.test(l) ? i : -1).filter(i => i >= 0).pop();
  if (at === undefined) return undefined;
  const secs = Number(/^nonsim split: (\d+) s,/.exec(lines[at])?.[1] ?? 0);
  const classes: Cls[] = [], outside: Record<string, number[]> = {};
  for (let i = at + 1; i < lines.length; i++) {
    const l = lines[i];
    if (/^nonsim split: /.test(l)) break;
    const o = /^nonsim outside (footprint|plans): us ([\d,]+)$/.exec(l);
    if (o) { outside[o[1]] = o[2].split(',').map(Number); continue; }
    const c = /^nonsim class (\d+) (\w+) runs (\d+) us ([\d,]+) name (.*)$/.exec(l);
    if (c) classes.push({ kind: c[2], runs: Number(c[3]), us: c[4].split(',').map(Number), name: c[5] });
  }
  return { secs, head: lines[at], classes, outside };
}

// ---- classes (docs/329 §4). Household rows are always (a): household lots and household-owned objects stay live by rule ----
export type K = 'a' | 'b' | 'c' | 'd';
const GLOBAL_C: [RegExp, K, string][] = [
  [/Camera|Tooltip|\.UI\.|UIEvent|Hud|Responder|MapTag|Notification/, 'a', 'UI / camera: visible'],
  [/Season|Weather|SimClock|AlarmManager(?!.*Lot)/, 'a', 'global clock / weather / seasons'],
  [/Situation|Party|Wedding|Festival/, 'a', 'situations (Sims taking part)'],
  [/StoryProgression|NRaas/, 'c', 'story progression (off in this queue: -disablestoryprogression)'],
  [/Service|RoleManager|MetaAutonomy|StrayPet|PetAdoption|WildHorse|Traffic|SimBuilder|Spawner/, 'c', 'world population / services'],
  [/Ts3Fixes|Ts3Agent|LoopTask|StuckWatchdog|PollTask|MtkVis|SmoothPatch/, 'c', 'ours / mods / harness loops'],
];
export function globalClass(name: string): [K, string] {
  const m = GLOBAL_C.find(([re]) => re.test(name));
  return m ? [m[1], m[2]] : ['c', 'other global task (throttle candidate)'];
}
// Lot.Simulate sub-owners (managed sample shares) and their class on a far lot (a near or household lot is always a)
export const LOT_PARTS: [string, RegExp, K][] = [
  ['reactions (ReactionBroadcaster pulses)', /ReactionBroadcaster|UpdateReactions|BroadcasterPulse/, 'b'],
  ['alarms: object state (spawners, plants, wildflowers, spoilage)', /ObjectSpawner|PlantGrowth|Plant\.|Wildflower|Spoil|Harvest|Decay/, 'b'],
  ['alarms: UI / clock callbacks', /FireSimMinuteChanged|HudModel|Responder/, 'a'],
  ['alarms: other callbacks (pets, phone, meta autonomy, ours)', /AlarmTimerCallback|AlarmManager/, 'c'],
  ['detail priority (lot LOD)', /DetailPriority|LotSetDetailPriority/, 'c'],
  ['zombies / door privacy / rest of Lot.Simulate', /./, 'b'],
];
export function lotShares(managed: string[][]): { name: string; k: K; share: number }[] {
  const c = new Map<string, number>();
  let n = 0;
  for (const s of managed) {
    const i = s.lastIndexOf('Sims3.Gameplay.Core.Lot.Simulate');
    if (i < 0) continue;
    const above = s.slice(0, i).join(' ');
    const p = LOT_PARTS.find(([, re]) => re.test(above)) ?? LOT_PARTS[LOT_PARTS.length - 1];
    c.set(p[0], (c.get(p[0]) ?? 0) + 1); n++;
  }
  return LOT_PARTS.map(([name, , k]) => ({ name, k, share: n ? (c.get(name) ?? 0) / n : (name.startsWith('zombies') ? 1 : 0) }));
}

const nz = (r: Row | undefined, k: string) => Number(r?.[k]) || 0;
export function simRows(nat: Run['native'], secs: number) {
  const a = nat?.after ?? {}, b = nat?.before ?? {}, edges = nz(a, 'ubEdges');
  if (!edges || !(secs > 0)) return undefined;
  const site = (s: number, r: number) => (nz(a, `ubUs_${s}_${r}`) - nz(b, `ubUs_${s}_${r}`)) / 1000 / secs;
  const em = Array.from({ length: edges }, (_, i) => nz(a, `ubEdgeM_${i}`));
  const farBand = (r: number) => (r ? em[r - 1] : 0) >= FAR_M;
  let near = 0, far = 0, loco = 0, pose = 0;
  for (let r = 0; r <= 8; r++) { loco += site(0, r); pose += site(4, r); }
  for (let r = 0; r <= edges; r++) { const v = [0, 4, 5, 6].reduce((s, x) => s + site(x, r), 0); if (farBand(r)) far += v; else near += v; }
  const hh = [0, 4, 5, 6].reduce((s, x) => s + site(x, 7), 0), noPos = [0, 4, 5, 6].reduce((s, x) => s + site(x, 8), 0);
  return { edges: em, hh, near, far, noPos, loco, pose, farBand };
}

export type Line = { owner: string; kind: string; k: K | '-'; ms: number; near: number; far: number; hh: number; glob: number; note: string };
export function analyzeRun(r: Run, dump: Dump | undefined) {
  const t0 = r.rows[0]?.t ?? 0;
  let prev = NaN, wallMs = 0;
  const sum: Row = {};
  for (const row of r.rows) {
    const dt = row.t - prev; prev = row.t;
    if (!(dt > 0 && dt < 5000) || !row.iters || row.t - t0 < SKIP_MS) continue;
    wallMs += dt;
    for (const [k, v] of Object.entries(row)) sum[k] = (sum[k] ?? 0) + (Number(v) || 0);
  }
  const perS = (k: string) => wallMs ? (sum[k] ?? 0) * 1000 / wallMs : 0;
  const secs = r.rows.length > 1 ? (r.rows[r.rows.length - 1].t - r.rows[0].t) / 1000 : 0;
  const sims = simRows(r.native, secs);
  const lines: Line[] = [];
  const sched = perS('script') + perS('taskObjects') + perS('oneShot') + perS('threaded');
  let seam = 0;
  const farOf = (us: number[], edges: number[]) => us.slice(0, 7).reduce((s, v, i) => s + ((i ? edges[i - 1] ?? Infinity : 0) >= FAR_M ? v : 0), 0);
  const nearOf = (us: number[], edges: number[]) => us.slice(0, 7).reduce((s, v, i) => s + ((i ? edges[i - 1] ?? Infinity : 0) >= FAR_M ? 0 : v), 0);
  const lot = lotShares(r.managed);
  if (dump && dump.secs > 0) {
    const ms = (us: number) => us / 1000 / dump.secs, edges = sims?.edges ?? [];
    for (const c of dump.classes) {
      const tot = ms(c.us.reduce((s, v) => s + v, 0));
      seam += tot;
      if (c.kind === 'sim' || c.kind === 'autonomy') continue; // in the Sim rows
      const near = ms(nearOf(c.us, edges)), far = ms(farOf(c.us, edges)), hh = ms(c.us[HH]), glob = ms(c.us[GLOB] + c.us[NONE]);
      if (c.kind === 'lot') {
        for (const p of lot) if (p.share > 0) lines.push({ owner: `${c.name}: ${p.name}`, kind: 'lot', k: p.k, ms: tot * p.share, near: near * p.share, far: far * p.share, hh: hh * p.share, glob: glob * p.share, note: 'split by managed samples [inferred]' });
      } else if (c.kind === 'object' || c.kind === 'invoke') {
        lines.push({ owner: c.name, kind: c.kind, k: 'b', ms: tot, near, far, hh, glob, note: 'per-object state: far = lazy candidate' });
      } else if (c.kind === 'simdrv') {
        lines.push({ owner: c.name, kind: c.kind, k: 'b', ms: tot, near, far, hh, glob, note: 'Sim-driven (that Sim\'s band): far part belongs to the far-Sim model (docs/324)' });
      } else {
        const [k, note] = globalClass(c.name);
        lines.push({ owner: c.name, kind: c.kind, k, ms: tot, near, far, hh, glob, note });
      }
    }
  } else {
    // provisional: managed-sample shares of the scheduler stages (no bands, no seam total)
    const ne = r.managed.filter(s => s.length);
    const root = (s: string[]) => { const i = s.lastIndexOf('ScriptCore.ScriptProxy.Simulate'); return i > 0 ? s[i - 1] : s[s.length - 1]; };
    const c = new Map<string, number>();
    for (const s of ne) c.set(root(s), (c.get(root(s)) ?? 0) + 1);
    const share = (re: RegExp) => [...c].filter(([k]) => re.test(k)).reduce((s, [, v]) => s + v, 0) / (ne.length || 1);
    const simShare = share(/^Sims3\.Gameplay\.Actors\.Sim\.Simulate$|AutonomyManager\.Simulate$/);
    seam = sched; // all script time in the stages, split by samples
    lines.push({ owner: '(Sim task + autonomy: in the Sim rows)', kind: 'sim', k: '-', ms: 0, near: 0, far: 0, hh: 0, glob: 0, note: `sample share ${(simShare * 100).toFixed(0)}%` });
    const lotMs = sched * share(/Lot\.Simulate$/);
    for (const p of lot) if (p.share > 0) lines.push({ owner: `Lot: ${p.name}`, kind: 'lot', k: p.k, ms: lotMs * p.share, near: NaN, far: NaN, hh: NaN, glob: NaN, note: 'provisional: sample share x stages' });
    lines.push({ owner: 'Sims3.Gameplay.Actors.SimUpdate', kind: 'simdrv', k: 'b', ms: sched * share(/SimUpdate\.Simulate$/), near: NaN, far: NaN, hh: NaN, glob: NaN, note: 'provisional; Sim-driven (far part = far-Sim model)' });
    for (const [k, v] of [...c].sort((x, y) => y[1] - x[1])) {
      if (/^Sims3\.Gameplay\.Actors\.Sim\.Simulate$|AutonomyManager\.Simulate$|Lot\.Simulate$|SimUpdate\.Simulate$/.test(k) || v < 3) continue;
      const [kk, note] = globalClass(k);
      lines.push({ owner: k, kind: 'task', k: kk, ms: sched * v / ne.length, near: NaN, far: NaN, hh: NaN, glob: NaN, note: `provisional; ${note}` });
    }
    const listed = lines.reduce((s, l) => s + l.ms, 0) + sched * simShare;
    lines.push({ owner: '(script roots under 3 samples)', kind: 'task', k: 'c', ms: Math.max(0, sched - listed), near: NaN, far: NaN, hh: NaN, glob: NaN, note: 'provisional' });
  }
  const eng = (owner: string, ms: number, note: string) => lines.push({ owner, kind: 'engine', k: 'd', ms: Math.max(0, ms), near: NaN, far: NaN, hh: NaN, glob: NaN, note });
  if (dump) eng('scheduler outside task runs (stages - seam runs)', sched - seam, 'task list walk, ScriptTick setup, waits between runs');
  const animAll = perS('animSacs') + perS('animPose') + perS('locomotion') + perS('publish');
  eng('anim + locomotion + publish of non-Sims (minus Sim loco / pose sites)', animAll - (sims ? sims.loco + sims.pose : 0), 'objects / vehicles / pets rigs, SACS pass, publish sync');
  eng('GC step mark + finalizers', perS('gcMark') - perS('gcForcedMs') + perS('fin'), 'forced GC sits inside the owner rows');
  eng('services (svcUpdate 0x59ED70: UTFWin visuals, audio, camera)', perS('svcUpdate'), 'docs/28 §0.3');
  eng('pre-locomotion step (preLoco 0x7FD520)', perS('preLoco'), 'docs/28 §0.3');
  eng('remote-call pump (0xD81FC3, lot streaming jobs)', perS('pump'), 'docs/28 §0.3');
  const simTotal = sims ? sims.hh + sims.near + sims.far + sims.noPos : 0;
  const listed = lines.reduce((s, l) => s + l.ms, 0);
  eng('other (untimed loop work, catch-up, pacing; remainder)', 1000 - simTotal - listed, 'remainder');
  return { tag: r.tag, world: worldOf(r.tag), sims, lines, provisional: !dump, dumpHead: dump?.head, outside: dump?.outside, dumpSecs: dump?.secs ?? 0, edges: sims?.edges ?? [] };
}

export function classTotals(x: ReturnType<typeof analyzeRun>) {
  const t: Record<K, number> = { a: 0, b: 0, c: 0, d: 0 };
  let farLot = 0, farSimDrv = 0;
  for (const l of x.lines) {
    if (l.k === '-') continue;
    // household rows are always live (a), whatever the owner's class
    if (Number.isFinite(l.hh)) { t.a += l.hh + (l.k === 'b' ? l.near : 0); t[l.k] += l.ms - l.hh - (l.k === 'b' ? l.near : 0); }
    else t[l.k] += l.ms;
    if (l.k === 'b' && Number.isFinite(l.far)) { if (l.kind === 'simdrv') farSimDrv += l.far; else farLot += l.far; }
  }
  const s = x.sims;
  if (s) t.a += s.hh + s.near + s.noPos; // Sims the player sees or controls; far NPCs are the far-Sim model's (docs/324)
  return { t, farLot, farSimDrv, farNpc: s?.far ?? 0 };
}

const f1 = (v: number) => Number.isFinite(v) ? v.toFixed(1) : '-';
export function report(x: ReturnType<typeof analyzeRun>, top = 25) {
  const ct = classTotals(x);
  const L = [`### ${x.world.toUpperCase()}: ${x.tag}${x.provisional ? ' (PROVISIONAL: no NonSimSplit dump, sample shares only)' : ''}`, ''];
  if (x.dumpHead) L.push(`dump: ${x.dumpHead}`, '');
  if (x.sims) L.push(`Sim rows (ms/s): household ${f1(x.sims.hh)}, NPC near ${f1(x.sims.near)}, NPC far ${f1(x.sims.far)}, no position ${f1(x.sims.noPos)}; non-Sim ${f1(1000 - x.sims.hh - x.sims.near - x.sims.far - x.sims.noPos)}`, '');
  L.push('| Owner | kind | class | ms/s | share | near < 150 m | far >= 150 m | household | no position / global | note |', '|---|---|---|---:|---:|---:|---:|---:|---:|---|');
  const rows = [...x.lines].sort((a, b) => (a.kind === 'engine' ? 1 : 0) - (b.kind === 'engine' ? 1 : 0) || b.ms - a.ms);
  const shown = rows.filter(l => l.kind === 'engine').concat(rows.filter(l => l.kind !== 'engine').slice(0, top));
  const rest = rows.filter(l => l.kind !== 'engine').slice(top);
  for (const l of shown.sort((a, b) => (a.kind === 'engine' ? 1 : 0) - (b.kind === 'engine' ? 1 : 0) || b.ms - a.ms))
    L.push(`| ${l.owner} | ${l.kind} | ${l.k} | ${f1(l.ms)} | ${(l.ms / 10).toFixed(1)}% | ${f1(l.near)} | ${f1(l.far)} | ${f1(l.hh)} | ${f1(l.glob)} | ${l.note} |`);
  if (rest.length) L.push(`| (${rest.length} smaller owners) | | | ${f1(rest.reduce((s, l) => s + l.ms, 0))} | | | | | | |`);
  if (x.outside) L.push('', `Sim footprint / plan time inside non-Sim runs (us over the dump, part of those rows): footprint ${x.outside.footprint?.join(',')}; plans ${x.outside.plans?.join(',')}`);
  L.push('', '| Class | ms/s | share |', '|---|---:|---:|',
    ...(['a', 'b', 'c', 'd'] as K[]).map(k => `| ${({ a: '(a) live: visible / global / household', b: '(b) lazy per-lot / per-object catch-up', c: '(c) throttle', d: '(d) engine' })[k]} | ${f1(ct.t[k])} | ${(ct.t[k] / 10).toFixed(1)}% |`),
    `| far NPC Sim rows (far-Sim model, docs/324) | ${f1(ct.farNpc)} | ${(ct.farNpc / 10).toFixed(1)}% |`,
    '', `Far-lot model could remove (class b, bands >= ${FAR_M} m, household excluded): ${x.provisional ? 'needs the NonSimSplit run (no bands here)' : `${f1(ct.farLot)} ms/s (${(ct.farLot / 10).toFixed(1)}%)`}` +
    `${x.provisional ? '' : `; plus Sim-driven work of far Sims outside their own task (SimUpdate etc.): ${f1(ct.farSimDrv)} ms/s, the far-Sim model's`}`);
  return L.join('\n');
}

// ---- inputs ----
export function dumpOf(tag: string): Dump | undefined {
  if (!existsSync(QUEUE)) return undefined;
  const f = readdirSync(QUEUE).filter(n => n.startsWith(`${tag}-`) && /-\d{4}-ts3native\.log$/.test(n) && n.slice(tag.length + 1).match(/^\d{4}-ts3native\.log$/))
    .map(n => join(QUEUE, n)).sort((a, b) => statSync(b).mtimeMs - statSync(a).mtimeMs)[0];
  return f ? parseDump(readFileSync(f, 'utf8')) : undefined;
}
const tagsBy = (re: RegExp) => existsSync(PROFILE) ? readdirSync(PROFILE).flatMap(f => { const m = /^(.*)-\d+-seconds\.csv$/.exec(f); return m && re.test(m[1]) ? [{ tag: m[1], m: statSync(join(PROFILE, f)).mtimeMs }] : []; }).sort((a, b) => b.m - a.m) : [];

function selfCheck() {
  const log = ['x', 'nonsim split: 600 s, runs 10, owners 3 (full 0), unresolved 0 ms, no slot 0 ms, lots 2 (household 1, pushes 3, bad 0), locates 5, simdrv nodes 1, edges 4, self 120 ns per run; rows us = bands 0-6, hh, none, glob (docs/329)',
    'nonsim outside footprint: us 0,0,0,0,0,0,0,0,0,0', 'nonsim outside plans: us 0,0,600000,0,0,0,0,0,0,0',
    'nonsim class 1 lot runs 9 us 0,6000000,12000000,18000000,0,0,0,6000000,0,0 name Sims3.Gameplay.Core.Lot',
    'nonsim class 2 simdrv runs 9 us 600000,0,0,6000000,0,0,0,600000,0,0 name Sims3.Gameplay.Actors.SimUpdate',
    'nonsim class 3 delegate runs 1 us 0,0,0,0,0,0,0,0,0,1200000 name Sims3.Gameplay.Services.Services::Simulate',
    'nonsim class 4 sim runs 1 us 0,0,0,60000000,0,0,0,0,0,0 name Sims3.Gameplay.Actors.Sim',
    'nonsim class 0 rest runs 0 us 0,0,0,0,0,0,0,0,0,0 name (owners below the top 60 + table full)', 'later line'].join('\n');
  // regex cases against the exact format strings of nonsim.cpp (AGENTS.md: every regex tested on a real line and a non-match)
  assert.equal(parseDump('nonsim split off ([Diag] NonSimSplit=0)'), undefined);
  assert.equal(parseDump('nonsim class 1 lot runs 9 us 1 name X'), undefined); // no head line: no dump
  const d = parseDump(log)!;
  assert.equal(d.secs, 600); assert.equal(d.classes.length, 5); assert.equal(d.classes[0].us[3], 18000000); assert.equal(d.outside.plans[2], 600000);
  assert.equal(d.classes[2].name, 'Sims3.Gameplay.Services.Services::Simulate');
  assert.deepEqual(globalClass('Sims3.Gameplay.Services.Services::Simulate')[0], 'c'); assert.equal(globalClass('Sims3.Gameplay.Core.Camera')[0], 'a');
  const managed = [['x', 'Sims3.Gameplay.Autonomy.ReactionBroadcaster.Simulate', 'Sims3.Gameplay.Core.Lot.UpdateReactions', 'Sims3.Gameplay.Core.Lot.Simulate', 'ScriptCore.ScriptProxy.Simulate'],
    ['Sims3.Gameplay.Abstracts.ObjectSpawner.TriggerSpawn', 'Sims3.Gameplay.Utilities.AlarmTimerCallback.Invoke', 'Sims3.Gameplay.Utilities.AlarmManager.Simulate', 'Sims3.Gameplay.Core.Lot.Simulate'],
    ['Sims3.Gameplay.Core.Lot.UpdateDetailPriority', 'Sims3.Gameplay.Core.Lot.Simulate'], ['Sims3.Gameplay.Core.Lot.Simulate']];
  const sh = lotShares(managed);
  assert.equal(sh[0].share, 0.25); assert.equal(sh[1].share, 0.25); assert.equal(sh[4].share, 0.25); assert.equal(sh[5].share, 0.25);
  const rows = Array.from({ length: 200 }, (_, i) => ({ t: i * 1000, iters: 40, script: 100, taskObjects: 50, oneShot: 0, threaded: 0, animSacs: 1, animPose: 60, locomotion: 40, publish: 2, gcMark: 6, gcForcedMs: 1, fin: 1, svcUpdate: 30, preLoco: 30, pump: 2 }));
  const nat = { before: {}, after: { ubEdges: 4, ubEdgeM_0: 50, ubEdgeM_1: 150, ubEdgeM_2: 400, ubEdgeM_3: 1000, ubUs_0_3: 199 * 20000, ubUs_4_3: 199 * 10000, ubUs_5_3: 199 * 60000, ubUs_5_7: 199 * 10000 } };
  const x = analyzeRun({ tag: 't-bp-x', rows, hitches: [], managed, simstack: [], native: nat, ics: new Set() }, d);
  assert.ok(x.sims && Math.abs(x.sims.far - 90) < 1e-6 && Math.abs(x.sims.hh - 10) < 1e-6);
  const lotFar = x.lines.filter(l => l.kind === 'lot').reduce((s, l) => s + l.far, 0);
  assert.ok(Math.abs(lotFar - 50) < 1e-6, `lot far ${lotFar}`); // (12+18) s over 600 s = 50 ms/s in bands >= 150 m
  const sched = x.lines.find(l => l.owner.startsWith('scheduler'))!;
  assert.ok(Math.abs(sched.ms - (150 - 100 - 13 - 2 - 100)) < 1e-6 || sched.ms === 0); // seam 215 ms/s > stages 150: clamps at 0
  const total = (x.sims!.hh + x.sims!.near + x.sims!.far + x.sims!.noPos) + x.lines.reduce((s, l) => s + l.ms, 0);
  assert.ok(Math.abs(total - 1000) < 1e-6 || total > 1000, `rows sum ${total}`);
  const ct = classTotals(x);
  assert.ok(Math.abs(ct.farSimDrv - 10) < 1e-6, `simdrv far ${ct.farSimDrv}`); // 6 s / 600 s in band 3
  // the household rows are (a): the lot's 6 s household part (10 ms/s) never counts as b
  assert.ok(ct.t.a >= 10 + x.sims!.hh);
  assert.match(report(x), /Far-lot model could remove \(class b, bands >= 150 m, household excluded\): \d/);
  const p = analyzeRun({ tag: 't-sv-x', rows, hitches: [], managed, simstack: [], native: nat, ics: new Set() }, undefined);
  assert.ok(p.provisional && p.lines.some(l => l.owner.startsWith('Lot: reactions')));
  assert.match(report(p), /PROVISIONAL/);
  console.log('nonsim-split-report self-check ok');
}

if (import.meta.main ?? process.argv[1]?.endsWith('nonsim-split-report.ts')) {
  const args = process.argv.slice(2);
  if (args.includes('--self-check')) { selfCheck(); process.exit(0); }
  const prov = args.includes('--provisional');
  let tags = args.filter(a => !a.startsWith('--'));
  if (!tags.length) for (const w of ['bp', 'ip', 'sv']) { const t = tagsBy(new RegExp(`^nonsim-split-.*-${w}-nonsim-split$`))[0]; if (t) tags.push(t.tag); }
  if (!tags.length) { console.error('no nonsim-split-* runs found; pass tags'); process.exit(1); }
  for (const t of tags) console.log(report(analyzeRun(load(t), prov ? undefined : dumpOf(t))), '\n');
}
