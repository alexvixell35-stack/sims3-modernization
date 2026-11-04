// Worst hitches (>= 300 ms wall per Simulate iteration) attributed from existing captures (docs/96). Reads only; never talks to the game.
//   node tools/harness/worst-hitches.ts [--min 300] [--since 2026-10-16] [--top 25] [--json out.json]
//   node tools/harness/worst-hitches.ts --self-check
// Inputs per run tag in probe-out/profile: <tag>-<secs>-hitches.jsonl and -seconds.csv (stutter-profile.js, QPC ms clock),
// -simstack.jsonl (native sim-thread stacks, epoch ms), -managed.jsonl (C# stacks, no time stamps), -activity.jsonl (ms since the
// profile-world start), -rng.json; probe-out/queue/<tag>.log (UTC stamps of the profile-world start, repro steps, samplers).
// Clock alignment (QPC ms -> epoch ms): epoch - QPC is one constant per Windows boot. Coarse: profile-world start stamp + 3.8 s (the
// profiler's first seconds row lands 3.2-4.6 s after it in runs with a fine fit) - first row QPC. Runs whose coarse offsets agree
// within 120 s share a boot. Fine: for runs with simstack samples, the offset (coarse +-15 s, 10 ms steps) where samples inside
// route-heavy hitch iterations show a route-search frame and samples inside route-free ones do not; fits with score >= 15 (Isla
// Paradiso runs, many long plans) give the boot offset for every run of that boot (they agree within 2-40 ms across a day).
// Managed samples are placed with stall-split.ts alignRun (uniform 5 Hz from ~57 s after the first row; per-run fit, inferred).
// Set TS3_REPO=<main checkout> when running from a git worktree.
import { existsSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadFunctions, lookup, split } from './sample-split.ts';
import { alignRun, category, rootOf } from './stall-split.ts';
import { split as budgetSplit, STEER } from './sim-budget-report.ts';
import { keyName } from './hitch-attr-report.ts';
import { OG_OTHER_MIN, summarize } from './machine-load.ts';
import { isSetup } from './repro-loop.ts';

const ROOT = process.env.TS3_REPO ?? join(import.meta.dirname, '..', '..');
const PROFILE = join(ROOT, 'probe-out', 'profile'), QUEUE = join(ROOT, 'probe-out', 'queue');
const LAG_MS = 3800, BOOT_GAP_MS = 120_000, FINE_MIN = 15;
// route search: Route_PlanLocked 0x82C6E0, planner 0x65E0D0, world/lot/cell A*, water JPS (docs/28 N3)
const ROUTE_FN = /FUN_00(82c6e0|65e0d0|676260|63e340|61e500|620560|61fe10)\+/i;

type H = Record<string, any> & { t: number; wallMs: number };
type Ev = { at: number; text: string };
export type Cls = 'harness step' | 'first minute after load' | 'route search' | 'TaskObjects burst' | 'locomotion' | 'ThreadedObjects, no route' | 'mixed route + script' | 'other';

const jsonl = (f: string) => existsSync(f) ? readFileSync(f, 'utf8').split('\n').filter(Boolean).flatMap(l => { try { return [JSON.parse(l)]; } catch { return []; } }) : [];
const csvRows = (text: string) => { const [head, ...ls] = text.trim().split(/\r?\n/); const c = head.split(','); return ls.map(l => Object.fromEntries(l.split(',').map((v, i) => [c[i], Number(v)]))); };
const med = (xs: number[]) => { const s = [...xs].sort((a, b) => a - b); return s.length ? s[s.length >> 1] : NaN; };
const pct = (n: number, d: number) => d ? `${(100 * n / d).toFixed(1)}%` : '-';

// the stage signature of one iteration; rel = s since the first seconds row; step = a harness step logged within the window
export function classify(h: H, rel: number, step: string | null): Cls {
  const w = h.wallMs, f = (k: string) => (h[k] ?? 0) / w;
  if (step || (h.steps === 0 && w >= 1000)) return 'harness step';
  if (rel < 60) return 'first minute after load';
  if (f('route') >= 0.5) return 'route search';
  if (f('taskObjects') >= 0.5) return 'TaskObjects burst';
  if (f('locomotion') >= 0.4) return 'locomotion';
  if (f('threaded') >= 0.5 && f('route') < 0.2) return 'ThreadedObjects, no route';
  if (f('threaded') >= 0.5) return 'mixed route + script';
  return 'other';
}

// one iteration's attribution, shared with freeze-report.ts: the class above, the sim-budget bucket with the most ms in it
// (sim-budget-report.ts split), the longest route-job wait ([HitchAttr] key, hitch-attr-report.ts names) and the plans by entry
export function cause(h: H, rel: number, step: string | null) {
  const p = budgetSplit(h, h.wallMs, STEER.has(String(h.rtMaxKey)) ? 1 : 0);
  const lead = (Object.entries(p) as [string, number][]).sort((a, b) => b[1] - a[1])[0][0];
  const waitMs = ['rtLock', 'rtOtherLock', 'rtJob', 'rtPlanWait'].reduce((s, k) => s + (h[k] ?? 0), 0);
  return { cls: classify(h, rel, step), lead, waitMs, wait: h.rtMaxKey ? `${keyName(h.rtMaxKey)} ${Math.round(h.rtMaxMs ?? 0)} ms` : '',
    plans: Object.entries(h.byEntry ?? {}).map(([k, v]: any) => `${k.replace('Route_', '')} ${v.n}/${Math.round(v.ms)}`).join(', '),
    tasks: taskList(h), topTask: h.tasks?.[0]?.name ?? '', holder: holderText(h) };
}
// [HitchAttr] Holders=1 (stutter-profile.js `holders`, docs/165 §8): the sim thread's longest lock wait, else the main thread's
export function holderText(h: H): string {
  const w = (h.holders ?? []) as { who: string; ms: number; lock: string; by: string; tid: number; site: string }[];
  const x = w.find(y => y.who === 'sim') ?? w[0];
  return x ? `${x.who} waited ${Math.round(x.ms)} ms on lock ${x.lock} held by ${x.by} ${x.tid} from ${x.site === '0x00000000' ? 'unknown site' : x.site}` : '';
}
// the profiler's slowest script runs of the iteration (stutter-profile.js task attribution, docs/166), top 3 as "name ms"
export function taskList(h: H, n = 3): string {
  return (h.tasks ?? []).slice(0, n).map((t: { name: string; ms: number; to?: string }) => `${t.name} ${Math.round(t.ms)}${t.to ? ` (yield ${t.to.split(' < ')[0]})` : ''}`).join(', ');
}

// queue log: "<ISO> $ ... profile-world.ts --tag <tag> ..." then "HH:MM:SS text" lines (UTC) until the next "$" line
// secs: the profiler length; a tag reused with another --minutes (verify-2026-10-16-*: 420 and 480 s) keeps only its own run
export function queueEvents(log: string, tag: string, secs = 0): { start: number | null; events: Ev[] } {
  let start: number | null = null, day = '', events: Ev[] = [], inRun = false;
  for (const l of log.split(/\r?\n/)) {
    const m = /^(\d{4}-\d\d-\d\d)T([\d:.]+)Z \$ (.*)$/.exec(l);
    if (m) {
      const min = /--minutes (\d+(?:\.\d+)?)/.exec(m[3]);
      inRun = m[3].includes('profile-world.ts') && m[3].includes(`--tag ${tag} `) && (!secs || !min || Math.round(Number(min[1]) * 60) === secs);
      if (inRun) { start = Date.parse(`${m[1]}T${m[2]}Z`); day = m[1]; events = []; }
      continue;
    }
    const e = /^(\d\d:\d\d:\d\d) (.*)$/.exec(l);
    if (inRun && e && start !== null) { let at = Date.parse(`${day}T${e[1]}Z`); if (at < start - 3600_000) at += 86400_000; events.push({ at, text: e[2] }); }
  }
  return { start, events };
}
const isStep = (text: string) => /^repro .* step (?!set_speed|camera_pan|wait)/.test(text) || /save_game|saving/.test(text);

