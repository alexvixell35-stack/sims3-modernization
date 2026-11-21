// Recurring game problems across every soak and play log, ranked, with whether a doc / registry row already covers each.
//   node tools/harness/log-issues.ts [--runs ~/dev/ts3-runs] [--user-dir <The Sims 3 user folder>] [--top 25] [--all] [--doc out.md]
//   node tools/harness/log-issues.ts --self-check
// Sources: <runs>/phase-*/ts3native.log (+ earlyfix.txt for the switches a run turned on, leak-N.json / alarms-N.json
// snapshots), <runs>/userlogs/*.log (the player's own sessions), <user dir>/ScriptError_*.xml (NRaas ErrorTrap reports, one
// per load). A run counts as "fixes on" when its earlyfix.txt turns on 5 or more switches, else "default".
// Issue kinds (key = normalised text; digits, ids and names dropped):
//   routefail    ts3fixes routefail lines: interaction x target type x result (household / active Sims; ts3fixes logs those)
//   routejob     ts3native "routefail job" lines not cut by our deadlines: interaction x target x result code
//   rfspam       one Sim failing the same interaction >= 5 times in a run (key: interaction x result; count = Sims)
//   syncwait     ts3fixes syncwait timeouts / failures: waiter interaction x partner interaction x outcome
//   exception    managed lines with Exception / threw; native "exception <code> at ..." by code and module
//   etderef      ts3fixes etderef: destroyed type x holder type
//   errortrap    ErrorTrap report lines (Destroyed Object Found, SimDescription Removed, exception frames)
//   reset        ts3fixes lines that report a reset (watchdog, owreset, ...)
//   stuck        stuck / loop lines (ts3fixes ufloop, watchdog, stuck checks)
//   growth       leak / alarm census numbers that grow from the first to the last snapshot of a run (key: the number's path)
//   rising       a line kind whose count in a run's second half is >= 2x its first half (>= 20 lines)
// Covered: the issue's most specific token (interaction, type, holder) appears in docs/5*.md or docs/registry.md.
import { existsSync, readdirSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';

const ROOT = resolve(import.meta.dirname, '..', '..');
export type Issue = { kind: string; key: string; token: string; count: number; ms: number; max: number; runsOn: Set<string>; runsOff: Set<string>; runsUser: Set<string>; example: string; covered?: string };
export type Run = { name: string; mode: 'on' | 'off' | 'user'; log: string; snaps: any[]; health?: string };

export const RE_RF = /ts3fixes: routefail: (.+?) 0x[0-9A-F]+ \w+ (\w+), (\S+) target (\S+), \S+ (?:sync|async) (\w+) blocker/;
export const RE_RJ = /routefail job: route [0-9A-F]{8}, (\S+) target (\S+), job seq \d+ \d+ ms result (\d+), (cut|uncut)/;
export const RE_SW = /ts3fixes: syncwait: .+? -> .+?, \w+, (timeout|failed|exit), [0-9.]+\/[0-9.E+]+ min, .*?, in (\S+), partner in (\S+)/;
export const RE_ET = /ts3fixes: etderef: destroyed (\S+) 0x[0-9A-F]+, held by (\S+)/;
export const RE_LOCK = /lock holders: sim waited (\d+) ms on lock [0-9A-F]+ \(at (0x[0-9A-F]+)\), held by (\w+) \d+ from (0x[0-9A-F]+)/;
export const RE_HOLD = /ts3fixes: hold: .+? \[(\w+)\] start, (\d+) sim-min/;
export const RE_KDHOLD = /kd cam recheck: route ([0-9A-F]{8}) (would hold|held), ([a-z ]+)/;
export const RE_BLOCK = /ts3fixes: routefail: .+?, (\S+) target (\S+), .*? blocker Object 0x[0-9A-F]+ (\S+)/;
const RE_ROUTE_IA = /route ([0-9A-F]{8}), (\S+) target /;
const LOCKS: Record<string, string> = { '0x004A94EC': 'package lock', '0x0072BD3A': 'routing-snapshot lock' };
export const RE_NEX = /^exception ([0-9A-F]{8}) at [0-9A-F]+ (?:\((\S+?)\+[0-9A-F]+\) )?/;
const RE_KIND = /^(ts3fixes: [a-zA-Z][a-zA-Z0-9 _-]{0,30}:|[A-Za-z][A-Za-z0-9 _-]{2,30}:)/;

export const norm = (s: string) => s.replace(/0x[0-9A-Fa-f]+/g, 'H').replace(/\b[0-9A-F]{8,16}\b/g, 'H').replace(/(?<![A-Za-z])-?\d+(\.\d+)?\b/g, 'N').replace(/\s+/g, ' ').trim();

export class Census {
  issues = new Map<string, Issue>();
  add(run: Run, kind: string, key: string, token: string, example: string, n = 1, ms = 0) {
    const id = kind + '|' + key;
    let i = this.issues.get(id);
    if (!i) this.issues.set(id, i = { kind, key, token, count: 0, ms: 0, max: 0, runsOn: new Set(), runsOff: new Set(), runsUser: new Set(), example: example.slice(0, 260) });
    i.count += n; i.ms += ms; if (ms > i.max) i.max = ms;
    (run.mode === 'on' ? i.runsOn : run.mode === 'off' ? i.runsOff : i.runsUser).add(run.name);
  }
}

export function scanLog(c: Census, run: Run) {
  const lines = run.log.split(/\r?\n/), half = lines.length / 2;
  const kinds = new Map<string, [number, number]>(), spam = new Map<string, number>(), routeIa = new Map<string, string>(), kdHolds: [string, string, string][] = [];
  let lastIa = 'unknown interaction';
  for (let n = 0; n < lines.length; n++) {
    const l = lines[n].replace(/^[^A-Za-z]*/, '');
    const k = RE_KIND.exec(l);
    if (k) { const kk = norm(k[1]); const v = kinds.get(kk) ?? [0, 0]; v[n < half ? 0 : 1]++; kinds.set(kk, v); }
    let m = RE_ROUTE_IA.exec(l);
    if (m && m[2] !== 'interaction') { routeIa.set(m[1], m[2]); lastIa = m[2]; }
    if ((m = /ts3fixes: gohere start: .*?interaction (\w+)/.exec(l))) lastIa = m[1];
    if ((m = RE_LOCK.exec(l))) { c.add(run, 'lock', `${LOCKS[m[2]] ?? 'lock at ' + m[2]} (waiter at ${m[2]}, holder ${m[3]} from ${m[4]}) during ${lastIa}`, lastIa, l, 1, Number(m[1])); continue; }
    if ((m = RE_HOLD.exec(l))) { c.add(run, 'held', `unseen walk-time hold: ${m[1]}`, m[1], l, 1, Number(m[2])); continue; }
    if ((m = RE_KDHOLD.exec(l))) { kdHolds.push([m[1], `${m[2]}, ${m[3].trim()}`, l]); continue; }
    if ((m = RE_BLOCK.exec(l))) c.add(run, 'reserved', `blocked by ${m[3]} (${m[1]} target ${m[2]})`, m[3], l);
    if (/ts3fixes: syncwait: /.test(l) && / partner (?!not )in a sync wait/.test(l)) c.add(run, 'synclock', norm(l.replace(/^.*?, in /, 'in ')).slice(0, 100), (/in (\w+)/.exec(l) ?? ['', 'sync'])[1], l);
    m = RE_RF.exec(l);
    if (m) {
      lastIa = m[3];
      if (/DestinationPortalLocked|ObjectInUse/.test(m[5])) c.add(run, 'reserved', `${m[5].replace(/^FailedBecause/, '')}: ${m[3]} target ${m[4]}`, m[4], l);
      const res = m[5].replace(/^FailedBecause/, '');
      c.add(run, 'routefail', `${m[3]} target ${m[4]}: ${res}`, m[3], l);
      const sk = `${m[3]}: ${res}|${m[1]}`;
      spam.set(sk, (spam.get(sk) ?? 0) + 1);
      continue;
    }
    if ((m = RE_RJ.exec(l))) { if (m[4] === 'uncut' && m[3] !== '1') c.add(run, 'routejob', `${m[1]} target ${m[2]}: result ${m[3]}`, m[1], l); continue; }
    if ((m = /ts3fixes: syncwait: .*?, in (\S+), partner/.exec(l))) lastIa = m[1];
    if ((m = RE_SW.exec(l))) { c.add(run, 'syncwait', `${m[2]} / partner ${m[3]}: ${m[1]}`, m[2], l); continue; }
    if ((m = RE_ET.exec(l))) { c.add(run, 'etderef', `${m[1]} held by ${m[2]}`, m[2].split('.').pop()!.split('+')[0], l); continue; }
    if ((m = RE_NEX.exec(l))) { c.add(run, 'exception', `native ${m[1]}${m[2] ? ' in ' + m[2] : ''}`, m[2] ?? m[1], l); continue; }
    if (/^ts3fixes: /.test(l) && /Exception\b| threw /.test(l)) { c.add(run, 'exception', norm(l).slice(0, 120), (/(\w+Exception)/.exec(l) ?? ['', 'Exception'])[1], l); continue; }
    if (/^ts3fixes: (ufloop|watchdog|stuck\w*):/.test(l) || /^ts3fixes: .*\bstuck\b/.test(l) && !/settings on|test switches/.test(l)) {
      const key = norm(l.replace(/ts3fixes: (\w+): .*?(route plans failing|restarts|stuck)/, 'ts3fixes: $1: $2')).replace(/household Sim .+? route/, 'household Sim route').slice(0, 110);
      c.add(run, 'stuck', key, (/(\w+): /.exec(l.slice(10)) ?? ['', 'stuck'])[1], l); continue;
    }
    if (/^ts3fixes: (owreset|\w*reset\w*):/.test(l)) c.add(run, 'reset', norm(l).slice(0, 100), 'reset', l);
  }
  // sim_health census (health.jsonl): interactions that held a Sim > 120 sim-min, once per Sim and interaction at its longest
  const long = new Map<string, [string, number, string]>();
  for (const hl of (run.health ?? '').split(/\r?\n/)) {
    const i = hl.indexOf('{');
    if (i < 0) continue;
    try { for (const x of (JSON.parse(hl.slice(i)).result ?? {}).sims ?? []) if (x.interaction && x.interaction_min > 120) { const k = x.id + '|' + x.interaction; if ((long.get(k)?.[1] ?? 0) < x.interaction_min) long.set(k, [x.interaction, x.interaction_min, `${x.name} [${x.species}, ${x.scope}] ${x.interaction} ${x.interaction_min} min, still ${x.still_min} min`]); } } catch { }
  }
  for (const [ia, min, ex] of long.values()) c.add(run, 'held', `interaction running > 120 sim-min: ${ia}`, ia, ex, 1, min);
  for (const [r, how, l] of kdHolds) c.add(run, 'held', `kd cam recheck ${how}: ${routeIa.get(r) ?? 'interaction unknown'}`, routeIa.get(r) ?? 'kd', l);
  for (const [k, n] of spam) if (n >= 5) c.add(run, 'rfspam', k.split('|')[0], k.split(':')[0], `${k.split('|')[1]}: ${k.split('|')[0]} x${n}`);
  // harness-driven and per-plan lines (go_here strolls, route jobs, KD, census) rise with the soak's phases, not with a bug
  for (const [k, [a, b]] of kinds) if (b >= 20 && b >= 2 * Math.max(a, 1) && !/gohere|routefail|^kd |ufplan|^route |lock holders|hitchwatch|syncwait|play:|ap |alarmtime|lotpass|hourlypass|^thread|^P\dc|autshadow|^diag|unseen fallback|footprint|initobj|idle check|goal validation|avoid RO|arena tail|decompress|cas trim|ts3fixes: mem|pkglock|gc blcap|lptrim/.test(k)) c.add(run, 'rising', k, k.replace(/^ts3fixes: /, '').replace(/:$/, ''), `${k} first half ${a}, second half ${b}`);
}

const flat = (o: any, p = '', out = new Map<string, number>()) => {
  if (o && typeof o === 'object') for (const [k, v] of Object.entries(o)) flat(v, p ? p + '.' + k : k, out);
  else if (typeof o === 'number') out.set(p, o);
  return out;
};
export function scanSnaps(c: Census, run: Run) {
  if (run.snaps.length < 2) return;
  const a = flat(run.snaps[0]), b = flat(run.snaps[run.snaps.length - 1]);
  for (const [k, v] of b) {
    const u = a.get(k) ?? 0;
    if (/\b(id|ms|max_ms|mb|bytes)\b|_ms$|time|tick/i.test(k)) continue;
    if (v - u >= 10 && v >= 1.5 * Math.max(u, 1)) c.add(run, 'growth', k, k.split('.').pop()!, `${k}: ${u} -> ${v}`, v - u);
  }
}

export function scanErrorTrap(c: Census, file: string, text: string) {
  const run: Run = { name: file, mode: 'user', log: '', snaps: [] };
  const body = text.split('<Content>')[1]?.split('Loaded Assemblies')[0] ?? '';
  const seen = new Map<string, number>();
  for (const raw of body.split(/\r?\n/)) {
    const l = raw.trim();
    if (!l || l.startsWith('--')) continue;
    const key = /^SimDescription Removed/.test(l) ? 'SimDescription Removed' : /^\s*at /.test(raw) ? 'frame ' + norm(l).slice(0, 100) : norm(l).slice(0, 120);
    seen.set(key, (seen.get(key) ?? 0) + 1);
  }
  for (const [k, n] of seen) c.add(run, 'errortrap', k, k.split(/[ .:]/).filter(x => x.length > 5).pop() ?? k, k, n);
}

export function coverage(c: Census, docs: { name: string; text: string }[]) {
  for (const i of c.issues.values()) {
    const t = i.token.replace(/[^A-Za-z0-9_]/g, '');
    if (t.length < 4) continue;
    const re = new RegExp('\\b' + t + '\\b');
    const d = docs.find(x => re.test(x.text));
    if (d) i.covered = d.name;
  }
}

const score = (i: Issue) => (i.runsOn.size + i.runsOff.size + i.runsUser.size) * Math.log10(10 + i.count);

export function loadRuns(dir: string): Run[] {
  const out: Run[] = [];
  if (!existsSync(dir)) return out;
  for (const d of readdirSync(dir).filter(x => x.startsWith('phase-')).sort()) {
    const p = join(dir, d), log = join(p, 'ts3native.log');
    if (!existsSync(log)) continue;
    const ef = existsSync(join(p, 'earlyfix.txt')) ? readFileSync(join(p, 'earlyfix.txt'), 'utf8') : '';
    const snaps: any[] = [];
    for (const f of readdirSync(p).filter(x => /^leak-\d+\.json$/.test(x)).sort((a, b) => Number(a.match(/\d+/)![0]) - Number(b.match(/\d+/)![0]))) {
      try { const t = readFileSync(join(p, f), 'utf8'); const j = JSON.parse(t.slice(t.indexOf('{'))); const al = join(p, f.replace('leak', 'alarms')); let a: any = null; if (existsSync(al)) { const s = readFileSync(al, 'utf8'); a = JSON.parse(s.slice(s.indexOf('{'))).result; } snaps.push({ leak: j.result, alarms: a }); } catch { }
    }
    out.push({ name: d, mode: (ef.match(/:true/g) ?? []).length >= 5 ? 'on' : 'off', log: readFileSync(log, 'latin1'), snaps, health: existsSync(join(p, 'health.jsonl')) ? readFileSync(join(p, 'health.jsonl'), 'utf8') : undefined });
  }
  const u = join(dir, 'userlogs');
  if (existsSync(u)) for (const f of readdirSync(u).filter(x => x.endsWith('.log')).sort()) out.push({ name: 'userlogs/' + f, mode: 'user', log: readFileSync(join(u, f), 'latin1'), snaps: [] });
  return out;
}

export const LOCK_KINDS = new Set(['lock', 'held', 'reserved', 'synclock']);
const unit = (i: Issue) => i.kind === 'lock' ? `, wait ${(i.ms / 1000).toFixed(1)} s total, max ${i.max} ms` : i.kind === 'held' && i.ms ? `, ${i.ms} sim-min total, max ${i.max}` : '';
export function renderRows(rows: Issue[]): string[] {
  return rows.map((i, n) => `${n + 1}. **${i.kind}**: \`${i.key}\` [verified] ${i.count}${unit(i)} in ${i.runsOn.size} fixes-on / ${i.runsOff.size} default / ${i.runsUser.size} player runs${i.covered ? `; mentioned in ${i.covered}` : ''}\n   e.g. \`${i.example.replace(/`/g, "'")}\``);
}

