// Switch test tracks without stale state (Rodney 2026-10-30): chain scripts (probe-out/queue/chain-*.sh, mono-loop.sh
// under nohup) and queue.ts processes killed by hand left build holds, a job-running marker with a dead pid, a stale
// main.lock and `tail -F` watchers. One command each, short output. Always works on the MAIN checkout's probe-out/queue
// (git common dir), also when run from a worktree; TS3_QUEUE_DIR overrides.
//   node tools/harness/track.ts status                       hold, job, game, chains, queues, main.lock, watchers; flags stale
//   node tools/harness/track.ts stop [--after-job]           stop chains + queue.ts (+ job tree + TS3W.exe unless --after-job), clean
//   node tools/harness/track.ts switch <script.sh> [--after-job]   stop, then start the script detached (out: probe-out/queue/<name>.out)
//   node tools/harness/track.ts wait-chain <script.sh>         block until that chain, every queue.ts and the job are gone (use in the next chain)
//   node tools/harness/track.ts build-window <reason>       hold "build-window: <reason>", wait for the running job, "ok to build"
//   node tools/harness/track.ts build-done                   release the hold if it is a build-window hold
//   node tools/harness/track.ts --self-check                 temp dir, fake process list
import { execFileSync, spawn } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, openSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';

export type Proc = { pid: number; ppid: number; name: string; cmd: string; start?: string };
type Env = { dir: string; procs: () => Proc[]; kill: (pid: number) => void; sleepMs: number; log: (m: string) => void };

const read = (f: string) => { try { return readFileSync(f, 'utf8').trim(); } catch { return null; } };
// max(0): clock skew on a fresh dir gave < 0, read as "absent"
const ageMin = (f: string) => { try { return Math.max(0, Date.now() - statSync(f).mtimeMs) / 60000; } catch { return -1; } };
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));

function mainQueueDir() {
  if (process.env.TS3_QUEUE_DIR) return resolve(process.env.TS3_QUEUE_DIR);
  try {
    const common = execFileSync('git', ['rev-parse', '--path-format=absolute', '--git-common-dir'], { cwd: import.meta.dirname, timeout: 10000 }).toString().trim();
    return join(dirname(common), 'probe-out', 'queue');
  } catch { return resolve(import.meta.dirname, '..', '..', 'probe-out', 'queue'); }
}
function realProcs(): Proc[] {
  const ps = `Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,CommandLine,@{n='S';e={$_.CreationDate.ToString('s')}} | ConvertTo-Json -Compress`;
  const out = execFileSync('powershell', ['-NoProfile', '-Command', ps], { timeout: 30000, maxBuffer: 64 << 20 }).toString();
  return (JSON.parse(out) as any[]).map(p => ({ pid: p.ProcessId, ppid: p.ParentProcessId, name: p.Name ?? '', cmd: p.CommandLine ?? '', start: p.S }));
}
const realKill = (pid: number) => { try { execFileSync('taskkill', ['/T', '/F', '/PID', String(pid)], { stdio: 'ignore', timeout: 15000 }); } catch { /* gone */ } };

