// Report of the hitch-cause logging (docs/247 §9). Reads only; never talks to the game.
//   node tools/harness/hitch-cause-report.ts <ts3native.log> [--frames <frames.csv>] [--top 5]
//   node tools/harness/hitch-cause-report.ts --self-check
// Reads the ts3fixes lines alarmtime:, lotpass:, boatreplan:, gangway: (docs/247 §13.3 R1), owreset: and the sync plan line ts3native writes right before a
// boatreplan: line ([RouteJobPhases] SyncPlans=1). frames.csv (--svcsplit runs): frames >= 100 ms by svcTop.
// Boat split: seen = Sim or boat in view at the replan, unseen = neither (the part a teleport could remove).
import { readFileSync } from 'node:fs';
import assert from 'node:assert/strict';

export type Boat = { ms: number; mode: string; unseen: boolean; could: boolean; why: string; sim: string; prev: string; route: number; paths: number; dist: number; plan?: string };
type Tally = Map<string, { n: number; ms: number; max: number }>;

const tally = (t: Tally, k: string, ms: number) => { const e = t.get(k) ?? { n: 0, ms: 0, max: 0 }; e.n++; e.ms += ms; e.max = Math.max(e.max, ms); t.set(k, e); };
const top = (t: Tally, n: number) => [...t].sort((a, b) => b[1].ms - a[1].ms).slice(0, n).map(([k, v]) => `${k} n=${v.n} sum=${v.ms} max=${v.max}`);

export function parseBoat(line: string, planLine?: string): Boat | null {
  const m = /boatreplan: (\d+) ms, (\w+) .*?prev (\S+), sim (\S+).*?class (seen|unseen), job (could|no) why (\S+), .*?dist (-?[\d.]+), route (-?[\d.]+) paths (\d+)/.exec(line);
  if (!m) return null;
  const p = planLine ? /sync plan (\d+) ms .*?planlocked (\d+); loops (\S+).*?(grid=\S+)?\s*$/.exec(planLine) : null;
  return { ms: +m[1], mode: m[2], prev: m[3], sim: m[4], unseen: m[5] === 'unseen', could: m[6] === 'could', why: m[7], dist: +m[8], route: +m[9], paths: +m[10],
    plan: p ? `plan ${p[1]} ms planlocked ${p[2]} ${p[3]}${p[4] ? ' ' + p[4] : ''}` : undefined };
}

// gangway fixup replans inside PostPlanCallback: ms, Exit / Enter, sync / async, Sim class
export const GANGWAY = /gangway: (\d+) ms, (Exit|Enter), (sync|async) \w+, sim .*?, class (unseen|household|seen),/;

