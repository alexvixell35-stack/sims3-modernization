// What makes Sims wait in a soak: ranks wait causes from a phase run dir (ts3native.log, plus health.jsonl from the sim_health
// census when present), in sim-minutes where the log has them and in real ms / counts where it does not.
//   node tools/harness/wait-report.ts <run dir> [<other run dir>] [--top 25] [--stand-min 60]
//   node tools/harness/wait-report.ts --self-check
// With two run dirs the table has both columns (e.g. all-on vs all-off) and is ranked by the first.
// Causes (key = cause | detail):
//   social sync      ts3fixes "syncwait:" lines (logged waits only: SyncWaitLog logs long or failed ones), by the waiter's
//                    interaction and outcome (ok / failed / timeout); sim-min = the waited minutes. "partner idle" when the
//                    partner has no link to the wait.
//   service queue    the same lines for bar / restaurant service (Order* customer side, Make* server side): customer waits
//                    split by what the server was doing (serving this customer / busy elsewhere / idle, no link).
//   socialfinish off ts3fixes "socialfinish: X would signal, wait failed after N min" (switch off: the stand EA's missing
//                    Finished signal causes; the same wait is also in a social sync timeout row).
//   route fail       ts3fixes "routefail:" by result (PortalLocked = door / privacy locks: "door lock") and interaction; count.
//   route job        ts3native "route async: long job" (the owner Sim waits for its plan), real ms by START kind / cut.
//   unseen plan      ts3fixes "ufplan:" plans of unseen walkers, real ms by why (0 = unseen).
//   game lock        ts3native "lock holders: sim waited N ms" by acquire site, real ms.
//   stand still      health.jsonl: a Sim whose head interaction ran > --stand-min sim-min without moving, by interaction.
//   queue behind     health.jsonl: a Sim with more queued behind a head that ran > --stand-min, by head interaction.
// Waits for a busy object (ObjectInUse) are not logged per Sim; the census counts them in game_cancelled / spam only.
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { parseArgs } from 'node:util';

export type Tally = { min: number; ms: number; n: number; ex: string[] };
export type Census = Map<string, Tally>;

export const RE_SYNC = /ts3fixes: syncwait: (.+?) -> (.+?), (\w+), (\w+), ([0-9.]+)\/[0-9.E+]+ min, .*?, in (\S+), partner in (\S+) \(([^)]*)\)/;
export const RE_SFIN = /ts3fixes: socialfinish: (\S+) would signal, wait failed after ([0-9.]+)\/[0-9.]+ min/;
export const RE_RFAIL = /ts3fixes: routefail: (.+?) 0x[0-9A-F]+ \w+ (\w+), (\S+) target .*? (?:sync|async) (\w+) blocker/;
export const RE_RJOB = /route async: long job (\d+) ms, (own|foreign) START, .*?route [0-9A-F]{8}, (capped \((\w+)\))?.*?scope ([\w-]+)/;
export const RE_UFPLAN = /ts3fixes: ufplan: kind (-?\d+) (\d+) ms why (-?\d+)/;
export const RE_LOCK = /lock holders: sim waited (\d+) ms on lock [0-9A-F]+ \(at (0x[0-9A-F]+)\), held by (\w+)/;
const SERVICE = /^(Order|Make)(Drink|Food|Conversation|Dinner|Meal)|^(BarOrder|Bartend)/;

function add(c: Census, key: string, min: number, ms: number, ex: string) {
  let t = c.get(key);
  if (!t) c.set(key, t = { min: 0, ms: 0, n: 0, ex: [] });
  t.min += min; t.ms += ms; t.n++;
  if (t.ex.length < 3 && ex) t.ex.push(ex.length > 220 ? ex.slice(0, 220) + '...' : ex);
}

export function censusLog(text: string, c: Census = new Map()): Census {
  for (const raw of text.split(/\r?\n/)) {
    const l = raw.replace(/^[^A-Za-z]*/, '');
    let m = RE_SYNC.exec(l);
    if (m) {
      const [, who, partner, state, outcome, w, ia, pia, link] = m, min = Number(w);
      const brief = `${who} -> ${partner}: ${ia} ${state} ${outcome} ${w} min, partner in ${pia} (${link})`;
      if (SERVICE.test(ia)) {
        const side = ia.startsWith('Make') || ia.startsWith('Bartend') ? 'server waits for customer' : 'customer waits';
        const srv = link === 'no link' ? 'server idle / no link' : SERVICE.test(pia) ? 'server serving this customer' : `server busy in ${pia}`;
        add(c, `service queue | ${ia} ${side}, ${side === 'customer waits' ? srv : outcome}`, min, 0, brief);
      } else add(c, `social sync | ${ia} ${outcome}${link === 'no link' ? ', partner idle (no link)' : ''}`, min, 0, brief);
      continue;
    }
    if ((m = RE_SFIN.exec(l))) { add(c, `socialfinish off | ${m[1]} stood until the sync timed out`, Number(m[2]), 0, l.slice(l.indexOf('socialfinish'))); continue; }
    if ((m = RE_RFAIL.exec(l))) {
      const res = m[4].replace(/^FailedBecause/, '');
      add(c, `${res === 'DestinationPortalLocked' ? 'door lock' : 'route fail'} | ${res}, ${m[3]} (${m[2]})`, 0, 0, l.slice(l.indexOf('routefail')));
      continue;
    }
    if ((m = RE_RJOB.exec(l))) { add(c, `route job | ${m[2]} START, ${m[4] ? 'cut by ' + m[4] : 'uncut'}, scope ${m[5]}`, 0, Number(m[1]), l); continue; }
    if ((m = RE_UFPLAN.exec(l))) { add(c, `unseen plan | why ${m[3]}`, 0, Number(m[2]), l.slice(l.indexOf('ufplan'))); continue; }
    if ((m = RE_LOCK.exec(l))) add(c, `game lock | sim waits at ${m[2]} (holder ${m[3]})`, 0, Number(m[1]), l);
  }
  return c;
}

