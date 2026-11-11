// Night run on a clean machine (Rodney, 2026-10-17): close the everyday apps that cost CPU (browsers, Slack, IntelliJ,
// Docker, ...), then start a test queue, so the runs measure the game and nothing else.
//
//   node tools/harness/night-run.ts                       report only: what runs, what would close, lock and idle state
//   node tools/harness/night-run.ts --close [--wait] [--queue clean-night.json] [--only a,b] [--force]
//   node tools/harness/night-run.ts --close [--wait] --queue-list a.json,b.json [--stop-on-fail] [--resume]
//
// --queue-list runs the queues in order (after one close step); a queue that FAILs (queue.ts exit != 0) ends the list only
// with --stop-on-fail, a queue stopped by a signal always ends it. --resume (with --queue or --queue-list) reads the jsonl
// of the queue that ran last (newest probe-out/queue/<name>-<date>.jsonl), continues it --from its first job without a row,
// then runs the queues after it; queues before it count as done.
//
// Safety: nothing closes without --close (Claude passes it only when Rodney asks), and --close refuses unless the screen
// is locked (console session state WTS_SESSIONSTATE_LOCK) AND there was no keyboard/mouse input for --idle-min minutes (default 5). Apps get a
// normal close first (WM_CLOSE, like clicking X: browsers restore their tabs next start); --force kills what is still
// open after 30 s. Never touched: the game, Steam, Claude, terminals, shells, node/python (the harness itself).
// Windows only, like the rest of the harness. Self-check: --self-check.
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { basename, join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import assert from 'node:assert/strict';
import { GAME_NAME } from './game-exe.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
const { values } = parseArgs({ options: {
  close: { type: 'boolean' }, force: { type: 'boolean' }, wait: { type: 'boolean' }, queue: { type: 'string' }, only: { type: 'string' }, from: { type: 'string' },
  'queue-list': { type: 'string' }, 'stop-on-fail': { type: 'boolean' }, resume: { type: 'boolean' },
  'idle-min': { type: 'string', default: '5' }, 'self-check': { type: 'boolean' } } });

// app -> process names (without .exe). Edge covers Disney+ and Netflix in the browser; the Netflix store app runs as WWAHost.
export const APPS: Record<string, string[]> = {
  chrome: ['chrome'], edge: ['msedge'], firefox: ['firefox'], slack: ['slack'], teams: ['ms-teams', 'Teams'],
  discord: ['Discord'], spotify: ['Spotify'], netflix: ['WWAHost'], intellij: ['idea64'], vscode: ['Code'],
  docker: ['Docker Desktop', 'com.docker.backend', 'com.docker.build'], onedrive: ['OneDrive'],
  logi: ['logioptionsplus_agent', 'lghub_agent'],
};
// never closed, even if a name above ever matched one of them
export const PROTECTED = new Set([GAME_NAME, 'TS3W', 'Sims3Launcher', 'steam', 'steamwebhelper', 'claude', 'node', 'python', 'WindowsTerminal', 'OpenConsole',
  'powershell', 'pwsh', 'cmd', 'bash', 'conhost', 'explorer', 'dwm', 'LogonUI', 'frida-server']);

export const toClose = (running: string[]) => Object.entries(APPS)
  .map(([app, names]) => ({ app, procs: names.filter(n => running.includes(n) && !PROTECTED.has(n)) }))
  .filter(a => a.procs.length);

// retries: Add-Type compiles into %TEMP%, and the AV sometimes locks that DLL (BadImageFormat / access denied, 2026-11-11)
const ps = (script: string): string => {
  for (let i = 1; ; i++) {
    try { return execFileSync('powershell', ['-NoProfile', '-NonInteractive', '-Command', script], { timeout: 30000 }).toString().trim(); }
    catch (e) { if (i >= 4) throw e; Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 3000 * i); }
  }
};
const running = (): { name: string; cpu: number }[] => JSON.parse(ps(
  "Get-Process | Group-Object ProcessName | ForEach-Object { [pscustomobject]@{ name = $_.Name; cpu = [int](($_.Group | Measure-Object CPU -Sum).Sum) } } | ConvertTo-Json -Compress") || '[]');
