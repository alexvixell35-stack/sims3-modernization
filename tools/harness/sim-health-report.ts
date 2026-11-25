// Soak health report from the ts3agent `sim_health` census (SimHealthHandlers.cs): one JSON answer per line in
// health.jsonl (the soak's `health` / `unseengroups` phases append them), optionally joined with ts3native.log by the Sims'
// object ids (sim_health "oid" = the "follower <hex>" / "sim <hex>" fields of ts3native's routefail and KD lines).
//   node tools/harness/sim-health-report.ts <health.jsonl> [--log ts3native.log] [--stuck-min 120] [--still-min 240] [--json]
//   node tools/harness/sim-health-report.ts --self-check
// Prints: Sims locked in a room they cannot leave, stuck Sims (same head interaction > --stuck-min sim minutes, or not
// moved for > --still-min, sleeping and other long by-design interactions listed too: judge by the name), the sample
// spacing (sim minutes between calls; counters cover since_min, the span since the census's previous call), failure spam
// (Sim x definition x count, failure exit reasons only), short ends (by design often, lower ranked), ends our own
// shortcuts cut (ended by us: an unseenfb hold or ts3fixes move touched the interaction and it then failed, was cancelled
// or ended short; never spam) apart from ones they helped (touched, then completed), each with the Sims' hunger / energy
// at the sample (censuses before since_min: censuses before since_min mixed game cancels and
// short ends into spam and had no ours, labelled), and per group (human, dog, cat, horse, other, each occult, unseen, held) the
// rates per Sim-hour of route fails, locked-portal fails, failed / game-cancelled / short interactions, unseen holds and
// shortcut moves, plus from the log: unseen cuts, cut -> retry success, KD NPC ceiling cuts; and the state of non-human
// Sims after a hold ended (posture / walk style / terrain), so a group whose unseen handling breaks stands out.
// Household Sims are reported like any other (the census reads, never acts); scope is a column.
import { readFileSync } from 'node:fs';
import { parseArgs } from 'node:util';

type Row = Record<string, any>;
type Sample = { sim_minutes: number; since_min: number | null; sims: Row[] };

export function parseHealth(text: string): Sample[] {
  const out: Sample[] = [];
  for (const line of text.split(/\r?\n/)) {
    const i = line.indexOf('{');
    if (i < 0) continue;
    let j: any;
    try { j = JSON.parse(line.slice(i)); } catch { continue; }
    const r = j && typeof j === 'object' && 'result' in j ? j.result : j;
    if (r && Array.isArray(r.sims)) out.push({ sim_minutes: Number(r.sim_minutes) || 0, since_min: typeof r.since_min === 'number' ? r.since_min : null, sims: r.sims });
  }
  return out;
}

// ts3native lines keyed by follower hex (upper case, 16 digits)
export const RE_JOB = /^.*?routefail job: route [0-9A-F]{8}, .*?, (cut \(([A-Za-z]+), mask \d+\)|uncut(?: \(exempt\))?), kind .*?, follower ([0-9A-F]{16}),/;
export const RE_RETRY = /^.*?routefail retry: route [0-9A-F]{8}, .*?, backoff level \d+, (succeeded|failed), .*?, follower ([0-9A-F]{16}),/;
export const RE_KDNPC = /^.*?KD NPC CEILING CUT: route [0-9A-F]{8} sim ([0-9A-F]{16}) kind /;

type LogTally = { cuts: number; ufCuts: number; retryOk: number; retryFail: number; kdNpc: number; jobFails: number };
export function parseLog(text: string): Map<string, LogTally> {
  const m = new Map<string, LogTally>();
  const get = (id: string) => { let t = m.get(id); if (!t) m.set(id, t = { cuts: 0, ufCuts: 0, retryOk: 0, retryFail: 0, kdNpc: 0, jobFails: 0 }); return t; };
  for (const l of text.split(/\r?\n/)) {
    let x = RE_JOB.exec(l);
    if (x) { const t = get(x[3]); t.jobFails++; if (x[2]) { t.cuts++; if (x[2] === 'UnseenFallback') t.ufCuts++; } continue; }
    x = RE_RETRY.exec(l);
    if (x) { const t = get(x[2]); if (x[1] === 'succeeded') t.retryOk++; else t.retryFail++; continue; }
    x = RE_KDNPC.exec(l);
    if (x) get(x[1]).kdNpc++;
  }
  return m;
}