export function report(log: string, frames: string | null, topN: number): string[] {
  const alarms: Tally = new Map(), passes: Tally = new Map(), resets: Tally = new Map(), why: Tally = new Map(), gang: Tally = new Map();
  const boats: Boat[] = [];
  const lines = log.split(/\r?\n/);
  lines.forEach((l, i) => {
    let m: RegExpExecArray | null;
    if ((m = /alarmtime: (\d+) ms, ([^,]+), target ([^,]*)/.exec(l))) tally(alarms, `${m[2]} on ${m[3]}`, +m[1]);
    else if ((m = /lotpass: (\d+) ms, lot ([^,]*),.*?door (\d+) ms, detail (\d+) ms, rest<= (\S+) ms/.exec(l))) tally(passes, `${m[2]} door=${m[3]} detail=${m[4]} rest<=${m[5]}`, +m[1]);
    else if ((m = GANGWAY.exec(l))) tally(gang, `${m[2]} ${m[3]} ${m[4]}`, +m[1]);
    else if ((m = /owreset: OnPerform ([^,]*), ([^,]*), (\d+) ms with sleeps, after (\S+) ms, suffix (\S+), reset (\w+)/.exec(l))) tally(resets, `OnPerform ${m[2]} suffix=${m[5]} reset=${m[6]} after=${m[4]}`, +m[3]);
    else if ((m = /owreset: Perform ([^,]*), (\d+) ms.*?(create \d+ \d+ ms null \d+, retry \d+ \d+ ms, safe \d+ \d+ ms, restore \d+ ms)/.exec(l))) tally(resets, `Perform ${m[1]} ${m[3]}`, +m[2]);
    else if ((m = /owreset: InstantiateAtHome ([^,]*), (?:([^,]*), )?(\d+) ms, yielded \w+, result (\w+), tries (\S+)/.exec(l))) tally(resets, `InstantiateAtHome ${m[2] ?? ''} result=${m[4]} tries=${m[5]}`, +m[3]);
    else if (l.includes('boatreplan:')) {
      const b = parseBoat(l, lines.slice(Math.max(0, i - 3), i).reverse().find(x => x.includes('sync plan ')));
      if (b) { boats.push(b); tally(why, `${b.unseen ? 'unseen' : 'seen'} ${b.why}`, b.ms); }
    }
  });
  const sum = (a: Boat[]) => a.reduce((s, b) => s + b.ms, 0), unseen = boats.filter(b => b.unseen), all = sum(boats);
  const out = [`hitch-cause: alarmtime ${[...alarms.values()].reduce((s, e) => s + e.n, 0)} lines, lotpass ${[...passes.values()].reduce((s, e) => s + e.n, 0)}, boatreplan ${boats.length}, owreset ${[...resets.values()].reduce((s, e) => s + e.n, 0)}`];
  out.push(...top(alarms, topN).map(s => `  alarm ${s}`), ...top(passes, topN).map(s => `  lotpass ${s}`), ...top(resets, topN).map(s => `  owreset ${s}`));
  if (boats.length) {
    out.push(`  boat ${all} ms: seen ${boats.length - unseen.length} replans ${all - sum(unseen)} ms, unseen ${unseen.length} replans ${sum(unseen)} ms (${(100 * sum(unseen) / Math.max(all, 1)).toFixed(0)}%), job could ${boats.filter(b => b.could).length}`);
    out.push(...top(why, topN).map(s => `  boat why ${s}`));
    out.push(...[...boats].sort((a, b) => b.ms - a.ms).slice(0, topN).map(b => `  boat worst ${b.ms} ms ${b.mode} ${b.unseen ? 'unseen' : 'seen'} ${b.sim} route ${b.route} dist ${b.dist} paths ${b.paths}${b.plan ? ', ' + b.plan : ''}`));
  }
  if (gang.size) out.push(...top(gang, topN).map(s => `  gangway ${s}`));
  if (frames !== null) {
    const rows = frames.split(/\r?\n/), head = rows[0].split(','), iTop = head.indexOf('svcTop'), iMs = head.indexOf('svcTopMs'), iFrame = head.findIndex(h => /^(ms|frameMs|dtMs)$/.test(h));
    const svc: Tally = new Map();
    if (iTop < 0) out.push('  svc no svcTop column (run without --svcsplit)');
    else for (const r of rows.slice(1)) {
      const c = r.split(',');
      if (c[iTop] && c[iTop] !== 'na' && +c[iMs] >= 20 && (iFrame < 0 || +c[iFrame] >= 100)) tally(svc, c[iTop], +c[iMs]);
    }
    out.push(...top(svc, topN).map(s => `  svc ${s}`));
  }
  return out;
}

