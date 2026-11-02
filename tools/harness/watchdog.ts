// Watches a running harness session and diagnoses it every --every minutes (default 5):
// game process, probe, world/live mode, sim clock progress, dialogs, address space, sim-thread stack.
// Tries to recover by itself (clear dialogs, leave Edit Town); exits non-zero with a diagnosis when the run
// is stuck or the game is gone, so whoever started it in the background gets notified.
// Only in a loaded world does a frozen clock count; the main menu and loading screens are skipped.
// Usage: node tools/harness/watchdog.ts [--every 1] [--port 47891] [--min-free-mb 300]
import { execFileSync } from 'node:child_process';
import { appendFileSync, existsSync, mkdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { mayBeStuck } from './stuck-rule.ts';
import { sampleGame, stallStep } from './game-alive.ts';
import { GAME_EXE } from './game-exe.ts';

const { values } = parseArgs({ options: {
  every: { type: 'string', default: '1' }, port: { type: 'string', default: '47891' }, 'min-free-mb': { type: 'string', default: '300' },
} });
const ROOT = resolve(import.meta.dirname, '..', '..');
const LOG = join(ROOT, 'probe-out', 'watchdog.log');
const SAVING = join(ROOT, 'probe-out', 'saving'); // written by age-save.ts while a save runs
const SAVE_GRACE_MS = 3 * 60000;
function savingFor(): number | null {
  try { return existsSync(SAVING) ? Date.now() - JSON.parse(readFileSync(SAVING, 'utf8')).started : null; } catch { return null; }
}
mkdirSync(join(ROOT, 'probe-out'), { recursive: true });
const base = `http://127.0.0.1:${values.port}`;
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const stamp = () => new Date().toISOString().replace('T', ' ').slice(0, 19);
const log = (m: string) => { const line = `${stamp()} ${m}`; console.log(line); appendFileSync(LOG, line + '\n'); };

async function get(path: string, timeoutMs = 30000): Promise<any> {
  const r = await fetch(`${base}/${path}`, { signal: AbortSignal.timeout(timeoutMs) });
  return r.json();
}
async function agent(cmd: string, args?: object): Promise<any> {
  const q = new URLSearchParams({ cmd, timeout: '8000' });
  if (args) q.set('args', JSON.stringify(args));
  return get(`agent?${q}`);
}
// Windows marks a window "Not Responding" when its UI thread stops pumping messages for ~5 s.
function windowStatus(): string {
  const csv = execFileSync('tasklist', ['/V', '/FO', 'CSV', '/NH', '/FI', `IMAGENAME eq ${GAME_EXE}`]).toString().trim();
  const cols = csv.split('","');
  return cols.length > 6 ? cols[5] : 'unknown';
}
const STUCK_FILE = join(ROOT, 'probe-out', 'stuck-sims.jsonl');
const EXPECTED_SIM_MIN_PER_MIN: Record<string, number> = { Triple: 460, Double: 150, Normal: 25 }; // measured at speed 3: ~0.32 sim days per real minute
const simSeen = new Map<string, { key: string; checks: number; reported: boolean }>();
let lastHeartbeat: number | null = null;
let lastCheckAt = Date.now();

// Gameplay-level stuck Sims: same spot and same interaction (or routing without moving) across several checks.
async function checkSims(): Promise<void> {
  const r = await agent('sims_activity').catch(() => null);
  if (!r?.ok) return; // older agent without the command
  const now = new Set<string>();
  for (const s of r.result) {
    now.add(s.id);
    const key = `${s.x},${s.z},${s.interaction},${s.routing}`;
    const prev = simSeen.get(s.id);
    const entry = prev && prev.key === key ? { key, checks: prev.checks + 1, reported: prev.reported } : { key, checks: 1, reported: false };
    simSeen.set(s.id, entry);
    const limit = s.routing ? 3 : 6; // routing without moving for ~1 sim day, or one activity for ~2 sim days
    if (!entry.reported && mayBeStuck(s.interaction, !!s.routing, !!s.in_transit) && entry.checks >= limit) {
      entry.reported = true;
      log(`STUCK SIM? ${s.name} on "${s.interaction}" at (${s.x}, ${s.z}) ${s.lot ?? ''}${s.routing ? ' while routing' : ''} for ${entry.checks} checks (queue ${s.queue})`);
      appendFileSync(STUCK_FILE, JSON.stringify({ time: stamp(), checks: entry.checks, ...s }) + '\n');
    }
  }
  for (const id of [...simSeen.keys()]) if (!now.has(id)) simSeen.delete(id);
}

const gameRunning = () => execFileSync('tasklist', ['/NH', '/FI', `IMAGENAME eq ${GAME_EXE}`]).toString().includes(GAME_EXE);

async function simStack(): Promise<string> {
  try {
    const r = await get('simstack?frames=12', 30000); // sim thread only; never all threads (that once crashed the game)
    return r.stack?.length ? r.stack.map((f: string) => f.split(' ').slice(1).join(' ')).slice(0, 8).join(' < ') : 'sim thread not found';
  } catch (e) { return `stack unavailable: ${(e as Error).message}`; }
}

function fail(reason: string, details: string[]): never {
  log(`STUCK: ${reason}`);
  for (const d of details) log(`  ${d}`);
  // a crash leaves an xcpt report: show which change set it ran under
  if (!gameRunning()) log(execFileSync(process.execPath, [join(import.meta.dirname, 'crash-ledger.ts'), 'triage']).toString().trim());
  process.exit(1);
}

let lastMinutes: number | null = null;
let stalledChecks = 0;
const outside = { last: null as string | null, misses: 0 }; // fail-fast state for checks out of a world
log(`watchdog started, checking every ${values.every} min`);
for (;;) {
  await sleep(Number(values.every) * 60000);
  const realMin = (Date.now() - lastCheckAt) / 60000;
  lastCheckAt = Date.now();
  if (!gameRunning()) fail('game process is gone', ['look for an xcpt *.txt crash report in Documents\\Electronic Arts\\The Sims 3']);
  const status = await get('status', 30000).catch(e => ({ error: (e as Error).message }));
  if ((status as any).error) fail('probe not answering', [(status as any).error]);

  const state = await agent('game_state').catch(e => ({ ok: false, error: (e as Error).message }));
  const time = await agent('time').catch(e => ({ ok: false, error: (e as Error).message }));
  const mem = await get('memory', 60000).catch(() => null);
  const dialog = await agent('pending_dialogs').catch(() => null);
  const summary = [
    `state: ${state.ok ? `in_world=${state.result.in_world} sub=${state.result.sub_state} household=${state.result.active_household}` : state.error}`,
    `clock: ${time.ok ? `day ${time.result.day_number} ${time.result.clock} ${time.result.speed}` : time.error}`,
    `memory: ${mem ? `${mem.commitMB} MB committed, largest free ${mem.largestFreeMB} MB` : 'unavailable'}`,
    `dialog: ${dialog?.ok ? (dialog.result.open ? JSON.stringify(dialog.result.dialog.text?.[0] ?? '').slice(0, 80) : 'none') : 'unavailable'}`,
  ];

  if (!(state.ok && state.result.in_world)) { // menu, loading, or the agent not answering: no clock to judge
    // fail fast: two checks in a row with nothing moving (heartbeat, world flag, committed MB, ts3native.log) = hung,
    // not loading; before, this branch logged "not in a world yet" forever while a hung game sat at the soak deadline
    const why = stallStep(outside, await sampleGame(Number(values.port)));
    log(`not in a world yet (main menu or loading)${outside.misses ? ` and nothing moved (${outside.misses})` : ''} | ${summary.join(' | ')}`);
    if (why) fail(`out of the world and ${why} for ${outside.misses} checks`, [...summary, `sim thread: ${await simStack()}`]);
    lastMinutes = null;
    stalledChecks = 0;
    continue;
  }
  outside.last = null; outside.misses = 0;
  const saveAge = savingFor();
  if ((saveAge !== null && saveAge < SAVE_GRACE_MS) || state.result.save_in_progress) { // a save pauses the clock
    log(`saving (${saveAge !== null ? Math.round(saveAge / 1000) + ' s' : 'in progress'}); stall rule paused for up to 3 min | ${summary.join(' | ')}`);
    lastMinutes = null;
    stalledChecks = 0;
    continue;
  }
  const minutes = time.ok ? time.result.minutes_total : null;
  const prevMinutes = lastMinutes;
  const moved = minutes !== null && lastMinutes !== null && minutes > lastMinutes;
  if (minutes !== null && lastMinutes !== null && !moved) stalledChecks++; else stalledChecks = 0;
  lastMinutes = minutes ?? lastMinutes;
  log(`ok? ${stalledChecks === 0 ? 'yes' : `clock stalled (${stalledChecks})`} | ${summary.join(' | ')}`);

  if (mem && mem.largestFreeMB < Number(values['min-free-mb'])) log(`WARNING: largest free block ${mem.largestFreeMB} MB; out-of-address-space crash risk`);

  const win = windowStatus();
  if (/not responding/i.test(win)) log(`WARNING: game window is "Not Responding" (UI thread hung); sim thread: ${await simStack()}`);

  // The agent's mailbox heartbeat goes up once per script tick; frozen while unpaused = script/sim thread blocked.
  const hb = await get('agentstatus', 30000).catch(() => null);
  const paused = time.ok && /pause/i.test(time.result.speed);
  if (hb?.heartbeat !== undefined) {
    if (lastHeartbeat !== null && hb.heartbeat === lastHeartbeat && !paused)
      fail('script ticks stopped (agent heartbeat frozen) while the game is not paused', [...summary, `window: ${win}`, `sim thread: ${await simStack()}`]);
    lastHeartbeat = hb.heartbeat;
  }

  // Sim pace: a moving but crawling clock is sim lag, the stutter we are hunting; log it, don't fail.
  if (moved && time.ok && EXPECTED_SIM_MIN_PER_MIN[time.result.speed]) {
    const pace = (minutes! - (prevMinutes ?? minutes!)) / Math.max(realMin, 0.01);
    const pct = Math.round(100 * pace / EXPECTED_SIM_MIN_PER_MIN[time.result.speed]);
    if (pct < 40) log(`SLOW SIM: ${Math.round(pace)} sim-min per real minute at ${time.result.speed} (${pct}% of normal); sim thread: ${await simStack()}`);
  }
  await checkSims();

  if (stalledChecks > 0) {
    // recover: blocking dialog, Edit Town or another non-live mode, paused speed
    const d = await agent('auto_dialog').catch(() => null);
    if (d?.ok && d.result.open) log(`recovery: dialog "${d.result.title}" -> ${d.result.clicked}`);
    if (state.ok && state.result.in_world && state.result.sub_state !== 'LiveModeState') {
      log(`recovery: in ${state.result.sub_state}, entering live mode`);
      await agent('enter_live_mode').catch(() => null);
    }
    await sleep(20000); // give the recovery a moment, then judge
    const again = await agent('time').catch(() => null);
    if (again?.ok && again.result.minutes_total > (minutes ?? 0)) {
      log(`recovered: clock at day ${again.result.day_number} ${again.result.clock}`);
      lastMinutes = again.result.minutes_total;
      stalledChecks = 0;
    } else {
      fail(`sim clock has not moved for ${values.every} min in a loaded world`, [...summary, `sim thread: ${await simStack()}`]);
    }
  }
}
