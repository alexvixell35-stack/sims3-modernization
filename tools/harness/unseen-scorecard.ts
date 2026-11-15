// Unseen scorecard: does the unseen-Sim handling (ts3native UnseenFallback / KD cuts, ts3fixes unseenfb holds and shortcut
// moves) stay off seen and household Sims, and do the Sims it touches still get on with their lives? One PASS / WARN / FAIL
// row per check for each phase dir (ts3native.log + the ts3agent sim_health census in health.jsonl):
//   1 safety      unseen-only actions on a seen / household / selectable Sim: native cuts by scope (seen must be 0), cuts
//                 in view with KdCutCamRecheck off ("kd cam recheck: ... would hold, in view", listed with the camera
//                 evidence), and from the census: holds, shortcut moves and our ended interactions on household Sims
//   2 recovery    route retry after a cut: overall (native totals) and per species / occult (sampled routefail retry lines)
//   3 progress    interactions ended per Sim-hour, unseen vs seen NPCs; Sims unseen with no ended interaction for >= --stuck-h
//   4 shortcuts   interactions our shortcuts touched that then failed / were cancelled (ours) vs completed (helped), and
//                 whether a newly moved Sim is on the lot it was moved to
//   5 explain     ufdriverless (driverless vehicle routes) and goalhh (unseen NPCs exempted because they work for the
//                 household) lines, classified
//   6 cost        unseen fallback jobs' share of route jobs, long jobs by scope, ufplan wait (sim side) by why
//   node tools/harness/unseen-scorecard.ts <phase dir>... [--log <ts3native.log>] [--stuck-h 4] [--lists 12]
//   node tools/harness/unseen-scorecard.ts --self-check
import { existsSync, readFileSync } from 'node:fs';
import { basename, join } from 'node:path';
import { parseArgs } from 'node:util';
import { groupsOf, parseHealth } from './sim-health-report.ts';

type Row = Record<string, any>;
type Grade = 'PASS' | 'WARN' | 'FAIL' | 'n/a';
export type Check = { n: number; name: string; grade: Grade; line: string; list: string[] };

const last = (lines: string[], re: RegExp) => { for (let i = lines.length - 1; i >= 0; i--) { const m = re.exec(lines[i]); if (m) return m; } return null; };
const pct = (a: number, b: number) => (b > 0 ? Math.round(1000 * a / b) / 10 : NaN);
const q = (v: number[], p: number) => { if (!v.length) return 0; const s = [...v].sort((a, b) => a - b); return s[Math.min(s.length - 1, Math.floor(p * s.length))]; };
const worst = (...g: Grade[]): Grade => (g.includes('FAIL') ? 'FAIL' : g.includes('WARN') ? 'WARN' : g.every(x => x === 'n/a') ? 'n/a' : 'PASS');

