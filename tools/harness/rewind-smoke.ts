// Rewind smoke on the running test game (docs/270 §10), and the toy selftest of the rewind tool. Test-only, Windows only.
//   node tools/harness/rewind-smoke.ts --self-check
//       builds tools/harness/rewind (MSVC) and runs the toy selftest (every guard: success and refuse path), then drives
//       `rewind-spike serve` on a toy the way a game job does. Skips cleanly when the MSVC build tools are missing.
//   node tools/harness/rewind-smoke.ts --mode plan|plan-probe|strict|override|bridge-plan|bridge-apply|refuse-gpu [--renderer bridge|bridge-rewind|inproc]
//                                      [--run-s 20] [--n 2] [--port 47891] [--tag t]
//       queue.ts run.script job, game in world: pause (agent set_speed), snapshot, then n times: play run-s seconds, pause,
//       rewind or plan; then play on 20 s and check the game still answers. Rows go to probe-out/rewind/<tag>-<mode>.jsonl.
//       plan        nothing is written into the game: threads are suspended for the checks, handles duplicated, memory read;
//                   prints what a rewind would do and every reason it would refuse
//       plan-probe  as plan, plus the one-byte view probe (which section each mapped view belongs to)
//       strict      a real snapshot and rewind with every guard on (the probe module is allowed: queue.ts always attaches it)
//       override    a real rewind with the guards for sound, shared views, window objects, pool workers and unpinned views
//                   switched off: experimental, may end the game
//       bridge-plan / bridge-apply   on the patched RTX Remix bridge (docs/270 §12): the bridge client parks the game at a Present and
//                   passes the ops to the bridge server; plan lists what both sides would do, apply is one real rewind
//       refuse-gpu  plain DXVK check: the snapshot must be refused because the GPU device sits in TS3W (no queue job since
//                   2026-11-15: no plain-DXVK game runs; the guard is proved on the toy, docs/270 §14)
//       --renderer bridge|bridge-rewind|inproc: FAIL at once on the wrong Bin state: bridge = NvRemixBridge.exe runs,
//                   bridge-rewind = also rewindMode = True in Bin\bridge.conf (the patched copy), inproc = it does not run
// Verdict: FAIL when the tool ended the game (exit 3), the game is gone, or it no longer answers; a refusal is a result.
import assert from 'node:assert/strict';
import { execFileSync, spawn } from 'node:child_process';
import { appendFileSync, copyFileSync, existsSync, mkdirSync, readFileSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { BIN } from './game-exe.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
const DIR = resolve(import.meta.dirname, 'rewind'), EXE = join(DIR, 'out', 'rewind-spike.exe'), TOY = join(DIR, 'out', 'rewind-toy.exe');
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
export const TASK_ROW = /^"[^"]+","(\d+)"/m;
const pidOf = (name: string): number => {  // not imported from rewind-census.ts: that script runs its own command line on import
  const m = execFileSync('tasklist', ['/FI', `IMAGENAME eq ${name}`, '/FO', 'CSV', '/NH'], { encoding: 'utf8', timeout: 30000 }).match(TASK_ROW);
  if (!m) throw new Error(`${name} not running`);
  return +m[1];
};

// `tasklist /M frida-agent* /FI "PID eq n"` output: a row while the probe's Frida agent is still loaded in the game
export const fridaIn = (out: string) => TASK_ROW.test(out);
const fridaLoaded = (pid: number) =>
  fridaIn(execFileSync('tasklist', ['/M', 'frida-agent*', '/FI', `PID eq ${pid}`, '/FO', 'CSV', '/NH'], { encoding: 'utf8', timeout: 30000 }));

export type Row = { op: string; code: number; why: string[]; ms?: Record<string, number>; stats?: Record<string, number> };
export const RESULT = /^result (\{.*\})\s*$/;
export const parseRow = (line: string): Row | null => { const m = RESULT.exec(line); return m ? JSON.parse(m[1]) : null; };

