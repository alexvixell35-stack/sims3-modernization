// monohost selftest stress loop (docs/139 §4.4): interleaves arms in private exe folders, keeps every failing log, and
// reports the failure rate per check. No game, no probe.
//   node tools/monohost-stress.ts --game-copy <dir> --out <dir> [--runs 60] [--workers 3] [--arms stock,sub] [--bench 3000]
//                                 [--exe native/monohost/out] [--ignore GM4-sweep] [--self-check]
// Arms: stock, sub (MONOHOST_MINTFUSE=sub), jit (MONOHOST_JIT=verify, minutes per run; use --bench 300), subjit (both).
// --runs is per arm.
// Stops new runs while a queue job with ab or timing in its name runs (hold file under --out says why).
import { spawn, execFileSync } from 'node:child_process';
import { copyFileSync, mkdirSync, readFileSync, writeFileSync, appendFileSync, existsSync } from 'node:fs';
import { join, resolve } from 'node:path';
import { parseArgs } from 'node:util';

const ARM_ENV: Record<string, Record<string, string>> = {
  stock: {},
  sub: { MONOHOST_MINTFUSE: 'sub' },
  jit: { MONOHOST_JIT: 'verify' },
  subjit: { MONOHOST_MINTFUSE: 'sub', MONOHOST_JIT: 'verify' },
};

interface RunResult { jitMismatch: number; fails: string[]; passed: number; failed: number; checksum: string | null; mainPasses: number | null; done: boolean }

export function parseLog(text: string): RunResult {
  const fails = [...text.matchAll(/^FAIL (\S+)/gm)].map(m => m[1]);
  const end = /^SELFTEST (OK|FAILED): (\d+) passed, (\d+) failed/m.exec(text);
  const sums = [...text.matchAll(/^mintbench mode \d+ n \d+: best [\d.]+ ms, checksum (-?\d+)/gm)];
  const a2 = /^AS G-A2: .*?main passes (\d+)/m.exec(text);
  const jv = /^jit verify: calls \d+, verified ok \d+, MISMATCH (\d+)/m.exec(text);
  return {
    jitMismatch: jv ? +jv[1] : 0, fails, passed: end ? +end[2] : 0, failed: end ? +end[3] : 0, done: !!end,
    checksum: sums.length ? sums[sums.length - 1][1] : null, mainPasses: a2 ? +a2[1] : null,
  };
}

// a word or dash-separated part of the job name, so "stable" or "saved-stacks" do not hold the loop
export const isTimingJob = (cmd: string) => /queue\.ts/.test(cmd) && /(^|[-_\/\\.\s])(ab|timing)([-_.\s]|$)/i.test(cmd);

function timingJobRunning(): string {
  if (process.platform !== 'win32') return '';
  const out = execFileSync('powershell', ['-NoProfile', '-Command',
    "Get-CimInstance Win32_Process -Filter \"Name='node.exe'\" | % CommandLine"], { encoding: 'utf8', timeout: 30000 });
  return out.split(/\r?\n/).find(isTimingJob) ?? '';
}

function selfCheck() {
  const r = parseLog('PASS a x\nFAIL AS-G-A2 y\nAS G-A2: 250 requests (200 measured) in 9 ms: runs 200, main passes 195 (wrong 0)\n' +
    'mintbench mode 0 n 3000: best 1.0 ms, checksum -466393015\nSELFTEST FAILED: 38 passed, 2 failed\nFAIL GM4-sweep z\n');
  console.assert(r.fails.join() === 'AS-G-A2,GM4-sweep' && r.passed === 38 && r.failed === 2 && r.done);
  console.assert(r.checksum === '-466393015' && r.mainPasses === 195);
  console.assert(!parseLog('crash').done && parseLog('jit verify: calls 9, verified ok 8, MISMATCH 4, x\n').jitMismatch === 4);
  console.assert(isTimingJob('node tools/harness/queue.ts tools/harness/queues/mintcall-ab.json'));
  console.assert(isTimingJob('node tools/harness/queue.ts q/tool-timing-1.json'));
  console.assert(!isTimingJob('node tools/harness/queue.ts tools/harness/queues/saved-stacks-matrix2.json --no-smoke'));
  console.assert(!isTimingJob('node tools/foo-ab.ts'));
  console.log('monohost-stress self-check ok');
}

