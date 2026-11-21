// Lot-phase report (docs/667): where the slow PathPlanner_PlanHierarchical calls of a run spend their time, by phase, scope,
// result, lot, Sim, interaction and LotPlan call site, and how much of the lot phases is the worker's host-call wait.
//   node tools/harness/lot-route-report.ts <ts3native log | run dir>... [--top 15]
//   node tools/harness/lot-route-report.ts --self-check
// Reads ([RouteJobPhases] CandidateSplit=1, ts3fixes ufplansplit):
//   - `route candidate split: PlanHierarchical N ms code C, worker job|sync, route H, scope S: <phase ms/calls>...; lot legs by
//     call <site ms/calls>` (one per call >= CandidateSplitLogMs, 20 a minute: a sample, not a census);
//   - the last `route candidate split totals:` line (cumulative, every call);
//   - the job line that follows a split line for the same route (`route H ... job seq N M ms result R ... plan P host X other`):
//     X is the time the worker waited for the sim thread to run managed calls. On a build without the split's own host column
//     that wait sits inside lotSearch / lotPlan self ms (docs/667 §2);
//   - `route async: long job|sync plan ... route H ... at T ms` + ts3fixes `ufplan ... at T ms` / `ufplansplit ... result R,
//     dist D, <Sim> on <Lot>, tick` (joined by T within 40 ms) and `routefail job: route H, <Interaction> target <Type>`.
// The split line carries no lot id, so the lot is the Sim's current lot from ufplansplit, known only for plans ts3fixes logged.
import { existsSync, readFileSync, statSync } from 'node:fs';
import { join, resolve } from 'node:path';

export type Plan = {
  ms: number; code: number; mode: string; route: string; scope: string;
  ph: Record<string, [number, number]>; legs: Record<string, [number, number]>;
  at?: number; sim?: string; lot?: string; inter?: string; res?: string; host?: number; jobCut?: boolean;
};
const SPLIT = /^route candidate split: PlanHierarchical (\d+) ms code (\d+), ([^,]+), route ([0-9A-F]+), scope ([^:]+): (.*?) \(self ms\/calls\); (.*?); rest (\d+) ms/;
const UFPLAN = /ufplan: kind \d+ (\d+) ms why -?\d+ .*? at (\d+) ms/;
const UFSPLIT = /ufplansplit: .*result (\w+), dist \d+(?: \([^)]*\))?, (.*?) on (.*), tick/;
const TOTALS = /^route candidate split totals: (.*?) \(self ms\/calls\); PlanHierarchical rest (\d+) ms over (\d+) calls/;

export function parseSplit(l: string): Plan | null {
  const m = l.match(SPLIT);
  if (!m) return null;
  const p: Plan = { ms: +m[1], code: +m[2], mode: m[3], route: m[4], scope: m[5], ph: { rest: [+m[8], 0] }, legs: {} };
  for (const x of m[6].matchAll(/(\w+) (\d+)\/(\d+)/g)) p.ph[x[1]] = [+x[2], +x[3]];
  for (const x of m[7].matchAll(/([0-9A-F]{6}|other) (\d+)\/(\d+)/g)) p.legs[x[1]] = [+x[2], +x[3]];
  return p;
}