// Rodney 2026-11-15 (docs/270 §7): sound, pool workers and unpinned views are allowed in every smoke job.
// Ops that write into the game run with the probe's Frida agent out of the game (docs/270 §24): the tool's module guard
// stays on and the probe detaches before each op and attaches after it. Plan ops write nothing and keep the agent in.
const GAME = ['--allow-game', '--no-coop', '--allow-audio', '--reap-workers', '--recreate-views'];
const PLAN = ['--allow-module', 'frida-agent', '--plan-only'];
// Bridge modes (docs/270 §12): no --no-coop, the patched bridge client publishes the cooperation block and parks at a Present.
const BRIDGE = GAME.filter(f => f !== '--no-coop');
export const MODES: Record<string, { flags: string[]; op: 'plan' | 'rewind'; expectRefuse?: RegExp }> = {
  plan: { flags: [...GAME, ...PLAN, '--no-view-probe'], op: 'plan' },
  'plan-probe': { flags: [...GAME, ...PLAN], op: 'plan' },
  strict: { flags: GAME, op: 'rewind' },
  override: { flags: [...GAME, '--allow-shared-views', '--allow-gui'], op: 'rewind' },
  'bridge-plan': { flags: [...BRIDGE, ...PLAN], op: 'plan' },
  'bridge-apply': { flags: BRIDGE, op: 'rewind' },
  // plain DXVK: the GPU device sits in TS3W, the snapshot must be refused for that reason
  'refuse-gpu': { flags: GAME, op: 'rewind', expectRefuse: /the GPU device sits in this process/ },
};
// Which renderer a job expects: bridge = NvRemixBridge.exe runs (GPU device out of process), inproc = it does not.
// bridge-rewind = the patched bridge copy: the bridge runs and Bin\bridge.conf turns rewindMode on (docs/270 §12.6).
export const REWIND_ON = /^[ \t]*rewindMode[ \t]*=[ \t]*True[ \t]*\r?$/m;
export const rendererOk = (want: string | undefined, bridgeRuns: boolean, rewindOn = false) =>
  !want || (want === 'inproc' ? !bridgeRuns : bridgeRuns && (want !== 'bridge-rewind' || rewindOn));

// One `rewind-spike serve` process: send a command, get its result row.
export function serve(pid: number, flags: string[]) {
  const child = spawn(EXE, ['serve', String(pid), ...flags], { stdio: ['pipe', 'pipe', 'inherit'] });
  let buf = '', exit: number | null = null;
  const rows: Row[] = [];
  child.stdout.on('data', d => {
    buf += d;
    for (let i; (i = buf.indexOf('\n')) >= 0; buf = buf.slice(i + 1)) { const r = parseRow(buf.slice(0, i)); if (r) rows.push(r); }
  });
  child.on('exit', c => { exit = c ?? 1; });
  return {
    async cmd(c: string, timeoutS = 120): Promise<Row> {
      const n = rows.length;
      child.stdin.write(c + '\n');
      for (let t = 0; t < timeoutS * 20 && rows.length === n && exit === null; t++) await sleep(50);
      return rows[n] ?? { op: c, code: exit === null ? 1 : exit, why: [exit === null ? `no answer in ${timeoutS} s` : `rewind-spike exited ${exit}`] };
    },
    async quit(): Promise<number> { if (exit === null) child.stdin.end('quit\n'); for (let t = 0; t < 100 && exit === null; t++) await sleep(50); if (exit === null) child.kill(); return exit ?? 1; },
  };
}

