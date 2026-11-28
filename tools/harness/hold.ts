// Queue hold for crash-triage builds (Rodney 2026-10-25, AGENTS.md Rules): a crash-triage logging/fix build starts the
// moment the running job ends, and queue.ts must not start the next job (or its measurement smoke) while it runs.
//   node tools/harness/hold.ts on <reason>    create probe-out/queue/hold (reason + UTC time); queue.ts waits before each job
//   node tools/harness/hold.ts off [--force]  remove it; the queue goes on within 10 s. From a worktree only a hold that worktree set
//                                             (owner=<checkout> in the text), else --force
//   node tools/harness/hold.ts wait-job-end   block until the job running now ends (queue.ts writes probe-out/queue/job-running
//                                             "<job id> <pid>" at job start and deletes it at job end; gone, another job, or a
//                                             dead queue pid = ended, stale marker removed). Run it after `on`, so no new job starts in between.
//   node tools/harness/hold.ts --self-check   temp dir only
// hold, job-running and main.lock live in the MAIN checkout's probe-out/queue (git common dir) for every checkout, worktrees
// included; TS3_QUEUE_DIR overrides (2026-11-23: a worktree chain ignored the main build-window hold and timed during merges,
// and `track.ts stop` from a worktree released the main checkout's hold).
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';

export const CHECKOUT = resolve(import.meta.dirname, '..', '..');
export function mainRoot(cwd = import.meta.dirname) {
  try { return dirname(execFileSync('git', ['rev-parse', '--path-format=absolute', '--git-common-dir'], { cwd, timeout: 10000, stdio: ['ignore', 'pipe', 'ignore'] }).toString().trim()); }
  catch { return CHECKOUT; } // no git: this checkout
}
export const MAIN_ROOT = mainRoot();
export const QUEUE_DIR = process.env.TS3_QUEUE_DIR ? resolve(process.env.TS3_QUEUE_DIR) : join(MAIN_ROOT, 'probe-out', 'queue');
export const samePath = (a: string, b: string) => process.platform === 'win32' ? resolve(a).toLowerCase() === resolve(b).toLowerCase() : resolve(a) === resolve(b);
// hold text: "<reason> @ <UTC>Z owner=<checkout>"; a hold without owner (older) counts as the main checkout's
export const holdText = (reason: string, owner = CHECKOUT, at = new Date()) => `${reason} @ ${at.toISOString().slice(0, 19)}Z owner=${owner}`;
export const holdOwner = (text: string | null) => / owner=(.+)$/m.exec(text ?? '')?.[1].trim() ?? null;
// the main checkout releases any hold, a worktree only its own, force (explicit flag) any
export const mayRelease = (text: string | null, me = CHECKOUT, main = MAIN_ROOT, force = false) => force || samePath(me, main) || samePath(holdOwner(text) ?? main, me);
const read = (f: string) => { try { return readFileSync(f, 'utf8').trim(); } catch { return null; } };
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const alive = (pid: number) => { try { process.kill(pid, 0); return true; } catch (e: any) { return e.code === 'EPERM'; } };

