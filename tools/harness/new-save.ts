// Creates a fresh test save from the main menu: load a world, play a premade household, save under a new name.
// Usage: node tools/harness/new-save.ts --world "Bridgeport.world" --save Baseline-BP-fresh [--household Name] [--members 2]
// Without --household it picks the first household with --members Sims (sorted by name, so it is repeatable).
import { parseArgs } from 'node:util';
import { GUARD_MS, stallGuard } from './game-alive.ts';

const { values } = parseArgs({ options: {
  world: { type: 'string' }, save: { type: 'string' }, household: { type: 'string' },
  members: { type: 'string', default: '2' }, port: { type: 'string', default: '47891' },
} });
if (!values.world || !values.save) { console.error('usage: node tools/harness/new-save.ts --world "X.world" --save Name [--household Name]'); process.exit(2); }
const base = `http://127.0.0.1:${values.port}`;
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const log = (m: string) => console.log(`${new Date().toISOString().slice(11, 19)} ${m}`);
async function agent(cmd: string, args?: object): Promise<any> {
  const q = new URLSearchParams({ cmd, timeout: '8000' });
  if (args) q.set('args', JSON.stringify(args));
  return (await fetch(`${base}/agent?${q}`, { signal: AbortSignal.timeout(40000) })).json();
}
// accept: dialogs of the new-game flow this script started (world load, household pick); else the safe button (docs/236 #3)
async function clearDialogs(accept = false) {
  for (let i = 0; i < 8; i++) {
    if (accept) await agent('dialog_expect', { seconds: 60, accept: true }).catch(() => null); // the agent accepts the new-game dialog (413f985); older agents: autopilot off it (03e2bfe)
    const d = await agent('auto_dialog', accept ? { accept: true } : undefined).catch(() => null);
    if (!d?.ok || !d.result.open) return;
    log(`dialog "${String(d.result.title).slice(0, 60)}" -> ${d.result.clicked}`);
    await sleep(1500);
  }
}
// guard: fail-fast check (game-alive.ts stallGuard), run outside check() so its verdict is not swallowed
async function until(what: string, limitS: number, check: () => Promise<boolean>, guard = stallGuard(what, { port: Number(values.port) })) {
  const t0 = Date.now();
  while (!(await check().catch(() => false))) {
    if (Date.now() - t0 > limitS * 1000) throw new Error(`${what}: not reached within ${limitS} s`);
    const why = await guard();
    if (why) throw new Error(why);
    await clearDialogs(true);
    await sleep(3000);
  }
  log(`${what} after ${Math.round((Date.now() - t0) / 1000)} s`);
}

const s0 = await agent('game_state');
if (s0.result?.in_world) throw new Error('already in a world; restart to the main menu first (cycle.ts --restart)');
// "Mod Scripts Found" opens at the menu up to ~90 s after start; a load requested under it is lost (see cycle.ts).
{
  const t0 = Date.now();
  let saw = false, quietSince = Date.now();
  const guard = stallGuard('main menu', { port: Number(values.port) });
  while (Date.now() - t0 < 150000 && (!saw ? Date.now() - t0 < 120000 : Date.now() - quietSince < 5000)) {
    const why = await guard();
    if (why) throw new Error(why);
    const d = await agent('auto_dialog').catch(() => null);
    if (d?.ok && d.result.open) { log(`menu dialog "${d.result.title}" -> ${d.result.clicked}`); saw ||= /mod scripts/i.test(d.result.title ?? ''); quietSince = Date.now(); }
    await sleep(2000);
  }
}
const r = await agent('load_world', { file: values.world, state: 'LiveMode' });
if (!r.ok) throw new Error(`load_world: ${r.error}`);
await until('world loaded', 900, async () => (await agent('game_state')).result?.in_world === true);

const hh = (await agent('world_households')).result as { name: string; members: number; lot: string }[];
const pick = values.household
  ? hh.find(h => h.name.toLowerCase() === values.household!.toLowerCase())
  : hh.filter(h => h.members === Number(values.members)).sort((a, b) => a.name.localeCompare(b.name))[0];
if (!pick) throw new Error(`no matching household; available: ${hh.map(h => `${h.name} (${h.members})`).join(', ')}`);
log(`playing ${pick.name} (${pick.members} Sims) at ${pick.lot}`);
await agent('play_household', { name: pick.name });
await until('household active', 180, async () => (await agent('game_state')).result?.active_household === pick.name);
await until('live mode', 180, async () => (await agent('game_state')).result?.sub_state === 'LiveModeState');
await clearDialogs();

await agent('set_speed', { speed: 'pause' });
const saved = await agent('save_game', { name: values.save });
if (!saved.ok) throw new Error(`save_game: ${saved.error}`);
// Fail fast (Rodney 2026-10-16): the agent answers, no save in progress and no async_log line, twice ~30 s apart = the
// save never ran or ended without its line. The generic guard cannot see that: the heartbeat moves while paused.
const saveGuard = stallGuard('save', { port: Number(values.port) });
let idle = 0, idleAt = Date.now() + GUARD_MS;
await until('saved', 240, async () => {
  const st = await agent('game_state');
  const done = st.result?.save_in_progress === false && (await agent('async_log')).result.some((l: string) => l.includes(`save ${values.save} -> `));
  if (!done && Date.now() >= idleAt) { idleAt = Date.now() + GUARD_MS; idle = st.ok && st.result?.save_in_progress === false ? idle + 1 : 0; }
  return done;
}, async () => (await saveGuard()) || (idle >= 2 ? `save ${values.save}: not in progress and no async_log line for ~${2 * GUARD_MS / 1000} s (fail-fast guard)` : ''));
log(`created ${values.save}.sims3 in ${values.world} with the ${pick.name} household`);