// ---- real vs excluded (docs/161 §6), shared by hitch-ab.ts, freeze-report.ts and this file: only hitches a player would see
// in natural play count. Every slow iteration / publish gap gets exclusion() = null (real) or the first matching reason below;
// whole runs get RunCtx.invalid (left out of pair means and pools, named).
export const EXCL = ['first minute', 'profiler edge', 'repro-triggered', 'sampler', 'probe call', 'render-paced'] as const;
export type Excl = typeof EXCL[number];
// QPC ms (the profiler's clock); setupEnd: a setup step's window (repro-loop.ts isSetup) runs a .. setupEnd (end of its wait);
// ms: a probe call's own round trip (calls.jsonl; absent for the older upkeep-round guesses)
export type Win = { a: number; b: number; why: Excl; what: string; setupEnd?: number; ms?: number };
export type RunCtx = { wins: Win[]; foreground: boolean; invalid: string | null; offSrc: string };
export const SIM_MS = 50, EDGE_MS = 2000, STEP_POST_MS = 10_000;

// first minute after the first seconds row (load) and the profiler's stop edge. A window that did not start right after a load or
// reload (queue.ts reuseSession / profile.repros, docs/170 §2: <tag>-session.json sinceLoad > 0) keeps its first minute and loses
// only the first START_EDGE_S rows (profiler attach, unpause)
export const START_EDGE_S = 15;
export function baseWins(rows: { t: number }[], afterLoad = true): Win[] {
  if (!rows.length) return [];
  return [afterLoad ? { a: -Infinity, b: rows[Math.min(59, rows.length - 1)].t, why: 'first minute', what: 'load' }
    : { a: -Infinity, b: rows[Math.min(START_EDGE_S - 1, rows.length - 1)].t, why: 'profiler edge', what: 'profiler start (no load before)' },
  { a: rows.at(-1)!.t - EDGE_MS, b: Infinity, why: 'profiler edge', what: 'profiler stop' }];
}
// false when profile-world.ts wrote <tag>-session.json with sinceLoad > 0 (no load or reload right before this window)
export const afterLoad = (tag: string, dir = PROFILE) => {
  const f = join(dir, `${tag}-session.json`);
  try { return !(existsSync(f) && Number(JSON.parse(readFileSync(f, 'utf8')).sinceLoad) > 0); } catch { return true; }
};

// docs/178 T2: a probe call excludes only a hitch it can explain. Never one led by a route wait (rtLock/rtOtherLock/rtJob/
// rtPlanWait >= half the iteration: the sim waited on a route job's lock or result, which no agent call causes; sv-crowd-0333's
// 2085 ms steering 0x85FE10 freeze had a 58 ms game_state call on top); else the call's round trip must cover >= half the
// hitch, or the command leaves sim-thread work behind after it returns. Older upkeep-round guesses (no ms) still exclude
export const ROUTE_WAITS = ['rtLock', 'rtOtherLock', 'rtJob', 'rtPlanWait'], BLOCKING_CALLS = /^(spawn_sims|remove_sims|crowd_go|save_game)\b/;
export function callExplains(w: Pick<Win, 'what' | 'ms'>, ms: number, iter?: H): boolean {
  if (iter && ROUTE_WAITS.reduce((s, k) => s + (iter[k] ?? 0), 0) >= 0.5 * iter.wallMs) return false;
  return w.ms === undefined || BLOCKING_CALLS.test(w.what) || w.ms >= 0.5 * ms;
}
// the longest hitch of each seconds row (a hitch belongs to the first row whose t >= its t): a row's probe-call exclusion is
// judged by it (callExplains), so a real freeze keeps its second
export function rowLongest(rows: { t: number }[], hitches: H[]): (H | undefined)[] {
  const out: (H | undefined)[] = rows.map(() => undefined);
  for (const h of hitches) { let lo = 0, hi = rows.length; while (lo < hi) { const m = (lo + hi) >> 1; if (rows[m].t < h.t) lo = m + 1; else hi = m; } if (lo < rows.length && (out[lo]?.wallMs ?? -1) < h.wallMs) out[lo] = h; }
  return out;
}

// x = one iteration [t - wallMs, t] (steps from hitches.jsonl) or one publish gap [t0, t1] with the longest sim iteration inside
// and the iteration boundaries it spans, or one seconds row; iter = that iteration (the gap's / row's longest), for the
// probe-call rule above; null = real
export function exclusion(x: { a: number; b: number; wallMs?: number; steps?: number; gap?: { longestIterMs: number; iters: number }; iter?: H }, ctx: Pick<RunCtx, 'wins' | 'foreground'>): { why: Excl; what: string } | null {
  const dur = x.gap ? x.b - x.a : x.wallMs ?? x.iter?.wallMs; // undefined (rows without a hitch, frames): any overlapping call
  for (const why of EXCL) { const w = ctx.wins.find(v => v.why === why && v.a < x.b && v.b > x.a && (why !== 'probe call' || dur === undefined || callExplains(v, dur, x.iter))); if (w) return w; }
  // an unlogged save or load: no sim step and >= 1 s (classify's old 'harness step' rule)
  if (x.steps === 0 && (x.wallMs ?? 0) >= 1000) return { why: 'repro-triggered', what: '0-step iteration >= 1 s (save/load)' };
  // no sim iteration >= 50 ms inside: the screen waited on the render side (unfocused Sleep(10)/SleepEx(30), S3SS once-per-frame
  // publish, docs/41); with [Harness] ForegroundRender=1 a gap over >= 2 publish-less iterations is still a sim-side cause
  if (x.gap && x.gap.longestIterMs < SIM_MS && !(ctx.foreground && x.gap.iters >= 2)) return { why: 'render-paced', what: `no iteration >= ${SIM_MS} ms inside` };
  return null;
}