// health.jsonl: one sim_health answer per line; a Sim's stand counts once per head (its longest reading)
export function censusHealth(text: string, standMin: number, c: Census = new Map()): Census {
  const best = new Map<string, { min: number; key: string; ex: string }>();
  for (const line of text.split(/\r?\n/)) {
    const i = line.indexOf('{');
    if (i < 0) continue;
    let j: any;
    try { j = JSON.parse(line.slice(i)); } catch { continue; }
    const r = j?.result ?? j;
    for (const s of r?.sims ?? []) {
      const im = Number(s.interaction_min) || 0;
      if (!s.interaction || im <= standMin) continue;
      const ex = `${s.name} [${s.species}, ${s.scope}] ${s.interaction} ${im} min on ${s.lot}, queue ${s.queue}`;
      const id = `${s.id}|${s.interaction}`;
      if ((Number(s.still_min) || 0) >= im) best.set(id + '|stand', { min: im, key: `stand still | ${s.interaction}`, ex });
      if ((Number(s.queue) || 0) > 1) best.set(id + '|queue', { min: im, key: `queue behind | ${s.interaction}`, ex });
    }
  }
  for (const b of best.values()) add(c, b.key, b.min, 0, b.ex);
  return c;
}

export function censusRun(dir: string, standMin: number): Census {
  const c = new Map();
  const log = join(dir, 'ts3native.log'), h = join(dir, 'health.jsonl');
  if (existsSync(log)) censusLog(readFileSync(log, 'latin1'), c);
  if (existsSync(h)) censusHealth(readFileSync(h, 'utf8'), standMin, c);
  return c;
}

const fmt = (t?: Tally) => !t ? '-' : `${t.min ? t.min.toFixed(0) + ' min ' : ''}${t.ms ? (t.ms / 1000).toFixed(1) + ' s ' : ''}x${t.n}`;
export function render(a: Census, b: Census | null, names: string[], top: number): string {
  const score = (t: Tally) => t.min * 60000 + t.ms + t.n;  // sim-min first, then real ms, then count
  const keys = [...new Set([...a.keys(), ...(b ? b.keys() : [])])].sort((x, y) => score(a.get(y) ?? { min: 0, ms: 0, n: 0, ex: [] }) - score(a.get(x) ?? { min: 0, ms: 0, n: 0, ex: [] }));
  const o = [`wait causes (sim-min | real s | count), ranked by ${names[0]}`, `  ${'cause | detail'.padEnd(78)} ${names[0].padEnd(22)} ${b ? names[1] : ''}`];
  for (const k of keys.slice(0, top)) {
    o.push(`  ${k.padEnd(78)} ${fmt(a.get(k)).padEnd(22)} ${b ? fmt(b.get(k)) : ''}`);
    for (const e of (a.get(k) ?? b?.get(k))?.ex.slice(0, 2) ?? []) o.push(`      e.g. ${e}`);
  }
  const by = (c: Census) => { const g = new Map<string, Tally>(); for (const [k, t] of c) { const x = k.split(' | ')[0]; const s = g.get(x) ?? { min: 0, ms: 0, n: 0, ex: [] }; s.min += t.min; s.ms += t.ms; s.n += t.n; g.set(x, s); } return g; };
  const ga = by(a), gb = b ? by(b) : null;
  o.push('', 'per cause group:');
  for (const k of [...new Set([...ga.keys(), ...(gb ? gb.keys() : [])])].sort()) o.push(`  ${k.padEnd(20)} ${fmt(ga.get(k)).padEnd(26)} ${gb ? fmt(gb.get(k)) : ''}`);
  return o.join('\n');
}