async function selfCheck() {
  assert.equal(parseRow('result {"op":"rewind","code":2,"why":["thread 9840 is in NtDelayExecution; at the snapshot it was running"],"ms":{"park":0.0},"stats":{"freezeAttempts":16}}')?.why.length, 1);
  assert.equal(parseRow('  ok   ok(r) && r.stat("viewsRemapped") == 2'), null);
  assert.equal(parseRow('case basic'), null);
  assert.equal(TASK_ROW.exec('"TS3W.exe","23456","Console","1","1,812,344 K"')?.[1], '23456');
  assert.equal(TASK_ROW.exec('INFO: No tasks are running which match the specified criteria.'), null);
  for (const [name, m] of Object.entries(MODES)) assert.ok(m.flags.includes('--allow-game') && m.flags.includes('--no-coop') !== name.startsWith('bridge-'), name);
  assert.ok(MODES.plan.flags.includes('--no-view-probe') && !MODES['plan-probe'].flags.includes('--no-view-probe'));
  assert.ok(!MODES.strict.flags.some(f => /^--(allow-shared-views|allow-gui|plan-only)$/.test(f)), 'strict keeps the shared-view and window guards');
  assert.ok(['--allow-audio', '--reap-workers', '--recreate-views'].every(f => Object.values(MODES).every(m => m.flags.includes(f))), 'audio, workers, views allowed in all jobs');
  for (const [name, m] of Object.entries(MODES)) assert.equal(m.flags.includes('frida-agent'), m.op === 'plan', `${name}: the probe agent stays in only for plan ops`);
  assert.equal(fridaIn('"TS3W.exe","61884","Console","1","1,812,344 K"\r\n'), true);
  assert.equal(fridaIn('INFO: No tasks are running which match the specified criteria.\r\n'), false);
  for (const m of ['bridge-plan', 'bridge-apply']) assert.ok(!MODES[m].flags.some(f => /^--(no-coop|allow-shared-views|allow-gpu)$/.test(f)), `${m} uses the bridge client's block and keeps the guards`);
  // real refusal line of rewind-spike.cpp (GPU module guard) and one of another guard
  assert.ok(MODES['refuse-gpu'].expectRefuse!.test('module vulkan-1.dll is loaded: the GPU device sits in this process and is not rewound (run under the RTX Remix bridge)'));
  assert.ok(!MODES['refuse-gpu'].expectRefuse!.test('thread 9840 belongs to the sound module dsound.dll'));
  assert.deepEqual([rendererOk('bridge', true), rendererOk('bridge', false), rendererOk('inproc', false), rendererOk('inproc', true), rendererOk(undefined, true)], [true, false, true, false, true]);
  assert.deepEqual([rendererOk('bridge-rewind', true, true), rendererOk('bridge-rewind', true, false), rendererOk('bridge-rewind', false, true)], [true, false, false]);
  // real lines: the install copy's bridge.conf ends with the first one; Apex's shipped conf with patch 0015 has the commented one
  assert.ok(REWIND_ON.test('serverChannelMemSize = 72MB\r\n\r\nrewindMode = True\r\n') && !REWIND_ON.test('serverChannelMemSize = 72MB\n# rewindMode = True\n'));
  const vswhere = join(process.env['ProgramFiles(x86)'] ?? '', 'Microsoft Visual Studio', 'Installer', 'vswhere.exe');
  if (process.platform !== 'win32' || !existsSync(vswhere)) { console.log('rewind-smoke self-check: flags and parser ok; toy selftest skipped (no MSVC build tools)'); return; }
  let out = '';
  try { out = execFileSync('cmd', ['/c', join(DIR, 'build.cmd')], { encoding: 'utf8', timeout: 200000, stdio: ['ignore', 'pipe', 'pipe'] }); }
  catch (e: any) { console.error(String(e.stdout ?? '').split('\n').filter((l: string) => /FAIL|error|result/.test(l)).join('\n')); throw new Error(`toy selftest failed: ${e.message.split('\n')[0]}`); }
  const oks = out.split('\n').filter(l => l.startsWith('  ok')).length;
  assert.ok(/^PASS\s*$/m.test(out) && !/^\s+FAIL/m.test(out) && oks >= 80, `toy selftest: ${oks} ok, no PASS line`);
  // the serve protocol on a toy, as a game job uses it
  const toy = spawn(TOY, [], { stdio: 'ignore' });
  try {
    await sleep(700);
    const s = serve(toy.pid!, []);
    const snap = await s.cmd('snapshot'), rew = await s.cmd('rewind'), plan = await s.cmd('plan'), bad = await s.cmd('nonsense');
    assert.deepEqual([snap.code, rew.code, plan.code, bad.code], [0, 0, 0, 1], JSON.stringify([snap, rew, plan]));
    assert.ok(rew.stats!.hashEqual === 1 && snap.stats!.viewsPinned >= 4);
    assert.equal(await s.quit(), 0);
  } finally { toy.kill(); }
  console.log(`rewind-smoke self-check: toy selftest PASS (${oks} checks), serve protocol ok`);
}