export function groupsOf(r: Row, unseen: boolean, held: boolean): string[] {
  const g = [String(r.species ?? 'other')];
  for (const o of r.occults ?? []) g.push(String(o));
  if (unseen) g.push('unseen');
  if (held) g.push('held');
  return g;
}

type Acc = { sims: Set<string>; simMin: number; route: number; locked: number; failed: number; gameCancel: number; short: number; ours: number; ended: number; holds: number; moves: number;
  cuts: number; ufCuts: number; retryOk: number; retryFail: number; kdNpc: number; logSims: number };
const newAcc = (): Acc => ({ sims: new Set(), simMin: 0, route: 0, locked: 0, failed: 0, gameCancel: 0, short: 0, ours: 0, ended: 0, holds: 0, moves: 0,
  cuts: 0, ufCuts: 0, retryOk: 0, retryFail: 0, kdNpc: 0, logSims: 0 });

export function report(samples: Sample[], log: Map<string, LogTally> | null, stuckMin: number, stillMin: number) {
  const groups = new Map<string, Acc>();
  const acc = (g: string) => { let a = groups.get(g); if (!a) groups.set(g, a = newAcc()); return a; };
  const locked = new Map<string, string>(), stuck = new Map<string, string>(), spam = new Map<string, number>(), shortEnds = new Map<string, number>(), ours = new Map<string, number>(), helped = new Map<string, number>(), after: string[] = [];
  const gaps: number[] = [];
  const legacy = samples.length > 0 && samples.every((s) => s.since_min === null);
  const simGroups = new Map<string, Set<string>>(), names = new Map<string, string>();
  for (let k = 0; k < samples.length; k++) {
    const s = samples[k];
    // the span this sample's counters cover: the census's own since_min, else (older censuses) the gap to the previous sample
    const dt = s.since_min !== null ? s.since_min : k > 0 ? Math.max(0, s.sim_minutes - samples[k - 1].sim_minutes) : 0;
    if (k > 0) gaps.push(Math.max(0, s.sim_minutes - samples[k - 1].sim_minutes));
    for (const r of s.sims) {
      if (r.error || !r.id) continue;
      const id = String(r.id), who = `${r.name} [${r.species}${(r.occults ?? []).length ? ' ' + r.occults.join('+') : ''}, ${r.scope}]`;
      names.set(String(r.oid ?? ''), id);
      const gs = groupsOf(r, r.unseen_why === 0, !!r.uf_hold || (r.holds ?? 0) > 0);
      let sg = simGroups.get(id); if (!sg) simGroups.set(id, sg = new Set());
      for (const g of gs) {
        sg.add(g);
        const a = acc(g);
        a.sims.add(id); a.simMin += dt;
        a.route += r.route_fails ?? 0; a.locked += r.route_locked ?? 0; a.failed += r.failed ?? 0; a.gameCancel += r.game_cancelled ?? 0;
        a.short += r.short ?? 0; a.ours += r.ours ?? 0; a.ended += r.ended ?? 0; a.holds += r.holds ?? 0; a.moves += r.moved_new ? 1 : 0;
      }
      if (r.trapped === true || r.trapped === 'True')  // older censuses wrote the bool as a string
        locked.set(id, `${who} room ${r.room} on ${r.lot}${r.hidden_room ? ' (hidden room)' : ''}`);
      if ((r.interaction_min ?? 0) > stuckMin) stuck.set(id, `${who}: ${r.interaction} for ${r.interaction_min} sim-min on ${r.lot}`);
      else if ((r.still_min ?? 0) > stillMin) stuck.set(id, `${who}: not moved for ${r.still_min} sim-min (${r.interaction ?? 'idle'}) on ${r.lot}`);
      const mot = typeof r.hunger === 'number' ? ` (hunger ${r.hunger} energy ${r.energy})` : '';
      const add = (m: Map<string, number>, l: any[] | undefined, tag = '') => { for (const sp of l ?? []) { const key = `${who} x ${sp.definition}${tag}`; m.set(key, (m.get(key) ?? 0) + sp.count); } };
      add(spam, r.spam); add(shortEnds, r.short_ends); add(ours, r.ours_ends, mot); add(helped, r.helped_ends, mot);
      if (r.species !== 'human' || (r.occults ?? []).length) for (const h of r.after_hold ?? []) if (after.length < 60) after.push(`${who}: ${h}`);
    }
  }
  if (log) for (const [oid, t] of log) {
    const id = names.get(oid);
    const gs = id ? simGroups.get(id) : undefined;
    for (const g of gs ?? ['(not in census)']) {
      const a = acc(g);
      a.cuts += t.cuts; a.ufCuts += t.ufCuts; a.retryOk += t.retryOk; a.retryFail += t.retryFail; a.kdNpc += t.kdNpc; a.logSims++;
    }
  }
  gaps.sort((a, b) => a - b);
  const spacing = gaps.length ? { min: gaps[0], median: gaps[gaps.length >> 1], max: gaps[gaps.length - 1] } : null;
  return { groups, locked, stuck, spam, shortEnds, ours, helped, after, samples: samples.length, spacing, legacy };
}