if (process.argv.includes('--self-check')) {
  const log = ['route phases: sync plan 334 ms ok, entry x, route 0, planlocked 3; loops entry=9/9 grid=512x512',
    'boatreplan: 340 ms, sync Success, caller c, prev GetInBoatRouteAction, sim A uf=x, Boat seen=0, class unseen, job could why ForeignKinds=0, ufskip follower=boat,kind=3, townieUnseen=1 goalUnseen=1, dist 40.0, route 80.5 paths 2, lot 1 goalLot 2, a to b',
    'boatreplan: 20 ms, async Success, caller c, prev GetInBoatRouteAction, sim B uf=x, Boat seen=1, class seen, job no why afActive, ufskip f, dist 5.0, route 9.0 paths 1, lot 1 goalLot 2, a to b',
    'alarmtime: 956 ms, Lot.Tick, target Lot, task Lot, day 1 hour 2.00, ended=run',
    'lotpass: 900 ms, lot WorldLot, alarms 3 890 ms, top 800 ms Lot.Tick, door 1 ms, detail 2 ms, rest<= 7 ms, day 1 hour 2.00',
    'owreset: Perform Ann Lee, 439 ms with sleeps, tail 3 ms, home True fade True result True, create 1 430 ms null 0, retry 0 0 ms, safe 1 2 ms, restore 5 ms, day 1',
    'owreset: InstantiateAtHome Ann Lee, Horse Adult, 431 ms, yielded False, result True, tries 2',
    'ts3fixes: gangway: 212 ms, Exit, sync Succeeded, sim Ann Lee uf=3, class household, route 12.5 paths 3, lot 1A, from (1.0, 2.0, 3.0)',
    'ts3fixes: gangway: 8 ms, Enter, sync Succeeded, sim Bo Day uf=0, class unseen, route 4.0 paths 2, lot 0, from (1.0, 2.0, 3.0)',
    'owreset: OnPerform Ann Lee, Human Adult, 460 ms with sleeps, after 20 ms, suffix Stuck, reset yes, safe 1 2 ms no 1, day 1'].join('\n');
  const r = report(log, 'ms,svcTop,svcTopMs\n182,state4,170\n5,state1,1\n150,na,0\n', 5);
  assert.ok(r[0].includes('alarmtime 1 lines, lotpass 1, boatreplan 2, owreset 3'), r[0]);
  const t = r.join('\n');
  assert.ok(t.includes('boat 360 ms: seen 1 replans 20 ms, unseen 1 replans 340 ms (94%), job could 1'), t);
  assert.ok(t.includes('plan 334 ms planlocked 3 entry=9/9 grid=512x512'), t);
  assert.ok(t.includes('owreset Perform Ann Lee create 1 430 ms null 0, retry 0 0 ms, safe 1 2 ms, restore 5 ms n=1 sum=439'), t);
  assert.ok(t.includes('owreset InstantiateAtHome Horse Adult result=True tries=2 n=1 sum=431'), t);
  assert.ok(t.includes('owreset OnPerform Human Adult suffix=Stuck reset=yes after=20 n=1 sum=460'), t);
  assert.ok(t.includes('svc state4 n=1 sum=170') && !t.includes('svc state1') && !t.includes('svc na'), t);
  assert.equal(parseBoat('nothing'), null);
  assert.ok(t.includes('gangway Exit sync household n=1 sum=212') && t.includes('gangway Enter sync unseen n=1 sum=8'), t);
  assert.equal(GANGWAY.exec('ts3fixes: gangway: 5 ms, Sideways, sync Succeeded, sim X uf=0, class unseen, route 1'), null);  // not a gangway method
  assert.equal(GANGWAY.exec('ts3fixes: boatreplan: 5 ms, sync Succeeded, sim X uf=0, class unseen, job no'), null);
  assert.ok(report('', 'a,b\n', 5).join('\n').includes('no svcTop column'));
  console.log('hitch-cause-report self-check OK');
} else {
  const a = process.argv.slice(2), opt = (n: string) => { const i = a.indexOf(n); return i >= 0 ? a.splice(i, 2)[1] : undefined; };
  const f = opt('--frames'), n = +(opt('--top') ?? 5);
  if (!a[0]) { console.error('usage: hitch-cause-report.ts <ts3native.log> [--frames <frames.csv>] [--top 5]'); process.exit(2); }
  console.log(report(readFileSync(a[0], 'utf8'), f ? readFileSync(f, 'utf8') : null, n).join('\n'));
}
