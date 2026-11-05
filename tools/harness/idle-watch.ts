// Idle watch (player report: Sims stop doing anything with our mod, normal after removal). Pure observation on a loaded live-mode
// world: normal speed, free will on, no keep_alive, no player orders, autopilot answers dialogs only. Every POLL_S it reads
// sims_activity and records per Sim the current interaction, seconds with no interaction (and not routing), interaction changes.
//   node tools/harness/idle-watch.ts --role ref|test --out <summary.json> [--ref <ref summary.json>] [--minutes 15] [--threshold 120] [--seed N] [--save name] [--tag t]
//   node tools/harness/idle-watch.ts --self-check
// Output: probe-out/idle-watch/<tag>.jsonl (one row per minute: active household per Sim, other Sims aggregated) and --out (summary).
// Verdict: FAIL when a household Sim's longest no-interaction time exceeds --threshold (default 120 s) and the v0.2.4 reference
// (--ref, written by the --role ref run of the same save) stayed under it. Ref role: PASS/WARN only, it is the baseline.
import { appendFileSync, existsSync, mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { parseArgs } from 'node:util';

export type Snap = { id: string; name: string; interaction: string | null; queue?: number; routing?: boolean; in_transit?: boolean; active_household?: boolean; motives?: Record<string, number> };
export type Track = { name: string; active: boolean; cur: string | null; idleS: number; maxIdleS: number; idleTotalS: number; heldS: number; changes: number; mIdleS: number; mChanges: number; polls: number; absent: number; spans: number };
export type Summary = { role: string; save?: string; minutes: number; threshold: number; household: { name: string; maxIdleS: number; idleTotalS: number; changes: number; polls: number; needsStart?: Record<string, number>; needsEnd?: Record<string, number> }[]; householdMaxIdleS: number; otherSims: number; otherMaxIdleS: number; otherIdleFraction: number; [k: string]: unknown };

// One poll: dt seconds since the previous one. A Sim missing from snaps is not instantiated (away): its idle run restarts.
export function step(tr: Map<string, Track>, snaps: Snap[], dt: number): void {
  const seen = new Set<string>();
  for (const s of snaps) {
    seen.add(s.id);
    const t = tr.get(s.id) ?? { name: s.name, active: false, cur: null, idleS: 0, maxIdleS: 0, idleTotalS: 0, heldS: 0, changes: 0, mIdleS: 0, mChanges: 0, polls: 0, absent: 0, spans: 0 };
    tr.set(s.id, t);
    t.active = !!s.active_household; t.polls++;
    const cur = s.interaction || null;
    if (t.polls > 1 && cur !== t.cur) { t.changes++; t.mChanges++; }
    t.cur = cur;
    if (!cur && !s.routing) { // in_transit (ts3fixes walk-time hold) is NOT excused: a Sim held with no interaction is what we look for
      if (t.idleS === 0) t.spans++;
      t.idleS += dt; t.idleTotalS += dt; t.mIdleS += dt; t.maxIdleS = Math.max(t.maxIdleS, t.idleS);
      if (s.in_transit) t.heldS += dt;
    } else t.idleS = 0;
  }
  for (const [id, t] of tr) if (!seen.has(id)) { t.idleS = 0; t.cur = null; t.absent++; }
}

export function minuteRow(tr: Map<string, Track>, min: number) {
  const act: any[] = [], oth = { n: 0, idleS: 0, changes: 0, idleNow: 0, worst: ['', 0] as [string, number] };
  for (const t of tr.values()) {
    if (t.active) act.push([t.name, t.cur, t.mIdleS, t.mChanges, t.idleS]); // name, interaction now, idle s this minute, changes, current idle run s
    else { oth.n++; oth.idleS += t.mIdleS; oth.changes += t.mChanges; if (t.idleS > 0) oth.idleNow++; if (t.mIdleS > oth.worst[1]) oth.worst = [t.name, t.mIdleS]; }
    t.mIdleS = 0; t.mChanges = 0;
  }
  return { min, active: act, other: oth };
}

export function verdict(role: string, s: Summary, ref: Summary | null): { status: 'PASS' | 'FAIL' | 'WARN' | 'INCONCLUSIVE'; why: string } {
  const over = s.householdMaxIdleS > s.threshold;
  const line = `household longest no-interaction ${s.householdMaxIdleS} s (threshold ${s.threshold} s)`;
  if (role === 'ref') return over ? { status: 'WARN', why: `${line}: the v0.2.4 reference itself idles, so later arms of this save cannot FAIL` } : { status: 'PASS', why: `${line}, reference ok` };
  if (!over) return { status: 'PASS', why: `${line}${ref ? `; v0.2.4 ${ref.householdMaxIdleS} s` : ''}` };
  if (!ref) return { status: 'FAIL', why: `${line}; no v0.2.4 reference summary (run the ref arm of this save first)` };
  const refOver = ref.householdMaxIdleS > ref.threshold;
  return refOver ? { status: 'INCONCLUSIVE', why: `${line}; v0.2.4 also over (${ref.householdMaxIdleS} s)` } : { status: 'FAIL', why: `${line}; v0.2.4 only ${ref.householdMaxIdleS} s` };
}

if (process.argv.includes('--self-check')) {
  let bad = false; const ok = (c: boolean, m: string) => { if (!c) { bad = true; console.error(`idle-watch self-check: ${m}`); } };
  const m = new Map<string, Track>(), A = (i: string | null, q = 0, r = false, h = true): Snap[] => [{ id: 'a', name: 'Al', interaction: i, queue: q, routing: r, active_household: h }, { id: 'b', name: 'Bo', interaction: 'Sleep', active_household: false }];
  step(m, A('Eat'), 10); step(m, A(null), 10); step(m, A(null), 10); step(m, A(null, 0, true), 10); step(m, A(null), 10); step(m, A('Eat'), 10);
  const a = m.get('a')!;
  ok(a.maxIdleS === 20 && a.idleTotalS === 30 && a.spans === 2 && a.changes === 2 && a.idleS === 0, `tracker ${JSON.stringify(a)}`);
  step(m, [], 10); ok(m.get('a')!.absent === 1 && m.get('b')!.absent === 1, 'absent');
  const r = minuteRow(m, 1);
  ok(r.active.length === 1 && r.active[0][2] === 30 && r.other.n === 1 && r.other.changes === 0 && m.get('a')!.mIdleS === 0, `row ${JSON.stringify(r)}`);
  const S = (v: number): Summary => ({ role: 'x', minutes: 15, threshold: 120, household: [], householdMaxIdleS: v, otherSims: 0, otherMaxIdleS: 0, otherIdleFraction: 0 });
  ok(verdict('test', S(130), S(20)).status === 'FAIL' && verdict('test', S(130), S(200)).status === 'INCONCLUSIVE' && verdict('test', S(120), S(0)).status === 'PASS', 'verdict test');
  ok(verdict('test', S(5), null).status === 'PASS' && verdict('test', S(500), null).status === 'FAIL' && verdict('ref', S(500), null).status === 'WARN' && verdict('ref', S(5), null).status === 'PASS', 'verdict ref');
  console.log(bad ? 'idle-watch FAIL' : 'idle-watch ok'); if (bad) process.exit(1);
  process.exit(0);
}

const { values } = parseArgs({ options: {
  role: { type: 'string', default: 'test' }, out: { type: 'string' }, ref: { type: 'string' }, minutes: { type: 'string', default: '15' },
  threshold: { type: 'string', default: '120' }, tag: { type: 'string' }, port: { type: 'string', default: '47891' }, save: { type: 'string' }, seed: { type: 'string' }, poll: { type: 'string', default: '10' },
} });
if (!values.out || !['ref', 'test'].includes(values.role!)) { console.error('usage: idle-watch.ts --role ref|test --out <summary.json> [--ref <ref.json>] [--minutes 15] [--threshold 120]'); process.exit(2); }
const ROOT = resolve(import.meta.dirname, '..', '..'), base = `http://127.0.0.1:${values.port}`;
const BIN = process.env.TS3_BIN ?? 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\The Sims 3\\Game\\Bin';
const USER = process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3');
const tag = values.tag ?? `idle-${new Date().toISOString().slice(0, 16).replace(/[-:T]/g, '')}`, outDir = join(ROOT, 'probe-out', 'idle-watch');
const POLL_S = Number(values.poll), MINUTES = Number(values.minutes), T = Number(values.threshold);
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const stamp = () => new Date().toISOString().slice(11, 19);
async function ag(cmd: string, args?: object): Promise<{ ok: boolean; result?: any; error?: string }> {
  const q = new URLSearchParams({ cmd, timeout: '10000' }); if (args) q.set('args', JSON.stringify(args));
  try { return await (await fetch(`${base}/agent?${q}`, { signal: AbortSignal.timeout(30000) })).json() as any; } catch (e: any) { return { ok: false, error: String(e.message ?? e) }; }
}
const fail = (m: string): never => { console.log(`FAIL: idle-watch: ${m}`); process.exit(1); };
// read-only counts in Game\Bin and the user folder: ScriptError files, ts3native.log lines (all, and error-like)
const envCounts = () => {
  const errs = (d: string) => (existsSync(d) ? readdirSync(d).filter(f => /^ScriptError_.*\.xml$/i.test(f)).length : 0);
  const log = existsSync(join(BIN, 'ts3native.log')) ? readFileSync(join(BIN, 'ts3native.log'), 'latin1').split('\n') : [];
  return { scriptErrorsBin: errs(BIN), scriptErrorsUser: errs(USER), nativeLogLines: log.length, nativeLogErrorLines: log.filter(l => /\b(FAIL|ERROR|exception|fault)\b/i.test(l)).length };
};

mkdirSync(outDir, { recursive: true }); mkdirSync(dirname(resolve(ROOT, values.out)), { recursive: true });
const rows = join(outDir, `${tag}.jsonl`); writeFileSync(rows, '');
const gs = await ag('game_state');
if (!gs.ok || !gs.result?.in_world || gs.result.sub_state !== 'LiveModeState') fail(`not in a live-mode world (${gs.error ?? JSON.stringify(gs.result?.sub_state)})`);
// free will: the game's per-Sim autonomy switch, read only (enabled: true changes nothing nobody disabled)
const fw = await ag('autonomy', { sim: 'household', enabled: true });
const on: Record<string, boolean> = fw.result?.autonomy_on ?? {};
if (!fw.ok || !Object.keys(on).length) fail(`free will not readable: ${fw.error ?? 'no household Sims instantiated'}`);
const off = Object.entries(on).filter(([, v]) => !v).map(([k]) => k);
if (off.length) fail(`free will OFF in this save for ${off.join(', ')} (Autonomy.AutonomyDisabled): the test would measure nothing`);
console.log(`${stamp()} free will on for ${Object.keys(on).length} household Sims: ${Object.keys(on).join(', ')}`);
if (values.seed) { const sd = await ag('seed_rng', { seed: Number(values.seed) }); if (!sd.ok) fail(`seed_rng: ${sd.error}`); }
// the only agent upkeep: speed normal and dialogs answered. No keep_alive, no decline_requests, no orders.
const ap = await ag('autopilot', { enabled: true, speed: 'normal', dialogs: true });
if (!ap.ok) fail(`autopilot: ${ap.error}`);
const env0 = envCounts(), tr = new Map<string, Track>(), answered: string[] = [];
const t0 = Date.now(), tm0 = (await ag('time')).result?.minutes_total;
let lastClock = tm0, lastClockAt = t0, misses = 0, needsStart: Record<string, Record<string, number>> = {}, needsEnd: Record<string, Record<string, number>> = {}, nextMin = 1, last = t0, first = true;
console.log(`${stamp()} watching ${MINUTES} min, poll ${POLL_S} s, threshold ${T} s, role ${values.role}, tag ${tag}`);
while (Date.now() - t0 < MINUTES * 60000) {
  await sleep(POLL_S * 1000);
  const r = await ag('sims_activity', { motives: true }), now = Date.now();
  if (!r.ok) { if (++misses >= 3) fail(`agent not answering for ${misses} polls: ${r.error}`); continue; }
  misses = 0;
  step(tr, r.result as Snap[], (now - last) / 1000); last = now;
  for (const s of r.result as Snap[]) if (s.motives) { if (first) needsStart[s.name] = s.motives; needsEnd[s.name] = s.motives; }
  first = false;
  if (now - lastClockAt >= 30000) { // a paused game makes every Sim look idle: clock guard
    const c = (await ag('time')).result?.minutes_total;
    if (c !== undefined && c === lastClock) fail(`game clock stopped for ${Math.round((now - lastClockAt) / 1000)} s (paused or a dialog is open): idle seconds would be false`);
    lastClock = c; lastClockAt = now;
  }
  if (now - t0 >= nextMin * 60000) {
    const a = await ag('autopilot'); if (a.ok) answered.push(...(a.result.cleared ?? []).map(String));
    const row = minuteRow(tr, nextMin++);
    appendFileSync(rows, JSON.stringify(row) + '\n');
    console.log(`${stamp()} min ${row.min}: household ${row.active.map(x => `${x[0]}=${x[1] ?? 'NONE'}/${x[2]}s`).join(' ')}; others ${row.other.n} idle-now ${row.other.idleNow}`);
  }
}
const hh = [...tr.values()].filter(t => t.active), oth = [...tr.values()].filter(t => !t.active);
if (!hh.length) fail('no active-household Sim seen in the window');
const sum: Summary = { role: values.role!, save: values.save, minutes: MINUTES, threshold: T, simMinutes: (await ag('time')).result?.minutes_total - tm0, household: hh.map(t => ({ name: t.name, maxIdleS: Math.round(t.maxIdleS), idleTotalS: Math.round(t.idleTotalS), heldS: Math.round(t.heldS), spans: t.spans, changes: t.changes, polls: t.polls, absent: t.absent, needsStart: needsStart[t.name], needsEnd: needsEnd[t.name] })),
  householdMaxIdleS: Math.round(Math.max(...hh.map(t => t.maxIdleS))), otherSims: oth.length, otherMaxIdleS: Math.round(Math.max(0, ...oth.map(t => t.maxIdleS))),
  otherIdleFraction: oth.length ? +(oth.reduce((a, t) => a + t.idleTotalS, 0) / Math.max(1, oth.reduce((a, t) => a + t.polls * POLL_S, 0))).toFixed(3) : 0,
  dialogsAnswered: answered.length, env0, env1: envCounts(), freeWill: on, rows: rows.replace(ROOT, '').replace(/^[\\/]/, '') };
writeFileSync(resolve(ROOT, values.out), JSON.stringify(sum, null, 1));
const ref: Summary | null = values.ref && existsSync(resolve(ROOT, values.ref)) ? JSON.parse(readFileSync(resolve(ROOT, values.ref), 'utf8')) : null;
const v = verdict(values.role!, sum, ref);
console.log(`${stamp()} script errors bin ${sum.env0.scriptErrorsBin}->${sum.env1.scriptErrorsBin} user ${sum.env0.scriptErrorsUser}->${sum.env1.scriptErrorsUser}; ts3native.log lines ${sum.env0.nativeLogLines}->${sum.env1.nativeLogLines} (error-like ${sum.env0.nativeLogErrorLines}->${sum.env1.nativeLogErrorLines}); other Sims ${sum.otherSims} idle ${(sum.otherIdleFraction * 100).toFixed(1)}%, longest ${sum.otherMaxIdleS} s`);
console.log(`${v.status === 'FAIL' ? 'FAIL' : v.status === 'PASS' ? 'PASS' : v.status}: idle-watch ${values.role}: ${v.why}`);
process.exit(v.status === 'FAIL' ? 1 : 0);