const per = (n: number, min: number) => (min > 0 ? (n * 60 / min).toFixed(2) : '-');
const pct = (a: number, b: number) => (a + b > 0 ? `${Math.round(100 * a / (a + b))}%` : '-');

export function render(r: ReturnType<typeof report>): string {
  const h = (m: number) => (m / 60).toFixed(1);
  const o: string[] = [`sim_health report: ${r.samples} samples` + (r.spacing ? `, ${h(r.spacing.min)} / ${h(r.spacing.median)} / ${h(r.spacing.max)} sim-hours apart (min / median / max)` : '')
    + (r.legacy ? ' (older census: counters span the whole gap, spam includes game cancels and short ends, no ours)' : '')];
  o.push(`\n== locked in a room (no portal out the Sim may pass): ${r.locked.size}`); for (const v of r.locked.values()) o.push('  ' + v);
  o.push(`\n== stuck (long head interaction or not moving): ${r.stuck.size}`); for (const v of r.stuck.values()) o.push('  ' + v);
  const sp = [...r.spam].sort((a, b) => b[1] - a[1]);
  o.push(`\n== failure spam (>= 3 ends with a failure exit reason between two calls): ${sp.length}`); for (const [k, n] of sp.slice(0, 40)) o.push(`  ${n}  ${k}`);
  const se = [...r.shortEnds].sort((a, b) => b[1] - a[1]);
  o.push(`\n== short ends (< 1 sim minute, >= 3 between two calls; often by design: outfit spins, reactions, orders): ${se.length}`); for (const [k, n] of se.slice(0, 20)) o.push(`  ${n}  ${k}`);
  const ou = [...r.ours].sort((a, b) => b[1] - a[1]);
  o.push(`\n== ended by us (our hold or move touched it, then it failed, was cancelled or ended short): ${ou.length}`); for (const [k, n] of ou.slice(0, 20)) o.push(`  ${n}  ${k}`);
  const he = [...r.helped].sort((a, b) => b[1] - a[1]);
  o.push(`\n== helped by us (our hold or move touched it, then it completed): ${he.length}`); for (const [k, n] of he.slice(0, 20)) o.push(`  ${n}  ${k}`);
  o.push('\n== per group, per Sim-hour (route fails, locked-portal fails, failed, game-cancelled, short ends, ours, unseen holds, shortcut moves); log: unseen cuts, retry ok after a cut, KD NPC cuts');
  o.push('  group              sims  simh   route  locked failed cancel short  ours   holds  moves | ufcut  cuts  retry-ok  kdnpc');
  const rows = [...r.groups].sort((a, b) => b[1].sims.size - a[1].sims.size);
  for (const [g, a] of rows)
    o.push(`  ${g.padEnd(18)} ${String(a.sims.size).padStart(4)} ${(a.simMin / 60).toFixed(0).padStart(5)}  ${per(a.route, a.simMin).padStart(6)} ${per(a.locked, a.simMin).padStart(6)} `
      + `${per(a.failed, a.simMin).padStart(6)} ${per(a.gameCancel, a.simMin).padStart(6)} ${per(a.short, a.simMin).padStart(5)} ${per(a.ours, a.simMin).padStart(6)} ${per(a.holds, a.simMin).padStart(6)} ${per(a.moves, a.simMin).padStart(6)} |`
      + ` ${String(a.ufCuts).padStart(5)} ${String(a.cuts).padStart(5)} ${pct(a.retryOk, a.retryFail).padStart(9)} ${String(a.kdNpc).padStart(6)}`);
  o.push(`\n== non-human Sims after an unseen hold (posture / walk style / terrain): ${r.after.length}`); for (const v of r.after) o.push('  ' + v);
  return o.join('\n');
}