// queue.ts: before each job and the smoke job. One line on entry, one every 5 min, a warning after 60 min; waits on.
// A hold whose text has "allow=<name>" lets through only the queue.ts started with TS3_HOLD_ALLOW=<name> (the chain that owns the
// hold runs its own jobs; every other queue and the chains that gate on the hold file keep waiting).
export const holdAllows = (text: string | null, me = process.env.TS3_HOLD_ALLOW) => !!me && !!text && new RegExp(`(^|\\s)allow=${me.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(\\s|$)`).test(text);
export async function waitHold(log: (m: string) => void, dir = QUEUE_DIR, tickMs = 10000) {
  const f = join(dir, 'hold');
  if (!existsSync(f)) return;
  if (holdAllows(read(f))) { log(`hold file present (${read(f) ?? '?'}), allowed for ${process.env.TS3_HOLD_ALLOW}: going on`); return; }
  log(`hold file present (${read(f) ?? '?'}), waiting`);
  for (let t = 1; existsSync(f); t++) {
    await sleep(tickMs);
    if (t % 30 === 0) log(`hold: held ${t / 6} min (${read(f) ?? '?'})`);
    if (t === 360) log('WARN: hold file present for 60 min: is the build stuck or was `hold.ts off` forgotten? still waiting');
  }
  log('hold file removed, going on');
}
// marker "<id> <pid> owner=<checkout>" (owner: experimental.ts reads the job's queue file there, track.ts stop kills only its own)
export function markJob(id: string, dir = QUEUE_DIR, owner = CHECKOUT) { mkdirSync(dir, { recursive: true }); writeFileSync(join(dir, 'job-running'), `${id} ${process.pid} owner=${owner}\n`); }
// owner registry <queue dir>/owners/<pid> = checkout (queue.ts at start, removed on exit): track.ts stop kills only its checkout's
export function registerOwner(dir = QUEUE_DIR, pid = process.pid, owner = CHECKOUT) {
  const f = join(dir, 'owners', String(pid));
  mkdirSync(dirname(f), { recursive: true }); writeFileSync(f, `${owner}\n`);
  if (pid === process.pid) process.on('exit', () => rmSync(f, { force: true }));
}
export function readOwners(dir = QUEUE_DIR) {
  const d = join(dir, 'owners'), m = new Map<number, string>();
  try { for (const n of readdirSync(d)) { const o = read(join(d, n)); if (/^\d+$/.test(n) && o) m.set(Number(n), o); } } catch { /* none yet */ }
  return m;
}
export function clearJob(dir = QUEUE_DIR) { rmSync(join(dir, 'job-running'), { force: true }); }
// '' = no job running now, else the marker text ("<id> <pid>")
export function runningJob(dir = QUEUE_DIR): string {
  const s = read(join(dir, 'job-running'));
  return s && alive(Number(s.split(' ')[1])) ? s : '';
}