const RE_KD = /route kind deadlines: .*?after a cut ok (\d+) fail (\d+) \(uncut ok (\d+)\).*?cuts by scope unknown (\d+) seen (\d+) unseen (\d+) npc (\d+); npc ceiling \d+ ms cuts (\d+); camera recheck mode (\d+) checks (\d+) held (\d+) stale (\d+) would hold (\d+)/;
const RE_CAM = /kd cam recheck: route ([0-9A-F]{8}) (held, in view|held, stale snapshot|would hold, in view \(switch off\)|would hold, stale snapshot \(switch off\)), job seq \d+ (\d+) ms.*?follower ([0-9A-F]{16}),/;
const RE_JOB = /routefail job: route ([0-9A-F]{8}), (.*?), job seq \d+ (\d+) ms result (-?\d+), (cut \(([A-Za-z]+), mask \d+\)|uncut(?: \(exempt\))?), kind .*?follower ([0-9A-F]{16}),.*, (unseen|seen npc|seen|scope unknown)$/;
const RE_RETRY = /routefail retry: route [0-9A-F]{8}, .*?, backoff level \d+, (succeeded|failed), .*?follower ([0-9A-F]{16}),/;
const RE_UFB = /unseen fallback: mode \d+ .*?jobs (\d+) cut (\d+)/;
const RE_ASYNC = /route async: mode \d+ started (\d+) refused \d+ collected \d+ dropped \d+ job (\d+) ms/;
const RE_LONG = /route async: long job (\d+) ms, .*?, scope ([a-z -]+), at \d+ ms/;
const RE_UFPLAN = /ufplan: kind (\d+) (\d+) ms why (-?\d+)(.*?), tick \d+, arm \S+, at (\d+) ms/;
const WHY: Record<string, string> = { '-1': 'not asked', '0': 'unseen', '1': 'not live', '2': 'gone', '3': 'household', '4': 'active lot', '5': 'on screen', '6': 'near viewers', '7': 'held', '8': 'social seen', '9': 'goal household', '10': 'birth' };  // UnseenFallbackCore kSeen.. (0 = unseen allowed)
// census classes: scope household / selected = the player's; npc unseen (why 0) or held (7, or in a hold) = unseen
export const cls = (r: Row) => (r.scope !== 'npc' ? 'household' : r.unseen_why === 0 || r.unseen_why === 7 || r.uf_hold ? 'unseen' : 'seen npc');
// census trapped is a bool, or its string form in older censuses
export const isTrapped = (r: Row) => r.trapped === true || r.trapped === 'True';
const idle = (r: Row) => !r.interaction && !(r.queue > 0);
const f1 = (x: unknown) => (typeof x === 'number' ? Math.round(x * 10) / 10 : x);
const LONG_BY_DESIGN = /sleep|nap|work|school|rabbit|class|career|lecture|mission|vacation|stage|perform|ghost|haunt|bury|grave/i;

