// Repro loops with a fixed-count stop rule (docs/168 §6): a repros.json entry may carry
//   loop: { steps: [...], arms: ["off","on","on","off"], until: {perArm: N} | {events: N}, maxMinutes? }
// beside (or instead of) its explicit steps. profile-world.ts runs the plain steps first, then the loop body once per arm, the
// arms list over and over (one pass of the list = one ABBA cycle), and ends the profile window early once the rule holds.
// - In loop step args the string "$on" becomes true on an "on" firing and false on an "off" one (e.g. a patches switch).
// - Every loop step's note becomes "<note or cmd> <arm> #<firing>" (e.g. "arm off #1", "fire off #1"), the form the
//   hand-written ABBA repros use; the hitch reports label repro windows by that arm (worst-hitches.ts runContext).
// - Events: a step with count: {counter: name} adds the rise of that ts3fixes counter (agent `patches`, read just before the
//   step and after its waitS); count: {result: field} adds that numeric field of the step's answer (a list field: its length). A body with no counted
//   step counts 1 event per firing.
// - The rule is checked only at the end of a whole arms cycle (arms stay balanced) and only against the fixed N written in
//   the repro before the run: never against a p-value or interval (no optional stopping; docs/168 §6).
// - maxMinutes (default: the profile's --minutes) caps the loop; reaching it ends the window too, with "rule not met".
// - until.minFirings (docs/168 §7): the rule also needs >= that many firings of every arm in loop.arms, so a counted rule
//   (instances per firing) cannot end an A/B at 2-4 firings per arm. A queue job overrides until / maxMinutes for its run with
//   run.profile.loopRule {until?, maxMinutes?} (profile-world --loop-rule): A/B queues long, correctness queues on the repro's own.
// Setup steps (docs/168 §7): a step with setup: true (default isSetup(): arm switches patches / native_set, strand_*,
//   spawn_sims, set_speed, unless the note starts with "fire") prepares a firing; its cost is not the fix's. profile-world logs
//   it as "repro <id> step <cmd> [note] (setup): ..." and runStep logs the end of its wait; hitch-ab.ts --scenario and
//   freeze-report.ts --repro-causes move the step start .. wait end out of the arm into "<arm> setup" rows. Mark every loop
//   step explicitly in repros.json when the default is wrong (build_mode on, crowd placement and returns: true; a churn step
//   that is the workload: false).
// Adaptive waits (docs/170 §3), per step, plain or loop: instead of a fixed waitS a step may carry
//   waitUntil: <cond> | [<cond>, ...] (all must hold) | {on: ..., off: ...} (picked by the arm in the step note, "fire off #3")
//   <cond> = {counter: name | [names], rises?: n, of?: name, slack?: n}  ts3fixes counters (agent `patches`, summed when a list)
//            rose >= n (default 1) since just before the step; with `of`: also rose >= rise(of) - slack and rise(of) > 0
//            (a sliced pass done: run caught up with queued)
//          | {quiet: ms, iterMs?: 100}  no sim iteration >= iterMs for ms: back-to-back agent `ping`s (the mailbox answers once per
//            sim tick, so a ping's round trip spans the iteration in progress); counted from the step's return, so set minS
//            past any alarm delay (1 sim-minute ~ 1 s at normal [inferred])
//          | {taskDone: name}  the stutter profiler's task records (io.taskDone; not wired in profile-world yet)
//          | {simMinutes: n}  agent `time` minutes_total rose >= n since just before the step (a wall-clock waitS at a fast
//            speed overshoots: 45 s at triple ran 475 sim-minutes, not ~35, on 2026-10-25)
//   minS (floor, default 0), maxS (cap, required). The step ends at the first poll with every cond met and minS passed
//   ("signal"), or at maxS ("cap": the effect may run into the next firing; hitch-ab.ts --scenario lists capped firings).
//   between: "normal" | "fast": the step's wait (fixed or adaptive) runs at ultra, then this speed is set back; only for steps
//   whose effect does not depend on wall time (setup between firings), never a firing.
//   Log line per adaptive step: "firing #n done after X s (signal|cap) [note]" (no arm note: "step <cmd> done after ...").
//   until: {cmd, args?, all: [{path, equals | atLeast | atMost}], everyS?: 5, maxS?: 300} (repro.ts semantics, also in
//   profile-world since 2026-10-25): after the step's wait, poll cmd until every path of its result holds; not met in maxS =
//   the step throws (fail fast). Before that fix profile-world ignored it: mem-suspects-verify's async loops (catalog_loop,
//   buy_sell_loop) and load_save reloads ran on into the next step (bp-memsus-reload: 'time' sent into a loading game).
//   runStep() is the step-level API: profile-world's plain steps can call it in place of `step(s); sleep(waitS)`.
//   node tools/harness/repro-loop.ts     self-check (fixtures, fake agent and clock; no game; plus every waitUntil in repros.json)
import assert from 'node:assert/strict';

