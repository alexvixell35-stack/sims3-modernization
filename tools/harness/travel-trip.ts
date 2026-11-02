// Round trips home -> destination -> home with timings and memory per leg (docs/19 section 5, PLAN item 10).
//   node tools/harness/travel-trip.ts [--to france|china|egypt|university|future] [--days 2] [--hours 6] [--trips 1]
//                                     [--sims "A B,C D"] [--save-stats <prefix>]
// Needs the probe server and a home world in live mode (cycle.ts --save X). Per trip: memory + travel_state, travel,
// wait for the destination in live mode, run --hours sim hours there (autopilot x3), travel_home, wait for the home
// world in live mode, memory + travel_state again. --save-stats P saves P-trip0 before and P-tripN after each return
// (new save files, the loaded one is left alone) and runs save-stats.ts over them at the end.
// Output: console + probe-out/travel/<to>-<time>.jsonl (one line per leg).
import { execFileSync } from 'node:child_process';
import { appendFileSync, mkdirSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { GUARD_MS, stallGuard } from './game-alive.ts';

const { values } = parseArgs({ options: {
  to: { type: 'string', default: 'france' }, days: { type: 'string', default: '2' }, hours: { type: 'string', default: '6' },
  trips: { type: 'string', default: '1' }, sims: { type: 'string' }, 'save-stats': { type: 'string' },
  port: { type: 'string', default: '47891' },
} });
const ROOT = resolve(import.meta.dirname, '..', '..');
const OUT = join(ROOT, 'probe-out', 'travel');
mkdirSync(OUT, { recursive: true });
const outFile = join(OUT, `${values.to}-${new Date().toISOString().replace(/[:.]/g, '-').slice(0, 19)}.jsonl`);
const base = `http://127.0.0.1:${values.port}`;
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const stamp = () => new Date().toISOString().slice(11, 19);
const log = (m: string) => console.log(`${stamp()} ${m}`);

async function get(path: string, timeoutMs = 30000): Promise<any> {
  return (await fetch(`${base}/${path}`, { signal: AbortSignal.timeout(timeoutMs) })).json();
}
async function agent(cmd: string, args?: object): Promise<any> {
  const q = new URLSearchParams({ cmd, timeout: '8000' });
  if (args) q.set('args', JSON.stringify(args));
  try { return await get(`agent?${q}`, 20000); } catch (e) { return { ok: false, error: String(e) }; }
}
// guard: fail-fast check (game-alive.ts stallGuard or a wait's own), run outside check() so its verdict is not swallowed
async function until(what: string, limitS: number, check: () => Promise<boolean>, everyMs = 1000, guard = stallGuard(what, { port: Number(values.port) })): Promise<number> {
  const t0 = Date.now();
  while (!(await check().catch(() => false))) {
    if (Date.now() - t0 > limitS * 1000) throw new Error(`${what}: not reached within ${limitS} s`);
    const why = await guard();
    if (why) throw new Error(why);
    await sleep(everyMs);
  }
  const s = (Date.now() - t0) / 1000;
  log(`${what} after ${s.toFixed(1)} s`);
  return s;
}
// accept: the trip's own dialogs (travel confirm); elsewhere the agent's safe button (docs/236 #3)
async function dialog(accept = false): Promise<void> {
  if (accept) await agent('dialog_expect', { seconds: 60, accept: true }); // the agent accepts it the frame it opens (413f985; rules apply); older agents leave it to this call (03e2bfe)
  const d = await agent('auto_dialog', accept ? { accept: true } : undefined);
  if (d.ok && d.result.open) log(`dialog "${String(d.result.title ?? '').slice(0, 60)}" -> ${d.result.clicked}`);
}
async function snapshot() {
  const mem = await get('memory', 60000).catch(() => null);
  const ts = (await agent('travel_state')).result ?? null;
  return { mem: mem && { commitMB: mem.commitMB, freeMB: mem.freeMB, largestFreeMB: mem.largestFreeMB }, travel: ts };
}
async function saveAs(name: string): Promise<void> {
  await agent('autopilot', { enabled: false }); // it would unpause the save
  await agent('set_speed', { speed: 'pause' });
  await dialog();
  const r = await agent('save_game', { name });
  if (!r.ok) { log(`save ${name} not started: ${r.error}`); return; }
  // fail fast: the agent answers, no save in progress and no async_log line, twice ~30 s apart = the save never ran
  const g = stallGuard(`save ${name}`, { port: Number(values.port) });
  let idle = 0, idleAt = Date.now() + GUARD_MS;
  await until(`saved ${name}`, 300, async () => {
    if ((await agent('async_log')).result?.some((l: string) => l.includes(`save ${name} -> `))) return true;
    if (Date.now() >= idleAt) { idleAt = Date.now() + GUARD_MS; const st = await agent('game_state'); idle = st.ok && st.result?.save_in_progress === false ? idle + 1 : 0; }
    return false;
  }, 2000, async () => (await g()) || (idle >= 2 ? `save ${name}: not in progress and no async_log line for ~${2 * GUARD_MS / 1000} s (fail-fast guard)` : ''));
  await agent('autopilot', { enabled: true, speed: 'ultra', dialogs: true });
}

// One world switch: send the command, wait until the old world is gone (board), then until the new one is in live mode (load).
async function leg(name: string, cmd: string, args: object, fromWorld: string) {
  const before = await snapshot();
  const t0 = Date.now();
  const r = await agent(cmd, args);
  if (!r.ok) throw new Error(`${cmd}: ${r.error}`);
  log(`${name}: ${cmd} ${JSON.stringify(r.result)}`);
  let n = 0;
  // Fail fast (as save-matrix.ts enterWorld, df4dcaa): every 30 s the trip must still be alive; two misses in a row
  // (~1 min) end the leg with a reason instead of the 10/15 min timeouts. Plus the generic guard (process, probe, progress
  // counters) while the world is unloaded. until() swallows errors thrown inside check(), so misses go to its guard.
  let dead = '', misses = 0, nextTrip = Date.now() + GUARD_MS;
  const tripQueued = async (): Promise<boolean | null> => {
    const q = await agent('queues');
    return q.ok ? q.result.some((i: any) => /travel|moving ?van|portal|university|future/i.test(`${i.type} ${i.name}`)) : null;
  };
  const tripGuard = (g: () => Promise<string>) => async () => dead || await g();
  // boarding: the van/portal routes and confirm dialogs happen here; the world flag drops when the old world unloads
  await agent('dialog_expect', { seconds: 60, accept: true }); // travel confirm: accepted by the agent, never cancelled by the autopilot (ts3agent 413f985)
  const boardS = await until(`${name}: left ${fromWorld}`, 600, async () => {
    if (++n % 3 === 0) await dialog(true);
    if ((await get('agentstatus', 5000)).worldLoaded === false) return true;
    const ts = await agent('travel_state');
    if (ts.ok && (ts.result.in_travel === true || ts.result.world !== fromWorld)) return true;
    if (Date.now() >= nextTrip) {
      nextTrip = Date.now() + GUARD_MS;
      misses = (await tripQueued()) === false ? misses + 1 : 0;
      if (misses >= 2) dead = `${name}: trip dropped: no travel interaction in the household queue for ~1 min (still in ${fromWorld}, fail-fast guard)`;
    }
    return false;
  }, 1000, tripGuard(stallGuard(`${name}: boarding`, { port: Number(values.port) })));
  let world = '';
  misses = 0; nextTrip = Date.now() + GUARD_MS;
  const loadS = await until(`${name}: arrived in live mode`, 900, async () => {
    if ((await get('agentstatus', 5000)).worldLoaded !== true) { misses = 0; return false; }
    if (++n % 10 === 0) await dialog(true);
    const ts = await agent('travel_state');
    if (!ts.ok || ts.result.in_travel) { misses = 0; return false; }
    if (ts.result.world === fromWorld) { // loaded, not travelling, still the start world: the switch was called off
      if (Date.now() >= nextTrip) { nextTrip = Date.now() + GUARD_MS; if (++misses >= 2) dead = `${name}: trip aborted: back in live mode in ${fromWorld} for ~1 min without travelling (fail-fast guard)`; }
      return false;
    }
    world = ts.result.world;
    return (await agent('game_state')).result?.sub_state === 'LiveModeState';
  }, 500, tripGuard(stallGuard(`${name}: world load`, { port: Number(values.port) })));
  const after = await snapshot();
  const row = { leg: name, cmd, from: fromWorld, to: world, boardS, loadS, totalS: (Date.now() - t0) / 1000, before, after };
  appendFileSync(outFile, JSON.stringify(row) + '\n');
  const m = (s: any) => s.mem ? `${s.mem.commitMB} MB committed, largest free ${s.mem.largestFreeMB} MB` : 'memory n/a';
  log(`${name}: board ${boardS.toFixed(0)} s + load ${loadS.toFixed(0)} s; ${m(before)} -> ${m(after)}; minisims ${before.travel?.minisims} -> ${after.travel?.minisims}`);
  return world;
}

async function liveHours(hours: number): Promise<void> {
  await agent('autopilot', { enabled: true, speed: 'ultra', dialogs: true });
  const start = (await agent('time')).result;
  if (!start) throw new Error('time: no answer');
  let lastMin = start.minutes_total, movedAt = Date.now();
  for (;;) {
    await dialog();
    await agent('decline_requests');
    await agent('keep_alive', { threshold: -50 });
    const t = (await agent('set_speed', { speed: 'ultra' })).result ?? (await agent('time')).result;
    if (t && t.minutes_total - start.minutes_total >= hours * 60) break;
    // fail fast: at speed 3 the clock moves every check (15 s); no answer or no move for 4 checks (~1 min) = stuck
    if (t && t.minutes_total !== lastMin) { lastMin = t.minutes_total; movedAt = Date.now(); }
    else if (Date.now() - movedAt > 2 * GUARD_MS) throw new Error(`live hours: sim clock ${t ? `stuck at ${t.minutes_total} min (speed ${t.speed})` : 'not answering'} for ~${Math.round((Date.now() - movedAt) / 1000)} s (fail-fast guard)`);
    await sleep(15000);
  }
  log(`${hours} sim hours done`);
}

const st = await agent('game_state');
if (st.result?.sub_state !== 'LiveModeState') throw new Error(`not in live mode: ${JSON.stringify(st)}`);
const home = (await agent('travel_state')).result;
if (!home || home.away) throw new Error(`start from the home world: ${JSON.stringify(home)}`);
log(`home ${home.world} (${home.world_file}), household ${home.household}, minisims ${home.minisims}; log ${outFile}`);
await agent('autopilot', { enabled: true, speed: 'ultra', dialogs: true });
await agent('quiet_ui', { memories_off: true });

const to = values.to!.toLowerCase();
const goCmd = to === 'university' ? 'travel_university' : to === 'future' ? 'travel_future' : 'travel_vacation';
const goArgs: any = to === 'university' ? { days: Number(values.days) } : to === 'future' ? {} : { world: to, days: Number(values.days) };
if (values.sims && goCmd === 'travel_vacation') goArgs.sims = values.sims.split(',').map(s => s.trim());
const prefix = values['save-stats'];
const saves: string[] = [];
if (prefix) { await saveAs(`${prefix}-trip0`); saves.push(`${prefix}-trip0`); }

for (let i = 1; i <= Number(values.trips); i++) {
  const away = await leg(`trip ${i} out`, goCmd, goArgs, home.world);
  await liveHours(Number(values.hours));
  await leg(`trip ${i} back`, 'travel_home', {}, away);
  if (prefix) { await saveAs(`${prefix}-trip${i}`); saves.push(`${prefix}-trip${i}`); }
}
if (saves.length) log(execFileSync(process.execPath, [join(ROOT, 'tools', 'harness', 'save-stats.ts'), ...saves]).toString());
log(`done; ${outFile}`);