function selfCheck() {
  const job = 'routefail job: route 2599E3F3, VisitCommunityLot target Lot, job seq 21 288 ms result 16, cut (UnseenFallback, mask 2), kind own Plan deadline 220 ms, follower 2B99003623895C20, plan 7 host 246 other 34 ms, capAt 283 tail 4 ms, nodes 0 in 1 plans, unseen';
  const uncut = 'routefail job: route 259513DB, Scare target Sim, job seq 130 0 ms result 10, uncut (exempt), kind own Plan deadline 10000 ms, follower 48EE0034EA1BE060, plan 0 host 0 other 0 ms, capAt -1 tail -1 ms, nodes 0 in 0 plans, seen';
  const retry = 'routefail retry: route 2599E3F3, VisitCommunityLot target Lot, after cut route 2599E3F3 58 ms ago, request changed, backoff level 1, succeeded, job seq 23 52 ms result 1, uncut, kind own Plan deadline 220 ms, follower 2B99003623895C20, plan 30 host 19 other 1 ms, capAt -1 tail -1 ms, nodes 17283 in 1 plans, unseen';
  const kd = 'KD NPC CEILING CUT: route 25B8D40E sim 48EE0034E1D3CC90 kind own Plan, 312 ms, job seq 48 287 ms result 16';
  const no = 'routefail summary: 15 min, fails 12, seen 3, unseen 9';
  const ok = (c: boolean, what: string) => { if (!c) { console.error('sim-health-report self-check FAILED: ' + what); process.exit(1); } };
  ok(RE_JOB.test(job) && RE_JOB.exec(job)![2] === 'UnseenFallback' && RE_JOB.exec(job)![3] === '2B99003623895C20', 'job cut line');
  ok(RE_JOB.test(uncut) && RE_JOB.exec(uncut)![2] === undefined, 'uncut job line');
  ok(RE_RETRY.exec(retry)?.[1] === 'succeeded' && RE_RETRY.exec(retry)?.[2] === '2B99003623895C20', 'retry line');
  ok(!RE_RETRY.test(job) && !RE_JOB.test(retry), 'job and retry lines do not cross-match');
  ok(RE_KDNPC.exec(kd)?.[1] === '48EE0034E1D3CC90', 'KD NPC line');
  ok(!RE_JOB.test(no) && !RE_RETRY.test(no) && !RE_KDNPC.test(no), 'other line');
  const sim = (id: string, extra: Row) => ({ id, oid: '2B99003623895C20', name: 'A B', species: 'dog', occults: [], scope: 'npc', room: 0, lot: 'L', interaction: 'Wander', interaction_min: 1, still_min: 0,
    unseen_why: 0, route_fails: 0, route_locked: 0, failed: 0, game_cancelled: 0, short: 0, ended: 0, holds: 0, after_hold: [], spam: [], ...extra });
  const text = [JSON.stringify({ ok: true, result: { sim_minutes: 0, sims: [sim('1', {})] } }),
    'agent noise ' + JSON.stringify({ ok: true, result: { sim_minutes: 120, sims: [sim('1', { route_fails: 4, failed: 2, trapped: true, hidden_room: true, spam: [{ definition: 'X', count: 3 }], after_hold: ['RidingPosture / Run / Grass'] })] } })].join('\n');
  const s = parseHealth(text);
  ok(s.length === 2, 'two samples');
  const r = report(s, parseLog([job, retry, kd].join('\n')), 120, 240);
  const dog = r.groups.get('dog')!;
  ok(dog.simMin === 120 && dog.route === 4 && dog.failed === 2 && dog.ufCuts === 1 && dog.retryOk === 1, 'dog group tallies');
  ok(r.groups.get('(not in census)')?.kdNpc === 1, 'unjoined KD line');
  ok(r.locked.size === 1 && r.spam.size === 1 && r.after.length === 1, 'locked, spam, after-hold');
  ok(r.legacy && r.spacing?.median === 120 && render(r).includes('older census'), 'legacy census labelled, spacing');
  // new census: since_min is the counters' span (the first call's reset sample covers 0), short and ours apart from spam
  const nu = parseHealth([JSON.stringify({ result: { sim_minutes: 0, since_min: 0, sims: [sim('1', {})] } }),
    JSON.stringify({ result: { sim_minutes: 900, since_min: 120, sims: [sim('1', { short: 4, ours: 2, short_ends: [{ definition: 'SwitchToOutfit', count: 4 }],
      ours_ends: [{ definition: 'GoHome', count: 2 }], helped: 1, helped_ends: [{ definition: 'EatHere', count: 1 }], hunger: 80, energy: -10 })] } })].join('\n'));
  const r2 = report(nu, null, 120, 240);
  ok(r2.groups.get('dog')!.simMin === 120 && r2.spam.size === 0 && r2.shortEnds.size === 1 && r2.ours.size === 1 && r2.helped.size === 1 && !r2.legacy, 'since_min span, short / ours not spam');
  ok(render(r2).includes('x EatHere (hunger 80 energy -10)') && render(r2).includes('helped by us'), 'helped listed apart, with motives');
  ok(render(r2).includes('15.0 / 15.0 / 15.0 sim-hours apart'), 'spacing stated');
  ok(render(r).includes('per Sim-hour'), 'render');
  console.log('sim-health-report self-check: ok');
}