function selfCheck() {
  const ok = (x: boolean, w: string) => { if (!x) { console.error('log-issues self-check FAILED: ' + w); process.exit(1); } };
  const log = [
    'ts3fixes: routefail: Matty Crewe 0x14BC00F55FA12DB0 YoungAdult active, ReadBook target BookGeneral, PlanFromPoint sync FailedBecauseDestinationPortalLocked blocker None, goal 815.1,29.4,1658.2',
    ...Array(5).fill('ts3fixes: routefail: William Fangmann 0x14BC00F55FA12DD0 YoungAdult active, SocialInteractionB target Sim, Replan sync FailedBecauseNoPathToDestination blocker Object 0x48EE0034EA1BE0E0 Sim at 501.9,34.1,1535.6'),
    'routefail job: route 25AC338F, VisitLot target Lot, job seq 163 1 ms result 10, uncut, kind own Plan deadline 220 ms, follower 48EE00355D3C2740, plan 0 host 0 other 1 ms',
    'routefail job: route 2599E3F3, VisitCommunityLot target Lot, job seq 21 288 ms result 16, cut (UnseenFallback, mask 2), kind own Plan deadline 220 ms',
    'ts3fixes: syncwait: Lisa -> Odine, Completed, timeout, 5.1/5 min, checks 0 0 ms, in ShowVideos, partner in SocialInteractionB (linked running), day 0 hour 16.88',
    'ts3fixes: etderef: destroyed MovieSet 0x0, held by Sims3.Gameplay.Objects.RabbitHoles.MovieSet.RabbitHoleProxy (destroyed 0x0); x',
    'exception C0000005 at 0F12A0B4 (Sims3SettingsSetter.asi+2A0B4) (info 0 0BF0) thread 12 eip 0F12A0B4 esp 0019F000; stack code refs:',
    'ts3fixes: watchdog: household Sim William Fangmann route plans failing 12, no reset',
    'ts3fixes: ufloop: PartyBartende Clubber 0x14BC restarts Sims3.Gameplay.Pools.GetInPool+Definition',
    'lock holders: sim waited 120 ms on lock 32315A20 (at 0x0072BD3A), held by main 228 from 0x0072C493 (wait #1)',
    'routefail job: route 25B8D40E, EmptyTrash target TrashcanIndoorWithLid, job seq 48 287 ms result 16, cut (UnseenFallback, mask 2), kind own Plan deadline 220 ms',
    'kd cam recheck: route 25B8D40E would hold, in view, job seq 48 287 ms result 16',
    'ts3fixes: hold: Rei Matsuri [AutonomouslyAttendShow] start, 63 sim-min, live True'];
  const c = new Census();
  const run: Run = { name: 'r1', mode: 'on', log: log.join('\n'), snaps: [{ leak: { objects: { in_world: 100 } } }, { leak: { objects: { in_world: 400 } } }] };
  scanLog(c, run); scanSnaps(c, run);
  const g = (k: string) => c.issues.get(k);
  ok(g('routefail|ReadBook target BookGeneral: DestinationPortalLocked')?.count === 1, 'routefail');
  ok(g('routefail|SocialInteractionB target Sim: NoPathToDestination')?.count === 5 && g('rfspam|SocialInteractionB: NoPathToDestination')?.count === 1, 'spam');
  ok(g('routejob|VisitLot target Lot: result 10')?.count === 1 && ![...c.issues.keys()].some(k => k.includes('VisitCommunityLot')), 'routejob uncut only');
  ok(g('syncwait|ShowVideos / partner SocialInteractionB: timeout')?.count === 1, 'syncwait');
  ok(g('etderef|MovieSet held by Sims3.Gameplay.Objects.RabbitHoles.MovieSet.RabbitHoleProxy')?.token === 'RabbitHoleProxy', 'etderef');
  ok(g('exception|native C0000005 in Sims3SettingsSetter.asi')?.count === 1, 'native exception');
  ok([...c.issues.values()].filter(i => i.kind === 'stuck').length === 2, 'stuck lines');
  ok(g('growth|leak.objects.in_world')?.count === 300, 'growth');
  scanErrorTrap(c, 'x.xml', '<Content>\n-- Correction Logs --\nDestroyed Object Found: Sims3.Gameplay.Objects.Door.DoorSingleCountry\nDestroyed Object Found: Sims3.Gameplay.Objects.Door.DoorSingleCountry\nSimDescription Removed: Karen Santana\nLoaded Assemblies:\n');
  ok(g('errortrap|Destroyed Object Found: Sims3.Gameplay.Objects.Door.DoorSingleCountry')?.count === 2 && g('errortrap|SimDescription Removed')?.count === 1, 'errortrap');
  coverage(c, [{ name: 'docs/546', text: 'the ShowVideos sync' }]);
  ok(g('syncwait|ShowVideos / partner SocialInteractionB: timeout')?.covered === 'docs/546' && !g('routefail|ReadBook target BookGeneral: DestinationPortalLocked')?.covered, 'coverage');
  ok(norm('a 0x1F b 12.5 c 48EE0034EA1BE0E0 Sims3') === 'a H b N c H Sims3', 'norm');
  ok(g('lock|routing-snapshot lock (waiter at 0x0072BD3A, holder main from 0x0072C493) during ShowVideos')?.max === 120, 'lock wait joined to the running interaction');
  ok(g('held|kd cam recheck would hold, in view: EmptyTrash')?.count === 1 && g('held|unseen walk-time hold: AutonomouslyAttendShow')?.ms === 63, 'holds');
  ok(g('reserved|DestinationPortalLocked: ReadBook target BookGeneral')?.count === 1 && g('reserved|blocked by Sim (SocialInteractionB target Sim)')?.count === 5, 'reservations');
  console.log('log-issues self-check: ok');
}