async function main() {
  const { values } = parseArgs({ options: { mode: { type: 'string' }, 'run-s': { type: 'string', default: '20' }, n: { type: 'string', default: '2' }, port: { type: 'string', default: '47891' }, tag: { type: 'string' }, renderer: { type: 'string' }, 'self-check': { type: 'boolean' } } });
  if (values['self-check']) return selfCheck();
  const mode = MODES[values.mode ?? ''];
  if (!mode) throw new Error(`--mode must be one of ${Object.keys(MODES).join(', ')}`);
  if (!existsSync(EXE)) throw new Error(`${EXE} missing: run tools/harness/rewind/build.cmd first`);
  const tag = values.tag ?? new Date().toISOString().slice(0, 16).replace(/[-:T]/g, ''), out = join(ROOT, 'probe-out', 'rewind');
  mkdirSync(out, { recursive: true });
  const file = join(out, `${tag}-${values.mode}.jsonl`);
  const ag = async (cmd: string, args?: object) => {
    const q = new URLSearchParams({ cmd, timeout: '10000' }); if (args) q.set('args', JSON.stringify(args));
    try { return await (await fetch(`http://127.0.0.1:${values.port}/agent?${q}`, { signal: AbortSignal.timeout(30000) })).json() as any; } catch (e: any) { return { ok: false, error: String(e.message ?? e) }; }
  };
  const speed = async (s: string) => { const r = await ag('set_speed', { speed: s }); if (!r.ok) console.log(`rewind-smoke: set_speed ${s}: ${r.error ?? JSON.stringify(r)}`); await sleep(2000); return !!r.ok; };
  const log = (r: Row) => {
    appendFileSync(file, JSON.stringify({ utc: new Date().toISOString(), mode: values.mode, ...r }) + '\n');
    console.log(`rewind-smoke: ${r.op} code=${r.code} ms=${JSON.stringify(r.ms ?? {})} stats=${JSON.stringify(r.stats ?? {})}`);
    for (const w of r.why) console.log(`rewind-smoke:   ${r.code === 3 ? 'ENDED' : 'refuse'}: ${w}`);
  };
  const pid = pidOf('TS3W.exe');
  let bridgeRuns = true;
  try { pidOf('NvRemixBridge.exe'); } catch { bridgeRuns = false; }
  console.log(`rewind-smoke: renderer: ${bridgeRuns ? 'RTX Remix bridge (NvRemixBridge.exe runs)' : 'in process (no NvRemixBridge.exe)'}`);
  const conf = join(BIN, 'bridge.conf'), rewindOn = existsSync(conf) && REWIND_ON.test(readFileSync(conf, 'utf8'));
  if (!rendererOk(values.renderer, bridgeRuns, rewindOn)) { console.log(`rewind-smoke: ${values.mode}: FAIL (this job expects renderer ${values.renderer}: wrong Bin state)`); process.exit(1); }
  const s = serve(pid, mode.flags);
  let ended = false;
  // ts3native's route worker parks while the sim still runs: a route job waits for script ticks, which a paused game does not
  // run (docs/270 §21); the tool holds the park until the op after the pause ends
  const parkWorker = async () => { const p = await s.cmd('park', 30); if (p.code) log(p); };
  // an op that writes into the game: the probe's Frida agent leaves first and comes back after (docs/270 §24)
  const probe = async (p: string) => {
    try { return await (await fetch(`http://127.0.0.1:${values.port}/${p}`, { signal: AbortSignal.timeout(60000) })).json() as any; } catch (e: any) { return { error: String(e.message ?? e) }; }
  };
  const op = async (c: string) => {
    if (mode.op === 'plan') return s.cmd(c, 300);
    const d = await probe('fridadetach');
    let t = 0;
    for (; t < 100 && fridaLoaded(pid); t++) await sleep(200);
    console.log(`rewind-smoke: probe agent out of the game (${d.detached ? `unloaded after ${t * 0.2} s` : `detach: ${d.error}`})`);
    const r = await s.cmd(c, 300);
    const a = await probe('fridaattach');
    if (!a.attached) console.log(`rewind-smoke: probe attach after ${c}: ${a.error ?? JSON.stringify(a)}`);
    return r;
  };
  await parkWorker();
  await speed('pause');
  const snap = await op('snapshot');
  log(snap);
  if (mode.expectRefuse) {  // the snapshot must be refused for the named reason, and the game must play on
    const hit = snap.code === 2 && snap.why.some(w => mode.expectRefuse!.test(w));
    await s.quit();
    await speed('normal');
    await sleep(20000);
    const st = await ag('game_state'), alive = !!st.ok && !!st.result?.in_world;
    console.log(`rewind-smoke: ${values.mode}: ${hit && alive ? 'PASS' : 'FAIL'} (snapshot ${hit ? 'refused as expected' : `code ${snap.code}, expected refusal missing`}, game ${alive ? 'answers' : 'does not answer'}); rows in ${file}`);
    process.exit(hit && alive ? 0 : 1);
  }
  for (let i = 0; i < Number(values.n) && !ended; i++) {
    await speed('normal');
    await sleep(Number(values['run-s']) * 1000);
    await parkWorker();
    await speed('pause');
    const r = await op(mode.op);
    log(r);
    ended = r.code === 3;
    if (mode.op === 'rewind' && r.code !== 0) break;  // refused: later tries would say the same
  }
  const exit = await s.quit();
  let alive = !ended;
  if (alive) {
    await speed('normal');
    await sleep(20000);
    const st = await ag('game_state');
    alive = !!st.ok && !!st.result?.in_world;
    if (!alive) console.log(`rewind-smoke: game_state after the run: ${JSON.stringify(st).slice(0, 200)}`);
  }
  // the bridge's own [Rewind] lines (client and server): the Bin copy goes when the job puts Bin back (docs/270 §26)
  for (const b of bridgeRuns ? ['bridge32.log', 'bridge64.log'] : []) {
    const src = join(BIN, 'rtx-remix', 'logs', b);
    if (existsSync(src)) copyFileSync(src, join(out, `${tag}-${values.mode}-${b}`));
  }
  console.log(`rewind-smoke: ${values.mode}: ${alive && exit !== 3 ? 'PASS' : 'FAIL'} (tool exit ${exit}, game ${alive ? 'answers' : ended ? 'ended by the tool' : 'does not answer'}); rows in ${file}`);
  process.exit(alive && exit !== 3 ? 0 : 1);
}

await main();
