// Queue hold for crash-triage builds (Rodney 2026-10-25, AGENTS.md Rules): a crash-triage logging/fix build starts the
// moment the running job ends, and queue.ts must not start the next job (or its measurement smoke) while it runs.
//   node tools/harness/hold.ts on <reason>    create probe-out/queue/hold (reason + UTC time); queue.ts waits before each job
//   node tools/harness/hold.ts off            remove it; the queue goes on within 10 s
//   node tools/harness/hold.ts wait-job-end   block until the job running now ends (queue.ts writes probe-out/queue/job-running
//                                             "<job id> <pid>" at job start and deletes it at job end; gone, another job, or a
//                                             dead queue pid = ended, stale marker removed). Run it after `on`, so no new job starts in between.
//   node tools/harness/hold.ts --self-check   temp dir only
import { existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

export const QUEUE_DIR = resolve(import.meta.dirname, '..', '..', 'probe-out', 'queue');
const read = (f: string) => { try { return readFileSync(f, 'utf8').trim(); } catch { return null; } };
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));
const alive = (pid: number) => { try { process.kill(pid, 0); return true; } catch (e: any) { return e.code === 'EPERM'; } };

// queue.ts: before each job and the smoke job. One line on entry, one every 5 min, a warning after 60 min; waits on.
export async function waitHold(log: (m: string) => void, dir = QUEUE_DIR, tickMs = 10000) {
  const f = join(dir, 'hold');
  if (!existsSync(f)) return;
  log(`hold file present (${read(f) ?? '?'}), waiting`);
  for (let t = 1; existsSync(f); t++) {
    await sleep(tickMs);
    if (t % 30 === 0) log(`hold: held ${t / 6} min (${read(f) ?? '?'})`);
    if (t === 360) log('WARN: hold file present for 60 min: is the build stuck or was `hold.ts off` forgotten? still waiting');
  }
  log('hold file removed, going on');
}
export function markJob(id: string, dir = QUEUE_DIR) { mkdirSync(dir, { recursive: true }); writeFileSync(join(dir, 'job-running'), `${id} ${process.pid}\n`); }
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
      assert.equal(runningJob(d), '');
      markJob('j1', d); assert.match(runningJob(d), /^j1 \d+$/);
      writeFileSync(join(d, 'job-running'), 'j1 999999999'); assert.equal(runningJob(d), '', 'dead pid = ended');
      clearJob(d); assert.equal(runningJob(d), '');
    } finally { rmSync(d, { recursive: true, force: true }); }
    console.log('self-check ok');
  } else if (cmd === 'on' && rest.length) {
    mkdirSync(QUEUE_DIR, { recursive: true });
    writeFileSync(join(QUEUE_DIR, 'hold'), `${rest.join(' ')} @ ${new Date().toISOString().slice(0, 19)}Z\n`);
    log(`on: ${rest.join(' ')}${runningJob() ? `; job running: ${runningJob().split(' ')[0]} (wait-job-end next)` : '; no job running'}`);
  } else if (cmd === 'off') {
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
  } else { console.error('usage: node tools/harness/hold.ts on <reason> | off | wait-job-end | --self-check'); process.exit(2); }
}
