// Log watcher for agent Monitors, replacing `tail -F log | grep ...`. On Windows (Git Bash) a Monitor timeout kills the shell but
// not tail.exe, so every re-armed watch left one more `tail -F` behind (54 piled up 2026-11-11..13). This script reads the file
// itself (no child process) and always exits: on --until, on --timeout, or when --pid is gone.
//   node tools/harness/watch.ts <log> [--match <re>] [--until <re>] [--timeout <s>] [--pid <n>] [--from-start]
//   node tools/harness/watch.ts --self-check
// Prints each new line matching --match (default: every line matching --until, else every line). Exit 0 on --until or dead --pid,
// 2 on --timeout (re-arm), 1 on bad arguments. Starts at the end of the file unless --from-start; follows truncation/rotation.
import { closeSync, existsSync, mkdtempSync, openSync, readSync, rmSync, statSync, writeFileSync, appendFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { parseArgs } from 'node:util';

export type WatchOpts = { file: string; match?: RegExp; until?: RegExp; timeoutS: number; pid?: number; fromStart?: boolean; pollMs?: number };

const alive = (pid: number) => { try { process.kill(pid, 0); return true; } catch (e: any) { return e?.code === 'EPERM'; } };

export async function watch(o: WatchOpts, out: (l: string) => void = l => console.log(l)): Promise<'until' | 'timeout' | 'pid'> {
  const deadline = Date.now() + o.timeoutS * 1000, poll = o.pollMs ?? 1000;
  let pos = !o.fromStart && existsSync(o.file) ? statSync(o.file).size : 0, rest = '';
  for (;;) {
    if (existsSync(o.file)) {
      const size = statSync(o.file).size;
      if (size < pos) { pos = 0; rest = ''; }  // truncated or replaced: read the new file from its start
      if (size > pos) {
        const fd = openSync(o.file, 'r'), buf = Buffer.alloc(size - pos);
        try { readSync(fd, buf, 0, buf.length, pos); } finally { closeSync(fd); }
        pos = size;
        const lines = (rest + buf.toString('utf8')).split(/\r?\n/);
        rest = lines.pop() ?? '';
        for (const l of lines) {
          if (o.match ? o.match.test(l) : !o.until || o.until.test(l)) out(l);
          if (o.until?.test(l)) return 'until';
        }
      }
    }
    if (o.pid && !alive(o.pid)) { out(`watch: pid ${o.pid} gone`); return 'pid'; }
    if (Date.now() >= deadline) { out(`watch: timeout after ${o.timeoutS} s (re-arm)`); return 'timeout'; }
    await new Promise(r => setTimeout(r, poll));
  }
}

async function selfCheck() {
  let bad = false; const ok = (c: boolean, m: string) => { if (!c) { bad = true; console.error(`watch self-check: ${m}`); } };
  const d = mkdtempSync(join(tmpdir(), 'watch-')), f = join(d, 'q.log');
  try {
    writeFileSync(f, 'old: queue: x: FAIL\n');
    const got: string[] = [];
    const p = watch({ file: f, match: /queue: \S+: (PASS|FAIL)|crash/, until: /QUEUE DONE/, timeoutS: 10, pollMs: 20 }, l => got.push(l));
    setTimeout(() => appendFileSync(f, 'noise line\nqueue: a: PASS\ngame crashed at 0x1\npartial'), 60);
    setTimeout(() => appendFileSync(f, ' line\nQUEUE DONE\n'), 120);
    ok(await p === 'until', 'until not hit');
    ok(got.join('|') === 'queue: a: PASS|game crashed at 0x1', `matched ${JSON.stringify(got)} (old line and noise must be skipped)`);
    ok(await watch({ file: f, until: /never/, timeoutS: 0.1, pollMs: 20 }, () => {}) === 'timeout', 'timeout not reported');
    ok(await watch({ file: join(d, 'missing.log'), timeoutS: 5, pid: 2 ** 30, pollMs: 20 }, () => {}) === 'pid', 'dead pid not reported');
    const g2: string[] = [];
    ok(await watch({ file: f, until: /^QUEUE DONE$/, timeoutS: 5, fromStart: true, pollMs: 20 }, l => g2.push(l)) === 'until' && g2.length === 1, '--from-start');
  } finally { rmSync(d, { recursive: true, force: true }); }
  console.log(bad ? 'watch self-check: FAIL' : 'watch self-check: PASS');
  process.exit(bad ? 1 : 0);
}

if (/[\\/]watch\.ts$/.test(process.argv[1] ?? '')) {
  if (process.argv.includes('--self-check')) await selfCheck();
  else {
    const { values: v, positionals: p } = parseArgs({ allowPositionals: true, options: { match: { type: 'string' }, until: { type: 'string' }, timeout: { type: 'string', default: '1800' }, pid: { type: 'string' }, 'from-start': { type: 'boolean' } } });
    if (!p[0]) { console.error('usage: node tools/harness/watch.ts <log> [--match re] [--until re] [--timeout s] [--pid n] [--from-start]'); process.exit(1); }
    const r = await watch({ file: p[0], match: v.match ? new RegExp(v.match) : undefined, until: v.until ? new RegExp(v.until) : undefined, timeoutS: Number(v.timeout), pid: v.pid ? Number(v.pid) : undefined, fromStart: v['from-start'] });
    process.exit(r === 'timeout' ? 2 : 0);
  }
}