export function parseLog(text: string): { plans: Plan[]; totals: Record<string, [number, number]> | null; calls: number } {
  const L = text.split(/\r?\n/);
  const uf: { at: number; sim: string; lot: string; res: string }[] = [];
  for (let i = 0; i + 1 < L.length; i++) {
    const m = L[i].match(UFPLAN);
    const s = m && L[i + 1].match(UFSPLIT);
    if (m && s) uf.push({ at: +m[2], res: s[1], sim: s[2], lot: s[3] });
  }
  const plans: Plan[] = [];
  let totals: Record<string, [number, number]> | null = null, calls = 0;
  for (let i = 0; i < L.length; i++) {
    const t = L[i].match(TOTALS);
    if (t) {
      totals = { rest: [+t[2], +t[3]] };
      calls = +t[3];
      for (const x of t[1].matchAll(/(\w+) (\d+)\/(\d+)/g)) totals[x[1]] = [+x[2], +x[3]];
      continue;
    }
    const p = parseSplit(L[i]);
    if (!p) continue;
    for (let j = i + 1; j < Math.min(L.length, i + 12); j++) {
      if (!L[j].includes(p.route)) continue;
      let x: RegExpMatchArray | null;
      if (p.at === undefined && (x = L[j].match(/^route async: (?:long job|sync plan) .* at (\d+) ms/))) p.at = +x[1];
      if (p.host === undefined && (x = L[j].match(/job seq \d+ \d+ ms result \d+, (cut \(|uncut).*? plan -?\d+ host (-?\d+) other/))) p.jobCut = x[1] !== 'uncut', p.host = +x[2];
      if (!p.inter && (x = L[j].match(/^routefail job: route [0-9A-F]+, (.*?), job seq/))) p.inter = x[1];
    }
    if (p.at !== undefined) {
      let best: (typeof uf)[number] | undefined;
      for (const u of uf) if (Math.abs(u.at - p.at) <= 40 && (!best || Math.abs(u.at - p.at) < Math.abs(best.at - p.at))) best = u;
      if (best) p.sim = best.sim, p.lot = best.lot || '(no lot)', p.res = best.res;
    }
    plans.push(p);
  }
  return { plans, totals, calls };
}

const PHASES = ['lotSearch', 'lotPlan', 'cell', 'world', 'water', 'post', 'insert', 'shore', 'gather', 'closestWps', 'choose', 'host', 'rest'];
export const result = (p: Plan) => p.code === 5 ? 'fail' : p.code === 0 ? 'ok' : `code ${p.code}`;
export const dominant = (p: Plan) => PHASES.reduce((b, k) => (p.ph[k]?.[0] ?? 0) > (p.ph[b]?.[0] ?? 0) ? k : b, 'rest');

function rank(out: string[], plans: Plan[], title: string, key: (p: Plan) => string, val: (p: Plan) => number, top: number) {
  const m = new Map<string, [number, number]>();
  for (const p of plans) {
    const v = val(p);
    if (!v) continue;
    const e = m.get(key(p)) ?? [0, 0];
    e[0] += v, e[1]++;
    m.set(key(p), e);
  }
  out.push('', `== ${title}`);
  for (const [k, [v, n]] of [...m].sort((a, b) => b[1][0] - a[1][0]).slice(0, top)) out.push(`${String(v).padStart(8)} ms ${String(n).padStart(5)} plans  ${k}`);
}

export function report(name: string, text: string, top: number): string[] {
  const { plans, totals, calls } = parseLog(text);
  const out: string[] = [`# ${name}`];
  const tot = plans.reduce((a, p) => a + p.ms, 0);
  out.push(`slow plans (logged split lines) ${plans.length}, ${tot} ms; with a job line ${plans.filter(p => p.host !== undefined).length}, with Sim and lot ${plans.filter(p => p.lot).length}, with interaction ${plans.filter(p => p.inter).length}`);
  if (totals) {
    const all = Object.values(totals).reduce((a, v) => a + v[0], 0);
    out.push('', `== every PlanHierarchical call (cumulative totals line): ${calls} calls, ${all} ms`);
    for (const [k, [ms, n]] of Object.entries(totals).sort((a, b) => b[1][0] - a[1][0])) out.push(`${k.padEnd(10)} ${String(ms).padStart(8)} ms ${(100 * ms / (all || 1)).toFixed(1).padStart(5)}%  calls ${n}`);
  }
  out.push('', '== slow plans by phase (self ms, calls, plans where it is the largest phase)');
  for (const k of PHASES) {
    let ms = 0, c = 0, dom = 0;
    for (const p of plans) ms += p.ph[k]?.[0] ?? 0, c += p.ph[k]?.[1] ?? 0, dom += dominant(p) === k ? 1 : 0;
    if (ms || c) out.push(`${k.padEnd(10)} ${String(ms).padStart(8)} ms ${(100 * ms / (tot || 1)).toFixed(1).padStart(5)}%  calls ${c}  largest in ${dom}`);
  }
  rank(out, plans, 'scope x result x thread', p => `${p.scope} | ${result(p)} | ${p.mode}`, p => p.ms, top);
  rank(out, plans, 'largest phase x scope x result x thread', p => `${dominant(p)} | ${p.scope} | ${result(p)} | ${p.mode}`, p => p.ms, top);
  // the worker's host-call wait against the lot phases' self time (builds without the split's own host column)
  const j = plans.filter(p => p.host !== undefined && p.mode !== 'sync');
  const lot = (p: Plan) => (p.ph.lotSearch?.[0] ?? 0) + (p.ph.lotPlan?.[0] ?? 0) + (p.ph.host?.[0] ?? 0);
  const close = j.filter(p => Math.abs(lot(p) - p.host!) <= Math.max(15, 0.15 * lot(p))).length;
  out.push('', `== worker plans with a job line: ${j.length}; lotSearch + lotPlan (+ host) self ${j.reduce((a, p) => a + lot(p), 0)} ms, the jobs' host-call wait ${j.reduce((a, p) => a + p.host!, 0)} ms; within 15% of each other in ${close} plans`);
  out.push(`   failed (code 5) worker plans whose job was cut at its deadline: ${j.filter(p => p.code === 5 && p.jobCut).length} of ${j.filter(p => p.code === 5).length}`);
  const sync = plans.filter(p => p.mode === 'sync');
  out.push(`== sync plans (sim thread): ${sync.length}, ${sync.reduce((a, p) => a + p.ms, 0)} ms; lotSearch + lotPlan self ${sync.reduce((a, p) => a + lot(p), 0)} ms, cell ${sync.reduce((a, p) => a + (p.ph.cell?.[0] ?? 0), 0)} ms; failed ${sync.filter(p => p.code === 5).length} (${sync.filter(p => p.code === 5).reduce((a, p) => a + p.ms, 0)} ms, cell ${sync.filter(p => p.code === 5).reduce((a, p) => a + (p.ph.cell?.[0] ?? 0), 0)} ms over ${sync.filter(p => p.code === 5).reduce((a, p) => a + (p.ph.cell?.[1] ?? 0), 0)} searches)`);
  for (const k of ['lotSearch', 'lotPlan', 'cell']) {
    rank(out, plans, `${k} self ms by the Sim's lot (ufplansplit; ? = not logged by ts3fixes)`, p => p.lot ?? '?', p => p.ph[k]?.[0] ?? 0, top);
    rank(out, plans, `${k} self ms by interaction`, p => p.inter ?? '?', p => p.ph[k]?.[0] ?? 0, top);
    rank(out, plans, `${k} self ms by calls per plan`, p => { const c = p.ph[k]?.[1] ?? 0; return c <= 1 ? String(c) : c <= 4 ? '2-4' : c <= 20 ? '5-20' : '> 20'; }, p => p.ph[k]?.[0] ?? 0, top);
  }
  out.push('', '== lot legs by LotPlan call site (inclusive ms, calls, plans; ms in failed plans)');
  const lg = new Map<string, number[]>();
  for (const p of plans) for (const [s, [ms, c]] of Object.entries(p.legs)) {
    const e = lg.get(s) ?? [0, 0, 0, 0];
    e[0] += ms, e[1] += c, e[2]++, e[3] += p.code === 5 ? ms : 0;
    lg.set(s, e);
  }
  for (const [s, e] of [...lg].sort((a, b) => b[1][0] - a[1][0])) out.push(`${s.padEnd(6)} ${String(e[0]).padStart(8)} ms ${String(e[1]).padStart(6)} calls ${String(e[2]).padStart(5)} plans, failed ${e[3]} ms`);
  // repeats: the same route handle planned again, and the same plan shape (phase calls) failing again
  const byRoute = new Map<string, Plan[]>();
  for (const p of plans) if (!/^0+$/.test(p.route)) byRoute.set(p.route, [...(byRoute.get(p.route) ?? []), p]);
  const reps = [...byRoute.values()].filter(a => a.length > 1);
  const repPlans = reps.reduce((a, b) => a + b.length, 0);
  out.push('', `== repeats: ${reps.length} of ${byRoute.size} route handles have more than one slow plan: ${repPlans} plans, ${reps.reduce((a, b) => a + b.reduce((x, p) => x + p.ms, 0), 0)} ms; failed among them ${reps.reduce((a, b) => a + b.filter(p => p.code === 5).length, 0)}; most on one route ${Math.max(0, ...reps.map(a => a.length))}`);
  const shape = new Map<string, Plan[]>();
  for (const p of plans.filter(q => q.code === 5 && (q.ph.cell?.[1] ?? 0) > 4)) {
    const k = `${p.mode}, cell searches ${p.ph.cell[1]}, lotPlan ${p.ph.lotPlan?.[1] ?? 0}, world ${p.ph.world?.[1] ?? 0}`;
    shape.set(k, [...(shape.get(k) ?? []), p]);
  }
  out.push('== failed plans with more than 4 cell searches, by shape (the same failing flood per candidate, docs/667 §3)');
  for (const [k, a] of [...shape].sort((x, y) => y[1].reduce((s, p) => s + p.ms, 0) - x[1].reduce((s, p) => s + p.ms, 0)).slice(0, top))
    out.push(`${String(a.reduce((s, p) => s + p.ms, 0)).padStart(8)} ms ${String(a.length).padStart(4)}x  ${k}; cell ${a.reduce((s, p) => s + p.ph.cell[0], 0)} ms`);
  return out;
}

function selfCheck(): void {
  const bad: string[] = [];
  const ok = (c: boolean, what: string) => { if (!c) bad.push(what); };
  // real lines of phase-20261119-2238 (CI build 44626443641) and the format strings of plancand.cpp / plandeadline.cpp
  const split = 'route candidate split: PlanHierarchical 294 ms code 5, worker job, route 2506D5BD, scope unseen: closestWps 0/0, sameLand 0/0, sameWater 0/0, choose 0/1, gather 0/2, shore 0/0, lotSearch 293/2, cell 0/1, lotPlan 0/1, world 0/0, water 0/0, post 0/0, insert 0/5 (self ms/calls); lot legs by call 65F11C 0/1 (incl. ms/calls); rest 0 ms (own code + allocator + unhooked helpers); cut 0';
  const noLegs = 'route candidate split: PlanHierarchical 261 ms code 4, worker job, route 252C6D37, scope unseen: closestWps 0/0, sameLand 0/0, sameWater 0/0, choose 0/0, gather 0/1, shore 0/0, lotSearch 261/71, cell 0/1, lotPlan 0/0, world 0/0, water 0/0, post 0/0, insert 0/5 (self ms/calls); no lot legs; rest 0 ms (own code + allocator + unhooked helpers); cut 0';
  const withHost = split.replace('insert 0/5 (self', 'insert 0/5, host 286/9 (self').replace('lotSearch 293/2', 'lotSearch 7/2');
  const p = parseSplit(split), q = parseSplit(noLegs), h = parseSplit(withHost);
  ok(!!p && p.ms === 294 && p.code === 5 && p.mode === 'worker job' && p.route === '2506D5BD' && p.scope === 'unseen' && p.ph.lotSearch[0] === 293 && p.ph.lotSearch[1] === 2 && p.legs['65F11C'][1] === 1 && p.ph.rest[0] === 0, 'split line');
  ok(!!q && q.ph.lotSearch[1] === 71 && Object.keys(q.legs).length === 0 && dominant(q) === 'lotSearch' && result(q) === 'code 4', 'split line with no lot legs');
  ok(!!h && h.ph.host[0] === 286 && h.ph.host[1] === 9 && dominant(h) === 'host', 'split line with the host column');
  ok(parseSplit(split.replace('route candidate split:', 'route candidate top 2/8:')) === null, 'a top line is not counted twice');
  ok(parseSplit('route candidate split totals: closestWps 0/0 (self ms/calls); PlanHierarchical rest 0 ms over 0 calls') === null, 'the totals line is not a plan');
  const log = [
    split,
    'route async: long job 296 ms, own START, IC kind 0, route 2506D5BD, capped (UnseenFallback), no stuck-check mark, arm -, scope unseen, at 252847059 ms',
    'routefail job: route 2506D5BD, VisitLot target Lot, job seq 46 296 ms result 16, cut (UnseenFallback, mask 2), kind own Plan deadline 220 ms, follower 374A00542A64D790, plan 294 host 286 other -284 ms, capAt 295 tail 0 ms, nodes 0 in 1 plans, unseen',
    'ts3fixes: ufplan: kind 0 301 ms why 0 marked Lopita Alto own task, tick 19300, arm -, at 252847070 ms',
    'ts3fixes: ufplansplit: kind 0 301 ms, pre 0 wait 300 (3 polls, longest 133) post 1, why 0 cut, result FailedBecauseNoPathToDestination, dist 80, Lopita Alto on Luxury Villa - 2br, 1.5ba, tick 19300',
    'ts3fixes: ufplansplit: kind 3 227 ms, sync 213 ms (START refused), why 0, result Succeeded, dist 752 (sim 1505 m away), Chandell Scott on Tide Runner - 2br, 1ba, tick 304268',
    'route candidate split totals: closestWps 148/5263, lotSearch 46718/10437, cell 27960/27370, lotPlan 54981/16302 (self ms/calls); PlanHierarchical rest 5670 ms over 15524 calls',
  ].join('\n');
  const r = parseLog(log);
  const a = r.plans[0];
  ok(r.plans.length === 1 && a.at === 252847059 && a.host === 286 && a.jobCut === true && a.inter === 'VisitLot target Lot', 'job and routefail join');
  ok(a.sim === 'Lopita Alto' && a.lot === 'Luxury Villa - 2br, 1.5ba' && a.res === 'FailedBecauseNoPathToDestination', 'ufplansplit join (lot names hold commas)');
  ok(!!r.totals && r.totals.lotPlan[0] === 54981 && r.totals.rest[0] === 5670 && r.calls === 15524, 'totals line');
  const m = 'ts3fixes: ufplansplit: kind 3 227 ms, sync 213 ms (START refused), why 0, result Succeeded, dist 752 (sim 1505 m away), Chandell Scott on Tide Runner - 2br, 1ba, tick 304268'.match(UFSPLIT);
  ok(!!m && m[2] === 'Chandell Scott' && m[3] === 'Tide Runner - 2br, 1ba', 'ufplansplit with a distance note');
  ok(!UFPLAN.test('ts3fixes: ufplansplit: kind 0 301 ms, pre 0 wait 300'), 'ufplansplit is not a ufplan line');
  const uncut = parseLog([split, 'routefail job: route 2506D5BD, VisitCommunityLot target Lot, job seq 3 39 ms result 16, uncut, kind own Plan deadline 220 ms, follower 374A0055517A5910, plan 0 host 0 other 39 ms, capAt -1 tail -1 ms, nodes 0 in 1 plans, unseen'].join('\n')).plans[0];
  ok(uncut.jobCut === false && uncut.host === 0, 'an uncut job line');
  const text = report('self-check', log, 5).join('\n');
  ok(/host-call wait 286 ms; within 15% of each other in 1 plans/.test(text) && /cut at its deadline: 1 of 1/.test(text), 'report text');
  if (bad.length) { console.error(`lot-route-report self-check FAILED: ${bad.join('; ')}`); process.exit(1); }
  console.log('lot-route-report self-check: OK');
}

if (import.meta.filename === resolve(process.argv[1] ?? '')) {
  const args = process.argv.slice(2);
  if (args.includes('--self-check')) selfCheck();
  else {
    const ti = args.indexOf('--top');
    const top = ti >= 0 ? +args[ti + 1] || 15 : 15;
    const files = args.filter((a, i) => !a.startsWith('--') && (ti < 0 || i !== ti + 1));
    if (!files.length) { console.error('usage: node tools/harness/lot-route-report.ts <ts3native log | run dir>... [--top 15] | --self-check'); process.exit(2); }
    for (const f of files) {
      const p = existsSync(f) && statSync(f).isDirectory() ? join(f, 'ts3native-full.log') : f;
      console.log(report(p.replace(/^.*[\\/](?=[^\\/]+[\\/][^\\/]+$)/, ''), readFileSync(p, 'latin1'), top).join('\n'));
      console.log('');
    }
  }
}