function selfCheck() {
  const ok = (c: boolean, what: string) => { if (!c) { console.error('wait-report self-check FAILED: ' + what); process.exit(1); } };
  const lines = [
    '12:00:01.000 ts3fixes: syncwait: Lisa -> Odine, Completed, timeout, 5.1/5 min, checks 0 0 ms, in ShowVideos, partner in SocialInteractionB (linked running), partner sync Committed/Receiver on me, own Completed/Initiator own flags 1101 partner flags 0010, partner step receive<ok 17.9 min ago, partner not in a sync wait, day 0 hour 16.88',
    'ts3fixes: syncwait: Lenny -> Brandy, Routed, ok, 1.3/120 min, checks 0 0 ms, in OrderDrink, partner in MakeDrink (linked running), day 0 hour 17.23',
    'ts3fixes: syncwait: Brandy -> Lilly-Bo, Committed, ok, 20.8/340282300000000000000000000000000000000 min, checks 0 0 ms, in MakeConversation, partner in OrderConversation (linked running), day 0 hour 19.45',
    'ts3fixes: syncwait: HH Matty -> HH Katelyn, Completed, ok, 3.5/40 min, checks 0 0 ms, in BeScared, partner in none (no link), day 0 hour 8.88',
    'ts3fixes: socialfinish: ShowVideos would signal, wait failed after 5.3/5 min, day 1 hour 6.20',
    'ts3fixes: routefail: Matty Crewe 0x14BC00F55FA12DB0 YoungAdult active, ReadBook target BookGeneral, PlanFromPoint sync FailedBecauseDestinationPortalLocked blocker None, goal 815.1,29.4,1658.2',
    'ts3fixes: routefail: Apollo Bloom 0x14BC00F55FA12DF0 YoungAdult active, SocialInteractionB target Sim, Replan sync FailedBecauseNoPathToDestination blocker Object 0x48EE0034EA1BE0E0 Sim at 1,2,3',
    'route async: long job 287 ms, own START, IC kind 0, route 25B8D40E, capped (UnseenFallback), no stuck-check mark, arm -, scope unseen, at 56896345 ms',
    'route async: long job 209 ms, foreign START, IC kind 0, route 253923B3, capAt -1 ms tail -1 ms, plan 5 ms host 200 ms other 4 ms, arm -, scope household-or-seen, at 56901331 ms',
    'ts3fixes: ufplan: kind 0 351 ms why 0 marked Jett Atkins own task, tick 318866, arm -, at 58006173 ms',
    'lock holders: sim waited 120 ms on lock 32315A20 (at 0x0072BD3A), held by main 228 from 0x0072C493 (wait #1)',
    'ts3fixes: syncwait heartbeat: 6000 waits, 285 logged, 0 in progress, 0 errors'];
  const c = censusLog(lines.join('\n'));
  const g = (k: string) => c.get(k);
  ok(g('social sync | ShowVideos timeout')?.min === 5.1, 'social timeout');
  ok(g('service queue | OrderDrink customer waits, server serving this customer')?.n === 1, 'customer wait');
  ok(g('service queue | MakeConversation server waits for customer, ok')?.min === 20.8, 'server wait');
  ok(g('social sync | BeScared ok, partner idle (no link)')?.n === 1, 'no-link partner');
  ok(g('socialfinish off | ShowVideos stood until the sync timed out')?.min === 5.3, 'socialfinish');
  ok(g('door lock | DestinationPortalLocked, ReadBook (active)')?.n === 1 && g('route fail | NoPathToDestination, SocialInteractionB (active)')?.n === 1, 'routefail');
  ok(g('route job | own START, cut by UnseenFallback, scope unseen')?.ms === 287 && g('route job | foreign START, uncut, scope household-or-seen')?.ms === 209, 'route jobs');
  ok(g('unseen plan | why 0')?.ms === 351 && g('game lock | sim waits at 0x0072BD3A (holder main)')?.ms === 120, 'ufplan, lock');
  ok(c.size === 11, 'heartbeat not counted, 11 keys: ' + c.size);
  const h = censusHealth([JSON.stringify({ result: { sims: [{ id: '1', name: 'A', species: 'human', scope: 'npc', lot: 'L', interaction: 'Sit', interaction_min: 90, still_min: 90, queue: 3 }] } }),
    JSON.stringify({ result: { sims: [{ id: '1', name: 'A', species: 'human', scope: 'npc', lot: 'L', interaction: 'Sit', interaction_min: 150, still_min: 150, queue: 3 }] } })].join('\n'), 60);
  ok(h.get('stand still | Sit')?.min === 150 && h.get('stand still | Sit')?.n === 1 && h.get('queue behind | Sit')?.n === 1, 'health stand / queue counted once at the longest');
  ok(render(c, h, ['a', 'b'], 5).includes('per cause group'), 'render');
  console.log('wait-report self-check: ok');
}

if (process.argv.includes('--self-check')) selfCheck();
else {
  const { values, positionals } = parseArgs({ allowPositionals: true, options: { top: { type: 'string' }, 'stand-min': { type: 'string' } } });
  if (!positionals[0]) { console.error('usage: node tools/harness/wait-report.ts <run dir> [<other run dir>] [--top 25] [--stand-min 60]'); process.exit(1); }
  const sm = Number(values['stand-min'] ?? 60), names = positionals.map(p => p.replace(/\/+$/, '').split('/').pop()!);
  console.log(render(censusRun(positionals[0], sm), positionals[1] ? censusRun(positionals[1], sm) : null, names, Number(values.top ?? 25)));
}