// console session lock state: WTSQuerySessionInformationW(current server, active console session, WTSSessionInfoEx = 25) ->
// WTSINFOEXW { Level at +0; level-1 union at +8: SessionId +8, SessionState +12, SessionFlags +16 } (verified on this machine
// 2026-10-24: flags 1 at +16 while unlocked). LogonUI.exe also runs at sign-in and UAC prompts, so it was no lock proof.
// -2 = the query failed
const sessionFlags = () => Number(ps(`Add-Type 'using System; using System.Runtime.InteropServices; public static class Wts {
  [DllImport("wtsapi32.dll")] static extern bool WTSQuerySessionInformationW(IntPtr h, uint id, int cls, out IntPtr buf, out uint n);
  [DllImport("wtsapi32.dll")] static extern void WTSFreeMemory(IntPtr p);
  [DllImport("kernel32.dll")] static extern uint WTSGetActiveConsoleSessionId();
  public static int Flags() { IntPtr b; uint n; if (!WTSQuerySessionInformationW(IntPtr.Zero, WTSGetActiveConsoleSessionId(), 25, out b, out n)) return -2;
    try { return Marshal.ReadInt32(b, 16); } finally { WTSFreeMemory(b); } } }'; [Wts]::Flags()`));
// WTS_SESSIONSTATE_LOCK = 0, UNLOCK = 1, UNKNOWN = -1: only a definite LOCK counts as locked
export const isLockFlags = (flags: number) => flags === 0;
const locked = () => isLockFlags(sessionFlags());
const idleMin = () => Number(ps("Add-Type 'using System; using System.Runtime.InteropServices; public static class Idle { [StructLayout(LayoutKind.Sequential)] public struct L { public uint cb; public uint t; } [DllImport(\"user32.dll\")] static extern bool GetLastInputInfo(ref L l); public static uint Ms() { L l = new L(); l.cb = 8; GetLastInputInfo(ref l); return (uint)Environment.TickCount - l.t; } }'; [Idle]::Ms()")) / 60000;

// --resume: the queue that ran last (newest jsonl) continues at its first job without a row, else the next queue; null = all done
export function resumeAt(qs: { ids: string[]; done: string[] | null; mtime: number }[]): { q: number; from?: string } | null {
  let last = -1;
  qs.forEach((q, i) => { if (q.done && (last < 0 || q.mtime > qs[last].mtime)) last = i; });
  if (last < 0) return { q: 0 };
  const from = qs[last].ids.find(id => !qs[last].done!.includes(id));
  return from ? { q: last, from } : last + 1 < qs.length ? { q: last + 1 } : null;
}
// runs the queues from start; run() = the queue's exit code, null = stopped by a signal. Returns the list's exit code.
export function runList(qs: string[], start: { q: number; from?: string }, run: (q: string, from?: string) => number | null, stopOnFail: boolean, log: (s: string) => void): number {
  let rc = 0;
  for (let i = start.q; i < qs.length; i++) {
    const st = run(qs[i], i === start.q ? start.from : undefined);
    if (st === null) { log(`queue ${qs[i]} stopped by a signal: list ends`); return 1; }
    if (st === 0) continue;
    rc = st;
    if (stopOnFail) { log(`queue ${qs[i]} FAIL (exit ${st}): --stop-on-fail, list ends (resume: same command + --resume)`); break; }
    log(`queue ${qs[i]} FAIL (exit ${st}): next queue`);
  }
  return rc;
}
const QOUT = join(ROOT, 'probe-out', 'queue');
const qPath = (q: string) => (q.includes('/') || q.includes('\\') ? q : join(ROOT, 'tools', 'harness', 'queues', q));
// the queue's ids, the jobs with a row in its newest <name>-YYYY-MM-DD.jsonl (null = never ran) and that file's mtime
function queueState(q: string) {
  const name = basename(q).replace(/\.json$/i, ''), qj = JSON.parse(readFileSync(qPath(q), 'utf8'));
  const ids: string[] = (Array.isArray(qj) ? qj : qj.jobs).map((j: { id: string }) => j.id); // queue.ts takes a list or { jobs }
  const fs = existsSync(QOUT) ? readdirSync(QOUT).filter(f => f.startsWith(`${name}-`) && /^\d{4}-\d\d-\d\d\.jsonl$/.test(f.slice(name.length + 1))) : [];
  const f = fs.map(f => ({ f, m: statSync(join(QOUT, f)).mtimeMs })).sort((a, b) => b.m - a.m)[0];
  if (!f) return { ids, done: null, mtime: 0 };
  const done = readFileSync(join(QOUT, f.f), 'utf8').split('\n').flatMap(l => { try { return l ? [JSON.parse(l).job as string] : []; } catch { return []; } });
  return { ids, done, mtime: f.m };
}

