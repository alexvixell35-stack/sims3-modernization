// Sync fallback plans by managed caller (docs/163): reads ts3native logs (`route async: sync plan N ms, IC X, reason R ...`
// and the `script stack of route H` line LogFallbackStack adds) plus the profile hitches of the same jobs. Reads only.
//   node tools/harness/nostart-report.ts [--filter <regex on log name>] [--jobs]   default filter: every *-ts3native.log
//   node tools/harness/nostart-report.ts --self-check
// A sync plan of N ms is a sim iteration (hitch) of at least N ms, so the >= 300 / >= 1000 ms columns count hitches per caller.
// --jobs adds one row per log: sync plans count / sum / max ms, frames >= 300 ms and hitches per minute (profile hitches.jsonl).
// Set TS3_REPO=<main checkout> when running from a git worktree.
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';

const ROOT = process.env.TS3_REPO ?? join(import.meta.dirname, '..', '..');
const QUEUE = join(ROOT, 'probe-out', 'queue');
const PROFILE = join(ROOT, 'probe-out', 'profile');

type Plan = { ms: number; reason: string; route: string; log: string; stack: string[] | null };

export function parse(text: string, log: string): Plan[] {
  const plans: Plan[] = [];
  const open = new Map<string, Plan>();
  for (const l of text.split(/\r?\n/)) {
    let m = l.match(/route async: sync plan (\d+) ms, IC \w+, reason (\w+).*route ([0-9A-F]+)/);
    if (m) { const p = { ms: +m[1], reason: m[2], route: m[3], log, stack: null }; plans.push(p); open.set(m[3], p); continue; }
    m = l.match(/route async: script stack of route ([0-9A-F]+) \(innermost first\): (.*)/);
    if (m && open.has(m[1])) open.get(m[1])!.stack = m[2].split(' < ');
  }
  return plans;
}

// Caller key: our test agent, the docs/141 / docs/134 callers by name, else the first frames above the route funnel.
export function caller(s: string[] | null): string {
  if (!s) return '(no stack)';
  const j = s.join(' ');
  if (/Ts3Agent\./.test(j)) return 'TEST AGENT Ts3Agent.Handlers.* (repro command, not play)';
  if (/RunStuckCheck/.test(j)) return 'LotManager.RunStuckCheckHelper (3 AM stuck check alarm)';
  if (/ViewObjects/.test(j)) return 'ViewObjects.CreateRouteToObject (Los)';
  if (/LiteDriver/.test(j)) return 'LiteDriver.Simulate (traffic car)';
  if (/AttemptToFindSafeLocation|AttemptToPutInSafeLocation|IsPointInLotSafelyRoutable/.test(j)) return 'docs/141 #1 reset -> safe location';
  if (/ForceReplan|ReplanCallback|HandleEventNeedReplan/.test(j)) return 'docs/141 #2 route-follow replan callback';
  if (/GetFarthestLot/.test(j)) return 'docs/141 #3 LotManager.GetFarthestLot';
  if (/ForceReplanOfAllSimRoutes|OnBuildBuyExit/.test(j)) return 'docs/141 #4 replan all';
  if (/GetRoutableIndoorPositions/.test(j)) return 'docs/141 #5 Lot.GetRoutableIndoorPositions';
  if (/GoHome/.test(j)) return 'GoHome.Run -> Lot.PlanRouteIntoLot / PlanToLot';
  const fr = s.filter(x => !/^ScriptCore\.Route\.|^RouteBackoffHook\.|^Sims3\.SimIFace\.Route\./.test(x));
  if (fr.length === 0) return `(stack too short: depth ${s.length}, ends at ${s[s.length - 1].replace(/^Sims3\.SimIFace\./, '')})`;
  return fr.slice(0, 3).join(' < ').replace(/Sims3\.Gameplay\./g, '');
}

const world = (log: string) => (log.match(/-(bp|sv|ip)-/) ?? [, '?'])[1];