export type Arm = 'on' | 'off';
export type Until = ({ perArm: number } | { events: number }) & { minFirings?: number };
export type WaitCond = { counter: string | string[]; rises?: number; of?: string; slack?: number } | { quiet: number; iterMs?: number } | { taskDone: string } | { simMinutes: number };
type Conds = WaitCond | WaitCond[];
export type WaitUntil = Conds | { on: Conds; off: Conds };
export type LoopStep = {
  cmd: string; args?: unknown; waitS?: number; note?: string; optional?: boolean; count?: { counter: string } | { result: string }; tally?: string;
  waitUntil?: WaitUntil; minS?: number; maxS?: number; between?: 'normal' | 'fast'; setup?: boolean; until?: Until1;
};
export type Until1 = { cmd: string; args?: unknown; all: { path: string; equals?: unknown; atLeast?: number; atMost?: number }[]; everyS?: number; maxS?: number };
// one `until` (see top): resolves with the last answer once met, null when the window ended first; throws when not met in maxS
export async function untilMet(u: Until1, io: Pick<StepIO, 'ag' | 'sleep' | 'now' | 'halted'>): Promise<any> {
  const t0 = io.now(), get = (o: any, path: string) => path.split('.').reduce((x, k) => x?.[k], o);
  const ok = (res: any) => res?.ok && u.all.every(c => { const v = get(res.result, c.path);
    return c.equals !== undefined ? v === c.equals : c.atLeast !== undefined ? v >= c.atLeast : c.atMost !== undefined ? v <= c.atMost : !!v; });
  for (;;) {
    const last = await io.ag(u.cmd, u.args ?? {}).catch((e: Error) => ({ ok: false, error: e.message }));
    if (ok(last)) return last;
    if (io.halted()) return null;
    if (io.now() - t0 > (u.maxS ?? 300) * 1000) throw new Error(`${u.cmd} until ${JSON.stringify(u.all)} not met in ${u.maxS ?? 300} s: ${JSON.stringify(last?.result ?? last?.error).slice(0, 200)}`);
    await io.sleep((u.everyS ?? 5) * 1000);
  }
}
export const isSetup = (s: { cmd: string; note?: string; setup?: boolean }) =>
  s.setup ?? (/^(patches|native_set|strand_\w+|spawn_sims|set_speed)$/.test(s.cmd) && !/^fire\b/.test(s.note ?? ''));
export type Loop = { steps: LoopStep[]; arms: Arm[]; until: Until; maxMinutes?: number };
export type LoopResult = { why: 'rule' | 'cap' | 'window'; events: Record<Arm, number>; total: number; firings: number; cycles: number; ms: number; capped?: number };
export const QUIET_ITER_MS = 100, POLL_MS = 1000, PING_GAP_MS = 50;