async function main() {
  const { values: v } = parseArgs({ options: {
    'game-copy': { type: 'string' }, out: { type: 'string' }, runs: { type: 'string', default: '60' },
    workers: { type: 'string', default: '3' }, arms: { type: 'string', default: 'stock,sub' }, bench: { type: 'string', default: '3000' },
    exe: { type: 'string', default: 'native/monohost/out' }, ignore: { type: 'string', default: '' }, 'self-check': { type: 'boolean' },
  } });
  if (v['self-check']) return selfCheck();
  if (!v['game-copy'] || !v.out) { console.error('usage: see the head of tools/monohost-stress.ts'); process.exit(2); }
  const gc = resolve(v['game-copy']), out = resolve(v.out), arms = v.arms!.split(','), runs = +v.runs!, ignore = new Set(v.ignore!.split(','));
  for (const a of arms) if (!ARM_ENV[a]) { console.error(`unknown arm ${a}`); process.exit(2); }
  mkdirSync(join(out, 'fail'), { recursive: true });
  const queue: string[] = [];
  for (let i = 0; i < runs; i++) for (const a of arms) queue.push(a); // interleaved: same machine load for every arm
  const total = queue.length;
  const stats: Record<string, { n: number; fail: number; hang: number; jit: number; checks: Record<string, number>; sums: Set<string>; mp: number[] }> = {};
  for (const a of arms) stats[a] = { n: 0, fail: 0, hang: 0, jit: 0, checks: {}, sums: new Set(), mp: [] };
  const csv = join(out, 'runs.csv');
  if (!existsSync(csv)) writeFileSync(csv, 'at,arm,worker,exit,fails,mainPasses,checksum,jitMismatch\n');
  let seq = 0;

  async function worker(w: number) {
    const dir = join(out, `w${w}`);
    mkdirSync(dir, { recursive: true });
    for (const f of ['monohost.exe', 'MonoHostTest.dll']) copyFileSync(resolve(v.exe!, f), join(dir, f));
    for (;;) {
      const arm = queue.shift();
      if (!arm) return;
      for (let job = timingJobRunning(); job; job = timingJobRunning()) {
        writeFileSync(join(out, 'hold'), job);
        console.log(`hold: timing job running: ${job.slice(0, 120)}`);
        await new Promise(r => setTimeout(r, 60000));
      }
      const n = ++seq;
      const code = await new Promise<number>(res => {
        const p = spawn(join(dir, 'monohost.exe'), [join(gc, 'monohost', 'TS3W.exe'), join(gc, 'assemblies'), 'selftest', join(gc, 'monohost', 'hostdata.txt')],
          // shortcut: the timeout kills only monohost's launcher parent, its suspended-start child runs on; long so it never fires on a verify arm
          { stdio: 'ignore', env: { ...process.env, MONOHOST_MINTBENCH: v.bench!, ...ARM_ENV[arm] }, timeout: 3600000 });
        p.on('exit', c => res(c ?? -1));
        p.on('error', () => res(-2));
      });
      // the launcher exits first; its child runs on with the log open
      const childRunning = () => execFileSync('powershell', ['-NoProfile', '-Command', `(Get-CimInstance Win32_Process -Filter "Name='monohost.exe'" | ? { $_.ExecutablePath -eq '${join(dir, 'monohost.exe')}' }).Count`], { encoding: 'utf8', timeout: 30000 }).trim();
      for (;;) {
        await new Promise(r => setTimeout(r, 4000));
        const c = childRunning();
        if (!c || c === '0') break;
      }
      let log = '';
      for (;;) { try { log = readFileSync(join(dir, 'monohost.exe.log'), 'utf8'); break; } catch { await new Promise(r => setTimeout(r, 2000)); } }
      const r = parseLog(log), s = stats[arm], real = r.fails.filter(x => !ignore.has(x));
      s.n++;
      for (const f of real) s.checks[f] = (s.checks[f] ?? 0) + 1;
      if (real.length || !r.done) {
        s.fail++;
        if (!r.done) s.hang++;
      }
      s.jit += r.jitMismatch;
      // the §4.4 failure was lost to an overwritten log
      if (real.length || !r.done || r.jitMismatch) writeFileSync(join(out, 'fail', `${arm}-${n}.log`), log);
      if (r.checksum) s.sums.add(r.checksum);
      if (r.mainPasses !== null) s.mp.push(r.mainPasses);
      appendFileSync(csv, `${new Date().toISOString()},${arm},${w},${code},${r.fails.join(' ')},${r.mainPasses ?? ''},${r.checksum ?? ''},${r.jitMismatch}\n`);
      if (n % 10 === 0 || real.length || !r.done) console.log(`${n}/${total} ${arm}: ${r.done ? r.passed + ' passed' : 'no end line, exit ' + code}${real.length ? ' FAIL ' + real.join(' ') : ''}`);
    }
  }
  await Promise.all(Array.from({ length: +v.workers! }, (_, w) => worker(w)));
  for (const a of arms) {
    const s = stats[a], mp = [...s.mp].sort((x, y) => x - y);
    console.log(`${a}: ${s.fail} of ${s.n} failed (no end line ${s.hang}); checks ${JSON.stringify(s.checks)}; checksums ${[...s.sums].join(' ')}; ` +
      `AS-G-A2 main passes min ${mp[0]} median ${mp[mp.length >> 1]}; jit verify mismatches ${s.jit}`);
  }
}
await main();
