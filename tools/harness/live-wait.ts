// Load a world from the main menu and wait for live mode before a profile window samples (profile-world.ts --load-world,
// queue.ts run.profile.loadWorld). A menu-start job (save "menu") reaches profile-world.ts at the main menu; without this the
// window refused with "not in live mode" and a generated test world (no save, no household) had no frame times.
//   node tools/harness/live-wait.ts --self-check
// The live check is the one the SeaLocal frames repro polls (repros.json atlas-sealocal-frames-8192): game_state in_world true
// and sub_state LiveModeState, every 2 s, with a timeout.
import assert from 'node:assert/strict';

type Agent = (cmd: string, args?: object, timeoutMs?: number) => Promise<any>;
type Sleep = (ms: number) => Promise<unknown>;

export const isLive = (r: any): boolean => !!(r?.ok && r.result?.in_world === true && r.result?.sub_state === 'LiveModeState');

// polls game_state until isLive; throws "not in live mode after <s> s: <last reply>" past maxS
export async function waitLive(ag: Agent, maxS: number, sleep: Sleep, everyS = 2, now = Date.now): Promise<any> {
  const t0 = now();
  let last: any;
  while (!isLive(last = await ag('game_state'))) {
    if (now() - t0 > maxS * 1000) throw new Error(`not in live mode after ${maxS} s: ${JSON.stringify(last).slice(0, 300)}`);
    await sleep(everyS * 1000);
  }
  return last;
}

// the new-game flow of the menu-start repros (atlas-8192-load-thing-8192 steps 1-4): accept the Mod Scripts Found dialog when it
// shows (at most dialogS; no dialog is fine), let the autopilot accept the new-game dialogs, load the world in live mode, then
// waitLive. Returns the live game_state reply.
export async function loadWorldLive(ag: Agent, world: string, sleep: Sleep, o: { dialogS?: number; loadS?: number; log?: (m: string) => void } = {}, now = Date.now): Promise<any> {
  if (!/\.world$/i.test(world) || world.includes('"')) throw new Error(`--load-world ${world}: a .world file name`);
  const log = o.log ?? (() => {}), t0 = now();
  while (now() - t0 < (o.dialogS ?? 120) * 1000) {
    const d = await ag('auto_dialog', { accept: true });
    if (d?.ok && d.result?.title) { log(`dialog "${d.result.title}" -> ${d.result.clicked ?? 'accepted'}`); break; }
    await sleep(2000);
  }
  await ag('dialog_expect', { seconds: 600, accept_all: true, all_seconds: 600 });
  const r = await ag('load_world', { file: world, state: 'LiveMode' }, 900000);
  if (!r?.ok) throw new Error(`load_world ${world}: ${JSON.stringify(r).slice(0, 300)}`);
  log(`load_world ${world}: asked, waiting for live mode`);
  return waitLive(ag, o.loadS ?? 600, sleep, 5, now);
}

export async function selfCheck(): Promise<void> {
  assert.ok(isLive({ ok: true, result: { in_world: true, sub_state: 'LiveModeState' } }));
  assert.ok(!isLive({ ok: true, result: { in_world: true, sub_state: 'EditTownState' } }), 'Edit Town is not live');
  assert.ok(!isLive({ ok: true, result: { in_world: false, sub_state: 'LiveModeState' } }), 'not in a world');
  assert.ok(!isLive({ ok: false, error: 'timeout' }) && !isLive(null));
  // a fake clock: each sleep moves it; game_state answers from a script of replies
  const fake = (replies: any[]) => { let t = 0, i = 0; const calls: string[] = [];
    return { now: () => t, sleep: async (ms: number) => { t += ms; }, calls,
      ag: async (cmd: string, args?: any) => { calls.push(cmd + (args?.file ? ' ' + args.file : ''));
        if (cmd === 'game_state') return replies[Math.min(i++, replies.length - 1)];
        if (cmd === 'auto_dialog') return { ok: true, result: t >= 4000 ? { title: 'Mod Scripts Found', clicked: 'OK' } : {} };
        return { ok: true, result: {} }; } }; };
  const live = { ok: true, result: { in_world: true, sub_state: 'LiveModeState' } }, menu = { ok: true, result: { in_world: false, sub_state: 'MainMenuState' } };
  { const f = fake([menu, menu, live]); assert.deepEqual(await waitLive(f.ag, 60, f.sleep, 2, f.now), live); assert.equal(f.now(), 4000, 'two 2 s polls before live'); }
  { const f = fake([menu]); await assert.rejects(waitLive(f.ag, 10, f.sleep, 2, f.now), /not in live mode after 10 s: .*MainMenuState/); }
  { const f = fake([menu, { ok: true, result: { in_world: true, sub_state: 'PlayFlowState' } }, live]);
    const r = await loadWorldLive(f.ag, 'AtlasStripSet.world', f.sleep, { dialogS: 30, loadS: 60 }, f.now);
    assert.deepEqual(r, live);
    assert.deepEqual(f.calls.filter(c => c !== 'auto_dialog'), ['dialog_expect', 'load_world AtlasStripSet.world', 'game_state', 'game_state', 'game_state'], 'dialog, load, then live polls'); }
  { const f = fake([menu]); f.ag = (async (cmd: string) => cmd === 'auto_dialog' ? { ok: true, result: {} } : cmd === 'load_world' ? { ok: false, error: 'no such world' } : { ok: true, result: {} }) as any;
    await assert.rejects(loadWorldLive(f.ag, 'Missing.world', f.sleep, { dialogS: 4 }, f.now), /load_world Missing\.world: .*no such world/); }
  await assert.rejects(loadWorldLive(async () => ({ ok: true }), 'x.sims3', async () => {}), /a \.world file name/);
  console.log('live-wait self-check ok');
}

if (process.argv[1]?.endsWith('live-wait.ts') && process.argv.includes('--self-check')) await selfCheck();
