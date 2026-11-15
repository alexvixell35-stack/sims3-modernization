// Places every hitch_watch hitch of a phase run (hw-<k>.json snapshots) in ts3native.log and tags harness time (docs/247 §14).
// Reads only; never talks to the game.
//   node tools/harness/hw-align.ts <run dir> [--min 300] [--context 0]   run dir = phasesoak output (hw-*.json, ts3native.log)
//   node tools/harness/hw-align.ts --self-check
// Position: a hitch with wall_ms (agent from 2026-11-14 on) goes to the first log line stamped "at N ms" at or after it.
// An older hitch has only sim_time ("Sun. at 9:18 PM"): its sim minute is turned into SimClock ticks (37.5 a minute) and
// placed between the ufplan lines that carry both "tick N" and "at N ms"; the copy of that minute inside the hitch's phase
// window wins (the run spans more than one sim week). ufplan lines are capped at 120 a minute, so a capped stretch widens
// the window.
// Tags: agent = the hitch's own agent field (newer agent); mem_gc = between a "mem: gc-before" and "mem: gc-after" line
// (the snapshot's forced GC.Collect, two rounds); snapshot = after a "mem: p-<tag>" line and before the snapshot's
// hitch_watch read (leak_census, alarm_audit, aut_census); game = everything else. Periodic agent calls (social_round
// every ~15 s) show as a steady dt column in old data.
import { existsSync, readdirSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import assert from 'node:assert/strict';

export const UFPLAN = /ufplan: .*?, tick (\d+), arm \S+, at (\d+) ms/;
export const STAMP = / at (\d+) ms\b/;
export const MARK = /^ts3fixes: mem: (gc-before|gc-after|p-\S+)/;
const DAYS = ['Sun', 'Mon', 'Tue', 'Wed', 'Thu', 'Fri', 'Sat'];

// "Sun. at 9:18 PM" -> minute of the sim week (0 .. 10079)
export function weekMinute(s: string): number | null {
  const m = /^(\w{3})\w*\. at (\d+):(\d+) (AM|PM)/.exec(s);
  if (!m || !DAYS.includes(m[1])) return null;
  return DAYS.indexOf(m[1]) * 1440 + ((+m[2] % 12) + (m[4] === 'PM' ? 12 : 0)) * 60 + +m[3];
}

type Pt = { line: number; tick: number; wall: number };
type Hitch = { ms: number; sim_time: string; tick?: number; wall_ms?: number; gc?: number; agent?: string; before?: string[]; phase: number };
export type Placed = Hitch & { line: number; wall: number; tag: string };

export function place(log: string[], hs: Hitch[]): Placed[] {
  const pts: Pt[] = [], marks: { line: number; what: string }[] = [], stamped: { line: number; wall: number }[] = [];
  log.forEach((l, i) => {
    let m: RegExpExecArray | null;
    if ((m = UFPLAN.exec(l))) pts.push({ line: i, tick: +m[1], wall: +m[2] });
    if ((m = STAMP.exec(l))) stamped.push({ line: i, wall: +m[1] });
    if ((m = MARK.exec(l))) marks.push({ line: i, what: m[1] });
  });
  if (pts.length < 2) throw new Error('no ufplan "tick N ... at N ms" lines: cannot place hitches');
  const pmarks = marks.filter(x => x.what.startsWith('p-')).map(x => x.line);
  const win = (k: number) => [k === 0 ? 0 : (pmarks[k - 1] ?? 0), pmarks[k] ?? log.length];
  const at = (tick: number) => {  // linear between the two ufplan points around tick
    let j = pts.findIndex(p => p.tick >= tick);
    if (j <= 0) j = j < 0 ? pts.length - 1 : 1;
    const a = pts[j - 1], b = pts[j], f = b.tick === a.tick ? 0 : Math.min(1, Math.max(0, (tick - a.tick) / (b.tick - a.tick)));
    return { line: Math.round(a.line + f * (b.line - a.line)), wall: Math.round(a.wall + f * (b.wall - a.wall)) };
  };
  const out: Placed[] = [];
  for (const h of hs) {
    let pos: { line: number; wall: number } | null = null;
    if (h.wall_ms != null) {
      const s = stamped.find(x => x.wall >= h.wall_ms!) ?? stamped[stamped.length - 1];
      pos = { line: s.line, wall: h.wall_ms };
    } else {
      const wm = weekMinute(h.sim_time);
      if (wm == null) continue;
      const [a, b] = win(h.phase), m0 = Math.floor(pts[0].tick / 37.5) - 10080, m1 = Math.floor(pts[pts.length - 1].tick / 37.5);
      for (let wk = Math.floor(m0 / 10080); wk * 10080 <= m1; wk++) {
        const tick = (wk * 10080 + wm) * 37.5;
        if (tick + 37.5 < pts[0].tick || tick > pts[pts.length - 1].tick) continue;  // outside the stamped run
        const p = at(tick);
        if (p.line >= a && p.line <= b + 80) { pos = p; break; }  // + 80: the hw read comes after the p- mark's census calls
      }
    }
    if (!pos) continue;
    const gcb = marks.filter(x => x.what === 'gc-before' && x.line <= pos!.line + 3).pop();  // + 3: interpolation slack
    const gca = gcb && marks.find(x => x.what === 'gc-after' && x.line > gcb.line);
    const pm = pmarks.filter(x => x <= pos!.line).pop();
    let tag = 'game';
    if (h.agent && h.agent.length) tag = 'agent ' + h.agent;
    else if (gcb && gca && pos.line <= gca.line + 2) tag = 'mem_gc';
    else if (pm != null && pos.line - pm <= 80 && h.phase === pmarks.indexOf(pm)) tag = 'snapshot';
    out.push({ ...h, line: pos.line, wall: pos.wall, tag });
  }
  return out;
}

export function readRun(dir: string): Hitch[] {
  const seen = new Set<string>(), hs: Hitch[] = [];
  const files = readdirSync(dir).filter(f => /^hw-\d+\.json$/.test(f)).sort((a, b) => +a.slice(3, -5) - +b.slice(3, -5));
  for (const f of files) {
    const k = +f.slice(3, -5), top = JSON.parse(readFileSync(join(dir, f), 'utf8')).result?.top ?? [];
    for (const t of top) {
      const key = `${t.ms}|${t.sim_time}|${t.tick ?? ''}|${(t.before ?? []).join(';')}`;
      if (!seen.has(key)) { seen.add(key); hs.push({ ...t, phase: k }); }
    }
  }
  return hs;
}

if (process.argv.includes('--self-check')) {
  // real lines, phase-20261114-1737 ts3native.log
  const uf = 'ts3fixes: ufplan: kind 0 316 ms why 3 Katelyn Missoni own task, tick 167106, arm -, at 31464791 ms';
  const uf2 = 'ts3fixes: ufplan: kind 0 671 ms why -1, tick 167352, arm -, at 31466949 ms';
  assert.deepEqual(UFPLAN.exec(uf)?.slice(1), ['167106', '31464791']);
  assert.ok(UFPLAN.exec(uf2));
  assert.equal(UFPLAN.exec('ts3fixes: ufplan: lines capped at 30000'), null);
  assert.equal(STAMP.exec('route async: long job 268 ms, own START, IC kind 0, route 257561F1, capped (UnseenFallback), no stuck-check mark, arm -, scope unseen, at 32379804 ms')?.[1], '32379804');
  assert.equal(STAMP.exec('ts3fixes: syncwait: Jun -> Michael, Completed, ok, 24.5/120 min, checks 92 2 ms, in OrderDrink'), null);
  assert.equal(MARK.exec('ts3fixes: mem: gc-before')?.[1], 'gc-before');
  assert.equal(MARK.exec('ts3fixes: mem: p-socials31')?.[1], 'p-socials31');
  assert.equal(MARK.exec('diag: mem #7 mark ev=gc-before priv=0.0'), null);
  // new agent line (format string in HitchWatchHandlers.cs Keep)
  assert.equal(STAMP.exec('ts3fixes: hitchwatch: 736 ms, tick 425925, Sun. at 9:18 PM, gc +0, agent -, at 32385746 ms')?.[1], '32385746');
  assert.equal(weekMinute('Sun. at 9:18 PM'), 21 * 60 + 18);
  assert.equal(weekMinute('Sat. at 12:05 AM'), 6 * 1440 + 5);
  assert.equal(weekMinute('Mon. at 12:50 PM'), 1440 + 12 * 60 + 50);
  assert.equal(weekMinute('?'), null);
  // placement: two weeks of ufplan points, phase window picks the second copy; GC and snapshot tags
  const log = [
    'ts3fixes: ufplan: kind 0 200 ms why -1, tick 0, arm -, at 1000 ms',
    'ts3fixes: mem: p-base',
    'ts3fixes: ufplan: kind 0 200 ms why -1, tick 378000, arm -, at 2000 ms',
    'ts3fixes: ufplan: kind 0 200 ms why -1, tick 425925, arm -, at 3000 ms',
    'ts3fixes: ufplan: kind 0 200 ms why -1, tick 426000, arm -, at 3002 ms',
    'filler', 'filler', 'filler',
    'ts3fixes: mem: gc-before',
    'ts3fixes: ufplan: kind 0 200 ms why -1, tick 427000, arm -, at 3500 ms',
    'ts3fixes: mem: gc-after',
    'ts3fixes: mem: p-socials1',
  ];
  const r = place(log, [
    { ms: 736, sim_time: 'Sun. at 9:18 PM', phase: 1 },
    { ms: 900, sim_time: 'Sun. at 9:45 PM', phase: 1 },
    { ms: 400, sim_time: 'x', wall_ms: 3001, agent: 'social_round 390', phase: 1 },
  ]);
  assert.equal(r.length, 3);
  assert.deepEqual([r[0].line, r[0].tag], [3, 'game']);
  assert.equal(r[1].tag, 'mem_gc');
  assert.equal(r[2].tag, 'agent social_round 390');
  console.log('hw-align self-check OK');
} else {
  const dir = process.argv[2];
  if (!dir || !existsSync(join(dir, 'ts3native.log'))) { console.error('usage: node tools/harness/hw-align.ts <run dir> [--min 300] [--context 0]'); process.exit(2); }
  const arg = (n: string, d: number) => { const i = process.argv.indexOf(n); return i > 0 ? +process.argv[i + 1] : d; };
  const min = arg('--min', 300), ctx = arg('--context', 0);
  const log = readFileSync(join(dir, 'ts3native.log'), 'latin1').split(/\r?\n/);
  const rows = place(log, readRun(dir).filter(h => h.ms >= min)).sort((a, b) => a.wall - b.wall);
  let prev: number | null = null;
  for (const r of rows) {
    const dt = r.tag === 'game' && prev != null ? ((r.wall - prev) / 1000).toFixed(1) + ' s' : '';
    if (r.tag === 'game') prev = r.wall;
    console.log(`phase ${r.phase}  ${String(r.ms).padStart(6)} ms  ${r.sim_time.padEnd(18)} line ${r.line}  at ${r.wall}  ${r.tag}${r.gc ? ` gc +${r.gc}` : ''}  ${dt}`);
    if (ctx > 0) for (const l of log.slice(Math.max(0, r.line - ctx), r.line + ctx)) console.log('    ' + l.slice(0, 220));
  }
  const by = new Map<string, number[]>();
  for (const r of rows) { const k = r.tag.startsWith('agent') ? 'agent ' + r.tag.split(' ')[1] : r.tag; by.set(k, [...(by.get(k) ?? []), r.ms]); }
  console.log(`\n>= ${min} ms by tag:`);
  for (const [k, v] of by) console.log(`  ${k.padEnd(24)} n=${v.length} >=500 ${v.filter(x => x >= 500).length} sum=${Math.round(v.reduce((a, b) => a + b, 0))} max=${Math.max(...v)}`);
}