if (values['self-check']) {
  const t = toClose(['chrome', 'msedge', 'claude', 'node', 'idea64', 'TS3W', 'Docker Desktop']);
  assert.ok(JSON.stringify(t.map(a => a.app)) === JSON.stringify(['chrome', 'edge', 'intellij', 'docker']), 'apps found');
  assert.ok(!t.some(a => a.procs.some(p => PROTECTED.has(p))), 'protected never closed');
  for (const n of Object.values(APPS).flat()) assert.ok(!PROTECTED.has(n), `listed app is protected: ${n}`);
  assert.ok(isLockFlags(0) && !isLockFlags(1) && !isLockFlags(-1) && !isLockFlags(-2), 'lock flags');
  const fl = sessionFlags(); // live, read-only: the query must answer LOCK or UNLOCK
  assert.ok(fl === 0 || fl === 1, `session flags ${fl}`);
  console.log(`session flags ${fl} (${fl === 0 ? 'locked' : 'unlocked'})`);
  // --resume: newest jsonl wins, continue at the first job without a row; a finished queue hands over to the next one
  const st = (ids: string[], done: string[] | null, mtime = 0) => ({ ids, done, mtime });
  assert.deepEqual(resumeAt([st(['a', 'b'], null), st(['c'], null)]), { q: 0 });
  assert.deepEqual(resumeAt([st(['a', 'b'], ['a', 'b'], 5), st(['c', 'd', 'e'], ['c', 'e'], 9)]), { q: 1, from: 'd' });
  assert.deepEqual(resumeAt([st(['a', 'b'], ['a', 'b'], 9), st(['c'], ['c'], 2)]), { q: 1 }); // old jsonl of q1 from an earlier night
  assert.deepEqual(resumeAt([st(['a'], ['a'], 1), st(['c'], ['c'], 9)]), null);
  // --queue-list with stubbed runners: FAIL goes on without --stop-on-fail, stops with it; a signal always stops; from only for the first
  const calls: string[] = [], logs: string[] = [];
  const stub = (codes: Record<string, number | null>) => (q: string, from?: string) => { calls.push(from ? `${q}@${from}` : q); return q in codes ? codes[q] : 0; };
  assert.equal(runList(['a', 'b', 'c'], { q: 0, from: 'x' }, stub({ b: 1 }), false, l => logs.push(l)), 1);
  assert.deepEqual(calls.splice(0), ['a@x', 'b', 'c']);
  assert.equal(runList(['a', 'b', 'c'], { q: 1 }, stub({ b: 1 }), true, l => logs.push(l)), 1);
  assert.deepEqual(calls.splice(0), ['b']);
  assert.equal(runList(['a', 'b', 'c'], { q: 0 }, stub({ a: null }), false, l => logs.push(l)), 1);
  assert.deepEqual(calls.splice(0), ['a']);
  assert.equal(runList(['a', 'b'], { q: 0 }, stub({}), true, l => logs.push(l)), 0);
  assert.deepEqual(calls.splice(0), ['a', 'b']);
  assert.equal(logs.length, 3);
  console.log('self-check ok');
  process.exit(0);
}