function table(plans: Plan[]): string[] {
  const g = new Map<string, { n: number; sum: number; max: number; h300: number; h1000: number; worlds: Set<string>; reasons: Map<string, number> }>();
  for (const p of plans) {
    const k = caller(p.stack);
    const e = g.get(k) ?? { n: 0, sum: 0, max: 0, h300: 0, h1000: 0, worlds: new Set(), reasons: new Map() };
    e.n++; e.sum += p.ms; e.max = Math.max(e.max, p.ms); if (p.ms >= 300) e.h300++; if (p.ms >= 1000) e.h1000++; e.worlds.add(world(p.log)); e.reasons.set(p.reason, (e.reasons.get(p.reason) ?? 0) + 1);
    g.set(k, e);
  }
  return [...g.entries()].sort((a, b) => b[1].sum - a[1].sum).map(([k, e]) =>
    `| ${k} | ${e.n} | ${(e.sum / 1000).toFixed(2)} | ${e.h300} | ${e.h1000} | ${e.max} | ${[...e.worlds].join(', ')} | ${[...e.reasons].map(([r, n]) => `${r} ${n}`).join(', ')} |`);
}

function jobRow(log: string, plans: Plan[]): string {
  const tag = log.replace(/-\d{4}-ts3native\.log$/, '');
  const hf = existsSync(PROFILE) ? readdirSync(PROFILE).find(f => f.startsWith(tag + '-') && f.endsWith('-hitches.jsonl')) : undefined;
  let frames = '-', perMin = '-';
  if (hf) {
    const h = readFileSync(join(PROFILE, hf), 'utf8').trim().split('\n').filter(Boolean).map(l => JSON.parse(l));
    const secs = +(hf.match(/-(\d+)-hitches\.jsonl$/)?.[1] ?? 0);
    frames = String(h.filter(x => x.wallMs >= 300).length);
    if (secs > 0) perMin = (h.length / (secs / 60)).toFixed(1);
  }
  const sum = plans.reduce((a, p) => a + p.ms, 0), max = plans.reduce((a, p) => Math.max(a, p.ms), 0);
  return `| ${tag} | ${plans.length} | ${sum} | ${max} | ${plans.filter(p => p.stack).length} | ${frames} | ${perMin} |`;
}

// main only when run directly, so route-stutter-report.ts can import caller()
if (import.meta.filename === resolve(process.argv[1] ?? '')) {
  if (process.argv.includes('--self-check')) {
    const t = 'route async: sync plan 731 ms, IC Plan, reason Foreign, entry nojob, route 2519BFE1, caller 3F7D2227\n' +
      'route async: script stack of route 2519BFE1 (innermost first): ScriptCore.Route.Route_Plan < RouteBackoffHook.Plan < Sims3.SimIFace.Route.PlanToPoint < RunStuckCheckHelper.RunOrig\n' +
      'route async: sync plan 7361 ms, IC Plan, reason NoStart, entry nojob, route 251EF11F, caller 40AE2227\n';
    const p = parse(t, 'x-sv-y-0000-ts3native.log');
    const ok = p.length === 2 && caller(p[0].stack).startsWith('LotManager.RunStuckCheckHelper') && caller(p[1].stack) === '(no stack)' && p[1].ms === 7361;
    console.log(ok ? 'nostart-report self-check ok' : 'nostart-report self-check FAILED');
    process.exit(ok ? 0 : 1);
  }

  const fi = process.argv.indexOf('--filter');
  const re = new RegExp(fi > 0 ? process.argv[fi + 1] : '.');
  const logs = readdirSync(QUEUE).filter(f => f.endsWith('-ts3native.log') && re.test(f));
  const all: Plan[] = [];
  const rows: string[] = [];
  for (const f of logs) {
    const p = parse(readFileSync(join(QUEUE, f), 'utf8'), f);
    if (process.argv.includes('--jobs')) rows.push(jobRow(f, p));
    all.push(...p);
  }
  const stacked = all.filter(p => p.stack);
  console.log(`${logs.length} logs, ${all.length} sync plans, ${(all.reduce((a, p) => a + p.ms, 0) / 1000).toFixed(1)} s; with a stack ${stacked.length} (${(stacked.reduce((a, p) => a + p.ms, 0) / 1000).toFixed(1)} s); >= 1 s: ${all.filter(p => p.ms >= 1000).length}, with a stack ${all.filter(p => p.ms >= 1000 && p.stack).length}`);
  console.log('\n| caller | count | sum s | iterations >= 300 ms | >= 1000 ms | max ms | worlds | reasons |\n|---|---|---|---|---|---|---|---|');
  for (const r of table(all)) console.log(r);
  if (rows.length) {
    console.log('\n| job | sync plans | sum ms | max ms | with stack | frames >= 300 ms | hitches/min |\n|---|---|---|---|---|---|---|');
    for (const r of rows) console.log(r);
  }
}