// classification (exported for the self-check)
const CHAIN = /([\w.-]*(?:chain-[\w.-]*|mono-loop[\w.-]*|probe-out[\\/]queue[\\/][\w.-]+)\.sh)\b/;
export function classify(all: Proc[], self = process.pid) {
  // never list or kill ourselves or our ancestors (the calling shell's command line names the chain script too)
  const byPid = new Map(all.map(p => [p.pid, p])), mine = new Set<number>();
  for (let p = self; p && !mine.has(p); p = byPid.get(p)?.ppid ?? 0) mine.add(p);
  const ps = all.filter(p => !mine.has(p.pid));
  const pids = new Set(all.map(p => p.pid));
  const bashes = ps.filter(p => /^(bash|sh)(\.exe)?$/i.test(p.name) && CHAIN.test(p.cmd));
  // nohup'd scripts show as a bash with a bash parent running the same script: keep the outermost
  const chains = bashes.filter(p => !bashes.some(q => q.pid === p.ppid)).map(p => ({ ...p, script: basename(CHAIN.exec(p.cmd)![1].replace(/\\/g, '/')) }));
  const queues = ps.filter(p => /^node(\.exe)?$/i.test(p.name) && p.pid !== self && /harness[\\/]queue\.ts/.test(p.cmd) && !/--self-check|--dry-run/.test(p.cmd))
    .map(p => ({ ...p, queue: /--queue[ =]"?([^\s"]+)/.exec(p.cmd)?.[1] ?? /([^\s"\\/]+\.json)/.exec(p.cmd)?.[1] ?? '?' }));
  const tails = ps.filter(p => /^tail(\.exe)?$/i.test(p.name) && /-F/.test(p.cmd) && /probe-out[\\/]queue/.test(p.cmd))
    .map(p => ({ ...p, orphan: !pids.has(p.ppid) || !ps.some(q => q.pid === p.ppid && /bash|sh|node/i.test(q.name)) }));
  const game = ps.filter(p => /^TS3W\.exe$/i.test(p.name));
  const builds = ps.filter(p => /^(cl|link|csc|msbuild)\.exe$/i.test(p.name));
  return { chains, queues, tails, game, builds, alive: (pid: number) => pids.has(pid) };
}

function jobState(e: Env, alive: (pid: number) => boolean) {
  const s = read(join(e.dir, 'job-running'));
  if (!s) return null;
  const [id, pid] = s.split(' ');
  return { id, pid: Number(pid), alive: alive(Number(pid)) };
}

// stale markers; returns what it did
export function clean(e: Env, c = classify(e.procs())) {
  const did: string[] = [];
  const job = jobState(e, c.alive);
  if (job && !job.alive) { rmSync(join(e.dir, 'job-running'), { force: true }); did.push(`removed stale job-running (${job.id}, pid ${job.pid} dead)`); }
  const lock = join(e.dir, 'main.lock'), age = ageMin(lock);
  if (age >= 0 && (age > 30 || !c.builds.length)) { rmSync(lock, { recursive: true, force: true }); did.push(`removed main.lock (${age.toFixed(0)} min, ${c.builds.length ? 'old' : 'no build running'})`); }
  const orphans = c.tails.filter(t => t.orphan);
  for (const t of orphans) e.kill(t.pid);
  if (orphans.length) did.push(`killed ${orphans.length} orphan tail watcher(s)`);
  return did;
}

const holdFile = (e: Env) => join(e.dir, 'hold');
function setHold(e: Env, reason: string) { mkdirSync(e.dir, { recursive: true }); writeFileSync(holdFile(e), `${reason} @ ${new Date().toISOString().slice(0, 19)}Z\n`); }

// wait until job-running is gone, names another job, or its pid is dead (stale marker removed)
async function waitJobEnd(e: Env) {
  const first = jobState(e, classify(e.procs()).alive);
  if (!first) return 'no job running';
  for (let t = 1; ; t++) {
    const j = jobState(e, classify(e.procs()).alive);
    if (!j || j.id !== first.id) return `${first.id} ended`;
    if (!j.alive) { rmSync(join(e.dir, 'job-running'), { force: true }); return `${first.id}: pid ${j.pid} dead, stale marker removed`; }
    if (t % 60 === 0) e.log(`still running: ${first.id} (${t * e.sleepMs / 60000} min)`);
    await sleep(e.sleepMs);
  }
}

// next chain waits for the previous chain's PROCESS, not its last echo line: chain-sat8 (10-31 02:55 PHT) exited after
// its report without printing "CHAIN SAT8 DONE" and the grep-waiting chain sat idle 3 h
export async function waitChain(e: Env, script: string) {
  const name = basename(script);
  for (let t = 1; ; t++) {
    const c = classify(e.procs()), j = jobState(e, c.alive);
    if (!c.chains.some(p => p.script === name) && !c.queues.length && !j?.alive) return `${name} gone, no queue or job running`;
    if (t % 60 === 0) e.log(`waiting for ${name} (${t * e.sleepMs / 60000} min)`);
    await sleep(e.sleepMs);
  }
}

export async function stop(e: Env, afterJob: boolean) {
  const did: string[] = [];
  if (afterJob) { setHold(e, 'track stop'); did.push(`hold "track stop"; ${await waitJobEnd(e)}`); }
  let c = classify(e.procs());
  for (const p of c.chains) e.kill(p.pid);
  for (const p of c.queues) e.kill(p.pid);
  if (c.chains.length || c.queues.length) did.push(`killed ${c.chains.map(p => `${p.script}(${p.pid})`).concat(c.queues.map(p => `queue ${p.queue}(${p.pid})`)).join(', ')}`);
  if (!afterJob) {
    const j = jobState(e, c.alive);
    if (j?.alive) { e.kill(j.pid); did.push(`killed job ${j.id} tree (pid ${j.pid})`); }
    for (const g of c.game) e.kill(g.pid);
    if (c.game.length) did.push(`killed TS3W.exe (${c.game.map(g => g.pid).join(', ')}): check Game\\Bin pair before the next run`);
  }
  c = classify(e.procs());
  did.push(...clean(e, c));
  if (existsSync(holdFile(e))) { did.push(`released hold (${read(holdFile(e))})`); rmSync(holdFile(e), { force: true }); }
  return did.length ? did : ['nothing to stop'];
}

export function status(e: Env) {
  const c = classify(e.procs()), L: string[] = [];
  const h = read(holdFile(e));
  L.push(`hold:        ${h ? `${h} (${ageMin(holdFile(e)).toFixed(0)} min)` : '-'}`);
  const j = jobState(e, c.alive);
  L.push(`job:         ${j ? `${j.id} pid ${j.pid} ${j.alive ? 'alive' : 'DEAD (stale marker)'}` : '-'}`);
  L.push(`game:        ${c.game.length ? `TS3W.exe ${c.game.map(g => g.pid).join(', ')}` : '-'}`);
  L.push(`chains:      ${c.chains.length ? c.chains.map(p => `${p.script} pid ${p.pid} since ${p.start ?? '?'}`).join('; ') : '-'}`);
  L.push(`queues:      ${c.queues.length ? c.queues.map(p => `${p.queue} pid ${p.pid}`).join('; ') : '-'}`);
  const la = ageMin(join(e.dir, 'main.lock'));
  L.push(`main.lock:   ${la < 0 ? '-' : `${la.toFixed(0)} min${la > 30 || !c.builds.length ? ' STALE' : ''}${c.builds.length ? ` (build running: ${c.builds.length})` : ''}`}`);
  const orph = c.tails.filter(t => t.orphan).length;
  L.push(`watchers:    ${c.tails.length} tail -F${orph ? ` (${orph} ORPHAN)` : ''}`);
  if (h && !j && !c.queues.length && !c.chains.length) L.push('STALE: hold set but nothing running');
  if (h?.startsWith('build-window') && !c.builds.length && ageMin(holdFile(e)) > 30) L.push('STALE?: build-window hold > 30 min, no build process: `build-done` when done');
  return L;
}

function env(): Env {
  return { dir: mainQueueDir(), procs: realProcs, kill: realKill, sleepMs: 5000, log: m => console.log(`${new Date().toISOString().slice(11, 19)} track: ${m}`) };
}

if (import.meta.main) {
  const die = (e: any) => { console.error(`track: ERROR ${e?.stack ?? e}`); process.exit(1); };
  process.on('uncaughtException', die); process.on('unhandledRejection', die);
  const args = process.argv.slice(2), afterJob = args.includes('--after-job');
  const [cmd, ...rest] = args.filter(a => a !== '--after-job');
  if (cmd === '--self-check') {
    const assert = (await import('node:assert')).strict;
    const d = mkdtempSync(join(tmpdir(), 'track-'));
    try {
      let ps: Proc[] = [
        { pid: 10, ppid: 1, name: 'bash.exe', cmd: 'bash probe-out/queue/chain-mono.sh' },
        { pid: 11, ppid: 10, name: 'bash.exe', cmd: 'bash probe-out/queue/chain-mono.sh' },
        { pid: 12, ppid: 10, name: 'node.exe', cmd: 'node tools/harness/queue.ts --queue mono-x' },
        { pid: 13, ppid: 999, name: 'tail.exe', cmd: 'tail -n 0 -F probe-out/queue/a.log' },
        { pid: 14, ppid: 10, name: 'tail.exe', cmd: 'tail -n 0 -F probe-out/queue/b.log' },
        { pid: 15, ppid: 12, name: 'TS3W.exe', cmd: 'TS3W.exe' },
        { pid: 16, ppid: 1, name: 'bash.exe', cmd: 'bash -c ls' },
      ];
      const killed: number[] = [];
      const e: Env = { dir: d, procs: () => ps, kill: pid => { killed.push(pid); ps = ps.filter(p => p.pid !== pid && p.ppid !== pid); }, sleepMs: 1, log: () => {} };
      const c = classify(ps, 0);
      assert.deepEqual(c.chains.map(p => [p.pid, p.script]), [[10, 'chain-mono.sh']], 'outermost chain bash only');
      assert.deepEqual(c.queues.map(p => p.queue), ['mono-x']);
      assert.deepEqual(c.tails.map(t => t.orphan), [true, false]);
      // caller chain: bash 30 (cmd names the script) -> node 31 = self: neither listed
      const caller = [...ps, { pid: 30, ppid: 1, name: 'bash.exe', cmd: 'bash -c "node track.ts switch probe-out/queue/mono-loop.sh"' }, { pid: 31, ppid: 30, name: 'node.exe', cmd: 'node track.ts' }];
      assert.deepEqual(classify(caller, 31).chains.map(p => p.pid), [10], 'self and ancestors excluded');
      // stale job-running (dead pid) + stale main.lock (no build process)
      writeFileSync(join(d, 'job-running'), 'j1 777'); mkdirSync(join(d, 'main.lock'));
      assert.ok(status(e).some(l => /DEAD/.test(l)) && status(e).some(l => /main\.lock: .*STALE/.test(l)));
      const did = clean(e);
      assert.ok(!existsSync(join(d, 'job-running')) && !existsSync(join(d, 'main.lock')), 'stale markers removed');
      assert.ok(did.some(l => /orphan tail/.test(l)) && killed.includes(13) && !killed.includes(14));
      // fresh lock with a build running stays
      mkdirSync(join(d, 'main.lock')); ps.push({ pid: 20, ppid: 1, name: 'cl.exe', cmd: 'cl' });
      clean(e); assert.ok(existsSync(join(d, 'main.lock')), 'lock kept while a build runs');
      // build-window: tagged hold, live job ends when its marker goes, dead pid = ended
      writeFileSync(join(d, 'job-running'), 'j2 12');
      setHold(e, 'build-window: x'); assert.match(read(holdFile(e))!, /^build-window: x @/);
      const p = waitJobEnd(e); setTimeout(() => rmSync(join(d, 'job-running')), 20);
      assert.equal(await p, 'j2 ended');
      writeFileSync(join(d, 'job-running'), 'j3 4242');
      assert.match(await waitJobEnd(e), /dead, stale marker removed/); assert.ok(!existsSync(join(d, 'job-running')));
      // stop: kills chain, queue, game; releases hold
      writeFileSync(join(d, 'job-running'), 'j4 12');
      const out = await stop(e, false);
      assert.ok([10, 12, 15].every(pid => killed.includes(pid)) && !killed.includes(16), 'unrelated bash untouched');
      assert.ok(!existsSync(holdFile(e)) && out.some(l => /released hold/.test(l)) && !existsSync(join(d, 'job-running')));
      // wait-chain: returns once the chain and its queue are gone, not before
      ps = [{ pid: 20, ppid: 1, name: 'bash.exe', cmd: 'bash probe-out/queue/chain-a.sh' }, { pid: 21, ppid: 20, name: 'node.exe', cmd: 'node tools/harness/queue.ts x.json' }];
      let ticks = 0; const e2: Env = { ...e, procs: () => ps, sleepMs: 0 };
      const w = waitChain({ ...e2, procs: () => { if (++ticks === 3) ps = [ps[1]]; if (ticks === 5) ps = []; return ps; } }, 'probe-out/queue/chain-a.sh');
      assert.match(await w, /chain-a\.sh gone/); assert.equal(ticks, 5, 'waits for the orphaned queue too');
    } finally { rmSync(d, { recursive: true, force: true }); }
    console.log('self-check ok');
  } else if (cmd === 'status') {
    const e = env(); console.log(`queue dir:   ${e.dir}`); for (const l of status(e)) console.log(l);
  } else if (cmd === 'stop' || (cmd === 'switch' && rest[0])) {
    const e = env();
    let script = '';
    if (cmd === 'switch') { script = resolve(rest[0]); if (!existsSync(script)) { console.error(`no such script: ${script}`); process.exit(2); } }
    for (const l of (await stop(e, afterJob)).slice(0, 10)) console.log(l);
    if (script) {
      const git = 'C:\\Program Files\\Git\\bin\\bash.exe'; // not System32 bash.exe (WSL)
      const outF = join(e.dir, `${basename(script).replace(/\.sh$/, '')}.out`), fd = openSync(outF, 'a');
      const ch = spawn(existsSync(git) ? git : 'bash', [script], { cwd: dirname(dirname(e.dir)), detached: true, stdio: ['ignore', fd, fd], windowsHide: true });
      ch.unref();
      console.log(`started ${basename(script)} pid ${ch.pid}, out ${outF}`);
    }
  } else if (cmd === 'wait-chain' && rest[0]) {
    const e = env(); console.log(`${new Date().toISOString().slice(11, 19)} track: ${await waitChain(e, rest[0])}`);
  } else if (cmd === 'build-window' && rest.length) {
    const e = env();
    setHold(e, `build-window: ${rest.join(' ')}`);
    e.log(await waitJobEnd(e)); console.log('ok to build');
  } else if (cmd === 'build-done') {
    const e = env(), h = read(holdFile(e));
    if (h?.startsWith('build-window')) { rmSync(holdFile(e), { force: true }); console.log(`released: ${h}`); }
    else console.log(`WARN: ${h ? `hold is not a build-window hold (${h}), left in place` : 'no hold set'}`);
  } else { console.error('usage: node tools/harness/track.ts status | stop [--after-job] | switch <script.sh> [--after-job] | wait-chain <script.sh> | build-window <reason> | build-done | --self-check'); process.exit(2); }
}
