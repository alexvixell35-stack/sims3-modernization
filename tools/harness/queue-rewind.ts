// Rewind reuse in queue.ts (docs/270 §30, Rodney 2026-11-16: "use the tool whenever we can"). Consecutive jobs with the same game
// start (queue.ts keyOf: asi + sha, ini, fixes, save, content, configs, pin) whose run starts from the loaded world share one game:
// after the first job's load the rewind tool (tools/harness/rewind, `rewind-spike serve`) takes a snapshot, and each later job of
// the group starts from a rewind to it instead of quit + relaunch + load (fresh start ~2 min on BP, rewind ~2-2.5 s). Only what a
// job changes at runtime may differ inside a group (profile.ab / fixesOn / fixesOff / native_set steps / repro): a different asi,
// ini value, package, script DLL, config or save is a different key and relaunches. The rewind puts memory, counters, switches and
// the sim clock back to the snapshot, so every job reads its own /native and patches baselines after it, as after a fresh load.
//   rewindBlock(job)       '' or why a job cannot start from a rewind
//   rewindGroups(...)      runs of >= 2 consecutive eligible jobs with equal start keys (Map job -> group number)
//   rewindSession(pid, io) one serve process per load: snapshot() after the load, rewind() before each later job; the probe's Frida
//                          agent is out of the game during every op (docs/270 §24), the route worker parks first (§21)
//   node tools/harness/queue-rewind.ts state <queue.json>   prints the Bin state a chain sets for the queue: apex-rewind or apex
//   node tools/harness/queue-rewind.ts --self-check
// Needs Bin state apex-rewind (the patched bridge pair, rewindMode = True) and ts3native [Rewind] Park=1 Offline=1 (REWIND_INI,
// added to every grouped job; ProbeSnap.CallSites off as in the rewind queues). Anything missing: the job starts fresh, logged.
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { EXE, fridaLoaded, MODES, parseRow, REWIND_ON, serve, type Row } from './rewind-smoke.ts';
import type { Switches } from './session-reuse.ts';

export type RewindMode = 'auto' | 'off';
// shortcut: off until the pilot job proves rewind in a real queue (docs/270 §30.4), then 'auto' as the default for every queue
export const REWIND_DEFAULT: RewindMode = 'off';
export const parseRewindMode = (v: unknown, at = 'rewind'): RewindMode => {
  if (v === undefined) return REWIND_DEFAULT;
  if (v === 'auto' || v === 'off') return v;
  throw new Error(`${at} must be "auto" or "off"`);
};
// queue-level "rewind" (TS3_REWIND=auto|off overrides it: dry-run census of old queues, a chain forcing it off)
export const queueRewind = (queueJson: any, env: Record<string, string | undefined> = process.env): RewindMode =>
  parseRewindMode(env.TS3_REWIND || (Array.isArray(queueJson) ? undefined : queueJson?.rewind), env.TS3_REWIND ? 'TS3_REWIND' : 'queue rewind');
export const REWIND_INI = { 'Rewind.Park': 1, 'Rewind.Offline': 1 } as const;

// '' when a job may start from a rewind (its run plays from the loaded world and ends in it), else why not
export function rewindBlock(j: any, vanilla = false): string {
  if (j.rewind === false) return 'rewind: false';
  if (vanilla || j.ourMods === false) return 'no ts3native ([Rewind] Park needs it)';
  if (j.d3d9Dll) return 'd3d9Dll: not the bridge';
  if (j.callSites === true) return 'callSites: true';
  if (Number(j.ini?.['ProbeSnap.CallSites'])) return 'ProbeSnap.CallSites on (int3 sites)';
  if (!j.save || j.save === 'menu') return 'no save to share';
  const p = j.run?.profile;
  if (p) {
    if (p.travel) return 'profile.travel changes the world';
    if (p.story) return 'profile.story plays a scratch save copy';
    // one kept frame-times session per load (--frames-shared) stays in the game: the tool's frida-agent guard would refuse
    if (p.repros) return 'profile.repros keeps a frames session in the game';
    return '';
  }
  if (j.run?.repro) return '';
  return `run.${Object.keys(j.run ?? {})[0]}: only profile and repro runs start from a rewind (others restart, save, travel or quit)`;
}

