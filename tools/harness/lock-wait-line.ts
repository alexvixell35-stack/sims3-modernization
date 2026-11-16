// docs/264 §4.4 (issue #338): when an agent call times out, the likely cause is a sim thread frozen on the route lock, not a
// missing agent command. lastLockWaitLine(text) returns the last ts3native.log line that says so (the 2 s "lock ... busy" line or
// the >= 1 s "sim waited" line), or '' if there is none. profile-world.ts prints it next to the agent FAIL.
//   node tools/harness/lock-wait-line.ts --self-check
import { closeSync, openSync, readSync, statSync } from 'node:fs';
import { resolve } from 'node:path';

const RE = /^route async: (lock [0-9A-Fa-f]+ busy for \d+ s during the job|sim waited \d+ ms on lock )/;

export function lastLockWaitLine(text: string): string {
  const lines = text.split(/\r?\n/);
  for (let i = lines.length - 1; i >= 0; i--) if (RE.test(lines[i])) return lines[i];
  return '';
}

// the last 256 KB of the log (a freeze line is recent); '' if the file cannot be read
export function lastLockWaitLineInFile(path: string): string {
  try {
    const size = statSync(path).size, n = Math.min(size, 256 * 1024), b = Buffer.alloc(n), fd = openSync(path, 'r');
    try { readSync(fd, b, 0, n, size - n); } finally { closeSync(fd); }
    return lastLockWaitLine(b.toString('latin1'));
  } catch { return ''; }
}

if (process.argv.includes('--self-check') && import.meta.filename === resolve(process.argv[1] ?? '')) {
  const busy = 'route async: lock 0D5CAD08 busy for 18 s during the job of route 25025701, still waiting (owner thread 11108: worker 11108, replay consumer 0, last gate acquirer 11108; async state 1, P4 phase 1, replay phase 0x0)';
  const waited = 'route async: sim waited 1432 ms on lock 0D5CAD08 (route lock), last job seen route 252A49F2: own START, IC kind 2, scope unseen, watchdog check, uncut: watchdog';
  const other = [
    'route async: long job 9784 ms, own START, IC kind 2, route 252A49F2, watchdog check, arm -, scope unseen, at 256788046 ms',
    'route async: thread 1234 waited 2 s for the job of route 25025701, still waiting',
    'diag: rc holders #3: skipped, cache lock busy for 12 ms',
  ];
  const fail = (m: string) => { console.error(`lock-wait-line self-check FAILED: ${m}`); process.exit(1); };
  if (lastLockWaitLine([busy, ...other].join('\n')) !== busy) fail('busy line not found');
  if (lastLockWaitLine([busy, waited, other[0]].join('\r\n')) !== waited) fail('not the last matching line');
  if (lastLockWaitLine(other.join('\n')) !== '') fail('matched a non-wait line');
  if (lastLockWaitLineInFile(resolve('no-such-ts3native.log')) !== '') fail('missing file');
  console.log('lock-wait-line self-check ok');
}