if (import.meta.main) {
  const [cmd, ...rest] = process.argv.slice(2);
  const log = (m: string) => console.log(`${new Date().toISOString().slice(11, 19)} hold: ${m}`);
  if (cmd === '--self-check') {
    const assert = (await import('node:assert')).strict;
    const d = mkdtempSync(join(tmpdir(), 'hold-')), lines: string[] = [];
    try {
      await waitHold(m => lines.push(m), d, 1); assert.deepEqual(lines, [], 'no hold file: no wait, no line');
      writeFileSync(join(d, 'hold'), 'crash triage x');
      await waitHold(m => { lines.push(m); if (m.startsWith('WARN')) rmSync(join(d, 'hold')); }, d, 0); // removed at the warning
      assert.match(lines[0], /hold file present \(crash triage x\), waiting/);
      assert.ok(lines.some(l => /held 5 min/.test(l)), 'heartbeat every 30 ticks');
      assert.ok(lines.some(l => /^WARN: .*60 min/.test(l)), 'warning after 360 ticks');
      assert.match(lines.at(-1)!, /removed/);
      assert.ok(holdAllows('timing run allow=chain-x @ 2026-11-20T12:00:00Z', 'chain-x'), 'owner allowed');
      assert.ok(!holdAllows('timing run allow=chain-x2 @ t', 'chain-x'), 'prefix of another name: not allowed');
      assert.ok(!holdAllows('timing run allow=chain-x @ t', undefined), 'no env: not allowed');
      assert.ok(!holdAllows('crash triage x', 'chain-x'), 'plain hold: not allowed');
      assert.equal(runningJob(d), '');
      markJob('j1', d, 'C:\wt a'); assert.match(runningJob(d), /^j1 \d+ owner=C:\wt a$/); assert.equal(holdOwner(runningJob(d)), 'C:\wt a', 'owner parse, path with a space');
      assert.equal(holdOwner('j1 42'), null, 'old marker: no owner');
      registerOwner(d, 4242, 'C:\wt'); registerOwner(d); writeFileSync(join(d, 'owners', 'junk'), 'x');
      assert.deepEqual([...readOwners(d)].sort(), [[4242, 'C:\wt'], [process.pid, CHECKOUT]].sort(), 'registry: pid -> checkout, junk skipped');
      writeFileSync(join(d, 'job-running'), 'j1 999999999'); assert.equal(runningJob(d), '', 'dead pid = ended');
      clearJob(d); assert.equal(runningJob(d), '');
      // one control dir for all checkouts: this checkout's git common dir names the main checkout
      assert.ok(samePath(mainRoot(CHECKOUT), MAIN_ROOT) && existsSync(join(MAIN_ROOT, '.git')), `main root ${MAIN_ROOT} has .git`);
      if (!process.env.TS3_QUEUE_DIR) assert.ok(samePath(QUEUE_DIR, join(MAIN_ROOT, 'probe-out', 'queue')), 'worktree queue dir = main checkout queue dir');
      assert.equal(mainRoot(tmpdir()), CHECKOUT, 'no git: own checkout');
      // ownership: worktree releases only its own hold; main and --force release any
      const main = join(d, 'main'), wt = join(main, '.claude', 'worktrees', 'lc'), other = join(main, '.claude', 'worktrees', 'x');
      const wtHold = holdText('build-window: merge allow=chain-x', wt, new Date('2026-11-23T04:57:00Z'));
      assert.equal(wtHold, `build-window: merge allow=chain-x @ 2026-11-23T04:57:00Z owner=${wt}`);
      assert.equal(holdOwner(wtHold), wt); assert.ok(holdAllows(wtHold, 'chain-x'), 'owner suffix keeps allow= working');
      assert.ok(mayRelease(wtHold, wt, main) && mayRelease(wtHold, main, main), 'owner and main release');
      assert.ok(!mayRelease(wtHold, other, main), 'another worktree may not');
      assert.ok(!mayRelease(holdText('build-window: merge', main), wt, main), 'main hold: worktree may not (the 11-23 bug)');
      assert.ok(!mayRelease('crash triage x @ 2026-11-23T04:57:00Z', wt, main), 'old hold without owner = main');
      assert.ok(mayRelease(holdText('x', main), wt, main, true), '--force');
    } finally { rmSync(d, { recursive: true, force: true }); }
    console.log('self-check ok');
  } else if (cmd === 'on' && rest.length) {
    mkdirSync(QUEUE_DIR, { recursive: true });
    writeFileSync(join(QUEUE_DIR, 'hold'), `${holdText(rest.join(' '))}\n`);
    log(`on: ${rest.join(' ')}${runningJob() ? `; job running: ${runningJob().split(' ')[0]} (wait-job-end next)` : '; no job running'}`);
  } else if (cmd === 'off') {
    const h = read(join(QUEUE_DIR, 'hold'));
    if (!mayRelease(h, CHECKOUT, MAIN_ROOT, rest.includes('--force'))) { log(`WARN: hold not set by this worktree (${h}), left in place: hold.ts off --force to release`); process.exit(1); }
    rmSync(join(QUEUE_DIR, 'hold'), { force: true }); log('off');
  } else if (cmd === 'wait-job-end') {
    const dropStale = () => { const s = read(join(QUEUE_DIR, 'job-running')); if (s && !runningJob()) { clearJob(); log(`removed stale job-running (${s}: pid dead)`); } };
    dropStale();
    const job = runningJob();
    if (!job) { log('no job running'); process.exit(0); }
    log(`waiting for ${job.split(' ')[0]} to end`);
    for (let t = 1; runningJob() === job; t++) { await sleep(5000); if (t % 60 === 0) log(`still running: ${job.split(' ')[0]} (${t / 12} min)`); }
    dropStale();
    log(`${job.split(' ')[0]} ended${existsSync(join(QUEUE_DIR, 'hold')) ? '' : ' (WARN: no hold file: the next job may start; run `hold.ts on` first)'}`);
  } else { console.error('usage: node tools/harness/hold.ts on <reason> | off [--force] | wait-job-end | --self-check'); process.exit(2); }
}