// group number per job for runs of >= 2 consecutive jobs that may rewind and share the start key; others are not in the map
export function rewindGroups<J>(jobs: J[], on: (j: J) => boolean, block: (j: J) => string, key: (j: J) => string): Map<J, number> {
  const out = new Map<J, number>();
  let g = 0;
  for (let i = 0; i < jobs.length;) {
    let e = i + 1;
    if (on(jobs[i]) && !block(jobs[i])) { const k = key(jobs[i]); while (e < jobs.length && on(jobs[e]) && !block(jobs[e]) && key(jobs[e]) === k) e++; }
    if (e - i >= 2) { g++; for (let x = i; x < e; x++) out.set(jobs[x], g); }
    i = e;
  }
  return out;
}

// real ts3native.log lines (rewind-bridge-1115 bp-rewind-bridge-apply, 2026-11-15): the asi has [Rewind] and the ini turned it on
export const PARK_ON = /^\[Rewind\] Park on: /m, OFFLINE_ON = /^\[Rewind\] Offline on: /m;
// '' when a snapshot may be taken in this game, else why not (the job then runs without one and the next job starts fresh)
export function rewindReady(x: { exe: boolean; conf: string | null; bridgeRuns: boolean; log: string }): string {
  if (!x.exe) return 'rewind-spike.exe missing (tools\\harness\\rewind\\build.cmd)';
  if (x.conf === null || !REWIND_ON.test(x.conf)) return 'Bin state is not apex-rewind (no rewindMode = True in Bin\\bridge.conf)';
  if (!x.bridgeRuns) return 'NvRemixBridge.exe does not run';
  if (!PARK_ON.test(x.log)) return 'no "[Rewind] Park on" line in ts3native.log (asi without [Rewind]?)';
  if (!OFFLINE_ON.test(x.log)) return 'no "[Rewind] Offline on" line in ts3native.log';
  return '';
}

// the agent's game speed names (time / game_state "speed") as set_speed arguments
const SPEED: Record<string, string> = { Pause: 'pause', Normal: 'normal', Double: 'fast', Triple: 'ultra' };
export const speedArg = (s: unknown) => SPEED[String(s)] ?? 'normal';
export type Io = { agent: (cmd: string, args?: object) => Promise<any>; get: (path: string, timeoutMs?: number) => Promise<any>; log: (m: string) => void };
// code 0 = done and verified; 2 = refused before any change (game as it was); 3 = failed after a change or the game does not answer
export type Op = { code: number; why: string[]; ms: number; row?: Row };
// a rewind counts only with code 0 and, when the tool reports it, the memory hash equal to the snapshot's
export const rewindOk = (r: Row) => r.code === 0 && (r.stats?.hashEqual === undefined || r.stats.hashEqual === 1);
// switches (session-reuse.ts readSwitches) read after a rewind that differ from the read after the load; a switch the second read
// did not return is not counted (a read error is not a rewind error; the memory verify already covers the bytes)
export const switchDiff = (a: Switches, b: Switches): string[] => [
  ...Object.entries(a.patches).filter(([k, v]) => k in b.patches && b.patches[k] !== v).map(([k, v]) => `${k} ${v} -> ${b.patches[k]}`),
  ...Object.entries(a.native).filter(([k, v]) => k in b.native && b.native[k] !== v).map(([k, v]) => `${k} ${v} -> ${b.native[k]}`)];