const hms = (day: string, s: string, after: number) => { let t = Date.parse(`${day}T${s}Z`); if (t < after - 3600_000) t += 86400_000; return t; };
// the run's time windows and verdict from its files. off = epoch - QPC when known exactly (frames.csv); else the coarse
// queue-log start + LAG_MS (+-1.5 s, windows padded by that)
export const stepLabel = (text: string) => {
  const m = /^repro \S+ step (\w+)(?: \[[^\]]*\b(on|off) #\d+\])?/.exec(text);
  return m ? `${m[1]}${m[2] ? ` ${m[2]}` : ''}` : null;
};
export function runContext(tag: string, rows: { t: number }[], secs: number, off = NaN): RunCtx {
  const wins = baseWins(rows, afterLoad(tag)), qf = join(QUEUE, `${tag}.log`), log = existsSync(qf) ? readFileSync(qf, 'utf8') : '';
  const q = log ? queueEvents(log, tag, secs) : { start: null, events: [] as Ev[] };
  const exact = Number.isFinite(off), pad = exact ? 200 : 1500;
  if (!exact && q.start !== null && rows.length) off = q.start + LAG_MS - rows[0].t;
  const offSrc = exact ? 'frames.csv' : Number.isFinite(off) ? 'queue start + lag' : 'none';
  const add = (a: number, b: number, why: Excl, what: string, ms?: number) => { if (Number.isFinite(off)) wins.push({ a: a - off - pad, b: b - off + pad, why, what, ...(ms === undefined ? {} : { ms }) }); };
  const day = q.start !== null ? new Date(q.start).toISOString().slice(0, 10) : '';
  // repro steps (from the end of the previous step, at most 15 s before the logged return: a sync plan runs inside the call),
  // saves, speed changes, camera_pan, test_env season/moon resets; each to 10 s after
  let prev = q.start ?? 0, setupWin: Win | undefined;
  const marked = q.events.some(e => SETUP_MARK.test(e.text)), nth = new Map<string, number>();
  for (const e of q.events) {
    // a setup step's wait ends at its "done after" line (repro-loop.ts runStep); 1 s for the log's second resolution, no clock
    // pad (a 1.5 s coarse pad ate the next firing step's call in 2026-10-22 bp-stuckcheck-loop)
    // any step's wait end also starts the next step's window there: a firing's fire step keeps its wait (its effect) instead of
    // the next firing's first step taking it over
    if (/^(?:firing #\d+|step \w+) done after/.test(e.text)) {
      if (setupWin) setupWin.setupEnd = Math.max(setupWin.setupEnd!, e.at + 1000 - off);
      setupWin = undefined; prev = Math.max(prev, e.at + 1000); continue;
    }
    const m = /^repro \S+ step (\w+)/.exec(e.text), other = /save_game|saving|test_env guard/.test(e.text);
    if (m && m[1] === 'wait') continue;
    // A/B repros: the arm from the step note ("[fire off #3]") joins the label, so --scenario lists each arm apart
    if (m || other) {
      const n = wins.length;
      add(m ? Math.max(prev, e.at - 15_000) : e.at - 3000, e.at + 1000 + STEP_POST_MS, 'repro-triggered', m ? stepLabel(e.text)! : e.text.slice(0, 30)); if (m) prev = e.at + 1000;
      setupWin = m && stepSetup(e.text, marked, nth) && wins.length > n ? wins[n] : undefined;
      if (setupWin) setupWin.setupEnd = e.at + 1000 - off; // no done line (older logs): the step's return
    }
  }
  // samplers (both suspend the sim thread, 5 Hz: every iteration >= 200 ms in the window overlaps a suspension)
  const sj = jsonl(join(PROFILE, `${tag}-samplers.jsonl`));
  if (sj.length) for (const s of sj) add(s.t0, s.t1, 'sampler', s.name);
  else for (const e of q.events) { const m = /^(managed|native) sampler (\d+) s$/.exec(e.text); if (m && +m[2] > 0) add(e.at, e.at + 1000 * (+m[2] + 1), 'sampler', m[1]); }
  // agent calls: they run on the sim thread (keep_alive, sims_activity, autopilot, patches, time, test_env)
  const calls = jsonl(join(PROFILE, `${tag}-calls.jsonl`));
  if (calls.length) for (const c of calls) add(c.t0, c.t1, 'probe call', c.cmd, c.t1 - c.t0);
  else if (day) {
    // older runs: one upkeep round per activity line (t = ms since profile-world's t0, logged after sims_activity); t0 from the
    // upkeep.log stamps written at the end of the same round (1 s resolution: the tightest round bounds it)
    const act = jsonl(join(PROFILE, `${tag}-activity.jsonl`)), uf = join(PROFILE, `${tag}-upkeep.log`);
    const up = existsSync(uf) ? readFileSync(uf, 'utf8').split(/\r?\n/).flatMap(l => { const m = /^(\d\d:\d\d:\d\d) /.exec(l); return m ? [hms(day, m[1], q.start!)] : []; }) : [];
    const t0 = act.length && up.length === act.length ? Math.min(...act.map((x, i) => up[i] + 1000 - x.t)) - 300 : q.start! + 1500;
    for (const x of act) add(t0 + x.t - 2500, t0 + x.t + 1000, 'probe call', 'upkeep round');
  }
  const native = (existsSync(QUEUE) ? readdirSync(QUEUE) : []).filter(f => f.startsWith(`${tag}-`) && /^\d{4}-ts3native\.log$/.test(f.slice(tag.length + 1))).map(f => readFileSync(join(QUEUE, f), 'utf8')).join('\n');
  const foreground = /ForegroundRender\s*[=:]\s*1\b/.test(native) || q.events.some(e => /ForegroundRender\s*[=:]\s*1\b/.test(e.text));
  // a window ended early by its stop rule (profile-world.ts <tag>-stop.json): that length, not the cap in the file name
  const sf = join(PROFILE, `${tag}-stop.json`), ran = existsSync(sf) ? Number(JSON.parse(readFileSync(sf, 'utf8')).secs) || secs : secs;
  return { wins, foreground, invalid: runInvalid(join(PROFILE, `${tag}-machine.jsonl`), rows.length, ran, q), offSrc };
}

// repro firings (docs/168 §3), shared by hitch-ab.ts --scenario and freeze-report.ts --repro-causes. steps = queue-log texts
// of the repro-triggered windows, in runContext's order (one per window); with loop / ABBA arm notes ('[arm off #1]',
// '[fire on #2]') a firing runs from its first step's window start to the next firing's start (or the run end): delayed
// effects (stuck checks 1 sim-minute after the arming call) stay with their arm instead of the next step's window
// Setup steps (docs/168 §7) leave their firing: step start .. end of its wait becomes a '<arm> setup' piece (same n), so the
// arm counts only what the firing step and its effect cost; pieces are disjoint and in time order
export function firingWins(wins: Win[], steps: string[] | undefined, end: number) {
  const f: { a: number; b: number; arm: string; n: number }[] = [];
  if (steps?.length !== wins.length) return f; // no offset (no windows) or a log that does not line up: old per-step windows
  wins.forEach((w, i) => { const m = /^repro \S+ step \w+ \[[^\]]*\b(on|off) #(\d+)\]/.exec(steps[i]); if (m && f.at(-1)?.n !== +m[2]) f.push({ a: w.a, b: end, arm: m[1], n: +m[2] }); });
  f.forEach((x, i) => { if (f[i + 1]) x.b = f[i + 1].a; });
  const cuts = wins.filter(w => w.setupEnd !== undefined).map(w => [w.a, w.setupEnd!]).sort((p, q) => p[0] - q[0]);
  return f.flatMap(x => {
    const out: typeof f = [];
    let t = x.a;
    for (const [a0, b0] of cuts) {
      const a = Math.max(a0, t), b = Math.min(b0, x.b);
      if (b <= a) continue;
      if (a > t) out.push({ ...x, a: t, b: a });
      if (out.at(-1)?.arm === `${x.arm} setup` && out.at(-1)!.b === a) out.at(-1)!.b = b; else out.push({ ...x, a, b, arm: `${x.arm} setup` });
      t = b;
    }
    if (t < x.b) out.push({ ...x, a: t });
    return out;
  });
}
// is this logged step a setup step: its ' (setup)' mark (profile-world.ts, docs/168 §7); in logs from before the mark, repros.json
// (loop steps by position in their firing, plain steps by command + note) with repro-loop.ts isSetup
export const SETUP_MARK = /^repro \S+ step \w+(?: \[[^\]]*\])? \(setup\):/;
let reproDefs: any[] | undefined;
export function stepSetup(text: string, marked: boolean, nth: Map<string, number>, defs?: any[]): boolean {
  if (marked) return SETUP_MARK.test(text);
  const m = /^repro (\S+) step (\w+)(?: \[([^\]]*)\])?/.exec(text); if (!m) return false;
  defs ??= reproDefs ??= JSON.parse(readFileSync(join(import.meta.dirname, 'repros.json'), 'utf8'));
  const e = defs!.find(r => r.id === m[1]), f = /^(.*) (on|off) #(\d+)$/.exec(m[3] ?? '');
  if (!e) return false;
  if (e.loop && f) { const i = nth.get(f[3]) ?? 0; nth.set(f[3], i + 1); const s = e.loop.steps[i]; return !!s && s.cmd === m[2] && isSetup(s); }
  const s = (e.steps ?? []).find((x: any) => x.cmd === m[2] && (x.note ?? '') === (m[3] ?? ''));
  return !!s && isSetup(s);
}
export const reproSteps = (tag: string, secs: number) => {
  const f = join(QUEUE, `${tag}.log`); if (!existsSync(f)) return undefined;
  // the same events, in the same order, as runContext's repro-triggered windows
  return queueEvents(readFileSync(f, 'utf8'), tag, secs).events.map(e => e.text).filter(t => { const m = /^repro \S+ step (\w+)/.exec(t); return m ? m[1] !== 'wait' : /save_game|saving|test_env guard/.test(t); });
};

// whole run invalid: profiler cut short, noisy machine (machine-load.ts: other >= 10% of the machine or clock drop = not
// comparable, or other apps >= 10% of the game CPUs with other CPU >= 5%), crash or abort (no 'done; game alive' after the profile window)
export function runInvalid(mf: string, nRows: number, secs: number, q: { start: number | null; events: Ev[] }): string | null {
  const why: string[] = [];
  if (nRows < 0.8 * secs) why.push(`profiler cut short (${nRows} of ${secs} s)`);
  // others on game CPUs over the measured window only: the load and first minute (already excluded) carry the save load and
  // the Frida start-up, 16-20% for two samples, which alone flagged clean runs noisy (freeze-pan crashrep 2026-10-22)
  const m = summarize(mf), all = jsonl(mf).filter((r: any) => typeof r.og === 'number');
  const rows = q.start !== null && all.some((r: any) => r.t >= q.start! + 60000) ? all.filter((r: any) => r.t >= q.start! + 60000) : all;
  const og = rows.length ? rows.reduce((s: number, r: any) => s + r.og, 0) / rows.length : 0;
  const other = rows.length ? rows.reduce((s: number, r: any) => s + (r.other ?? 0), 0) / rows.length : 0; // og alone can be kernel/driver time (machine-load.ts OG_OTHER_MIN)
  if (!m.ok || (og >= 10 && other >= OG_OTHER_MIN)) why.push(`noisy machine (${m.ok ? `others ${og.toFixed(1)}% of game CPUs` : m.line.replace(/^.*NOT comparable: /, '').replace(/;.*$/, '')})`);
  if (q.start !== null && !q.events.some(e => /^done; game alive/.test(e.text))) why.push('crash or abort (no "done; game alive")');
  return why.length ? why.join('; ') : null; // every reason named
}

// excluded points by reason: n, ms, >= 300 / >= 1000, max, and seconds of run time dropped with them
export type ExclRow = { n: number; ms: number; n300: number; n1000: number; max: number; secs: number };
export function exclAdd(t: Map<string, ExclRow>, why: string, ms: number, secs = 0) {
  const r = t.get(why) ?? { n: 0, ms: 0, n300: 0, n1000: 0, max: 0, secs: 0 };
  if (ms) { r.n++; r.ms += ms; r.n300 += +(ms >= 300); r.n1000 += +(ms >= 1000); r.max = Math.max(r.max, ms); }
  r.secs += secs; t.set(why, r);
}
export function exclTable(t: Map<string, ExclRow>, unit = 'iterations') {
  const L = [`Excluded (${unit} >= ${SIM_MS} ms; repro-triggered = game cost of the harness's own commands, not natural play)`, '', `| Reason | n | ms | >= 300 | >= 1000 | max ms | min dropped |`, '|---|---|---|---|---|---|---|'];
  for (const why of [...EXCL, ...[...t.keys()].filter(k => !(EXCL as readonly string[]).includes(k))]) { const r = t.get(why); if (r) L.push(`| ${why} | ${r.n} | ${Math.round(r.ms)} | ${r.n300} | ${r.n1000} | ${Math.round(r.max)} | ${(r.secs / 60).toFixed(1)} |`); }
  return L.join('\n');
}

// fine offset (epoch - QPC): samples inside route-heavy iterations should show a route frame, samples inside route-free ones not
export function fineOffset(samples: { t: number; r: boolean }[], hitches: H[], coarse: number, span = 15_000, step = 10) {
  const iv = hitches.filter(h => h.wallMs >= 100).map(h => ({ a: h.t - h.wallMs, b: h.t, rf: (h.route ?? 0) / h.wallMs })).sort((x, y) => x.a - y.a);
  const find = (q: number) => { let lo = 0, hi = iv.length - 1; while (lo <= hi) { const m = (lo + hi) >> 1; if (iv[m].b < q) lo = m + 1; else if (iv[m].a > q) hi = m - 1; else return iv[m]; } return null; };
  let best = { off: coarse, score: -Infinity };
  for (let off = coarse - span; off <= coarse + span; off += step) {
    let s = 0;
    for (const x of samples) { const h = find(x.t - off); if (!h) continue; if (h.rf >= 0.7) s += x.r ? 1 : -1; else if (h.rf === 0) s += x.r ? -1 : 0.1; }
    if (s > best.score) best = { off, score: s };
  }
  return best;
}
// runs whose coarse offsets lie within BOOT_GAP_MS share a boot; a boot takes the median good fine fit, else each run keeps its coarse one
export function bootOffsets(runs: { coarse: number; fine?: { off: number; score: number } }[]) {
  const order = runs.map((r, i) => i).sort((a, b) => runs[a].coarse - runs[b].coarse);
  const out: { off: number; src: string }[] = runs.map(r => ({ off: r.coarse, src: 'start+lag' }));
  let g: number[] = [];
  const flush = () => {
    const good = g.map(i => runs[i].fine).filter(f => f && f.score >= FINE_MIN && Math.abs(f.off - runs[g[0]].coarse) < BOOT_GAP_MS).map(f => f!.off);
    if (good.length) for (const i of g) out[i] = { off: med(good), src: `boot fit (${good.length})` };
    g = [];
  };
  for (const i of order) { if (g.length && runs[i].coarse - runs[g[g.length - 1]].coarse > BOOT_GAP_MS) flush(); g.push(i); }
  flush();
  return out;
}

function main(minMs: number, since: string, topN: number, jsonOut?: string) {
  const sinceMs = Date.parse(`${since}T00:00:00+08:00`);
  const an = join(ROOT, 'game-copy', 'analysis'), symf = join(an, 'ghidra-user-symbols.tsv');
  const fns = existsSync(join(an, 'functions.csv')) ? loadFunctions(readFileSync(join(an, 'functions.csv'), 'utf8'), existsSync(symf) ? readFileSync(symf, 'utf8') : '') : [];
  const files = readdirSync(PROFILE).filter(f => /-\d+-hitches\.jsonl$/.test(f) && statSync(join(PROFILE, f)).mtimeMs >= sinceMs);
  const runs = files.map(hf => {
    const tag = hf.replace(/-\d+-hitches\.jsonl$/, ''), secs = Number(/-(\d+)-hitches/.exec(hf)![1]);
    const rows = csvRows(readFileSync(join(PROFILE, hf.replace('-hitches.jsonl', '-seconds.csv')), 'utf8')), hitches: H[] = jsonl(join(PROFILE, hf));
    const q = existsSync(join(QUEUE, `${tag}.log`)) ? queueEvents(readFileSync(join(QUEUE, `${tag}.log`), 'utf8'), tag, secs) : { start: null, events: [] };
    const sim = jsonl(join(PROFILE, `${tag}-simstack.jsonl`)).map((s: any) => ({ t: s.t as number, stack: s.stack as string[], r: (s.stack as string[]).some(f => ROUTE_FN.test(f)) }));
    const q0 = rows[0]?.t ?? 0, coarse = q.start !== null ? q.start + LAG_MS - q0 : NaN;
    const fine = sim.length && !isNaN(coarse) ? fineOffset(sim, hitches, coarse) : undefined;
    return { tag, id: tag, secs, rows, hitches, q, sim, q0, coarse, fine, world: (/-(bp|ip|sv)-/.exec(tag) ?? [])[1] ?? '?', off: NaN, offSrc: '' };
  }).filter(r => r.rows.length > 30 && !isNaN(r.coarse));
  bootOffsets(runs).forEach((o, i) => { runs[i].off = o.off; runs[i].offSrc = o.src; });
  const ctxs = new Map(runs.map(r => [r, runContext(r.tag, r.rows, r.secs, r.offSrc.startsWith('boot') ? r.off : NaN)]));
  // a tag reused for two lengths (verify-2026-10-16-*: 420 and 480 s) is shown as tag@secs; files stay keyed by the tag
  runs.forEach(r => { if (runs.filter(x => x.tag === r.tag).length > 1) r.id = `${r.tag}@${r.secs}`; });

  type Rec = { tag: string; world: string; h: H; rel: number; at: number; cls: Cls; step: string | null; events: string[]; native: string[][]; managed: string[][]; acts: string[]; dSims: number; excl: string | null };
  const recs: Rec[] = [], baseNative: string[][] = [], baseManaged: string[][] = [];
  const actAll = new Map<string, number>(); let actN = 0;
  for (const r of runs) {
    const act = jsonl(join(PROFILE, `${r.tag}-activity.jsonl`));
    for (const a of act) { actN++; for (const [, i] of a.sims) if (i) actAll.set(i, (actAll.get(i) ?? 0) + 1); }
    const man = jsonl(join(PROFILE, `${r.tag}-managed.jsonl`));
    const ma = man.length ? alignRun(r.tag, man, r.rows, r.hitches, r.secs) : null; // ma.times on the QPC clock
    const near = (q: number, pad: number) => r.hitches.some(h => h.wallMs >= 50 && q >= h.t - h.wallMs - pad && q <= h.t + pad);
    for (const s of r.sim) if (!near(s.t - r.off, 1500)) baseNative.push(s.stack);
    if (ma && ma.r >= 0.2) ma.times.forEach((t, j) => { if (!near(t, 1500)) baseManaged.push(man[j]); });
    for (const h of r.hitches.filter(h => h.wallMs >= minMs)) {
      const at = h.t + r.off, a0 = at - h.wallMs;
      const evs = r.q.events.filter(e => e.at >= a0 - 3000 && e.at <= at + 5000);
      const step = evs.find(e => isStep(e.text))?.text.replace(/^repro \S+ step (\w+).*/, '$1').slice(0, 40) ?? null;
      const rel = (h.t - r.q0) / 1000;
      const native = r.sim.filter(s => s.t - r.off >= h.t - h.wallMs && s.t - r.off <= h.t).map(s => s.stack);
      const managed = ma && ma.r >= 0.2 ? man.filter((_, j) => ma.times[j] >= h.t - h.wallMs - 100 && ma.times[j] <= h.t + 100) : [];
      const sinceStart = at - r.q.start!;
      const ai = act.reduce((b: number, a: any, i: number) => a.t <= sinceStart ? i : b, -1);
      const acts = ai >= 0 ? act[ai].sims.map((x: any) => x[1]).filter(Boolean) : [];
      const dSims = ai >= 1 ? act[ai].sims.length - act[ai - 1].sims.length : 0;
      const ctx = ctxs.get(r)!, ex = ctx.invalid ? 'run invalid' : exclusion({ a: h.t - h.wallMs, b: h.t, wallMs: h.wallMs, steps: h.steps, iter: h }, ctx)?.why ?? null;
      recs.push({ tag: r.id, world: r.world, h, rel, at, cls: classify(h, rel, step), step, events: evs.map(e => e.text.slice(0, 60)), native, managed, acts, dSims, excl: ex });
    }
  }

  // game-only = real (exclusion() null, run valid); the excluded ones are listed by reason
  const all = recs.reduce((a, x) => a + x.h.wallMs, 0), game = recs.filter(x => !x.excl), gameMs = game.reduce((a, x) => a + x.h.wallMs, 0);
  const ext = new Map<string, ExclRow>(); recs.forEach(x => x.excl && exclAdd(ext, x.excl, x.h.wallMs));
  console.log(`${exclTable(ext, `iterations >= ${minMs} ms; every one counted`).replace(` >= ${SIM_MS} ms`, '')}\n\ninvalid runs: ${runs.filter(r => ctxs.get(r)!.invalid).map(r => `${r.id} (${ctxs.get(r)!.invalid})`).join(', ') || 'none'}\n`);
  console.log(`## Runs (${runs.length}, profiler files since ${since}; hitch >= ${minMs} ms)\n| Run | World | Iterations >= ${minMs} | ms | Offset source | simstack samples | fine score |\n|---|---|---|---|---|---|---|`);
  for (const r of runs) { const x = recs.filter(y => y.tag === r.id); console.log(`| ${r.id} | ${r.world} | ${x.length} | ${Math.round(x.reduce((a, y) => a + y.h.wallMs, 0))} | ${r.offSrc} | ${r.sim.length} | ${r.fine ? r.fine.score.toFixed(1) : '-'} |`); }

  const buckets = [[300, 500], [500, 1000], [1000, 2000], [2000, Infinity]];
  console.log(`\n## Size buckets (all ${recs.length} iterations, ${Math.round(all)} ms; game-only = real, excluded points out)\n| Wall ms | n | ms | n game-only | ms game-only |\n|---|---|---|---|---|`);
  for (const [a, b] of buckets) { const s = recs.filter(x => x.h.wallMs >= a && x.h.wallMs < b), g = s.filter(x => !x.excl); console.log(`| ${a}-${b === Infinity ? '' : b} | ${s.length} | ${Math.round(s.reduce((p, x) => p + x.h.wallMs, 0))} | ${g.length} | ${Math.round(g.reduce((p, x) => p + x.h.wallMs, 0))} |`); }
  const STAGES = ['threaded', 'taskObjects', 'locomotion', 'animPose', 'animSacs', 'script', 'gcMark', 'fin', 'oneShot', 'publish'];
  const unacc = game.reduce((a, x) => a + x.h.wallMs - STAGES.reduce((s, k) => s + (x.h[k] ?? 0), 0), 0);
  console.log(`\n## Stage share of game-only >= ${minMs} ms time (${game.length} iterations, ${Math.round(gameMs)} ms)\n| Stage | Share |\n|---|---|`);
  for (const k of [...STAGES, 'route']) console.log(`| ${k}${k === 'route' ? ' (nested in threaded/taskObjects)' : ''} | ${pct(game.reduce((a, x) => a + (x.h[k] ?? 0), 0), gameMs)} |`);
  console.log(`| not in a timed stage | ${pct(unacc, gameMs)} |`);

  const classes = [...new Set(recs.map(x => x.cls))];
  const runMin = runs.reduce((a, r) => a + (r.rows.at(-1)!.t - r.q0) / 60000, 0);
  console.log(`\n## Causes (share of all >= ${minMs} ms time; per hour over ${runMin.toFixed(0)} profiled minutes)\n| Class | n | ms | share all | share game-only | median ms | max ms | BP / IP n | per hour | with simstack | with managed |\n|---|---|---|---|---|---|---|---|---|---|---|`);
  for (const c of classes.sort((a, b) => recs.filter(x => x.cls === b).reduce((p, x) => p + x.h.wallMs, 0) - recs.filter(x => x.cls === a).reduce((p, x) => p + x.h.wallMs, 0))) {
    const s = recs.filter(x => x.cls === c), ms = s.reduce((p, x) => p + x.h.wallMs, 0);
    console.log(`| ${c} | ${s.length} | ${Math.round(ms)} | ${pct(ms, all)} | ${c === 'harness step' ? '-' : pct(ms, gameMs)} | ${Math.round(med(s.map(x => x.h.wallMs)))} | ${Math.round(Math.max(...s.map(x => x.h.wallMs)))} | ${s.filter(x => x.world === 'bp').length} / ${s.filter(x => x.world === 'ip').length} | ${(60 * s.length / runMin).toFixed(1)} | ${s.filter(x => x.native.length).length} | ${s.filter(x => x.managed.length).length} |`);
  }

  const name = (e: number) => fns.find(f => f.entry === e)?.name ?? `0x${e.toString(16)}`;
  const nb = split(baseNative, fns, 24);
  for (const c of classes) {
    const s = recs.filter(x => x.cls === c);
    console.log(`\n### ${c}`);
    const gaps = runs.flatMap(r => { const t = s.filter(x => x.tag === r.id).map(x => x.rel).sort((a, b) => a - b); return t.slice(1).map((v, i) => v - t[i]).filter(g => g > 5); });
    console.log(`stage means ms: ${['threaded', 'route', 'taskObjects', 'locomotion', 'gcMark'].map(k => `${k} ${Math.round(s.reduce((a, x) => a + (x.h[k] ?? 0), 0) / s.length)}`).join(', ')}; wall ${Math.round(s.reduce((a, x) => a + x.h.wallMs, 0) / s.length)}; ` +
      `GC mark in iteration ${pct(s.filter(x => x.h.gcMarks > 0).length, s.length)}; median gap between same-class hitches in a run ${gaps.length ? Math.round(med(gaps)) + ' s' : '-'} (${gaps.length} gaps)`);
    const steps = new Map<string, number>(); s.forEach(x => x.step && steps.set(x.step, (steps.get(x.step) ?? 0) + 1));
    if (steps.size) console.log(`harness steps: ${[...steps].map(([k, v]) => `${k} ${v}`).join(', ')}`);
    const nat = s.flatMap(x => x.native);
    if (nat.length) {
      const sp = split(nat, fns, 24);
      console.log(`native samples ${nat.length} (baseline ${baseNative.length}); top inclusive TS3W functions:\n| Function | in class | baseline | lift |\n|---|---|---|---|`);
      for (const [e, v] of [...sp.rows].sort((a, b) => b[1].incl - a[1].incl).slice(0, topN)) { const bi = (nb.rows.get(e)?.incl ?? 0) / Math.max(1, baseNative.length), ci = v.incl / nat.length; console.log(`| ${name(e)} | ${pct(v.incl, nat.length)} | ${pct(bi * 1000, 1000)} | ${bi ? (ci / bi).toFixed(1) : 'new'} |`); }
      const leafMod = new Map<string, number>(); nat.forEach(st => { const m = (/ ([\w.-]+?)(?:!|\+)/.exec(st[0] ?? '') ?? [])[1] ?? '?'; leafMod.set(m, (leafMod.get(m) ?? 0) + 1); });
      console.log(`leaf module: ${[...leafMod].sort((a, b) => b[1] - a[1]).map(([k, v]) => `${k} ${pct(v, nat.length)}`).join(', ')}`);
    }
    const man = s.flatMap(x => x.managed);
    if (man.length) {
      const has = (re: RegExp) => s.filter(x => x.managed.some(m => m.some(f => re.test(f)))).length;
      console.log(`hitches with managed samples ${s.filter(x => x.managed.length).length}; with a sample on: NRaas ${has(/^NRaas\./)}, NRaas Overwatch CleanupInventory ${has(/CleanupInventory/)}, our hooks/Ts3Fixes ${has(/Hook\.|^Ts3Fixes\./)}, Ts3Fixes SafeSpot ${has(/Ts3Fixes\.Fixes\.SafeSpot/)}, a route plan leaf ${s.filter(x => x.managed.some(m => /^ScriptCore\.Route\.Route_/.test(m[0] ?? ''))).length}`);
      const cnt = (xs: string[][], key: (m: string[]) => string) => { const m = new Map<string, number>(); xs.forEach(x => m.set(key(x), (m.get(key(x)) ?? 0) + 1)); return m; };
      for (const [title, key] of [['category', category], ['task root', rootOf], ['leaf', (m: string[]) => m[0] ?? '(none)']] as const) {
        const a = cnt(man, key), b = cnt(baseManaged, key);
        console.log(`managed ${title} (${man.length} samples, baseline ${baseManaged.length}):\n| Key | in class | baseline | lift |\n|---|---|---|---|`);
        for (const [k, v] of [...a].sort((x, y) => y[1] - x[1]).slice(0, Math.min(topN, 12))) { const bb = (b.get(k) ?? 0) / Math.max(1, baseManaged.length); console.log(`| ${k} | ${pct(v, man.length)} | ${pct(b.get(k) ?? 0, baseManaged.length)} | ${bb ? ((v / man.length) / bb).toFixed(1) : 'new'} |`); }
      }
    }
    const ia = new Map<string, number>(); let iaN = 0; s.forEach(x => { if (x.acts.length || x.dSims) iaN++; x.acts.forEach(i => ia.set(i, (ia.get(i) ?? 0) + 1)); });
    if (iaN) {
      const lifts = [...ia].filter(([, v]) => v >= 3).map(([k, v]) => [k, (v / iaN) / ((actAll.get(k) ?? 0) / actN), v] as const).sort((a, b) => b[1] - a[1]).slice(0, 8);
      console.log(`activity (last 30 s snapshot before the hitch, ${iaN} hitches): Sims count changed in ${s.filter(x => x.dSims).length}; interactions with the highest lift: ${lifts.map(([k, l, v]) => `${k} x${l.toFixed(1)} (${v})`).join('; ')}`);
    }
    const evc = new Map<string, number>(); s.forEach(x => x.events.forEach(e => { const k = e.replace(/[\d.:]+/g, '#').slice(0, 45); evc.set(k, (evc.get(k) ?? 0) + 1); }));
    if (evc.size) console.log(`queue log lines within -3..+5 s: ${[...evc].sort((a, b) => b[1] - a[1]).slice(0, 6).map(([k, v]) => `"${k}" ${v}`).join('; ')}`);
  }
  console.log(`\n## Worst ${topN} game-only iterations\n| Run | rel s | wall | threaded | route | taskObjects | locomotion | class | lead bucket / longest wait | plans (n/ms) | slowest script runs (ms) | native / managed samples |\n|---|---|---|---|---|---|---|---|---|---|---|---|`);
  for (const x of [...game].sort((a, b) => b.h.wallMs - a.h.wallMs).slice(0, topN)) {
    const c = cause(x.h, x.rel, x.step);
    console.log(`| ${x.tag} | ${Math.round(x.rel)} | ${Math.round(x.h.wallMs)} | ${Math.round(x.h.threaded ?? 0)} | ${Math.round(x.h.route ?? 0)} | ${Math.round(x.h.taskObjects ?? 0)} | ${Math.round(x.h.locomotion ?? 0)} | ${x.cls} | ${c.lead}${c.wait ? ` / ${c.wait}` : ''}${c.holder ? ` / ${c.holder}` : ''} | ${c.plans} | ${c.tasks} | ${x.native.length} / ${x.managed.length} |`);
  }
  // --ab runs: the switched patch on vs off per class (slices from ab.json, s since the profile-world start; warm-up slice left out)
  // first 60 s left out (load class); runs grouped by the switched patch names
  const abRows = new Map<string, [number, number, number, number]>(); // patch | class -> on n, on ms, off n, off ms
  const abSec = new Map<string, [number, number]>();
  for (const r of runs) {
    const f = join(PROFILE, `${r.tag}-ab.json`);
    if (!existsSync(f)) continue;
    const ab = JSON.parse(readFileSync(f, 'utf8')), sl: { from: number; on: boolean; warmup?: boolean }[] = ab.slices ?? [], p = (ab.names ?? []).join(',');
    const end = (r.rows.at(-1)!.t + r.off - r.q.start!) / 1000, sec = abSec.get(p) ?? [0, 0], skip = (r.q0 + r.off - r.q.start!) / 1000 + 60;
    sl.forEach((s, i) => { if (!s.warmup) sec[s.on ? 0 : 1] += Math.max(0, Math.min(sl[i + 1]?.from ?? end, end) - Math.max(s.from, skip)); });
    abSec.set(p, sec);
    for (const x of recs.filter(y => y.tag === r.id && !y.excl)) {
      const s = (x.at - r.q.start!) / 1000, k = sl.findLastIndex(y => y.from <= s);
      if (k < 0 || sl[k].warmup) continue;
      const key = `${p} | ${x.cls}`, v = abRows.get(key) ?? [0, 0, 0, 0], o = sl[k].on ? 0 : 2; v[o]++; v[o + 1] += x.h.wallMs; abRows.set(key, v);
    }
  }
  if (abRows.size) {
    console.log(`\n## A/B runs: switched patch on vs off (warm-up slices and the first 60 s left out)\n| Patch | Class | on n | on ms/h | off n | off ms/h |\n|---|---|---|---|---|---|`);
    for (const [k, v] of [...abRows].sort()) { const [p, c] = k.split(' | '), sec = abSec.get(p)!; console.log(`| ${p} (${(sec[0] / 60).toFixed(0)}/${(sec[1] / 60).toFixed(0)} min) | ${c} | ${v[0]} | ${Math.round(3600 * v[1] / sec[0])} | ${v[2]} | ${Math.round(3600 * v[3] / sec[1])} |`); }
  }
  // roots: task root per managed sample, taken before the 12-frame cut drops it (hitch-ab.ts --causes)
  if (jsonOut) writeFileSync(jsonOut, JSON.stringify(recs.map(x => ({ ...x, native: x.native.map(s => s.slice(0, 16)), managed: x.managed.map(m => m.slice(0, 12)), roots: x.managed.map(rootOf) })), null, 1));
}

function selfCheck() {
  const assert = (c: unknown, m: string) => { if (!c) throw new Error(`self-check failed: ${m}`); };
  const h = (o: Record<string, number>) => ({ t: 0, steps: 10, ...o }) as H;
  assert(classify(h({ wallMs: 400, route: 300, threaded: 350 }), 100, null) === 'route search', 'route');
  assert(classify(h({ wallMs: 400, taskObjects: 380 }), 100, null) === 'TaskObjects burst', 'task');
  assert(classify(h({ wallMs: 4000, taskObjects: 3900 }), 100, 'save_game') === 'harness step', 'step');
  assert(classify(h({ wallMs: 12000, taskObjects: 11900, steps: 0 }), 100, null) === 'harness step', 'steps 0');
  assert(classify(h({ wallMs: 400, threaded: 380, route: 10 }), 30, null) === 'first minute after load', 'load');
  assert(classify(h({ wallMs: 400, threaded: 380, route: 10 }), 100, null) === 'ThreadedObjects, no route', 'thr');
  assert(classify(h({ wallMs: 400, threaded: 300, route: 120 }), 100, null) === 'mixed route + script', 'mixed');
  assert(classify(h({ wallMs: 400, threaded: 150, locomotion: 200 }), 100, null) === 'locomotion', 'loco');
  const c = cause(h({ wallMs: 400, threaded: 380, route: 300, rtLock: 40, rtMaxMs: 30, rtMaxKey: '0x00760bff', byEntry: { Route_Plan: { n: 2, fail: 1, ms: 290.4 } } } as any), 100, null);
  assert(c.cls === 'route search' && c.lead === 'route' && c.waitMs === 40 && c.wait === 'gate 10 goal step (sync plan) 30 ms' && c.plans === 'Plan 2/290' && c.tasks === '' && c.holder === '', `cause ${JSON.stringify(c)}`);
  // lock holders (docs/165 §8): the sim wait first, else the main thread's
  const ch = cause(h({ wallMs: 300, threaded: 280, holders: [{ who: 'main', ms: 90, lock: '0x1a2b3c40', by: 'thread', tid: 7, site: '0x00000000' },
    { who: 'sim', ms: 41.6, lock: '0x0badf00d', by: 'route worker', tid: 42, site: '0x0062ef7a' }] } as any), 100, null);
  assert(ch.holder === 'sim waited 42 ms on lock 0x0badf00d held by route worker 42 from 0x0062ef7a', `holder ${ch.holder}`);
  assert(holderText(h({ wallMs: 100, holders: [{ who: 'main', ms: 90, lock: '0x1', by: 'thread', tid: 7, site: '0x00000000' }] } as any)) ===
    'main waited 90 ms on lock 0x1 held by thread 7 from unknown site', 'holder main');
  // task attribution (docs/166): top 3 runs, yield point's innermost frame
  const ct = cause(h({ wallMs: 600, taskObjects: 580, tasks: [{ stage: 'taskObjects', name: 'FunctionTask>NRaas.X.Run', ms: 410.4, to: 'A.DoLoop < B.Run' },
    { stage: 'taskObjects', name: 'Sims3.Gameplay.Autonomy.AutonomyManager', ms: 90 }, { stage: 'oneShot', name: 'Y', ms: 20 }, { stage: 'threaded', name: 'Z', ms: 10 }] } as any), 100, null);
  assert(ct.cls === 'TaskObjects burst' && ct.topTask === 'FunctionTask>NRaas.X.Run' && ct.tasks === 'FunctionTask>NRaas.X.Run 410 (yield A.DoLoop), Sims3.Gameplay.Autonomy.AutonomyManager 90, Y 20', `task cause ${JSON.stringify(ct)}`);
  const q = queueEvents('2026-10-18T03:17:51.346Z $ node profile-world.ts --tag x-ip --minutes 10\n03:18:52 managed sampler 180 s\n03:20:00 repro r step save_game: {}\n\n2026-10-18T04:00:00.000Z $ node other.ts\n04:00:01 not ours\n', 'x-ip');
  assert(q.start === Date.parse('2026-10-18T03:17:51.346Z') && q.events.length === 2 && isStep(q.events[1].text) && !isStep(q.events[0].text), 'queueEvents');
  // fine fit: samples taken 5 s (epoch) after QPC 10000.. ; route hitch at QPC 10000-10400, route-free one at 20000-20400
  const hs = [{ t: 10400, wallMs: 400, route: 390 }, { t: 20400, wallMs: 400, route: 0 }] as H[];
  const off = 1_000_000;
  const smp = [10100, 10200, 10300].map(t => ({ t: t + off, r: true })).concat([20100, 20200, 20300].map(t => ({ t: t + off, r: false })));
  const f = fineOffset(smp, hs, off + 2000, 3000, 10);
  assert(Math.abs(f.off - off) <= 100 && f.score >= 3, `fineOffset ${f.off - off} ${f.score}`);
  const b = bootOffsets([{ coarse: 5000 }, { coarse: 7000, fine: { off: 6100, score: 30 } }, { coarse: 900_000 }]);
  assert(b[0].off === 6100 && b[1].off === 6100 && b[2].off === 900_000 && b[2].src === 'start+lag', `bootOffsets ${JSON.stringify(b)}`);
  // repro window labels: the arm from a loop / hand-written ABBA note; plain steps keep the bare command
  assert(stepLabel('repro bp-x step reflect_call [fire off #3]: {"ok":true}') === 'reflect_call off' && stepLabel('repro bp-x step patches [arm on #12]: {}') === 'patches on'
    && stepLabel('repro bp-x step save_game [a scratch save name: never the loaded Baseline save]: {}') === 'save_game' && stepLabel('repro bp-x step cas_open: {}') === 'cas_open', 'stepLabel');
  // setup steps (docs/168 §7): the log mark when the run has one; else repros.json, loop steps by position in their firing
  const defs = [{ id: 'L', loop: { steps: [{ cmd: 'patches', note: 'arm' }, { cmd: 'strand_many', note: 'state' }, { cmd: 'build_mode', note: 'x', setup: true }, { cmd: 'build_mode', note: 'x' }] } },
    { id: 'P', steps: [{ cmd: 'patches', note: 'arm off #1' }, { cmd: 'reflect_call', note: 'fire off #1' }] }];
  const nth = new Map<string, number>(), ss = (t: string, marked = false) => stepSetup(t, marked, nth, defs);
  assert([ss('repro L step patches [arm off #1]: {}'), ss('repro L step strand_many [state off #1]: {}'), ss('repro L step build_mode [x off #1]: {}'), ss('repro L step build_mode [x off #1]: {}'),
    ss('repro L step patches [arm on #2]: {}'), ss('repro P step patches [arm off #1]: {}'), ss('repro P step reflect_call [fire off #1]: {}')].join() === 'true,true,true,false,true,true,false', 'stepSetup by repros.json');
  assert(ss('repro L step patches [arm off #3] (setup): {}', true) && !ss('repro L step build_mode [x off #3]: {}', true) && SETUP_MARK.test('repro L step strand_many (setup): {}'), 'stepSetup by mark');
  // exclusion: one fixture per reason. Rows every 1 s from QPC 1000 for 600 s; harness windows as runContext builds them
  const rows = Array.from({ length: 600 }, (_, i) => ({ t: 1000 + i * 1000 }));
  const wins: Win[] = [...baseWins(rows), { a: 200_000, b: 215_000, why: 'repro-triggered', what: 'sim_route_to' },
    { a: 300_000, b: 481_000, why: 'sampler', what: 'native' }, { a: 100_000, b: 103_500, why: 'probe call', what: 'upkeep round' }];
  const ctx = { wins, foreground: false }, it = (t: number, wallMs: number, steps = 3) => exclusion({ a: t - wallMs, b: t, wallMs, steps }, ctx)?.why ?? 'real';
  assert(it(30_000, 400) === 'first minute' && it(599_500, 400) === 'profiler edge', 'first minute / edge');
  // docs/170: a window with no load before it (session.json sinceLoad > 0) keeps its first minute, minus a 15 s start edge
  const rw = { wins: [...baseWins(rows, false)], foreground: false }, rit = (t: number) => exclusion({ a: t - 400, b: t, wallMs: 400, steps: 3 }, rw)?.why ?? 'real';
  assert(rit(10_000) === 'profiler edge' && rit(30_000) === 'real' && rit(599_500) === 'profiler edge', 'reused-session start edge');
  const sd = join(tmpdir(), `wh-session-${process.pid}`);
  writeFileSync(`${sd}-a-session.json`, JSON.stringify({ sinceLoad: 2 })); writeFileSync(`${sd}-b-session.json`, JSON.stringify({ sinceLoad: 0 }));
  assert(!afterLoad(`wh-session-${process.pid}-a`, tmpdir()) && afterLoad(`wh-session-${process.pid}-b`, tmpdir()) && afterLoad('no-such-tag', tmpdir()), 'afterLoad');
  rmSync(`${sd}-a-session.json`); rmSync(`${sd}-b-session.json`);
  assert(it(205_000, 2000) === 'repro-triggered' && it(350_000, 300) === 'sampler' && it(102_000, 400) === 'probe call', 'windows');
  assert(it(150_000, 400) === 'real' && it(150_000, 1200, 0) === 'repro-triggered', 'real / 0-step save');
  // docs/178 T2, rerun-0333-2026-10-23-sv-crowd-cause-0333: iteration n 5438 (2085 ms, rtLock 2069 ms on steering 0x85FE10 behind
  // a foreign route job; gap 129700985..129703069.76) under the 110 ms keep_alive call that ends it (calls.jsonl at QPC
  // 129702971..129703081 by the frames.csv offset; window padded 200 ms) stays real, as iteration, row and publish gap
  const sv = { t: 129703071.59, wallMs: 2085.49, steps: 10, locomotion: 2069.53, rtLock: 2069.05, rtMaxKey: '0x0085fe10' };
  const svWin: Win = { a: 129702971 - 200, b: 129703081 + 200, why: 'probe call', what: 'keep_alive', ms: 110 }, svCtx = { wins: [svWin], foreground: false };
  assert(exclusion({ a: sv.t - sv.wallMs, b: sv.t, wallMs: sv.wallMs, steps: sv.steps, iter: sv }, svCtx) === null
    && exclusion({ a: sv.t - 1000, b: sv.t, iter: sv }, svCtx) === null
    && exclusion({ a: 129700985, b: 129703069.76, gap: { longestIterMs: sv.wallMs, iters: 1 }, iter: sv }, svCtx) === null, 'T2 lock-led freeze under a probe call stays real');
  // without the route wait the same 110 ms call still does not explain a 2 s gap (round trip < half)
  assert(exclusion({ a: 129700985, b: 129703069.76, gap: { longestIterMs: sv.wallMs, iters: 1 }, iter: { t: sv.t, wallMs: sv.wallMs } }, svCtx) === null, 'T2 short call');
  // a true probe-call gap: the call's round trip covers most of it (no route wait) -> excluded; a short call under a long
  // non-route hitch -> real; a command that leaves work behind (spawn_sims) -> excluded; same 58 ms call with no hitch -> row excluded
  const pc = (ms: number, what: string, wallMs: number, extra = {}) => exclusion({ a: 160_000 - wallMs, b: 160_000, wallMs, steps: 3, iter: { t: 160_000, wallMs, threaded: wallMs, ...extra } },
    { wins: [{ a: 159_900 - ms, b: 159_950, why: 'probe call', what, ms }], foreground: false })?.why ?? 'real';
  assert(pc(350, 'keep_alive', 400) === 'probe call' && pc(26, 'keep_alive', 400) === 'real' && pc(26, 'spawn_sims', 400) === 'probe call'
    && pc(350, 'keep_alive', 400, { rtJob: 300 }) === 'real', 'T2 probe call explains only its own gap');
  assert(exclusion({ a: sv.t - 1000, b: sv.t }, svCtx)?.why === 'probe call' && callExplains({ what: 'upkeep round' }, 5000), 'T2 rows without a hitch / old upkeep guesses');
  assert(rowLongest([{ t: 10 }, { t: 20 }], [{ t: 12, wallMs: 60 }, { t: 15, wallMs: 90 }, { t: 30, wallMs: 500 }]).map(h => h?.wallMs).join() === ',90', 'rowLongest');
  // repro-triggered wins over sampler when both overlap
  assert(exclusion({ a: 299_900, b: 300_400 }, { wins: [...wins, { a: 300_200, b: 300_300, why: 'repro-triggered', what: 'x' }], foreground: false })?.why === 'repro-triggered', 'priority');
  const gap = (longestIterMs: number, iters: number, fg = false) => exclusion({ a: 150_000, b: 150_200, gap: { longestIterMs, iters } }, { wins, foreground: fg })?.why ?? 'real';
  assert(gap(10, 0) === 'render-paced' && gap(10, 3) === 'render-paced' && gap(180, 1) === 'real', 'render-paced unfocused');
  assert(gap(10, 3, true) === 'real' && gap(10, 0, true) === 'render-paced', 'ForegroundRender keeps a sim-side gap');
  // whole run invalid: cut short, noisy (others on the game CPUs, other CPU, clock drop), crash/abort, valid
  const tmp = join(tmpdir(), `wh-selfcheck-${process.pid}.jsonl`), mrow = (o: object) => JSON.stringify({ t: 0, other: 2, ts3w: 13, top: [], perf: 180, og: 4, ...o });
  const qok = { start: 0, events: [{ at: 1, text: 'done; game alive' }] }, inv = (lines: object[], n = 600, q: any = qok) => { writeFileSync(tmp, lines.map(mrow).join('\n') + '\n'); return runInvalid(tmp, n, 600, q); };
  assert(inv([{}], 400)?.startsWith('profiler cut short') === true, 'cut');
  assert(inv([{ og: 18.8 }]) === null, 'og alone with other CPU 2% (kernel/driver time) is valid');
  assert(inv([{ og: 18.8, other: 6 }])?.startsWith('noisy machine (others 18.8%') === true && inv([{ other: 12 }])?.startsWith('noisy machine') === true && inv([{}, { perf: 100 }, { perf: 100 }])?.startsWith('noisy machine') === true && inv([{}, { perf: 100 }]) === null, 'noisy (two clock dips; one is ignored)');
  assert(inv([{}], 600, { start: 0, events: [] })?.startsWith('crash') === true && inv([{}]) === null, 'crash / valid');
  rmSync(tmp);
  const et = new Map<string, ExclRow>(); exclAdd(et, 'sampler', 400, 180); exclAdd(et, 'sampler', 1200); exclAdd(et, 'render-paced', 60);
  assert(/\| sampler \| 2 \| 1600 \| 2 \| 1 \| 1200 \| 3\.0 \|/.test(exclTable(et)) && exclTable(et).indexOf('sampler') < exclTable(et).indexOf('render-paced'), 'exclTable');
  console.log('self-check ok');
}

const isMain = resolve(process.argv[1] ?? '') === fileURLToPath(import.meta.url);
const args = isMain ? process.argv.slice(2) : [];
const opt = (k: string, d: string) => { const i = args.indexOf(k); return i < 0 ? d : args[i + 1]; };
if (!isMain) { /* imported */ }
else if (args.includes('--self-check')) selfCheck();
else main(Number(opt('--min', '300')), opt('--since', '2026-10-16'), Number(opt('--top', '25')), args.includes('--json') ? opt('--json', '') : undefined);