if (process.argv.includes('--self-check')) selfCheck();
else {
  const { values } = parseArgs({ options: { runs: { type: 'string' }, 'user-dir': { type: 'string' }, top: { type: 'string' }, all: { type: 'boolean' }, doc: { type: 'string' } } });
  const runsDir = values.runs ?? join(homedir(), 'dev', 'ts3-runs');
  const userDir = values['user-dir'] ?? join(homedir(), 'Documents', 'The Sims 3 Collection', 'game', 'prefix', 'drive_c', 'users', 'Public', 'Documents', 'Electronic Arts', 'The Sims 3');
  const c = new Census();
  const runs = loadRuns(runsDir);
  for (const r of runs) { scanLog(c, r); scanSnaps(c, r); r.log = ''; }
  let xml = 0;
  if (existsSync(userDir)) for (const f of readdirSync(userDir).filter(x => /^ScriptError_.*\.xml$/.test(x))) { scanErrorTrap(c, f, readFileSync(join(userDir, f), 'utf8')); xml++; }
  const docs = [join(ROOT, 'docs', 'registry.md'), ...readdirSync(join(ROOT, 'docs')).filter(f => /^5\d\d-.*\.md$/.test(f)).map(f => join(ROOT, 'docs', f))]
    .filter(f => statSync(f).isFile()).map(f => ({ name: 'docs/' + f.split('/').pop()!.replace(/^(\d+)-.*/, '$1').replace('registry.md', 'registry'), text: readFileSync(f, 'utf8') }));
  coverage(c, docs);
  const all = [...c.issues.values()].sort((a, b) => score(b) - score(a));
  const top = Number(values.top ?? 25);
  // at most 6 rows per kind, so the census growth rows do not crowd out the other kinds (--all lifts the cap too)
  const perKind = new Map<string, number>();
  const rows = (values.all ? all : all.filter(i => !i.covered)).filter(i => !LOCK_KINDS.has(i.kind))
    .filter(i => { const n = (perKind.get(i.kind) ?? 0) + 1; perKind.set(i.kind, n); return values.all || n <= 6; }).slice(0, top);
  const locks = all.filter(i => LOCK_KINDS.has(i.kind)).sort((x, y) => x.kind === 'lock' && y.kind === 'lock' ? y.ms - x.ms : score(y) - score(x)).slice(0, top);
  const on = runs.filter(r => r.mode === 'on').length, off = runs.filter(r => r.mode === 'off').length, user = runs.filter(r => r.mode === 'user').length;
  const head = `${runs.length} logs (${on} fixes-on, ${off} default, ${user} player) + ${xml} ErrorTrap reports; ${c.issues.size} issue keys, ${all.filter(i => i.covered).length} mentioned in docs`;
  const body = `${head}\n\n## Issues not yet covered\n\n${renderRows(rows).join('\n')}\n\n## Interaction locks (lock waits by running interaction, holds, reservations, mutual sync waits)\n\n${renderRows(locks).join('\n')}\n`;
  if (values.doc) writeFileSync(values.doc, body);
  console.log(body);
}