// wall time: a fresh start (cycle.ts --restart, launch, load) measured ~2 min on BP (more on IP and big saves); a rewind op with
// park, pause, probe detach/attach and the switch read-back ~8 s; one snapshot per group about the same
export const FRESH_EST_MS = 120000, REWIND_EST_MS = 8000;
const sum = (x: number[]) => x.reduce((a, b) => a + b, 0);
// dry run (plan) or end of a run (with rewinds/fresh: what happened, saved against this run's own fresh starts when it had any)
export function rewindSummary(x: { groups: number; members: number; jobs: number; rewinds?: number[]; fresh?: number[]; refused?: number; failed?: number }): string {
  const planned = x.members - x.groups;
  if (!x.rewinds) return `${planned} of ${x.jobs} jobs start from a rewind (${x.groups} groups); est. saving ~${Math.round((planned * (FRESH_EST_MS - REWIND_EST_MS) - x.groups * REWIND_EST_MS) / 60000)} min (fresh start ${FRESH_EST_MS / 1000} s, rewind ~${REWIND_EST_MS / 1000} s)`;
  const fresh = x.fresh ?? [], est = fresh.length ? sum(fresh) / fresh.length : FRESH_EST_MS, n = x.rewinds.length;
  return `rewind: ${n} of ${planned} planned jobs started from a rewind${n ? ` (${(sum(x.rewinds) / n / 1000).toFixed(1)} s each)` : ''}, ${x.refused ?? 0} refused, ${x.failed ?? 0} failed (fresh load instead); `
    + `~${Math.round((n * est - sum(x.rewinds)) / 60000)} min saved vs fresh starts (${Math.round(est / 1000)} s each: ${fresh.length ? `mean of this run's ${fresh.length} fresh starts` : 'estimate'})`;
}
export const startLine = (op: Op | null) => !op ? 'fresh' : op.code === 0 ? `rewind (${op.ms} ms)`
  : `${op.code === 2 ? 'rewind refused' : 'rewind failed'} -> fresh (${op.why.join('; ').slice(0, 200)})`;

export function rewindSession(pid: number, io: Io, dep = { serve, loaded: fridaLoaded, settleMs: 1000 }) {
  const s = dep.serve(pid, MODES['bridge-apply'].flags);
  let speed = 'normal', ap: string | undefined; // game speed and the autopilot's speed target before the snapshot (set_speed changes the target)
  const op = async (c: 'snapshot' | 'rewind'): Promise<Op> => {
    const t0 = Date.now(), why: string[] = [];
    if (c === 'snapshot') { speed = speedArg((await io.agent('time'))?.result?.speed); ap = (await io.agent('autopilot'))?.result?.speed; }
    const p = await s.cmd('park', 30); // before the pause: a route job waits for script ticks (docs/270 §21); held until the op ends
    if (p.code) why.push(`park: ${p.why.join('; ')}`);
    const ps = await io.agent('set_speed', { speed: 'pause' });
    if (!ps?.ok) { await s.cmd('plan', 300); return { code: 2, why: [...why, `pause: ${ps?.error ?? 'no answer'}`], ms: Date.now() - t0 }; } // plan ends the park hold
    await new Promise(r => setTimeout(r, dep.settleMs));
    const d = await io.get('fridadetach', 60000);
    let n = 0;
    for (; n < 150 && dep.loaded(pid); n++) await new Promise(r => setTimeout(r, 200));
    io.log(`rewind: probe agent out (${d?.detached ? `unloaded after ${n * 0.2} s` : `detach: ${d?.error ?? 'no answer'}`})`);
    const row = await s.cmd(c, 300); // the tool's frida-agent module guard refuses when an agent is still in the game
    const a = await io.get('fridaattach', 60000);
    const ok = c === 'snapshot' ? row.code === 0 : rewindOk(row);
    if (row.code === 0 && !ok) row.why.push('memory hash differs from the snapshot');
    const code = ok ? 0 : row.code === 2 ? 2 : 3;
    if (!a?.attached) return { code: 3, why: [...row.why, `probe attach: ${a?.error ?? 'no answer'}`], ms: Date.now() - t0, row };
    if (code !== 3) {
      const sp = await io.agent('set_speed', { speed });
      if (sp?.ok && typeof ap === 'string') await io.agent('autopilot', { speed: ap });
      const st = await io.agent('game_state');
      if (!sp?.ok || !st?.ok || !st.result?.in_world) return { code: 3, why: [...row.why, `game does not answer after the ${c} (${sp?.error ?? st?.error ?? 'not in world'})`], ms: Date.now() - t0, row };
    }
    return { code, why: [...(code ? why : []), ...row.why], ms: Date.now() - t0, row };
  };
  return { snapshot: () => op('snapshot'), rewind: () => op('rewind'), quit: () => s.quit() };
}