export function scorecard(log: string, healthText: string, stuckH = 4, lists = 12): Check[] {
  const L = log.split(/\r?\n/), samples = parseHealth(healthText);
  const sim = new Map<string, Row>();  // oid -> last census row
  for (const s of samples) for (const r of s.sims) if (r.oid) sim.set(String(r.oid), r);
  const who = (oid: string) => { const r = sim.get(oid); return r ? `${r.name} [${r.species}${(r.occults ?? []).length ? ' ' + r.occults.join('+') : ''}, ${r.scope}]` : `sim ${oid} (not in census)`; };
  const out: Check[] = [];

  // 1 safety
  const kd = last(L, RE_KD), s1: string[] = [];
  let g1: Grade = kd ? 'PASS' : 'n/a', line1 = 'no "route kind deadlines" line';
  const jobIa = new Map<string, string>();
  let hhCuts = 0;
  for (const l of L) {
    const m = RE_JOB.exec(l);
    if (!m) continue;
    jobIa.set(m[1], m[2]);
    const r = sim.get(m[7]);
    if (m[6] === 'UnseenFallback' && r && r.scope !== 'npc') { hhCuts++; if (s1.length < lists) s1.push(`cut on household Sim: ${who(m[7])}, route ${m[1]}, ${m[2]}, ${m[3]} ms`); }
  }
  const cam: string[] = [];
  for (const l of L) { const m = RE_CAM.exec(l); if (m && m[2].startsWith('would hold')) cam.push(`in view, cut: ${who(m[4])}, route ${m[1]}${jobIa.has(m[1]) ? ', ' + jobIa.get(m[1]) : ''}, job ${m[3]} ms, camera: ${m[2].replace(' (switch off)', '')}`); }
  let hhHolds = 0, hhMoves = 0, hhOurs = 0, heldSeen = 0;
  for (const s of samples) for (const r of s.sims) {
    if (r.scope !== 'npc') {
      if ((r.holds ?? 0) > 0 || r.uf_hold) { hhHolds++; if (s1.length < lists) s1.push(`hold on household Sim: ${r.name} (${r.scope}), holds ${r.holds}`); }
      if (r.moved_new) { hhMoves++; if (s1.length < lists) s1.push(`shortcut move of household Sim: ${r.name} (${r.scope}): ${r.moved}`); }
      if ((r.ours ?? 0) > 0) { hhOurs += r.ours; if (s1.length < lists) s1.push(`our shortcut ended household Sim's interaction: ${r.name} x ${(r.ours_ends ?? []).map((e: Row) => e.definition).join(', ')}`); }
    } else if (r.uf_hold && [3, 4, 6].includes(r.unseen_why)) heldSeen++;
  }
  if (kd) {
    const [unk, seen, uns, npc, ceil, camMode, , , , would] = [4, 5, 6, 7, 8, 9, 10, 11, 12, 13].map(i => Number(kd[i]));
    g1 = worst(seen + hhCuts + hhHolds + hhMoves + hhOurs > 0 ? 'FAIL' : 'PASS', unk > 0 || (camMode === 0 && would > 0) || heldSeen > 0 ? 'WARN' : 'PASS');
    line1 = `native cuts by scope: seen ${seen}, unknown ${unk}, unseen ${uns}, seen-NPC ceiling ${npc} (by design, ${ceil} ceiling cuts); in view at the cut ${would} (KdCutCamRecheck ${camMode}${camMode === 0 ? ', off: cut anyway' : ''}); census: household cuts ${hhCuts}, holds ${hhHolds}, moves ${hhMoves}, ours ${hhOurs}; held while seen at a sample ${heldSeen}`;
  }
  out.push({ n: 1, name: 'safety', grade: g1, line: line1, list: [...s1, ...cam.slice(0, lists), ...(cam.length > lists ? [`... ${cam.length - lists} more in-view cuts logged`] : [])] });

  // 2 recovery
  const per = new Map<string, [number, number]>();
  for (const l of L) {
    const m = RE_RETRY.exec(l);
    if (!m) continue;
    const r = sim.get(m[2]), gs = r ? groupsOf(r, false, false) : ['(not in census)'];
    for (const g of gs) { const a = per.get(g) ?? [0, 0]; a[m[1] === 'succeeded' ? 0 : 1]++; per.set(g, a); }
  }
  const s2: string[] = [];
  let g2: Grade = 'n/a', line2 = 'no totals';
  if (kd) {
    const ok = Number(kd[1]), fail = Number(kd[2]), p = pct(ok, ok + fail);
    g2 = ok + fail === 0 ? 'n/a' : p < 75 ? 'FAIL' : p < 90 ? 'WARN' : 'PASS';
    line2 = `retry after a cut ok ${ok} fail ${fail} = ${p}%`;
    for (const [g, [o, f]] of [...per].sort((a, b) => (a[1][0] + a[1][1] > b[1][0] + b[1][1] ? -1 : 1))) {
      const pg = pct(o, o + f);
      s2.push(`${g}: ${o}/${o + f} = ${pg}% (sampled lines)`);
      if (o + f >= 5 && pg < 75) g2 = worst(g2, 'WARN');
    }
  }
  out.push({ n: 2, name: 'recovery', grade: g2, line: line2, list: s2 });

  // 3 progress
  const rate = new Map<string, [number, number]>();  // class -> [ended, sim minutes]
  const zero = new Map<string, [number, number]>();  // class -> [sim minutes in sample windows with no ended interaction, all]
  const idleN = new Map<string, [number, number]>();  // class -> [idle rows, rows]
  const run = new Map<string, number>(), worstRun = new Map<string, { min: number; r: Row }>(), simsOf = new Map<string, Set<string>>();
  for (let k = 0; k < samples.length; k++) {
    const s = samples[k], dt = s.since_min ?? (k ? Math.max(0, s.sim_minutes - samples[k - 1].sim_minutes) : 0);
    if (!(dt > 0)) continue;
    for (const r of s.sims) {
      if (!r.id) continue;
      const c = cls(r), a = rate.get(c) ?? [0, 0];
      a[0] += r.ended ?? 0; a[1] += dt; rate.set(c, a);
      const z = zero.get(c) ?? [0, 0]; z[0] += (r.ended ?? 0) === 0 ? dt : 0; z[1] += dt; zero.set(c, z);
      const iN = idleN.get(c) ?? [0, 0]; iN[0] += idle(r) ? 1 : 0; iN[1]++; idleN.set(c, iN);
      const id = String(r.id);
      (simsOf.get(c) ?? simsOf.set(c, new Set()).get(c)!).add(id);
      // a run of samples with no ended interaction, in one class (unseen or seen NPC; the baseline for "stuck")
      const key = `${c}|${id}`;
      for (const k of [`unseen|${id}`, `seen npc|${id}`]) if (k !== key) run.set(k, 0);
      if (c !== 'household' && (r.ended ?? 0) === 0) {
        const m = (run.get(key) ?? 0) + dt;
        run.set(key, m);
        if (m >= stuckH * 60 && m > (worstRun.get(key)?.min ?? 0)) worstRun.set(key, { min: m, r });
      } else run.set(key, 0);
    }
  }
  const rt = (c: string) => { const a = rate.get(c); return a && a[1] > 0 ? a[0] * 60 / a[1] : NaN; };
  const ru = rt('unseen'), rs = rt('seen npc'), ratio = ru / rs;
  const stuckOf = (c: string) => [...worstRun].filter(([k]) => k.startsWith(c + '|')).map(([, v]) => v).sort((a, b) => b.min - a.min);
  const stuck = stuckOf('unseen'), seenStuck = stuckOf('seen npc');
  const real = stuck.filter(x => !LONG_BY_DESIGN.test(String(x.r.interaction ?? '')));
  const idleStuck = stuck.filter(x => idle(x.r)).length;
  const ip = (c: string) => { const a = idleN.get(c); return a ? `${pct(a[0], a[1])}%` : '-'; };
  const unseenSims = simsOf.get('unseen') ?? new Set<string>(), seenSims = simsOf.get('seen npc') ?? new Set<string>();
  // stuck share, unseen vs the seen-NPC baseline (EA's NPCs idle a lot off the active lot either way)
  // graded on the time share of sample windows with no ended interaction (same window lengths for both classes; runs of
  // windows favour the class a Sim stays in, and unseen lasts longer than seen)
  const zp = (c: string) => { const a = zero.get(c); return a ? pct(a[0], a[1]) : NaN; };
  const su = pct(stuck.length, unseenSims.size), ss = pct(seenStuck.length, seenSims.size), extra = zp('unseen') - (Number.isFinite(zp('seen npc')) ? zp('seen npc') : 0);
  const g3: Grade = !Number.isFinite(ratio) ? 'n/a' : ratio < 0.4 || extra > 15 ? 'FAIL' : ratio < 0.7 || extra > 5 ? 'WARN' : 'PASS';
  out.push({ n: 3, name: 'progress', grade: g3,
    line: `ended per Sim-hour: unseen ${ru.toFixed(2)}, seen NPC ${rs.toFixed(2)}, household ${rt('household').toFixed(2)} (unseen/seen ${ratio.toFixed(2)}); no end for >= ${stuckH} h: unseen ${stuck.length} of ${unseenSims.size} (${su}%; ${real.length} not sleep/work-like, ${idleStuck} idle with an empty queue) vs seen NPC ${seenStuck.length} of ${seenSims.size} (${ss}%); time in windows with no end: unseen ${zp('unseen')}%, seen NPC ${zp('seen npc')}%; idle-with-empty-queue share of samples: unseen ${ip('unseen')}, seen NPC ${ip('seen npc')}, household ${ip('household')}`,
    list: stuck.slice(0, lists).map(({ min, r }) => `${(min / 60).toFixed(1)} h: ${r.name} [${r.species}${(r.occults ?? []).length ? ' ' + r.occults.join('+') : ''}] ${idle(r) ? 'idle, empty queue' : `${r.interaction} (${f1(r.interaction_min)} min)`}, still ${f1(r.still_min)} min, ${r.uf_hold ? 'in hold, ' : ''}holds ${r.holds ?? 0}, route fails ${r.route_fails ?? 0}${r.route_locked ? ' locked ' + r.route_locked : ''}${isTrapped(r) ? ', trapped in a room' : ''}, on ${r.lot}${LONG_BY_DESIGN.test(String(r.interaction ?? '')) ? ' (by design?)' : ''}`) });

  // 4 shortcuts
  let ours = 0, helped = 0, hasHelped = false, moved = 0, onGoal = 0;
  const whyN = new Map<string, number>();  // census ours_why (newer ts3agent): "definition: reason"
  const byDef = new Map<string, [number, number]>();  // interaction definition -> [ended by us, helped]
  for (const s of samples) for (const r of s.sims) {
    ours += r.ours ?? 0;
    if (typeof r.helped === 'number') { hasHelped = true; helped += r.helped; }
    for (const e of r.ours_ends ?? []) { const d = byDef.get(e.definition) ?? [0, 0]; d[0] += e.count; byDef.set(e.definition, d); }
    for (const e of r.ours_why ?? []) whyN.set(e.definition, (whyN.get(e.definition) ?? 0) + e.count);
    for (const e of r.helped_ends ?? []) { const d = byDef.get(e.definition) ?? [0, 0]; d[1] += e.count; byDef.set(e.definition, d); }
    if (r.moved_new && typeof r.moved === 'string') {
      moved++;
      const b = /^\S+ (.*?) @/.exec(r.moved);
      if (b && String(r.lot).trim() === b[1].trim()) onGoal++;
    }
  }
  const endedNpc = (rate.get('unseen')?.[0] ?? 0) + (rate.get('seen npc')?.[0] ?? 0), ofNpc = pct(ours, endedNpc);
  // without helped (older census) grade on ours against all NPC ends
  const share = hasHelped ? pct(ours, ours + helped) : ofNpc;
  const g4: Grade = !(ours + helped) ? 'n/a' : share > 20 ? 'FAIL' : share > 5 ? 'WARN' : 'PASS';
  out.push({ n: 4, name: 'shortcuts', grade: g4,
    line: `touched by a shortcut: ended by us ${ours} (${ofNpc}% of ${endedNpc} NPC ends), helped ${hasHelped ? helped : 'not in this census (older ts3agent)'}${hasHelped ? ` (${share}% ended by us)` : ''}; new moves ${moved}, on the moved-to lot at the sample ${onGoal} (${pct(onGoal, moved)}%; later moves elsewhere are normal)`,
    list: [...[...byDef].sort((a, b) => b[1][0] - a[1][0]).slice(0, lists).map(([d, [o, h]]) => `${d.replace(/^.*\.(?=[^.]*\+)|^.*\./, '')}: ended by us ${o}, helped ${h}${o >= 5 && o >= h ? '  <- lead: the shortcut breaks it more often than not' : ''}`),
      ...[...whyN].sort((a, b) => b[1] - a[1]).slice(0, lists).map(([k, v]) => `why ${v} x ${k.replace(/^[^:]*\.(?=[^.:]*:)/, '')}`)] });

  // 5 explain ufdriverless / goalhh
  const drv = new Map<string, number>(), gh = new Map<string, number>();
  for (const l of L) {
    let m = /ufdriverless: (\S+) (ownable|not ownable)( household occupant)?/.exec(l);
    if (m) { const k = `${m[1]} ${m[2]}${m[3] ?? ''}: ${m[2] === 'not ownable' && !m[3] ? 'ambient traffic, unseen rules apply' : 'kept as household, no cuts'}`; drv.set(k, (drv.get(k) ?? 0) + 1); continue; }
    m = /goalhh: .*? kind (\d+) exempt, (.*)$/.exec(l);
    if (m) { const k = `kind ${m[1]} ${m[2]}`; gh.set(k, (gh.get(k) ?? 0) + 1); }
  }
  const nd = [...drv.values()].reduce((a, b) => a + b, 0), ng = [...gh.values()].reduce((a, b) => a + b, 0);
  const failedChecks = [...gh].filter(([k]) => k.endsWith('check failed')).reduce((a, [, v]) => a + v, 0);
  out.push({ n: 5, name: 'explain', grade: nd + ng === 0 ? 'n/a' : failedChecks ? 'WARN' : 'PASS',
    line: `ufdriverless ${nd}${nd >= 50 ? ' (log cap 50 reached)' : ''}: routes with no Sim (a vehicle); unseen only for a non-ownable vehicle with no household occupant. goalhh ${ng}${ng >= 100 ? ' (cap 100 a load)' : ''}: unseen NPCs exempted from every unseen feature because the route serves the household${failedChecks ? `; ${failedChecks} exempted because the check threw` : ''}`,
    list: [...[...drv].map(([k, v]) => `ufdriverless ${v} x ${k}`), ...[...gh].map(([k, v]) => `goalhh ${v} x ${k}`)] });

  // 6 cost
  const ufb = last(L, RE_UFB), asy = last(L, RE_ASYNC);
  const longBy = new Map<string, [number, number]>();
  for (const l of L) { const m = RE_LONG.exec(l); if (m) { const a = longBy.get(m[2]) ?? [0, 0]; a[0]++; a[1] += Number(m[1]); longBy.set(m[2], a); } }
  const uf = new Map<string, number[]>();
  let t0 = -1, t1 = -1;
  for (const l of L) { const m = RE_UFPLAN.exec(l); if (!m) continue; const k = WHY[m[3]] ?? `why ${m[3]}`; (uf.get(k) ?? uf.set(k, []).get(k)!).push(Number(m[2])); const t = Number(m[5]); if (t0 < 0) t0 = t; t1 = t; }
  const hours = t1 > t0 ? (t1 - t0) / 3.6e6 : NaN, u = uf.get('unseen') ?? [];
  const p95 = q(u, 0.95);
  out.push({ n: 6, name: 'cost', grade: !ufb && !u.length ? 'n/a' : p95 >= 800 ? 'FAIL' : p95 >= 400 ? 'WARN' : 'PASS',
    line: `unseen fallback jobs ${ufb ? ufb[1] : '?'} of ${asy ? asy[1] : '?'} route jobs (${ufb && asy ? pct(Number(ufb[1]), Number(asy[1])) : '?'}%), cut ${ufb ? ufb[2] : '?'}; worker job time ${asy ? Math.round(Number(asy[2]) / 1000) : '?'} s; ufplan wait unseen n ${u.length} p50 ${q(u, 0.5)} p95 ${p95} ms, ${Number.isFinite(hours) ? Math.round(u.reduce((a, b) => a + b, 0) / 1000 / hours) : '?'} s per real hour (logged plans)`,
    list: [...[...uf].map(([k, v]) => `ufplan ${k}: n ${v.length} p50 ${q(v, 0.5)} p95 ${q(v, 0.95)} max ${q(v, 1)} ms, sum ${Math.round(v.reduce((a, b) => a + b, 0) / 1000)} s`),
      ...[...longBy].map(([k, [n, ms]]) => `long jobs (logged) scope ${k}: ${n}, ${Math.round(ms / 1000)} s`)] });
  return out;
}