if (!process.argv[1]?.endsWith('sim-health-report.ts')) { /* imported (unseen-scorecard.ts) */ }
else if (process.argv.includes('--self-check')) selfCheck();
else {
  const { values, positionals } = parseArgs({ allowPositionals: true, options: { log: { type: 'string' }, 'stuck-min': { type: 'string' }, 'still-min': { type: 'string' }, json: { type: 'boolean' } } });
  if (!positionals[0]) { console.error('usage: node tools/harness/sim-health-report.ts <health.jsonl> [--log ts3native.log] [--stuck-min 120] [--still-min 240] [--json]'); process.exit(1); }
  const samples = parseHealth(readFileSync(positionals[0], 'utf8'));
  const log = values.log ? parseLog(readFileSync(values.log, 'latin1')) : null;
  const r = report(samples, log, Number(values['stuck-min'] ?? 120), Number(values['still-min'] ?? 240));
  if (values.json) console.log(JSON.stringify({ samples: r.samples, locked: [...r.locked.values()], stuck: [...r.stuck.values()], spam: Object.fromEntries(r.spam),
    short_ends: Object.fromEntries(r.shortEnds), ours: Object.fromEntries(r.ours), helped: Object.fromEntries(r.helped), spacing: r.spacing, legacy: r.legacy, after: r.after,
    groups: Object.fromEntries([...r.groups].map(([g, a]) => [g, { ...a, sims: a.sims.size }])) }, null, 1));
  else console.log(render(r));
}