const isArmed = (w: WaitUntil): w is { on: Conds; off: Conds } => !Array.isArray(w) && 'on' in w && 'off' in w;
const condsOf = (w: WaitUntil | undefined, arm?: string): WaitCond[] => {
  if (!w) return [];
  if (isArmed(w)) { if (arm !== 'on' && arm !== 'off') throw new Error('waitUntil {on, off} needs an arm in the step note ("fire off #3")'); return [w[arm]].flat(); }
  return [w].flat();
};
export function checkStep(s: any, inLoop = false): LoopStep {
  const w = s?.waitUntil;
  if (w === undefined) return s;
  const where = `step ${s.cmd}${s.note ? ` [${s.note}]` : ''}`;
  if (!(Number(s.maxS) > 0)) throw new Error(`${where}: waitUntil needs maxS > 0 (the cap)`);
  if (s.minS !== undefined && !(Number(s.minS) >= 0 && s.minS <= s.maxS)) throw new Error(`${where}: minS must be 0..maxS`);
  if (s.between !== undefined && s.between !== 'normal' && s.between !== 'fast') throw new Error(`${where}: between must be "normal" or "fast"`);
  const all = isArmed(w) ? [w.on, w.off].flatMap(c => [c].flat()) : [w].flat();
  if (!all.length) throw new Error(`${where}: waitUntil is empty`);
  for (const c of all as any[]) {
    const ok = c && typeof c === 'object' && (
      'counter' in c ? [c.counter].flat().every((n: any) => typeof n === 'string' && n) && (c.rises === undefined || Number.isInteger(c.rises) && c.rises >= 0)
        && (c.of === undefined || typeof c.of === 'string') && (c.slack === undefined || Number.isInteger(c.slack) && c.slack >= 0)
      : 'quiet' in c ? Number(c.quiet) > 0 && (c.iterMs === undefined || Number(c.iterMs) > 0)
      : 'taskDone' in c ? typeof c.taskDone === 'string' && !!c.taskDone
      : 'simMinutes' in c ? Number(c.simMinutes) > 0 : false);
    if (!ok) throw new Error(`${where}: bad waitUntil cond ${JSON.stringify(c)} ({counter, rises?, of?, slack?} | {quiet, iterMs?} | {taskDone} | {simMinutes})`);
  }
  if (isArmed(w) && !inLoop && !/\b(on|off) #\d+/.test(s.note ?? '')) throw new Error(`${where}: waitUntil {on, off} needs an arm in the note`);
  return s;
}

export type StepIO = {
  ag: (cmd: string, args?: unknown) => Promise<any>;
  step: (s: LoopStep) => Promise<any>;
  sleep: (ms: number) => Promise<unknown>;
  now: () => number;
  halted: () => boolean;
  log?: (line: string) => void;                                   // default: console.log with an HH:MM:SS UTC stamp
  taskDone?: (name: string, sinceMs: number) => Promise<boolean>; // stutter profiler task records ({taskDone} conds)
};
export type StepDone = { r: any; secs: number; why: 'fixed' | 'signal' | 'cap' | 'window'; rises: Record<string, number> };

// One step: counters read before it (`read` + the conds' own), the step, then its wait: fixed waitS, or waitUntil (see top)
export async function runStep(s: LoopStep, io: StepIO, read: string[] = []): Promise<StepDone> {
  const conds = condsOf(s.waitUntil, /\b(on|off) #\d+/.exec(s.note ?? '')?.[1]);
  const names = [...new Set([...read, ...conds.flatMap(c => 'counter' in c ? [...[c.counter].flat(), ...(c.of ? [c.of] : [])] : [])])];
  const patches = async (): Promise<Record<string, number>> => {
    const res = (await io.ag('patches'))?.result ?? {};
    return Object.fromEntries(names.map(n => {
      if (typeof res[n] !== 'number') throw new Error(`ts3fixes counter ${n} missing from the patches answer`);
      return [n, res[n]];
    }));
  };
  const simMin = async () => Number((await io.ag('time'))?.result?.minutes_total);
  const before = names.length ? await patches() : {}, min0 = conds.some(c => 'simMinutes' in c) ? await simMin() : NaN;
  const r = await io.step(s);
  const t0 = io.now();
  if (s.between) await io.ag('set_speed', { speed: 'ultra' });
  let why: StepDone['why'] = 'fixed';
  if (!conds.length) { if (s.waitS) await io.sleep(s.waitS * 1000); }
  else {
    const quiet = conds.filter((c): c is { quiet: number; iterMs?: number } => 'quiet' in c);
    const iterMs = Math.min(...quiet.map(c => c.iterMs ?? QUIET_ITER_MS)), quietMs = Math.max(0, ...quiet.map(c => c.quiet));
    const met = new Set<WaitCond>(); // counters and tasks only rise: once met, met
    let slowAt = t0, polled = -Infinity;
    for (;;) {
      if (io.halted()) { why = 'window'; break; }
      if (io.now() - t0 >= s.maxS! * 1000) { why = 'cap'; break; }
      if (quiet.length) { const a = io.now(), p = await io.ag('ping'); if (!p?.ok || io.now() - a >= iterMs) slowAt = io.now(); }
      if (io.now() - polled >= POLL_MS) {
        polled = io.now();
        const cur = names.length ? await patches() : {}, rise = (n: string) => cur[n] - before[n];
        for (const c of conds) {
          if (met.has(c)) continue;
          if ('counter' in c) {
            const up = [c.counter].flat().reduce((a, n) => a + rise(n), 0);
            if (up >= (c.rises ?? 1) && (!c.of || rise(c.of) > 0 && up >= rise(c.of) - (c.slack ?? 0))) met.add(c);
          } else if ('taskDone' in c) {
            if (!io.taskDone) throw new Error(`waitUntil {taskDone: ${c.taskDone}} needs the profiler's task records (io.taskDone), not wired here`);
            if (await io.taskDone(c.taskDone, t0)) met.add(c);
          } else if ('simMinutes' in c) {
            if (await simMin() - min0 >= c.simMinutes) met.add(c);
          }
        }
      }
      const el = io.now() - t0;
      if (conds.every(c => 'quiet' in c || met.has(c)) && (!quiet.length || io.now() - slowAt >= quietMs) && el >= (s.minS ?? 0) * 1000) { why = 'signal'; break; }
      await io.sleep(quiet.length ? PING_GAP_MS : POLL_MS);
    }
  }
  if (s.until && why !== 'window') {
    const met = await untilMet(s.until, io).catch((e: Error) => { throw new Error(`step ${s.cmd}: ${e.message}`); });
    if (met) (io.log ?? (l => console.log(`${new Date().toISOString().slice(11, 19)} ${l}`)))(`  until met after ${((io.now() - t0) / 1000).toFixed(0)} s: ${JSON.stringify(met.result).slice(0, 160)}`);
  }
  if (s.between) await io.ag('set_speed', { speed: s.between });
  const after = names.length ? await patches() : {};
  const secs = (io.now() - t0) / 1000;
  if (conds.length || isSetup(s)) { // setup steps: the wait end bounds their window (worst-hitches.ts runContext)
    const n = /#(\d+)/.exec(s.note ?? '')?.[1];
    (io.log ?? (l => console.log(`${new Date().toISOString().slice(11, 19)} ${l}`)))(
      `${n ? `firing #${n}` : `step ${s.cmd}`} done after ${secs.toFixed(1)} s (${why === 'window' ? 'window end' : why})${s.note ? ` [${s.note}]` : ''}`);
  }
  return { r, secs, why, rises: Object.fromEntries(names.map(n => [n, after[n] - before[n]])) };
}

const posInt = (n: unknown) => Number.isInteger(n) && (n as number) > 0;
export function checkLoop(l: any): Loop {
  if (!l || typeof l !== 'object') throw new Error('loop must be an object');
  if (!Array.isArray(l.steps) || !l.steps.length || l.steps.some((s: any) => typeof s?.cmd !== 'string')) throw new Error('loop.steps must be a non-empty list of {cmd, ...}');
  if (!Array.isArray(l.arms) || !l.arms.length || l.arms.some((a: any) => a !== 'on' && a !== 'off')) throw new Error('loop.arms must be a non-empty list of "on" / "off"');
  const u = l.until ?? {}, keys = Object.keys(u).filter(k => k !== 'minFirings');
  if (keys.length !== 1 || !['perArm', 'events'].includes(keys[0]) || !posInt(u[keys[0]])) throw new Error('loop.until must be {perArm: N} or {events: N} with N a positive integer (fixed before the run), plus optional minFirings');
  if (u.minFirings !== undefined && !posInt(u.minFirings)) throw new Error('loop.until.minFirings must be a positive integer');
  if (l.steps.some((s: any) => s.setup !== undefined && typeof s.setup !== 'boolean')) throw new Error('loop step setup must be true or false');
  if (l.steps.every((s: any) => isSetup(s))) throw new Error('loop.steps: every step is setup; mark the firing step setup: false');
  if ('perArm' in u && !(l.arms.includes('on') && l.arms.includes('off'))) throw new Error('loop.until.perArm needs both arms in loop.arms');
  if (l.maxMinutes !== undefined && !(Number(l.maxMinutes) > 0)) throw new Error('loop.maxMinutes must be > 0');
  for (const s of l.steps) {
    const c = s.count;
    if (c !== undefined && !(c && Object.keys(c).length === 1 && (typeof c.counter === 'string' || typeof c.result === 'string'))) throw new Error(`loop step ${s.cmd}: count must be {counter: name} or {result: field}`);
    checkStep(s, true);
  }
  return l as Loop;
}

// "$on" -> arm === 'on', anywhere in the args (nested objects and lists too); "$on:<onValue>:<offValue>" -> that integer per arm
// (a ladder rung whose base already has the switch at 1 and the arm raises it to 2, e.g. holdkeyed "$on:2:1")
const ON_VALUES = /^\$on:(-?\d+):(-?\d+)$/;
export const subst = (v: unknown, arm: Arm): unknown =>
  v === '$on' ? arm === 'on' : typeof v === 'string' && ON_VALUES.test(v) ? Number(ON_VALUES.exec(v)![arm === 'on' ? 1 : 2])
  : Array.isArray(v) ? v.map(x => subst(x, arm)) : v && typeof v === 'object' ? Object.fromEntries(Object.entries(v).map(([k, x]) => [k, subst(x, arm)])) : v;

// fired: firings so far of each arm the loop uses (minFirings)
export const ruleMet = (u: Until, ev: Record<Arm, number>, fired: Partial<Record<Arm, number>> = {}) =>
  ('perArm' in u ? ev.on >= u.perArm && ev.off >= u.perArm : ev.on + ev.off >= u.events) && Object.values(fired).every(n => n! >= (u.minFirings ?? 0));
export const ruleText = (u: Until) => `${'perArm' in u ? `${u.perArm} events per arm` : `${u.events} events`}${u.minFirings ? `, >= ${u.minFirings} firings per arm` : ''}`;
// a queue job's run.profile.loopRule over the repro's own until / maxMinutes (validated as one loop)
export const withRule = (l: any, rule?: { until?: Until; maxMinutes?: number }) =>
  checkLoop(rule ? { ...l, ...(rule.until && { until: rule.until }), ...(rule.maxMinutes !== undefined && { maxMinutes: rule.maxMinutes }) } : l);
// queue.ts reads this line into the result row (STOP_LINE)
export const stopLine = (r: LoopResult, u: Until) => {
  const head = r.why === 'rule' ? `stopped at ${r.total} events after ${(r.ms / 60000).toFixed(1)} min`
    : `loop ${r.why === 'cap' ? 'cap' : 'window end'} reached: ${r.total} events after ${(r.ms / 60000).toFixed(1)} min, rule not met`;
  return `${head} (off ${r.events.off}, on ${r.events.on}; ${r.firings} firings, ${r.cycles} cycles; rule ${ruleText(u)}${r.capped ? `; ${r.capped} steps capped` : ''})`;
};
export const STOP_LINE = /((?:stopped at \d+ events|loop (?:cap|window end) reached: \d+ events) after [\d.]+ min[^\r\n]*)/;

// ag: agent call (patches reads, pings); step: profile-world's step runner (logs, fail-fast on a failed non-optional step);
// halted: window over (cap, clock guard, another stop)
// set_speed while the game speed is locked (a dialog is open or the game is saving; ip-p6int-stalelot-ab 2026-10-25 failed on it):
// log the open dialog, retry every 5 s; fail-fast rule: progress checked every 30 s (still locked = a miss), 2 misses = the step
// fails with the dialog named. r0 = the first answer; any other answer goes back unchanged.
const LOCKED = /game speed is locked/;
export async function untilUnlocked(call: () => Promise<any>, r0: any,
  io: { sleep: (ms: number) => Promise<unknown>; now: () => number; log: (l: string) => void; dialog: () => Promise<string> }) {
  if (r0?.ok || !LOCKED.test(String(r0?.error))) return r0;
  const t0 = io.now();
  let r = r0, misses = 0, next = t0 + 30000;
  io.log(`set_speed: game speed locked, open dialog ${await io.dialog()}; retrying every 5 s (fail after 2 x 30 s)`);
  for (;;) {
    await io.sleep(5000);
    r = await call();
    if (r?.ok || !LOCKED.test(String(r?.error))) { io.log(`set_speed: unlocked after ${((io.now() - t0) / 1000).toFixed(0)} s`); return r; }
    if (io.now() < next) continue;
    const d = await io.dialog();
    if (++misses >= 2) return { ...r, error: `${r.error}; still locked after ${((io.now() - t0) / 1000).toFixed(0)} s (2 checks), open dialog ${d}` };
    io.log(`set_speed: still locked after ${((io.now() - t0) / 1000).toFixed(0)} s (check ${misses} of 2), open dialog ${d}`);
    next += 30000;
  }
}

// onArm: called at each firing's start, before its first step (profile-world.ts: arm slice + ts3native snapshot for wait-ab.ts
// and hitch-ab.ts, docs/189 §16)
export type LoopIO = StepIO & { onArm?: (arm: Arm, firing: number) => Promise<void> };

export async function runLoop(loop: Loop, capMs: number, io: LoopIO): Promise<LoopResult> {
  const t0 = io.now(), ev: Record<Arm, number> = { on: 0, off: 0 }, fired: Partial<Record<Arm, number>> = Object.fromEntries(loop.arms.map(a => [a, 0]));
  let firings = 0, cycles = 0, capped = 0;
  const done = (why: LoopResult['why']): LoopResult => ({ why, events: { ...ev }, total: ev.on + ev.off, firings, cycles, ms: io.now() - t0, ...(capped && { capped }) });
  const counted = loop.steps.some(s => s.count);
  for (;;) {
    for (const arm of loop.arms) {
      if (io.halted()) return done('window');
      firings++; fired[arm]!++;
      await io.onArm?.(arm, firings);
      let n = 0;
      for (const s of loop.steps) {
        if (io.halted()) return done('window'); // a firing cut short still counts what it saw
        const counter = s.count && 'counter' in s.count ? s.count.counter : null;
        const d = await runStep({ ...s, args: subst(s.args, arm), note: `${s.note ?? s.cmd} ${arm} #${firings}` }, io, counter ? [counter] : [])
          .catch(e => { throw new Error(`loop ${e.message}`); });
        if (d.why === 'cap') capped++;
        if (counter) n += Math.max(0, d.rises[counter]);
        // a list field counts its entries (crowd_do / crowd_go "sent": one per Sim; 10-30 sv-ufbd2-pre-loop saw 0 events)
        else if (s.count && 'result' in s.count) { const v = d.r?.result?.[s.count.result]; n += typeof v === 'number' ? v : Array.isArray(v) ? v.length : 0; }
      }
      ev[arm] += counted ? n : 1;
    }
    cycles++;
    if (ruleMet(loop.until, ev, fired)) return done('rule');
    if (io.now() - t0 >= capMs) return done('cap');
  }
}

if (import.meta.url === `file:///${process.argv[1]?.replace(/\\/g, '/').replace(/^\//, '')}`) {
  // fake agent: the clock moves only through sleep; patches answers a counter the fire step raises by `per`
  const sim = (per: (arm: boolean) => number, haltAt = Infinity) => {
    let t = 0, ctr = 0, on = false;
    const log: string[] = [];
    const io: LoopIO = {
      now: () => t, sleep: async ms => { t += ms; }, halted: () => t >= haltAt, log: () => {},
      ag: async cmd => ({ ok: true, result: cmd === 'patches' ? { owspread_alarms: ctr } : {} }),
      step: async s => {
        log.push(`${s.cmd} ${s.note} ${JSON.stringify(s.args)}`);
        if (s.cmd === 'patches') on = (s.args as any).overwatchspread;
        if (s.cmd === 'fire') ctr += per(on);
        return { ok: true, result: { n: per(on), sent: Array(per(on)).fill('x') } };
      },
    };
    return { io, log };
  };
  const steps = (count?: LoopStep['count']): LoopStep[] => [{ cmd: 'patches', args: { overwatchspread: '$on' }, waitS: 1, note: 'arm' }, { cmd: 'fire', args: ['all', 1], waitS: 45, note: 'fire', ...(count && { count }) }];
  const L = (until: Until, count?: LoopStep['count'], maxMinutes?: number) => checkLoop({ steps: steps(count), arms: ['off', 'on', 'on', 'off'], until, maxMinutes });

  // untilUnlocked: unlocks on the 3rd retry (15 s); stays locked -> fails at the 2nd 30 s check with the dialog; other errors pass
  { let t = 0, n = 0; const lines: string[] = [];
    const lio = { sleep: async (ms: number) => { t += ms; }, now: () => t, log: (l: string) => lines.push(l), dialog: async () => '{"open":true,"dialog":"Save"}' };
    const lockedR = { ok: false, error: 'game speed is locked (a dialog is open or the game is saving)' };
    let r = await untilUnlocked(async () => ++n >= 3 ? { ok: true } : lockedR, lockedR, lio);
    assert.deepEqual([r.ok, t, lines.length], [true, 15000, 2]);
    t = 0; lines.length = 0;
    r = await untilUnlocked(async () => lockedR, lockedR, lio);
    assert.deepEqual([r.ok, t], [false, 60000]); assert.match(r.error, /still locked after 60 s \(2 checks\), open dialog \{"open":true,"dialog":"Save"\}/);
    assert.equal(lines.length, 2); // first notice + check 1
    const other = { ok: false, error: 'no such speed' };
    assert.equal(await untilUnlocked(async () => ({ ok: true }), other, lio), other); }
  // onArm: once per firing, before its steps, with the arm and firing number
  { const s0 = sim(() => 1), seen: string[] = [];
    await runLoop(L({ perArm: 2 }), 30 * 60000, { ...s0.io, onArm: async (a, n) => { seen.push(`${a}${n}:${s0.log.length}`); } });
    assert.deepEqual(seen.map(x => x.split(':')[0]), ['off1', 'on2', 'on3', 'off4']);
    assert.equal(seen[1].split(':')[1], String(s0.log.length / 4)); } // firing 2 starts after firing 1's steps
  // firings counted: perArm 6 -> 3 ABBA cycles (12 firings), the old 12-firing repro; notes and $on as written by hand there
  let s = sim(() => 1), r = await runLoop(L({ perArm: 6 }), 30 * 60000, s.io);
  assert.deepEqual([r.why, r.events, r.firings, r.cycles], ['rule', { on: 6, off: 6 }, 12, 3]);
  assert.deepEqual(s.log.slice(0, 4), ['patches arm off #1 {"overwatchspread":false}', 'fire fire off #1 ["all",1]', 'patches arm on #2 {"overwatchspread":true}', 'fire fire on #2 ["all",1]']);
  assert.equal(s.log[9], 'fire fire off #5 ["all",1]'); // cycle 2 starts with off again (ABBA repeating)
  assert.equal(stopLine(r, { perArm: 6 }), 'stopped at 12 events after 9.2 min (off 6, on 6; 12 firings, 3 cycles; rule 6 events per arm)');
  assert.ok(STOP_LINE.test(`12:00:00 ${stopLine(r, { perArm: 6 })}`));
  // rule checked only at a cycle end: 5 events total is met after firing 5, but the cycle finishes (8 firings)
  r = await runLoop(L({ events: 5 }), 30 * 60000, sim(() => 1).io);
  assert.deepEqual([r.why, r.total, r.firings], ['rule', 8, 8]);
  // counter: on firings raise it by 3, off by 1 -> perArm 6 needs off to reach 6: 3 cycles (off 6, on 18)
  r = await runLoop(L({ perArm: 6 }, { counter: 'owspread_alarms' }), 30 * 60000, sim(on => on ? 3 : 1).io);
  assert.deepEqual([r.why, r.events, r.cycles], ['rule', { on: 18, off: 6 }, 3]);
  // result field
  r = await runLoop(L({ events: 8 }, { result: 'n' }), 30 * 60000, sim(() => 2).io);
  assert.deepEqual([r.why, r.total, r.cycles], ['rule', 8, 1]);
  r = await runLoop(L({ events: 8 }, { result: 'sent' }), 30 * 60000, sim(() => 2).io);
  assert.deepEqual([r.why, r.total, r.cycles], ['rule', 8, 1], 'list result field counts its entries');
  // zero events: the cap ends it (checked at cycle ends: 4 firings x 46 s = 3.07 min per cycle, cap 5 min -> 2 cycles)
  r = await runLoop(L({ perArm: 6 }, { counter: 'owspread_alarms' }, 5), 5 * 60000, sim(() => 0).io);
  assert.deepEqual([r.why, r.total, r.cycles], ['cap', 0, 2]);
  assert.ok(stopLine(r, { perArm: 6 }).startsWith('loop cap reached: 0 events after 6.1 min, rule not met'));
  // the window ends first (another stop or the profile cap): ends mid-cycle, events so far kept
  r = await runLoop(L({ perArm: 6 }), 60 * 60000, sim(() => 1, 100_000).io);
  assert.deepEqual([r.why, r.firings, r.events], ['window', 3, { off: 1, on: 2 }]);
  // a counter missing from the answer fails the run (wrong name or old ts3fixes), not a silent 0
  await assert.rejects(runLoop(L({ perArm: 1 }, { counter: 'nope' }), 60000, sim(() => 1).io), /counter nope missing/);
  // validation
  assert.throws(() => checkLoop({ steps: steps(), arms: ['off', 'on'], until: { perArm: 0 } }), /positive integer/);
  assert.throws(() => checkLoop({ steps: steps(), arms: ['off', 'on'], until: { perArm: 2, events: 3 } }), /until/);
  assert.throws(() => checkLoop({ steps: steps(), arms: ['on'], until: { perArm: 2 } }), /both arms/);
  assert.throws(() => checkLoop({ steps: steps(), arms: ['off', 'maybe'], until: { events: 2 } }), /arms/);
  assert.throws(() => checkLoop({ steps: [], arms: ['off', 'on'], until: { events: 2 } }), /steps/);
  assert.throws(() => checkLoop({ steps: [{ cmd: 'x', count: { counter: 1 } }], arms: ['off', 'on'], until: { events: 2 } }), /count/);
  assert.throws(() => checkLoop({ steps: steps(), arms: ['off', 'on'], until: { events: 2 }, maxMinutes: 0 }), /maxMinutes/);
  assert.deepEqual(subst({ a: '$on', b: ['$on', 'x'], c: 1 }, 'off'), { a: false, b: [false, 'x'], c: 1 });
  assert.deepEqual(subst({ h: '$on:2:1', s: '$on:1:0' }, 'on'), { h: 2, s: 1 });
  assert.deepEqual(subst({ h: '$on:2:1', s: '$on:1:0' }, 'off'), { h: 1, s: 0 });
  // minFirings (docs/168 §7): on firings count 30 instances, off 10 -> perArm 20 alone stops after one cycle (2 firings per
  // arm); minFirings 10 holds it to 5 cycles = 10 firings per arm
  r = await runLoop(L({ perArm: 20 }, { counter: 'owspread_alarms' }), 60 * 60000, sim(on => on ? 30 : 10).io);
  assert.deepEqual([r.why, r.cycles, r.firings], ['rule', 1, 4]);
  r = await runLoop(L({ perArm: 20, minFirings: 10 }, { counter: 'owspread_alarms' }), 60 * 60000, sim(on => on ? 30 : 10).io);
  assert.deepEqual([r.why, r.cycles, r.firings, r.events], ['rule', 5, 20, { on: 300, off: 100 }]);
  assert.match(stopLine(r, { perArm: 20, minFirings: 10 }), /rule 20 events per arm, >= 10 firings per arm\)$/);
  // single-arm loop: minFirings counts that arm only
  r = await runLoop(checkLoop({ steps: steps(), arms: ['on'], until: { events: 1, minFirings: 3 } }), 60 * 60000, sim(() => 1).io);
  assert.deepEqual([r.why, r.firings], ['rule', 3]);
  assert.throws(() => checkLoop({ steps: steps(), arms: ['off', 'on'], until: { perArm: 2, minFirings: 0 } }), /minFirings/);
  // loopRule override (queue run.profile.loopRule) keeps the body, replaces until / maxMinutes, validated
  const wr = withRule(L({ perArm: 2 }), { until: { perArm: 2, minFirings: 10 }, maxMinutes: 18 });
  assert.deepEqual([wr.until, wr.maxMinutes, wr.steps.length], [{ perArm: 2, minFirings: 10 }, 18, 2]);
  assert.equal(withRule(L({ perArm: 2 })).until.perArm, 2);
  assert.throws(() => withRule(L({ perArm: 2 }), { until: { perArm: 0 } as any }), /positive integer/);
  // setup: default by command, explicit wins; a body of only setup steps measures nothing
  assert.deepEqual([isSetup({ cmd: 'patches', note: 'arm' }), isSetup({ cmd: 'strand_many' }), isSetup({ cmd: 'native_set', note: 'fire: x' }), isSetup({ cmd: 'reflect_call' }),
    isSetup({ cmd: 'build_mode', setup: true }), isSetup({ cmd: 'spawn_sims', setup: false })], [true, true, false, false, true, false]);
  assert.throws(() => checkLoop({ steps: [{ cmd: 'patches' }], arms: ['off', 'on'], until: { perArm: 1 } }), /every step is setup/);
  assert.throws(() => checkLoop({ steps: [{ cmd: 'fire', setup: 'yes' }], arms: ['off', 'on'], until: { perArm: 1 } }), /setup must be/);

  // ---- adaptive waits: a fake game whose counters are functions of the time since the step returned and whose slow
  // iterations hold a ping until they end (the mailbox answers once per sim tick)
  const game = (ctr: Record<string, (s: number) => number>, slow: [number, number][] = []) => {
    let t = 0, t0 = 0;
    const lines: string[] = [], calls: string[] = [];
    const io: StepIO = {
      now: () => t, sleep: async ms => { t += ms; }, halted: () => false, log: l => lines.push(l),
      ag: async (cmd, args) => {
        calls.push(cmd === 'set_speed' ? `set_speed ${(args as any).speed}` : cmd);
        if (cmd === 'ping') { const s = slow.find(([a, b]) => t - t0 >= a * 1000 && t - t0 < b * 1000); t = s ? t0 + s[1] * 1000 : t + 20; return { ok: true }; }
        return { ok: true, result: cmd === 'patches' ? Object.fromEntries(Object.entries(ctr).map(([k, f]) => [k, f((t - t0) / 1000)]))
          : cmd === 'time' && ctr.minutes_total ? { minutes_total: ctr.minutes_total((t - t0) / 1000) } : {} };
      },
      step: async () => { t0 = t; return { ok: true, result: {} }; },
    };
    return { io, lines, calls };
  };
  const fire = (x: Partial<LoopStep>): LoopStep => checkStep({ cmd: 'reflect_call', note: 'fire off #3', waitS: 23, ...x });
  // counter: the pass ends 7 s after the call; polled once a second -> done at 7 s, logged as a signal
  let g = game({ easpread_lots_passes: s => s >= 7 ? 1 : 0 }), d = await runStep(fire({ waitUntil: { counter: 'easpread_lots_passes' }, minS: 3, maxS: 23 }), g.io);
  assert.deepEqual([d.why, d.secs, d.rises], ['signal', 7, { easpread_lots_passes: 1 }]);
  assert.deepEqual(g.lines, ['firing #3 done after 7.0 s (signal) [fire off #3]']);
  // simMinutes: 10 sim-minutes per wall second from 1000 -> 35 minutes reached at 4 s
  g = game({ minutes_total: s => 1000 + 10 * Math.max(0, s) }); d = await runStep(fire({ waitUntil: { simMinutes: 35 }, maxS: 45 }), g.io);
  assert.deepEqual([d.why, d.secs], ['signal', 4]);
  assert.throws(() => fire({ waitUntil: { simMinutes: 0 } as any, maxS: 5 }), /bad waitUntil cond/);
  // minS floor: done at 1 s, ends at 4 s
  g = game({ c: s => s >= 1 ? 1 : 0 }); d = await runStep(fire({ waitUntil: { counter: 'c' }, minS: 4, maxS: 23 }), g.io);
  assert.deepEqual([d.why, d.secs], ['signal', 4]);
  // cap: never rises -> 23 s, flagged in the line
  g = game({ c: () => 0 }); d = await runStep(fire({ waitUntil: { counter: 'c' }, maxS: 23 }), g.io);
  assert.deepEqual([d.why, d.secs, g.lines[0]], ['cap', 23, 'firing #3 done after 23.0 s (cap) [fire off #3]']);
  // of + slack (sliced pass catching up): 8 queued at 1 s, one run per second from 2 s -> run 8 at 9 s; slack 1 -> 8 s
  const q = { queued: (s: number) => s >= 1 ? 8 : 0, run: (s: number) => Math.max(0, Math.min(8, Math.floor(s) - 1)) };
  g = game(q); d = await runStep(fire({ waitUntil: { counter: 'run', of: 'queued' }, maxS: 30 }), g.io);
  assert.deepEqual([d.why, d.secs], ['signal', 9]);
  g = game(q); d = await runStep(fire({ waitUntil: { counter: 'run', of: 'queued', slack: 1 }, maxS: 30 }), g.io);
  assert.equal(d.secs, 8);
  // nothing queued yet (rise(of) = 0) is not "caught up"
  g = game({ queued: () => 0, run: () => 0 }); d = await runStep(fire({ waitUntil: { counter: 'run', of: 'queued' }, maxS: 5 }), g.io);
  assert.equal(d.why, 'cap');
  // counter list summed: passes + theirs (either path ends the ErrorTrap check)
  g = game({ etspread_passes: () => 0, etspread_theirs: s => s >= 2 ? 1 : 0 });
  d = await runStep(fire({ waitUntil: { counter: ['etspread_passes', 'etspread_theirs'] }, maxS: 23 }), g.io);
  assert.deepEqual([d.why, d.secs], ['signal', 2]);
  // quiet: slow iterations 1.0-1.4 s and 2.0-2.5 s after the call -> 3 s quiet from 2.5 s; minS 4 does not bind
  g = game({}, [[1, 1.4], [2, 2.5]]); d = await runStep(fire({ waitUntil: { quiet: 3000 }, minS: 4, maxS: 23 }), g.io);
  assert.equal(d.why, 'signal'); assert.ok(d.secs >= 5.5 && d.secs < 5.6, `quiet ${d.secs}`);
  assert.ok(!g.calls.includes('patches'), 'quiet alone reads no counters');
  // a short iteration (80 ms) under iterMs is quiet; minS then binds
  g = game({}, [[1, 1.08]]); d = await runStep(fire({ waitUntil: { quiet: 2000 }, minS: 4, maxS: 23 }), g.io);
  assert.ok(d.secs >= 4 && d.secs < 4.1, `iterMs ${d.secs}`);
  // all conds: counter at 2 s, but a slow iteration at 1.5-3 s holds it to 5 s
  g = game({ c: s => s >= 2 ? 1 : 0 }, [[1.5, 3]]); d = await runStep(fire({ waitUntil: [{ counter: 'c' }, { quiet: 2000 }], maxS: 23 }), g.io);
  assert.ok(d.secs >= 5 && d.secs < 5.1, `all ${d.secs}`);
  // on/off picked by the note's arm
  const armed = { on: { counter: 'ours' }, off: { quiet: 1000 } };
  g = game({ ours: s => s >= 6 ? 1 : 0 }); d = await runStep(fire({ note: 'fire on #2', waitUntil: armed, maxS: 23 }), g.io);
  assert.deepEqual([d.secs, g.lines[0]], [6, 'firing #2 done after 6.0 s (signal) [fire on #2]']);
  g = game({ ours: () => 0 }); d = await runStep(fire({ waitUntil: armed, maxS: 23 }), g.io);
  assert.ok(d.secs >= 1 && d.secs < 1.1 && !g.calls.includes('patches'));
  await assert.rejects(runStep({ cmd: 'x', note: 'fire', waitUntil: armed, maxS: 5 }, game({}).io), /needs an arm/);
  // between: the wait runs at ultra, then the given speed; fixed waits keep waitS
  // until: polled after the fixed wait until the result path holds; not met in maxS = throw (fail fast)
  {
    let polls = 0, tu = 0;
    const io: StepIO = { now: () => tu, sleep: async ms => { tu += ms; }, halted: () => false, log: () => {}, step: async () => ({ ok: true, result: { done: false } }),
      ag: async cmd => cmd === 'loop' ? (++polls < 3 ? { ok: false, error: 'no reply' } : { ok: true, result: { done: polls >= 4 } }) : { ok: true, result: {} } };
    await runStep({ cmd: 'loop', waitS: 5, until: { cmd: 'loop', all: [{ path: 'done', equals: true }], everyS: 3, maxS: 60 } }, io);
    assert.deepEqual([polls, tu], [4, 5000 + 3 * 3000]);
    await assert.rejects(runStep({ cmd: 'loop', until: { cmd: 'never', all: [{ path: 'done', equals: true }], everyS: 3, maxS: 9 } }, io), /step loop: never until .* not met in 9 s/);
  }
  g = game({}); d = await runStep({ cmd: 'strand_many', waitS: 3, between: 'normal' }, g.io);
  assert.deepEqual([d.why, d.secs, g.calls], ['fixed', 3, ['set_speed ultra', 'set_speed normal']]);
  // taskDone: needs the profiler's records
  await assert.rejects(runStep(fire({ waitUntil: { taskDone: 'Ts3Fixes.EaSpreadNow' }, maxS: 5 }), game({}).io), /io.taskDone/);
  g = game({}); g.io.taskDone = async (_n, since) => g.io.now() - since >= 3000;
  d = await runStep(fire({ waitUntil: { taskDone: 'Ts3Fixes.EaSpreadNow' }, maxS: 23 }), g.io);
  assert.deepEqual([d.why, d.secs], ['signal', 3]);
  // a loop with adaptive firings: capped steps in the stop line
  g = game({ c: () => 0 });
  r = await runLoop(checkLoop({ steps: [{ cmd: 'fire', waitUntil: { counter: 'c' }, maxS: 2 }], arms: ['off', 'on'], until: { events: 2 } }), 60000, g.io);
  assert.deepEqual([r.why, r.capped, g.lines], ['rule', 2, ['firing #1 done after 2.0 s (cap) [fire off #1]', 'firing #2 done after 2.0 s (cap) [fire on #2]']]);
  assert.ok(stopLine(r, { events: 2 }).endsWith('rule 2 events; 2 steps capped)'));
  // validation
  assert.throws(() => checkStep({ cmd: 'x', waitUntil: { quiet: 1000 } }), /maxS/);
  assert.throws(() => checkStep({ cmd: 'x', waitUntil: { quiet: 1000 }, maxS: 5, minS: 6 }), /minS/);
  assert.throws(() => checkStep({ cmd: 'x', waitUntil: { counters: 'a' }, maxS: 5 }), /bad waitUntil/);
  assert.throws(() => checkStep({ cmd: 'x', waitUntil: [], maxS: 5 }), /empty/);
  assert.throws(() => checkStep({ cmd: 'x', note: 'fire', waitUntil: armed, maxS: 5 }), /arm in the note/);
  assert.throws(() => checkStep({ cmd: 'x', waitS: 1, waitUntil: { quiet: 1 }, maxS: 5, between: 'ultra' }), /between/);
  // every waitUntil in repros.json is well formed (plain steps and loop bodies)
  const { readFileSync } = await import('node:fs');
  const repros = JSON.parse(readFileSync(new URL('./repros.json', import.meta.url), 'utf8')) as any[];
  // docs/202 D repros (fix names unseenfbpre / unseenroute): crowd_do must not offer Sit or Read, whose plans are not D followers
  // (Sit plans in PlanToUnusedPart): a lot that only offers them made 2026-10-31 ip-ur-walk-across-loop act 2 times, not >= 10
  for (const e of repros) {
    if (!/unseenfbpre|unseenroute/.test(String(e.fix))) continue;
    const doSteps = [...(e.steps ?? []), ...(e.loop?.steps ?? [])].filter((s: any) => s.cmd === 'crowd_do');
    for (const s of doSteps) assert.ok(!/(^|\|)(Sit|Read)(\||$)/.test(String(s.args?.interaction)), `${e.id}: crowd_do offers Sit / Read (not a D follower)`);
  }
  let adaptive = 0;
  for (const e of repros) {
    for (const s of e.steps ?? []) if (s.waitUntil) { checkStep(s); adaptive++; }
    if (e.loop) {
      checkLoop(e.loop); adaptive += e.loop.steps.filter((s: any) => s.waitUntil).length;
      const unmarked = e.loop.steps.filter((s: any) => typeof s.setup !== 'boolean').map((s: any) => s.cmd);
      assert.equal(unmarked.length, 0, `${e.id}: loop steps without setup: true/false: ${unmarked.join(', ')}`);
    }
  }
  // A/B loop queues (docs/168 §7): freeze-measure.json's measurement settings, and a loopRule with minFirings on every loop job
  const MEASURE: Record<string, number> = { 'Harness.ForegroundRender': 1, 'HitchAttr.Enabled': 1, 'HitchAttr.Holders': 1, 'RouteJobPhases.Enabled': 1, 'RouteJobPhases.SyncPlans': 1 };
  let abJobs = 0;
  for (const q of ['route-hitch-loops', 'script-hitch-loops', 'overwatch-loop2']) {
    for (const j of JSON.parse(readFileSync(new URL(`./queues/${q}.json`, import.meta.url), 'utf8')).jobs) {
      const p = j.run?.profile ?? {}, at = `${q} ${j.id}`;
      for (const [k, v] of Object.entries(MEASURE)) assert.equal(j.ini?.[k], v, `${at}: ini ${k} must be ${v}`);
      assert.ok(p.noSamplers === true && !p.noFrames && !p.noProf && j.asi === 'game-copy/ts3native.asi.0332', `${at}: noSamplers, frames + profiler on, asi 0332`);
      const e = repros.find(x => x.id === p.repro);
      if (e?.loop) {
        const l = withRule(e.loop, p.loopRule);
        assert.ok(l.until.minFirings! >= 10, `${at}: loopRule.until.minFirings >= 10 for an A/B verdict`);
        assert.ok((l.maxMinutes ?? p.minutes) <= p.minutes && (j.timeoutMin ?? 60) >= p.minutes + 10, `${at}: loop cap within minutes, timeoutMin >= minutes + 10`);
      }
      abJobs++;
    }
  }
  console.log(`repro-loop selftest: PASS (${adaptive} adaptive steps in repros.json valid; ${abJobs} A/B queue jobs carry the measurement settings)`);
}