const qList = (values['queue-list'] ?? values.queue)?.split(',').map(s => s.trim()).filter(Boolean) ?? [];
let start: { q: number; from?: string } | null = { q: 0, from: values.from };
if (values.resume && qList.length) {
  start = resumeAt(qList.map(queueState));
  console.log(start ? `resume: ${qList[start.q]}${start.from ? ` --from ${start.from}` : ''}${start.q + 1 < qList.length ? `, then ${qList.slice(start.q + 1).join(', ')}` : ''}` : 'resume: every job of every queue has a row, nothing to run');
}
const procs = running(), names = procs.map(p => p.name);
const only = values.only?.split(',').map(s => s.trim());
const targets = toClose(names).filter(a => !only || only.includes(a.app));
const cpuOf = (n: string) => procs.find(p => p.name === n)?.cpu ?? 0;
const isLocked = locked(), idle = idleMin(), needIdle = Number(values['idle-min']);
console.log(`screen ${isLocked ? 'locked' : 'NOT locked'}, idle ${idle.toFixed(1)} min (need ${needIdle})`);
console.log(targets.length ? 'apps to close:' : 'no listed apps running');
for (const a of targets) console.log(`  ${a.app}: ${a.procs.map(p => `${p} (${cpuOf(p)} CPU s so far)`).join(', ')}`);
const java = procs.filter(p => p.name === 'java');
if (java.length) console.log(`note: java is running (${java.map(p => p.cpu).join(', ')} CPU s): IntelliJ, or builds from other sessions; not closed here`);

if (!values.close) { console.log('report only: add --close to close them (only when Rodney asks)'); process.exit(0); }
// --wait: poll every 30 s for up to 30 min until locked and idle long enough, instead of refusing at once
for (let t = 0; values.wait && t < 60 && !(locked() && idleMin() >= needIdle); t++) {
  if (t % 4 === 0) console.log(`waiting: screen ${locked() ? 'locked' : 'not locked'}, idle ${idleMin().toFixed(1)} min`);
  await new Promise(r => setTimeout(r, 30000));
}
if (!locked()) { console.log('refused: the screen is not locked'); process.exit(2); }
const idleNow = idleMin();
if (idleNow < needIdle) { console.log(`refused: input ${idleNow.toFixed(1)} min ago (someone may be using the laptop)`); process.exit(2); }

for (const a of targets) {
  if (a.app === 'docker') spawnSync('docker', ['desktop', 'stop'], { timeout: 60000, stdio: 'ignore' }); // its own clean stop
  for (const p of a.procs) spawnSync('taskkill', ['/IM', `${p}.exe`, '/T'], { stdio: 'ignore' }); // WM_CLOSE, like clicking X
}
await new Promise(r => setTimeout(r, 30000));
const left = toClose(running().map(p => p.name)).filter(a => targets.some(t => t.app === a.app));
if (left.length && values.force) for (const a of left) for (const p of a.procs) spawnSync('taskkill', ['/IM', `${p}.exe`, '/T', '/F'], { stdio: 'ignore' });
console.log(left.length ? `still open after 30 s: ${left.map(a => a.app).join(', ')}${values.force ? ' (killed with --force)' : ' (left alone; --force kills them)'}` : 'all closed');

if (qList.length && start) {
  process.exit(runList(qList, start, (q, from) => {
    console.log(`starting queue ${qPath(q)}${from ? ` --from ${from}` : ''}`);
    const r = spawnSync(process.execPath, [join(ROOT, 'tools', 'harness', 'queue.ts'), qPath(q), ...(from ? ['--from', from] : [])], { stdio: 'inherit', env: { ...process.env, TS3_NIGHT: '1' } }); // night rule: a failed reused session restarts fresh (docs/170 §2)
    return r.signal ? null : r.status ?? 1;
  }, !!values['stop-on-fail'], console.log));
}