export function render(name: string, cs: Check[], full: boolean): string {
  const o = [`## ${name}: ${worst(...cs.map(c => c.grade))}`];
  for (const c of cs) {
    o.push(`${c.grade.padEnd(4)} ${c.n} ${c.name}: ${c.line}`);
    if (full || c.grade === 'FAIL' || c.grade === 'WARN') for (const l of c.list) o.push(`       ${l}`);
  }
  return o.join('\n');
}

function selfCheck() {
  const ok = (c: unknown, m: string) => { if (!c) { console.error('unseen-scorecard self-check FAILED: ' + m); process.exit(1); } };
  const kd = (seen: number, would: number, okN: number, fail: number) => `route kind deadlines: mode 1; x; after a cut ok ${okN} fail ${fail} (uncut ok 0), stuck checks 0; cuts by scope unknown 0 seen ${seen} unseen 10 npc 1; npc ceiling 2000 ms cuts 1; camera recheck mode 0 checks 10 held 0 stale 0 would hold ${would}`;
  const row = (o: Row) => ({ id: o.oid, name: 'N', species: 'human', occults: [], scope: 'npc', unseen_why: 0, ended: 1, holds: 0, ours: 0, lot: 'L', ...o });
  const h = (sims: Row[], since = 120) => JSON.stringify({ result: { sim_minutes: 1000, since_min: since, sims } });
  const health = [h([row({ oid: 'A' }), row({ oid: 'B', unseen_why: 4, ended: 2 }), row({ oid: 'C', scope: 'household', unseen_why: 3 })]),
    h([row({ oid: 'A', ended: 0, interaction: 'Chat' }), row({ oid: 'B', unseen_why: 4, ended: 2 }), row({ oid: 'C', scope: 'household', unseen_why: 3 })], 300)].join('\n');
  let c = scorecard(kd(0, 0, 95, 5), health, 4);
  ok(c[0].grade === 'PASS' && c[1].grade === 'PASS', 'clean safety and recovery');
  ok(c[2].list.length === 1 && c[2].list[0].includes('5.0 h'), 'stuck unseen Sim found');
  c = scorecard(kd(1, 0, 95, 5), health);
  ok(c[0].grade === 'FAIL', 'a seen-scope cut fails safety');
  c = scorecard(kd(0, 3, 60, 40) + '\nkd cam recheck: route 0000ABCD would hold, in view (switch off), job seq 1 200 ms result 16, cut (UnseenFallback, mask 2), kind own Plan deadline 150 ms, follower 00000000000000AA, plan 1 host 1 other 1 ms, capAt 1 tail 0 ms, nodes 0 in 1 plans, unseen', health);
  ok(c[0].grade === 'WARN' && c[0].list.some(l => l.includes('in view')), 'in-view cuts warn and are listed');
  ok(c[1].grade === 'FAIL', 'low retry success fails recovery');
  const hh = h([row({ oid: 'C', scope: 'household', unseen_why: 3, holds: 1 })]);
  ok(scorecard(kd(0, 0, 95, 5), hh)[0].grade === 'FAIL', 'a hold on a household Sim fails safety');
  const e = scorecard('ts3fixes: ufdriverless: LightweightTaxi not ownable\nts3fixes: goalhh: X Y kind 0 exempt, household lot', '');
  ok(e[4].list.length === 2 && e[4].list[0].includes('ambient traffic'), 'ufdriverless / goalhh classified');
  console.log('unseen-scorecard self-check: ok');
}

const { values, positionals } = parseArgs({ allowPositionals: true, options: { log: { type: 'string' }, 'stuck-h': { type: 'string' }, lists: { type: 'string' }, full: { type: 'boolean' }, 'self-check': { type: 'boolean' } } });
if (process.argv[1]?.endsWith('unseen-scorecard.ts')) {
  if (values['self-check']) selfCheck();
  else {
    if (!positionals.length) { console.error('usage: node tools/harness/unseen-scorecard.ts <phase dir>... [--log file] [--stuck-h 4] [--lists 12] [--full] | --self-check'); process.exit(2); }
    for (const d of positionals) {
      const lp = values.log ?? join(d, 'ts3native.log'), hp = join(d, 'health.jsonl');
      const arm = existsSync(join(d, 'ab-arm.txt')) ? ` (${readFileSync(join(d, 'ab-arm.txt'), 'utf8').trim()})` : '';
      const cs = scorecard(existsSync(lp) ? readFileSync(lp, 'utf8') : '', existsSync(hp) ? readFileSync(hp, 'utf8') : '', Number(values['stuck-h'] ?? 4), Number(values.lists ?? 12));
      console.log(render(basename(d) + arm, cs, !!values.full) + '\n');
    }
  }
}
