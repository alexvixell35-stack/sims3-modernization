// Quit-to-desktop repro (diag/quit-finalizer-crash, player twopointjj 2026-11-14): on the loaded save, play N minutes (autopilot
// clears dialogs), save under a new name, quit to desktop (ts3agent quit_game: GameStates.TransitionToGameStateQuitNoCheck), wait
// for TS3W.exe to exit and judge the exit from ts3native.log written since the quit: an access violation in game code or ntdll,
// or a [QuitDiag] "quitdiag crash" line = "FAIL: game died at quit"; still running after --exit-s = "FAIL: quit hang".
// Before the quit it writes /memory and /native to $TS3_AFTERRUN_FILE (queue.ts quitsGame jobs read their "after" numbers there).
// Run by queue.ts as run.script (job quitsGame: true; the game is gone at the end by design).
//   node tools/harness/quit-cycle.ts [--minutes 2] [--save-name QuitCrash-x] [--exit-s 120] [--port 47891]
//   node tools/harness/quit-cycle.ts --self-check
import { existsSync, readFileSync, statSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { parseArgs } from 'node:util';
import assert from 'node:assert/strict';
import { BIN, gameRunning } from './game-exe.ts';

// a crash line of ts3native's [Crash] Log=1 handler: an AV (or wild call) at a TS3W.exe code address or inside ntdll
// (the player crash: "exception C0000005 at 0x77xxxxxx (ntdll.dll+0x40185)"), or [QuitDiag]'s crash line
export const CRASH_RE = /^exception (C0000005|C000001D|C0000096) at 0x00(?:[4-9A-E][0-9A-F]{5}|F[0-8][0-9A-F]{4})\b|^exception C0000005 at 0x[0-9A-F]{8} \(ntdll\.dll\+|^  quitdiag crash: /;
export const QD_RE = /^quitdiag: |^  quitdiag /;

export function judgeExit(logSince: string, exited: boolean, exitS: number): string[] {
  const lines = logSince.split(/\r?\n/);
  const crash = lines.filter(l => CRASH_RE.test(l));
  const qd = lines.filter(l => QD_RE.test(l));
  const out = [`quit timeline: ${qd.filter(l => l.startsWith('quitdiag: ')).length} quitdiag lines`];
  if (crash.length) out.push(`FAIL: game died at quit: ${crash[0].trim().slice(0, 300)}`, ...qd.filter(l => /crash|BAD/.test(l)).slice(0, 12).map(l => `  ${l.trim().slice(0, 400)}`));
  else if (!exited) out.push(`FAIL: quit hang: ${'TS3W'} still running ${exitS} s after quit_game`);
  else out.push('PASS: quit to desktop, no crash line');
  return out;
}

function selfCheck() {
  const yes = ['exception C0000005 at 0x77A40185 (ntdll.dll+0x40185) (info 00000001 00420031) thread 2008 eip 77A40185 esp 0019F0A0; stack code refs: 00E78E47',
    'exception C0000005 at 0x00E78E47 (info 00000000 0042002D) thread 2008 eip 00E78E47 esp 0019F0A0; stack code refs:',
    '  quitdiag crash: eip 77A40185, domain pass 1 entry 12 of 40, run_finalize calls 13, bad 1, world quit seen; mono thread: ...'];
  const no = ['exception C0000005 at 0x6A1B2C3D (ApexRadiance.dll+0x1B2C3D) (info 00000000 00000000) thread 77 eip 6A1B2C3D esp 0019F0A0; stack code refs:',
    'exception E06D7363 at 0x76C3B522 (info 19930520 0019F000) thread 2008 eip 76C3B522 esp 0019F000; stack code refs:',
    'exception C0000005 at 0x0FAB0000 (info 00000000 00000000) thread 9 eip 0FAB0000 esp 0019F0A0; stack code refs:',
    'quitdiag: world event 3 (world quit); mono thread: worker up state 2'];
  for (const l of yes) assert.ok(CRASH_RE.test(l), `should match: ${l}`);
  for (const l of no) assert.ok(!CRASH_RE.test(l), `should not match: ${l}`);
  assert.ok(QD_RE.test(no[3]) && QD_RE.test(yes[2]) && !QD_RE.test(yes[0]));
  assert.match(judgeExit(yes[0] + '\n' + no[3], true, 120).join('\n'), /FAIL: game died at quit: exception C0000005 at 0x77A40185/);
  assert.match(judgeExit(no.join('\n'), true, 120).at(-1)!, /^PASS: /);
  assert.match(judgeExit('', false, 120).at(-1)!, /^FAIL: quit hang/);
  assert.match(judgeExit(no[3], true, 120)[0], /1 quitdiag lines/);
  console.log('quit-cycle self-check ok');
}

if (import.meta.main ?? process.argv[1]?.endsWith('quit-cycle.ts')) {
  const { values } = parseArgs({ options: { minutes: { type: 'string', default: '2' }, 'save-name': { type: 'string', default: `QuitCrash-${new Date().toISOString().slice(11, 16).replace(':', '')}` },
    'exit-s': { type: 'string', default: '120' }, port: { type: 'string', default: '47891' }, 'self-check': { type: 'boolean', default: false } } });
  if (values['self-check']) { selfCheck(); process.exit(0); }
  const base = `http://127.0.0.1:${values.port}`;
  const get = async (p: string, ms = 20000) => { try { return await (await fetch(`${base}/${p}`, { signal: AbortSignal.timeout(ms) })).json(); } catch { return null; } };
  const ag = (cmd: string, args?: object, ms = 10000) => get(`agent?${new URLSearchParams({ cmd, timeout: String(ms), ...(args ? { args: JSON.stringify(args) } : {}) })}`, ms + 20000);
  const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
  const stamp = () => new Date().toISOString().slice(11, 19);
  const LOG = join(BIN, 'ts3native.log');
  const minutes = Number(values.minutes), exitS = Number(values['exit-s']), name = values['save-name']!;

  // 1. play: autopilot at normal speed clears dialogs; two missed answers in a row = the game died during play (fail fast)
  await ag('autopilot', { enabled: true, speed: 'normal', dialogs: true });
  let miss = 0;
  for (const end = Date.now() + minutes * 60000; Date.now() < end;) {
    await sleep(30000);
    const t = await ag('time');
    miss = t?.ok ? 0 : miss + 1;
    console.log(`${stamp()} play: ${t?.ok ? `sim ${t.result?.clock ?? '?'}` : 'no answer'}`);
    if (miss >= 2) { console.log(`FAIL: game died during play (no answer twice; game ${gameRunning() ? 'still running' : 'gone'})`); process.exit(1); }
  }
  // 2. save under a new name (never the Baseline save): the save runs as a game task; the game does not answer meanwhile
  await ag('autopilot', { enabled: false });
  await ag('set_speed', { speed: 'pause' });
  const sv = await ag('save_game', { name });
  if (!sv?.ok) { console.log(`FAIL: save_game ${name}: ${sv?.error ?? 'no answer'}`); process.exit(1); }
  let saved = false;
  for (const t0 = Date.now(); Date.now() - t0 < 180_000 && !saved;) {
    await sleep(2000);
    const st = await ag('game_state');
    saved = !!st?.ok && st.result?.save_in_progress !== true;
  }
  if (!saved) { console.log(`FAIL: save ${name} did not finish in 180 s`); process.exit(1); }
  console.log(`${stamp()} saved ${name}`);
  await sleep(5000);  // the player quit some seconds after the save
  // 3. the run's "after" numbers while the game still answers
  if (process.env.TS3_AFTERRUN_FILE) writeFileSync(process.env.TS3_AFTERRUN_FILE, JSON.stringify({ mem: await get('memory'), native: await get('native') }));
  // 4. quit to desktop; the game may not answer the command itself
  const off = existsSync(LOG) ? statSync(LOG).size : 0;
  const q = await ag('quit_game', undefined, 5000);
  console.log(`${stamp()} quit_game: ${q?.ok ? 'ok' : q?.error ?? 'no answer (expected while quitting)'}`);
  let exited = false;
  for (const t0 = Date.now(); Date.now() - t0 < exitS * 1000 && !(exited = !gameRunning());) await sleep(1000);
  await sleep(2000);  // the log's last lines
  const since = existsSync(LOG) ? readFileSync(LOG).subarray(off).toString('latin1') : '';
  const v = judgeExit(since, exited, exitS);
  for (const l of v) console.log(l);
  process.exit(v.some(l => l.startsWith('FAIL')) ? 1 : 0);
}