async function selfCheck() {
  // grouping: equal keys, eligible, consecutive, >= 2
  const J = (id: string, k: string, x: any = {}) => ({ id, k, save: 'Baseline-BP-Spring', run: { profile: { minutes: 10 } }, ...x });
  const js = [J('a', 'A'), J('b', 'A'), J('c', 'A', { rewind: false }), J('d', 'A'), J('e', 'B'), J('f', 'B', { run: { repro: 'r' } }), J('g', 'B', { run: { script: ['x.ts'] } }), J('h', 'B')];
  const gs = rewindGroups(js, () => true, j => rewindBlock(j), j => j.k);
  assert.deepEqual(js.map(j => gs.get(j) ?? 0), [1, 1, 0, 0, 2, 2, 0, 0], 'a+b, e+f; c opts out, d and h alone, g a script');
  assert.equal(rewindGroups(js, () => false, j => rewindBlock(j), j => j.k).size, 0, 'rewind off: no groups');
  assert.equal(rewindGroups([J('a', 'A')], () => true, () => '', j => j.k).size, 0, 'one job: nothing to share');
  for (const [x, re] of [[{ save: 'menu' }, /no save/], [{ ourMods: false }, /ts3native/], [{ d3d9Dll: 'x.dll' }, /bridge/], [{ callSites: true }, /callSites/], [{ ini: { 'ProbeSnap.CallSites': 1 } }, /int3/],
    [{ run: { profile: { minutes: 5, travel: 'egypt' } } }, /travel/], [{ run: { profile: { minutes: 5, repros: ['a'] } } }, /frames/], [{ run: { matrix: [] } }, /run\.matrix/],
    [{ run: { saveReload: [] } }, /run\.saveReload/], [{ run: { age: {} } }, /run\.age/], [{ run: { profile: { minutes: 5, story: {} } } }, /story/]] as const)
    assert.match(rewindBlock(J('x', 'A', x)), re, JSON.stringify(x));
  assert.match(rewindBlock(J('x', 'A'), true), /ts3native/, 'vanilla');
  assert.equal(rewindBlock(J('x', 'A', { run: { repro: 'r' } })), '');
  // modes
  assert.equal(parseRewindMode(undefined), REWIND_DEFAULT);
  assert.throws(() => parseRewindMode('on'), /auto/);
  assert.equal(queueRewind({ rewind: 'auto', jobs: [] }, {}), 'auto');
  assert.equal(queueRewind({ rewind: 'auto', jobs: [] }, { TS3_REWIND: 'off' }), 'off');
  assert.equal(queueRewind([], { TS3_REWIND: 'auto' }), 'auto');
  // readiness, on the real lines (rewind-bridge-1115 bp-rewind-bridge-apply ts3native.log, Bin bridge.conf of the install copy)
  const park = '[Rewind] Park on: the route worker parks in its idle wait when Local\\rewind-ts3n-59840 asks';
  const off = '[Rewind] Offline on: 18 wininet imports of TS3W.exe turned away, 1 left (InternetCrackUrlA only expected); TSLHost.dll not loaded (no internet session, no wininet pool work for EA online code)';
  const logOk = `ts3native 0.36.261-lanes1 2026-11-15 09:44:15\r\n${park}\r\n${off}\r\n`, conf = 'serverChannelMemSize = 72MB\r\n\r\nrewindMode = True\r\n';
  assert.equal(rewindReady({ exe: true, conf, bridgeRuns: true, log: logOk }), '');
  assert.match(rewindReady({ exe: false, conf, bridgeRuns: true, log: logOk }), /build\.cmd/);
  assert.match(rewindReady({ exe: true, conf: 'serverChannelMemSize = 72MB\n# rewindMode = True\n', bridgeRuns: true, log: logOk }), /apex-rewind/);
  assert.match(rewindReady({ exe: true, conf: null, bridgeRuns: true, log: logOk }), /apex-rewind/);
  assert.match(rewindReady({ exe: true, conf, bridgeRuns: false, log: logOk }), /NvRemixBridge/);
  assert.match(rewindReady({ exe: true, conf, bridgeRuns: true, log: off }), /Park on/);
  assert.match(rewindReady({ exe: true, conf, bridgeRuns: true, log: park }), /Offline on/);
  assert.ok(!PARK_ON.test('FAIL [Rewind] Park: mapping Local\\rewind-ts3n-1 (err 5)') && !OFFLINE_ON.test('; [Rewind] Offline on: comment'), 'non-matches');
  // result rows (real serve lines of the same job, 2026-11-15 20:30) and what the queue does with each code
  const good = parseRow('result {"op":"rewind","code":0,"why":[],"ms":{"park":110,"freeze":25,"plan":38.7,"handles":0.1,"map":2,"memory":498.7,"writeWatch":255,"threads":2,"verify":903.3,"resume":0.9},"stats":{"hashEqual":1}}')!;
  const refused = parseRow('result {"op":"rewind","code":2,"why":["thread 39152 (start 77e34e70) exited after the snapshot"],"ms":{"park":109.8,"freeze":7.4,"plan":35.7},"stats":{}}')!;
  const ended = parseRow('result {"op":"rewind","code":3,"why":["no clear answer to the rewind op: the bridge server state is unknown (target ended)"],"ms":{},"stats":{}}')!;
  assert.deepEqual([rewindOk(good), rewindOk(refused), rewindOk(ended), rewindOk({ ...good, stats: { hashEqual: 0 } })], [true, false, false, false]);
  const sw = { patches: { unseenpark: true, jit: false }, native: { routeworker: 1, kduncutceil: 0 } };
  assert.deepEqual(switchDiff(sw, sw), []);
  assert.deepEqual(switchDiff(sw, { patches: { unseenpark: false }, native: { routeworker: 1, kduncutceil: 1 } }), ['unseenpark true -> false', 'kduncutceil 0 -> 1'], 'missing reads not counted');
  assert.equal(rewindSummary({ groups: 2, members: 7, jobs: 9 }), '5 of 9 jobs start from a rewind (2 groups); est. saving ~9 min (fresh start 120 s, rewind ~8 s)');
  assert.equal(rewindSummary({ groups: 1, members: 3, jobs: 3, rewinds: [4000, 6000], fresh: [150000], refused: 0, failed: 0 }),
    'rewind: 2 of 2 planned jobs started from a rewind (5.0 s each), 0 refused, 0 failed (fresh load instead); ~5 min saved vs fresh starts (150 s each: mean of this run\'s 1 fresh starts)');
  assert.match(rewindSummary({ groups: 1, members: 2, jobs: 2, rewinds: [], fresh: [], refused: 1, failed: 0 }), /^rewind: 0 of 1 planned jobs started from a rewind, 1 refused, .* ~0 min saved .*estimate\)$/);
  assert.equal(startLine(null), 'fresh');
  assert.equal(startLine({ code: 0, why: [], ms: 4100 }), 'rewind (4100 ms)');
  assert.equal(startLine({ code: 2, why: refused.why, ms: 900 }), 'rewind refused -> fresh (thread 39152 (start 77e34e70) exited after the snapshot)');
  assert.match(startLine({ code: 3, why: ended.why, ms: 900 }), /^rewind failed -> fresh \(no clear answer/);
  assert.deepEqual(['Pause', 'Normal', 'Double', 'Triple', undefined].map(speedArg), ['pause', 'normal', 'fast', 'ultra', 'normal']);
  // the op sequence on fakes: park, pause, detach, op, attach, speed back, game_state; and the fallback paths
  const run = async (rows: Row[], o: { attach?: boolean; inWorld?: boolean; pause?: boolean } = {}) => {
    const calls: string[] = [];
    const fake = (_pid: number, _f: string[]) => ({ cmd: async (c: string) => { calls.push(`tool ${c}`); return c === 'park' ? { op: 'park', code: 0, why: [] } : c === 'plan' ? { op: 'plan', code: 0, why: [] } : rows.shift()!; }, quit: async () => 0 });
    const io: Io = { log: () => {}, get: async (p: string) => { calls.push(p); return p === 'fridadetach' ? { detached: true } : { attached: o.attach !== false }; },
      agent: async (c: string, a?: any) => { calls.push(`${c}${a?.speed ? ` ${a.speed}` : ''}`); return c === 'time' ? { ok: true, result: { speed: 'Triple' } } : c === 'autopilot' ? { ok: true, result: { speed: 'none' } } : c === 'game_state' ? { ok: true, result: { in_world: o.inWorld !== false } } : { ok: c !== 'set_speed' || a.speed !== 'pause' || o.pause !== false, error: 'game speed is locked' }; } };
    const s = rewindSession(1, io, { serve: fake as any, loaded: () => false, settleMs: 0 });
    return { snap: await s.snapshot(), rew: rows.length ? await s.rewind() : null, calls };
  };
  const snapRow = parseRow('result {"op":"snapshot","code":0,"why":[],"ms":{"park":150.3,"freeze":37.9,"copy":1645.7,"handles":5.1,"views":953.5,"resume":0.5},"stats":{}}')!;
  const a = await run([snapRow, good]);
  assert.deepEqual([a.snap.code, a.rew!.code], [0, 0]);
  assert.deepEqual(a.calls.slice(0, 11), ['time', 'autopilot', 'tool park', 'set_speed pause', 'fridadetach', 'tool snapshot', 'fridaattach', 'set_speed ultra', 'autopilot none', 'game_state', 'tool park'], 'speed and autopilot target read first, put back after');
  assert.deepEqual((await run([{ ...snapRow }, refused])).rew!.code, 2, 'refused: fresh, nothing changed');
  assert.deepEqual((await run([{ ...snapRow }, ended])).rew!.code, 3, 'ended: kill + fresh');
  assert.deepEqual((await run([{ ...snapRow }, { ...good, stats: { hashEqual: 0 } }])).rew!.code, 3, 'hash differs: kill + fresh');
  assert.equal((await run([{ ...snapRow }], { attach: false })).snap.code, 3, 'probe not back: kill');
  assert.equal((await run([{ ...snapRow }], { inWorld: false })).snap.code, 3, 'game does not answer: kill');
  const np = await run([{ ...snapRow }], { pause: false });
  assert.deepEqual([np.snap.code, np.calls.includes('tool plan'), np.calls.includes('fridadetach')], [2, true, false], 'pause refused: park released by a plan, nothing detached');
  console.log('queue-rewind self-check ok (grouping, blocks, readiness on real lines, op order and every fallback path)');
}

const [cmd, arg] = process.argv.slice(2);
if (import.meta.filename === resolve(process.argv[1] ?? '')) {
  if (cmd === '--self-check') await selfCheck();
  else if (cmd === 'state' && arg) console.log(queueRewind(JSON.parse(readFileSync(arg, 'utf8'))) === 'auto' && existsSync(EXE) ? 'apex-rewind' : 'apex');
  else { console.error('usage: node tools/harness/queue-rewind.ts --self-check | state <queue.json>'); process.exit(2); }
}
