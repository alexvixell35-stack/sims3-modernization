// Switch test tracks without stale state (Rodney 2026-10-30): chain scripts (probe-out/queue/chain-*.sh, mono-loop.sh
// under nohup) and queue.ts processes killed by hand left build holds, a job-running marker with a dead pid, a stale
// main.lock and `tail -F` watchers. One command each, short output. Always works on the MAIN checkout's probe-out/queue
// (git common dir, hold.ts QUEUE_DIR), also when run from a worktree; TS3_QUEUE_DIR overrides. A worktree releases only a hold
// it set (owner=<checkout>), else --force-hold (2026-11-23: `stop` from a worktree released the main checkout's build window).
//   node tools/harness/track.ts status                       hold, job, game, chains, queues, main.lock, watchers; flags stale
//   node tools/harness/track.ts stop [--after-job] [--force-hold] [--all]  stop this checkout's chains + queue.ts (+ job tree + TS3W.exe unless --after-job), clean
//   (--all: every checkout's and unknown-owner ones; without it those are listed, not killed). Everything under a stopped chain (by
//   Windows or MSYS parent: its queue.ts from a worktree too) goes with it. --after-job: hold, wait for the job and for the chain's
//   queue.ts to sit idle on the hold, TERM the chain bash so its EXIT trap runs (Bin restore, agent package,
//   chain lock; track does that work when no "Bin restored" marker shows), then kill what ran under it (2026-11-27 x2)
//   node tools/harness/track.ts switch <script.sh> [--after-job]   stop, then start the script detached (out: probe-out/queue/<name>.out)
//   node tools/harness/track.ts wait-chain <script.sh>         block until that chain, every queue.ts and the job are gone (use in the next chain)
//   node tools/harness/track.ts marker [--chain <name>] <text>  in a chain: prints "marker: <time> <text>" (the .out) and appends it to
//                                                             probe-out/queue/<chain>.markers (chain name: TS3_CHAIN, set by switch)
//   node tools/harness/track.ts wait-marker <chain> <text>   block until <chain>.markers has a line containing text (exit 0), or the
//                                                             chain process is gone without it (exit 3); use instead of grepping the .out
//   node tools/harness/track.ts build-window <reason>       hold "build-window: <reason>", wait for the running job, "ok to build"
//   node tools/harness/track.ts build-done [--force-hold]     release the hold if it is a build-window hold
//   node tools/harness/track.ts --self-check                 temp dir, fake process list
import { execFileSync, spawn } from 'node:child_process';
import { appendFileSync, copyFileSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { GAME_EXE } from './game-exe.ts';
import { CHECKOUT, MAIN_ROOT, QUEUE_DIR, holdOwner, holdText, mayRelease, readOwners, samePath } from './hold.ts';
import { pending as modsPending, restoreMods, snapshotMods } from './mods-restore.ts';

// mppid: Windows pid of the MSYS (Git Bash) parent; differs from ppid after an exec (the exec'ing Windows process exits)
export type Proc = { pid: number; ppid: number; name: string; cmd: string; start?: string; mppid?: number };
// me/main: this checkout and the main one, for hold ownership (default CHECKOUT / MAIN_ROOT of hold.ts); roots: every checkout
// (git worktree list), for process owners (default [] = owners only from the hold.ts registry)
// term: a signal bash can trap (its EXIT trap runs); kill: taskkill /T /F (no trap)
type Env = { dir: string; procs: () => Proc[]; kill: (pid: number) => void; term: (pid: number) => boolean; rescue?: (chain: string) => string; sleepMs: number; log: (m: string) => void; me?: string; main?: string; roots?: string[] };

const read = (f: string) => { try { return readFileSync(f, 'utf8').trim(); } catch { return null; } };
// max(0): clock skew on a fresh dir gave < 0, read as "absent"
const ageMin = (f: string) => { try { return Math.max(0, Date.now() - statSync(f).mtimeMs) / 60000; } catch { return -1; } };
const sleep = (ms: number) => new Promise(r => setTimeout(r, ms));

export function realProcs(): Proc[] {
  const ps = `Get-CimInstance Win32_Process | Select-Object ProcessId,ParentProcessId,Name,CommandLine,@{n='S';e={$_.CreationDate.ToString('s')}} | ConvertTo-Json -Compress`;
  const out = execFileSync('powershell', ['-NoProfile', '-Command', ps], { timeout: 30000, maxBuffer: 64 << 20 }).toString();
  const mp = msysParents();
  return (JSON.parse(out) as any[]).map(p => ({ pid: p.ProcessId, ppid: p.ParentProcessId, name: p.Name ?? '', cmd: p.CommandLine ?? '', start: p.S, mppid: mp.get(p.ProcessId) }));
}
// 2026-11-18 chain-1117j deadlock: `$(timeout 30 node track.ts status)` in a child chain: the $( ) subshell forks, execs timeout and
// exits, so timeout.exe's Windows parent is dead and the Windows walk never reached the chain scripts. MSYS ps keeps the parent link.
export function parseMsysPs(out: string) {
  const rows = out.split('\n').map(l => /^\s*[A-Z]?\s*(\d+)\s+(\d+)\s+\d+\s+(\d+)\s/.exec(l)).filter(m => m).map(m => m!.slice(1, 4).map(Number));
  const win = new Map(rows.map(([pid, , w]) => [pid, w])), m = new Map<number, number>();
  for (const [, ppid, w] of rows) { const pw = win.get(ppid); if (pw) m.set(w, pw); }
  return m;
}
function msysParents() {
  try { return parseMsysPs(execFileSync('ps', [], { timeout: 15000 }).toString()); } catch { return new Map<number, number>(); }   // no Git Bash ps: Windows links only
}
const realKill = (pid: number) => { try { execFileSync('taskkill', ['/T', '/F', '/PID', String(pid)], { stdio: 'ignore', timeout: 15000 }); } catch { /* gone */ } };
// Git Bash kill -W: SIGTERM to an MSYS bash by Windows pid; bash runs its EXIT trap at once, also while it waits on a pipeline [verified 11-27]
const realTerm = (pid: number) => { try { execFileSync('kill', ['-W', '-s', 'TERM', String(pid)], { stdio: 'ignore', timeout: 15000 }); return true; } catch { return false; } };
const isQueue = (p: Proc) => /^node(\.exe)?$/i.test(p.name) && /harness[\\/]queue\.ts/.test(p.cmd) && !/--self-check|--dry-run/.test(p.cmd);
// pids under root by Windows or MSYS parent (a subshell's exec breaks the Windows link); a "child" started before its parent is a reused pid
export function descendants(all: Proc[], root: number) {
  const by = new Map(all.map(p => [p.pid, p])), out = new Set([root]);
  const under = (p: Proc, a?: number) => !!a && out.has(a) && !(p.start && by.get(a)?.start && p.start < by.get(a)!.start!);
  for (let grew = true; grew;) { grew = false; for (const p of all) if (!out.has(p.pid) && (under(p, p.ppid) || under(p, p.mppid))) { out.add(p.pid); grew = true; } }
  out.delete(root);
  return out;
}

// classification (exported for the self-check)
const CHAIN = /([\w.-]*(?:chain-[\w.-]*|mono-loop[\w.-]*|probe-out[\\/]queue[\\/][\w.-]+)\.sh)\b/;
export function classify(all: Proc[], self = process.pid) {
  // never list or kill ourselves or our ancestors (the calling shell's command line names the chain script too)
  const byPid = new Map(all.map(p => [p.pid, p])), mine = new Set<number>();
  for (const todo = [self]; todo.length;) { const p = todo.pop()!; if (!p || mine.has(p)) continue; mine.add(p); const q = byPid.get(p); if (q) todo.push(q.ppid, q.mppid ?? 0); }
  const ps = all.filter(p => !mine.has(p.pid));
  const pids = new Set(all.map(p => p.pid));
  // a shell that only waits on a chain (an agent's `... track.ts wait-chain <chain>.sh`) is not that chain: counting it made waiters wait on each other forever
  const bashes = ps.filter(p => /^(bash|sh)(\.exe)?$/i.test(p.name) && CHAIN.test(p.cmd) && !/\bwait-chain\b/.test(p.cmd));
  // nohup'd scripts show as a bash with a bash parent running the same script: keep the outermost; an exec'd subshell has only an MSYS parent link
  const chains = bashes.filter(p => !bashes.some(q => q.pid === p.ppid || (!!p.mppid && q.pid === p.mppid))).map(p => ({ ...p, script: basename(CHAIN.exec(p.cmd)![1].replace(/\\/g, '/')) }));
  const queues = ps.filter(p => p.pid !== self && isQueue(p))
    .map(p => ({ ...p, queue: /--queue[ =]"?([^\s"]+)/.exec(p.cmd)?.[1] ?? /([^\s"\\/]+\.json)/.exec(p.cmd)?.[1] ?? '?' }));
  const tails = ps.filter(p => /^tail(\.exe)?$/i.test(p.name) && /-F/.test(p.cmd) && /probe-out[\\/]queue/.test(p.cmd))
    .map(p => ({ ...p, orphan: !pids.has(p.ppid) || !ps.some(q => q.pid === p.ppid && /bash|sh|node/i.test(q.name)) }));
  const game = ps.filter(p => p.name.toLowerCase() === GAME_EXE.toLowerCase());
  const builds = ps.filter(p => /^(cl|link|csc|msbuild)\.exe$/i.test(p.name));
  return { chains, queues, tails, game, builds, self: mine, alive: (pid: number) => pids.has(pid) };
}

function jobState(e: Env, alive: (pid: number) => boolean) {
  const s = read(join(e.dir, 'job-running'));
  if (!s) return null;
  const [id, pid] = s.split(' ');
  return { id, pid: Number(pid), alive: alive(Number(pid)), owner: holdOwner(s) };
}

// Owner checkout of a process, else of its nearest ancestor: the hold.ts registry (owners/<pid>, queue.ts), a checkout path in the
// command line (deepest root wins; Git Bash /c/ form too), a relative chain script found in exactly one checkout; null = unknown.
const DRIVE = /^[a-z]:[\\/]/i;   // a Windows drive root stays as is: path.resolve on Linux/macOS would prefix the cwd
const normCmd = (s: string) => s.replace(/\\/g, '/').replace(/(^|[\s"'=])\/([a-z])\//gi, '$1$2:/').toLowerCase();
export function procOwner(p: Proc, byPid: Map<number, Proc>, reg: Map<number, string>, roots: string[]): string | null {
  const seen = new Set<number>();
  for (let q: Proc | undefined = p; q && !seen.has(q.pid); q = byPid.get(q.ppid) ?? (q.mppid ? byPid.get(q.mppid) : undefined)) {
    seen.add(q.pid);
    if (reg.has(q.pid)) return reg.get(q.pid)!;
    const cmd = normCmd(q.cmd), abs = roots.filter(r => cmd.includes(`${normCmd(DRIVE.test(r) ? r : resolve(r))}/`)).sort((a, b) => b.length - a.length)[0];
    if (abs) return abs;
    const sc = CHAIN.exec(q.cmd)?.[1];
    if (sc && !/^([a-z]:)?[\\/]/i.test(sc)) { const has = roots.filter(r => existsSync(join(r, sc))); if (has.length === 1) return has[0]; }
  }
  return null;
}
// which stop targets this checkout may kill; the rest is listed
function stopFilter(e: Env, all: Proc[], everything: boolean) {
  const byPid = new Map(all.map(p => [p.pid, p])), reg = readOwners(e.dir), me = e.me ?? CHECKOUT;
  const owner = (p: Proc) => procOwner(p, byPid, reg, e.roots ?? []);
  const mine = (p: Proc) => { if (everything) return true; const o = owner(p); return o !== null && samePath(o, me); };
  return { mine, owner };
}

// stale markers; returns what it did
export function clean(e: Env, c = classify(e.procs())) {
  const did: string[] = [];
  const job = jobState(e, c.alive);
  if (job && !job.alive) { rmSync(join(e.dir, 'job-running'), { force: true }); did.push(`removed stale job-running (${job.id}, pid ${job.pid} dead)`); }
  const lock = join(e.dir, 'main.lock'), age = ageMin(lock);
  if (age >= 0 && (age > 30 || !c.builds.length)) { rmSync(lock, { recursive: true, force: true }); did.push(`removed main.lock (${age.toFixed(0)} min, ${c.builds.length ? 'old' : 'no build running'})`); }
  for (const pid of readOwners(e.dir).keys()) if (!c.alive(pid)) rmSync(join(e.dir, 'owners', String(pid)), { force: true }); // a reused pid must not inherit an owner
  const orphans = c.tails.filter(t => t.orphan);
  for (const t of orphans) e.kill(t.pid);
  if (orphans.length) did.push(`killed ${orphans.length} orphan tail watcher(s)`);
  return did;
}

const holdFile = (e: Env) => join(e.dir, 'hold');
function setHold(e: Env, reason: string) { mkdirSync(e.dir, { recursive: true }); writeFileSync(holdFile(e), `${holdText(reason, e.me ?? CHECKOUT)}\n`); }
const mayReleaseHold = (e: Env, h: string | null, force: boolean) => mayRelease(h, e.me ?? CHECKOUT, e.main ?? MAIN_ROOT, force);
const keptLine = (h: string) => `set by ${holdOwner(h) ?? 'the main checkout'}, not this worktree (${h}); --force-hold releases it`;

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

// Chain start (2026-11-16: no echo line of chain-mono-rest-rebaseline-1116.sh reached its .out, which broke a handover). Node's
// openSync(f, 'a') gives a handle with FILE_APPEND_DATA but no FILE_WRITE_DATA; Git Bash (MSYS) programs fail to write to it
// without an error (echo, tee), native ones (node, python) succeed, so the .out only had node lines [verified: repro in the
// self-check]. bash now opens the .out itself (>>, MSYS append) and every child inherits that handle. TS3_CHAIN names the chain
// for `track.ts marker`.
export const chainName = (script: string) => basename(script).replace(/\.sh$/, '');
export function startChain(script: string, outF: string, cwd: string) {
  const git = 'C:\\Program Files\\Git\\bin\\bash.exe'; // not System32 bash.exe (WSL)
  const fwd = (p: string) => p.replace(/\\/g, '/');
  return spawn(existsSync(git) ? git : 'bash', ['-c', 'exec >>"$0" 2>&1 </dev/null; exec bash "$1"', fwd(outF), fwd(script)],
    { cwd, detached: true, stdio: 'ignore', windowsHide: true, env: { ...process.env, TS3_CHAIN: chainName(script) } });
}
// a marker: "<UTC time> <text>" appended to probe-out/queue/<chain>.markers (waiters read this file, not the .out)
export function writeMarker(dir: string, chain: string, text: string, now = new Date()) {
  const line = `${now.toISOString().slice(0, 19)}Z ${text}`;
  mkdirSync(dir, { recursive: true }); appendFileSync(join(dir, `${chain}.markers`), `${line}\n`);
  return line;
}
// wait until <chain>.markers has a line containing text (plain substring); not ok when the chain process is gone without it
export async function waitMarker(e: Env, chain: string, text: string): Promise<{ ok: boolean; line: string }> {
  const f = join(e.dir, `${chainName(chain)}.markers`);
  for (let t = 1; ; t++) {
    const hit = (read(f) ?? '').split(/\r?\n/).find(l => l.includes(text));
    if (hit) return { ok: true, line: `marker: ${hit}` };
    if (!classify(e.procs()).chains.some(p => chainName(p.script) === chainName(chain))) {
      const again = (read(f) ?? '').split(/\r?\n/).find(l => l.includes(text)); // written just before the exit
      return again ? { ok: true, line: `marker: ${again}` } : { ok: false, line: `${chainName(chain)} is not running and wrote no marker "${text}"` };
    }
    if (t % 60 === 0) e.log(`waiting for marker "${text}" of ${chainName(chain)} (${t * e.sleepMs / 60000} min)`);
    await sleep(e.sleepMs);
  }
}

// everything (--all): every checkout's and unknown-owner chains, queues, job and game; default only this checkout's (2026-11-23)
// --after-job: the job has ended and the hold is set, so a chain's queue.ts waits on it before its next job. Idle = everything under
// the chains is a shell, timeout, sleep or tail, or a queue.ts with no child, for IDLE_POLLS polls in a row (a queue.ts end report,
// a chain's Bin state / agent copy / triage step finish first). shortcut: queue.ts in-process report work looks idle too; 30 s of quiet
// covers it, a hold-wait line in its .out would be exact
const IDLE_POLLS = 6, IDLE_CAP = 360;
async function waitIdle(e: Env, chainPids: number[]) {
  for (let t = 1, quiet = 0; ; t++) {
    const ps = e.procs(), by = new Map(ps.map(p => [p.pid, p])), live = chainPids.filter(pid => by.has(pid));
    if (!live.length) return 'chains ended on their own';
    const kids = live.flatMap(pid => [...descendants(ps, pid)]).map(pid => by.get(pid)!);
    const busy = kids.filter(p => !/^(bash|sh|timeout|sleep|tail)(\.exe)?$/i.test(p.name) && !(isQueue(p) && !kids.some(k => k.ppid === p.pid || k.mppid === p.pid)));
    quiet = busy.length ? 0 : quiet + 1;
    if (quiet >= IDLE_POLLS) return `chain children idle (${kids.filter(isQueue).map(p => `queue.ts ${p.pid}`).join(', ') || 'no queue.ts'})`;
    if (t >= IDLE_CAP) return `WARN: still busy after ${IDLE_CAP} polls (${busy.map(p => `${p.name} ${p.pid}`).join(', ')}): stopping anyway`;
    if (t % 60 === 0) e.log(`waiting for the chain children to go idle: ${busy.map(p => `${p.name} ${p.pid}`).join(', ')}`);
    await sleep(e.sleepMs);
  }
}
// TERM a chain bash (its children idle on the hold), wait for it to exit, then check what its EXIT trap left: night-plan chains
// write "Bin restored" / "CHAIN END" markers and release probe-out/queue/night-chain.lock
async function termChain(e: Env, ch: Proc & { script: string }) {
  const n = chainName(ch.script), mf = join(e.dir, `${n}.markers`), before = (read(mf) ?? '').split(/\r?\n/).filter(Boolean).length;
  if (!e.term(ch.pid)) return `${ch.script}(${ch.pid}): TERM failed (no Git Bash kill?), chain left running: its children are stopped`;
  let t = 0;
  for (; t < IDLE_CAP && e.procs().some(p => p.pid === ch.pid); t++) await sleep(e.sleepMs);
  if (t >= IDLE_CAP) return `${ch.script}(${ch.pid}): TERM sent, still running its EXIT trap (waits for ${GAME_EXE}?): check again with status`;
  const ms = (read(mf) ?? '').split(/\r?\n/).filter(Boolean).slice(before).join(' | ');
  let lock = 'lock free'; try { const o = JSON.parse(readFileSync(join(e.dir, 'night-chain.lock'), 'utf8')); if (o.night === n.replace(/^chain-/, '')) lock = `LOCK STILL HELD (${o.night}): node tools/harness/night-plan.ts --unlock ${o.night}`; } catch { /* no lock */ }
  if (!/Bin restored|Bin restore failed/.test(ms) && e.rescue) return classify(e.procs()).game.length ? `${ch.script}(${ch.pid}) stopped by TERM, no "Bin restored" marker and ${GAME_EXE} runs: Bin not restored; ${lock}` : `${ch.script}(${ch.pid}) stopped by TERM, its EXIT trap left no "Bin restored" marker: ${e.rescue(n)}`;
  const bin = /Bin restored/.test(ms) ? 'Bin restored' : /Bin restore failed/.test(ms) ? `BIN RESTORE FAILED: node tools/turbo-compat.ts restore probe-out/${n}` : `no "Bin restored" marker (not a night-plan chain?): check probe-out/${n}`;
  return `${ch.script}(${ch.pid}) stopped by TERM, EXIT trap ran: ${bin}; ${lock}`;
}
// the trap's work, done by track when a chain's EXIT trap did not log it (an older chain script, a trap cut short): Bin back to the
// chain's snapshot, each worktree's agent package back (agent-<queue>.bak, its worktree in agent-<queue>.wt; older chains have no
// .wt: listed), chain lock released. The snapshot dir stays (the next run of the same chain refuses it: rename it by hand)
function realRescue(n: string) {
  const D = join(QUEUE_DIR, '..', n), out: string[] = [];
  if (!existsSync(D)) return `no snapshot ${D}: not a night-plan chain, nothing restored`;
  const run = (args: string[]) => { try { execFileSync('node', ['--no-warnings', ...args], { cwd: MAIN_ROOT, timeout: 300000, stdio: 'ignore' }); return true; } catch { return false; } };
  out.push(run(['tools/turbo-compat.ts', 'restore', D]) ? 'track restored Bin from the snapshot' : `BIN RESTORE FAILED: node tools/turbo-compat.ts restore ${D}`);
  for (const f of readdirSync(D).filter(f => /^agent-.*\.bak$/.test(f))) {
    const wt = join(D, f.replace(/\.bak$/, '.wt'));
    if (!existsSync(wt)) { out.push(`agent ${join(D, f)}: no .wt, put it back by hand`); continue; }
    const dst = join(readFileSync(wt, 'utf8').trim().replace(/^\/([a-z])\//i, '$1:/'), 'build', 'ts3agent.package');
    try { if (!existsSync(dst) || !readFileSync(dst).equals(readFileSync(join(D, f)))) { copyFileSync(join(D, f), dst); out.push(`agent put back in ${dst}`); } } catch { out.push(`AGENT NOT PUT BACK: ${join(D, f)} -> ${dst}`); }
  }
  const night = n.replace(/^chain-/, '');
  out.push(run(['tools/harness/night-plan.ts', '--unlock', night]) ? 'lock released' : `UNLOCK FAILED: node tools/harness/night-plan.ts --unlock ${night}`);
  return out.join('; ');
}

export async function stop(e: Env, afterJob: boolean, forceHold = false, everything = false) {
  const did: string[] = [];
  if (afterJob) { setHold(e, 'track stop'); did.push(`hold "track stop"; ${await waitJobEnd(e)}`); }
  let ps0 = e.procs(), c = classify(ps0), f = stopFilter(e, ps0, everything);
  if (afterJob && c.chains.some(f.mine)) { did.push(await waitIdle(e, c.chains.filter(f.mine).map(p => p.pid))); ps0 = e.procs(); c = classify(ps0); f = stopFilter(e, ps0, everything); }
  const left: string[] = [];
  const pick = <T extends Proc>(xs: T[], name: (p: T) => string, kids = new Set<number>()) => xs.filter(p => f.mine(p) || kids.has(p.pid) || (left.push(`${name(p)}(${p.pid}) ${f.owner(p) ?? 'owner unknown'}`), false));
  const chains = pick(c.chains, p => p.script);
  // under a stopped chain = ours, whatever checkout it runs from (a worktree queue.ts of a main-checkout chain)
  const kids = new Set(chains.flatMap(ch => [...descendants(ps0, ch.pid)]).filter(pid => !c.self.has(pid)));
  const queues = pick(c.queues, p => `queue ${p.queue}`, kids);
  // --after-job: TERM each chain first, while its children sit idle on the hold, so its EXIT trap restores Bin; then kill the
  // children. 2026-11-27: killing the children first ended the chain's while-loop subshell, the chain ran to its end and the TERM
  // then cut its EXIT trap in its nogame / sleep (no Bin restore, lock held)
  const trapped: string[] = [];
  if (afterJob) for (const ch of chains) trapped.push(await termChain(e, ch));
  else for (const p of chains) e.kill(p.pid);
  const psK = afterJob ? e.procs() : ps0;   // children the chain started after ps0 (a rerun queue.ts) sit under the old children
  const all = new Set([...kids].flatMap(k => [k, ...descendants(psK, k)]).filter(pid => !c.self.has(pid)));
  for (const pid of all) e.kill(pid);
  for (const p of queues) e.kill(p.pid);
  if (chains.length || queues.length) did.push(`${afterJob ? 'stopped' : 'killed'} ${chains.map(p => `${p.script}(${p.pid})`).concat(queues.map(p => `queue ${p.queue}(${p.pid})`)).join(', ')}${all.size ? ` (${all.size} process(es) under the chains)` : ''}`);
  did.push(...trapped);
  if (!afterJob) {
    const j = jobState(e, c.alive), jp = j ? ps0.find(p => p.pid === j.pid) : undefined;
    const jOwner = j?.owner ?? (jp ? f.owner(jp) : null);
    if (j?.alive && (everything || kids.has(j.pid) || (jOwner !== null && samePath(jOwner, e.me ?? CHECKOUT)))) { e.kill(j.pid); did.push(`killed job ${j.id} tree (pid ${j.pid})`); }
    else if (j?.alive) left.push(`job ${j.id}(${j.pid}) ${jOwner ?? 'owner unknown'}`);
    const game = pick(c.game, () => GAME_EXE, kids);
    for (const g of game) e.kill(g.pid);
    if (game.length) did.push(`killed ${GAME_EXE} (${game.map(g => g.pid).join(', ')}): check Game\\Bin pair before the next run`);
  }
  if (left.length) did.push(`not killed (another checkout or owner unknown; --all kills them): ${left.join(', ')}`);
  c = classify(e.procs());
  // Mods packages a killed queue installed (cycle.ts build/ copies): back to the queue's start record (mods-restore.ts)
  const modsDir = join(e.dir, 'mods-before');
  if (modsPending(modsDir)) did.push(c.game.length ? `Mods packages not restored (${GAME_EXE} still running): node tools/harness/mods-restore.ts restore` : restoreMods(modsDir).line);
  did.push(...clean(e, c));
  const h = read(holdFile(e));
  if (h !== null && !mayReleaseHold(e, h, forceHold)) did.push(`hold kept: ${keptLine(h)}`);
  else if (h !== null) { did.push(`released hold (${h})`); rmSync(holdFile(e), { force: true }); }
  return did.length ? did : ['nothing to stop'];
}

export function status(e: Env) {
  const c = classify(e.procs()), L: string[] = [];
  const h = read(holdFile(e));
  L.push(`hold:        ${h ? `${h} (${ageMin(holdFile(e)).toFixed(0)} min)` : '-'}`);
  if (h) L.push(`hold owner:  ${holdOwner(h) ?? 'NONE (old hold without owner=: counts as the main checkout; a worktree releases it only with --force-hold)'}`);
  const j = jobState(e, c.alive);
  L.push(`job:         ${j ? `${j.id} pid ${j.pid} ${j.alive ? 'alive' : 'DEAD (stale marker)'}${j.owner ? ` owner ${j.owner}` : ''}` : '-'}`);
  L.push(`game:        ${c.game.length ? `${GAME_EXE} ${c.game.map(g => g.pid).join(', ')}` : '-'}`);
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

function worktreeRoots() {
  try { return execFileSync('git', ['worktree', 'list', '--porcelain'], { cwd: MAIN_ROOT, timeout: 15000 }).toString().split(/\r?\n/).filter(l => l.startsWith('worktree ')).map(l => resolve(l.slice(9))); }
  catch { return [MAIN_ROOT]; }
}
function env(): Env {
  return { dir: QUEUE_DIR, roots: worktreeRoots(), procs: realProcs, kill: realKill, term: realTerm, rescue: realRescue, sleepMs: 5000, log: m => console.log(`${new Date().toISOString().slice(11, 19)} track: ${m}`) };
}

if (import.meta.main) {
  const die = (e: any) => { console.error(`track: ERROR ${e?.stack ?? e}`); process.exit(1); };
  process.on('uncaughtException', die); process.on('unhandledRejection', die);
  const args = process.argv.slice(2), afterJob = args.includes('--after-job'), forceHold = args.includes('--force-hold'), everything = args.includes('--all');
  const [cmd, ...rest] = args.filter(a => a !== '--after-job' && a !== '--force-hold' && a !== '--all');
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
        { pid: 15, ppid: 12, name: GAME_EXE, cmd: GAME_EXE },
        { pid: 16, ppid: 1, name: 'bash.exe', cmd: 'bash -c ls' },
      ];
      const killed: number[] = [];
      const e: Env = { dir: d, procs: () => ps, kill: pid => { killed.push(pid); ps = ps.filter(p => p.pid !== pid && p.ppid !== pid); }, term: () => true, sleepMs: 1, log: () => {} };
      const c = classify(ps, 0);
      assert.deepEqual(c.chains.map(p => [p.pid, p.script]), [[10, 'chain-mono.sh']], 'outermost chain bash only');
      assert.deepEqual(c.queues.map(p => p.queue), ['mono-x']);
      assert.deepEqual(c.tails.map(t => t.orphan), [true, false]);
      // caller chain: bash 30 (cmd names the script) -> node 31 = self: neither listed
      const caller = [...ps, { pid: 30, ppid: 1, name: 'bash.exe', cmd: 'bash -c "node track.ts switch probe-out/queue/mono-loop.sh"' }, { pid: 31, ppid: 30, name: 'node.exe', cmd: 'node track.ts' }];
      assert.deepEqual(classify(caller, 31).chains.map(p => p.pid), [10], 'self and ancestors excluded');
      // child chain calling status via $(timeout ...): timeout 62's Windows parent (the exec'd subshell) is gone; MSYS parent = child chain 61
      const nested: Proc[] = [{ pid: 60, ppid: 1, name: 'bash.exe', cmd: 'bash probe-out/queue/chain-1117j.sh' }, { pid: 61, ppid: 60, name: 'bash.exe', cmd: 'bash probe-out/queue/chain-1117h-gi-rerun1.sh' },
        { pid: 62, ppid: 999, mppid: 61, name: 'timeout.exe', cmd: 'timeout 30 node track.ts status' }, { pid: 63, ppid: 62, name: 'node.exe', cmd: 'node track.ts status' }];
      assert.deepEqual(classify([...ps, ...nested], 63).chains.map(p => p.pid), [10], 'MSYS parents of an exec\'d subshell excluded too');
      assert.deepEqual(classify([...ps, ...nested.map(p => ({ ...p, mppid: undefined }))], 63).chains.map(p => p.pid), [10, 60], 'without MSYS links the parent chain shows (the 1117j bug)');
      // MSYS ps: optional flag column; parent link only to a listed MSYS pid
      const ms = parseMsysPs('      PID    PPID    PGID     WINPID   TTY         UID    STIME COMMAND\n    78717       1   78717       9148  ?         197609 03:30:06 /usr/bin/bash\nS   78719   78717   78717      19520  ?         197609 03:30:06 /usr/bin/tail\n');
      assert.deepEqual([...ms], [[19520, 9148]]);
      // another process's shell waiting on the chain is not the chain (two waiters used to wait on each other forever)
      const waiter = [...ps, { pid: 40, ppid: 1, name: 'bash.exe', cmd: 'bash -c "source x.sh && timeout 7000 node tools/harness/track.ts wait-chain probe-out/queue/chain-mono.sh"' }];
      assert.deepEqual(classify(waiter, 0).chains.map(p => p.pid), [10], 'wait-chain shells are not chains');
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
      // process owners: registry, checkout path in the command line (deepest root, Git Bash form), relative script in one checkout, parent
      const oMain = join(d, 'own', 'main'), oWt = join(oMain, '.claude', 'worktrees', 'lc'), oGb = 'C:\\gb\\main';   // Windows root: same Git Bash case on every OS
      mkdirSync(join(oWt, 'probe-out', 'queue'), { recursive: true }); writeFileSync(join(oWt, 'probe-out', 'queue', 'chain-lc.sh'), '');
      const op: Proc[] = [
        { pid: 1, ppid: 0, name: 'bash.exe', cmd: `bash ${oWt.replace(/\\/g, '/')}/probe-out/queue/chain-x.sh` },
        { pid: 2, ppid: 0, name: 'bash.exe', cmd: `bash ${oMain}\\probe-out\\queue\\chain-y.sh` },
        { pid: 3, ppid: 0, name: 'bash.exe', cmd: 'bash "/c/gb/main/probe-out/queue/chain-z.sh"' },
        { pid: 4, ppid: 0, name: 'bash.exe', cmd: 'bash probe-out/queue/chain-lc.sh' },
        { pid: 5, ppid: 4, name: 'node.exe', cmd: 'node tools/harness/queue.ts x.json' },
        { pid: 6, ppid: 0, name: 'node.exe', cmd: 'node tools/harness/queue.ts y.json' },
        { pid: 7, ppid: 0, name: 'node.exe', cmd: 'node tools/harness/queue.ts z.json' }];
      const oBy = new Map(op.map(p => [p.pid, p])), oReg = new Map([[7, oMain]]), oRoots = [oMain, oWt, oGb];
      assert.deepEqual(op.map(p => procOwner(p, oBy, oReg, oRoots)), [oWt, oMain, oGb, oWt, oWt, null, oMain], 'abs path, deepest root, Git Bash path, script in one checkout, parent, unknown, registry');
      // stop kills only this checkout's: unknown owners are listed, not killed; --all kills them
      writeFileSync(join(d, 'job-running'), 'j4 12');
      const kept = await stop({ ...e, me: oWt, roots: oRoots }, false);
      assert.deepEqual(killed.filter(p => [10, 12, 15].includes(p)), [], 'unknown owner: nothing killed');
      assert.ok(kept.some(l => /^not killed .*--all kills them\): chain-mono\.sh\(10\) owner unknown, queue mono-x\(12\) owner unknown, job j4\(12\) owner unknown, TS3W\.exe\(15\) owner unknown$/i.test(l)), kept.join(' | '));
      mkdirSync(join(d, 'owners'), { recursive: true }); writeFileSync(join(d, 'owners', '10'), oMain);
      writeFileSync(join(d, 'owners', '777'), oMain);
      const other = await stop({ ...e, me: oWt, roots: oRoots }, false);
      assert.ok(!existsSync(join(d, 'owners', '777')) && existsSync(join(d, 'owners', '10')), 'dead pid registry entry pruned, live kept');
      assert.ok(other.some(l => l.includes(`chain-mono.sh(10) ${oMain}`)) && !killed.includes(10), 'another checkout\'s chain listed, not killed');
      const k2: number[] = [];
      await stop({ ...e, kill: pid => k2.push(pid), me: oWt, roots: oRoots }, false, false, true);
      assert.ok([10, 12, 15].every(pid => k2.includes(pid)), '--all kills every checkout\'s');
      // status names the hold owner and flags an ownerless (old) hold
      writeFileSync(holdFile(e), 'build-window: old @ 2026-11-23T04:58:54Z\n');
      assert.ok(status(e).some(l => /^hold owner:  NONE \(old hold/.test(l)), 'ownerless hold flagged');
      setHold(e, 'build-window: y'); assert.ok(status(e).includes(`hold owner:  ${CHECKOUT}`));
      // stop: kills chain, queue, game (children of the registered chain); releases hold
      writeFileSync(join(d, 'owners', '10'), CHECKOUT);
      setHold(e, 'build-window: x');
      const out = await stop(e, false);
      assert.ok([10, 12, 15].every(pid => killed.includes(pid)) && !killed.includes(16), 'unrelated bash untouched');
      assert.ok(!existsSync(holdFile(e)) && out.some(l => /released hold/.test(l)) && !existsSync(join(d, 'job-running')));
      // stop from a worktree keeps a hold the main checkout (or no owner) set; owner, main and --force-hold release it (2026-11-23)
      const mainD = join(d, 'main'), ew: Env = { ...e, me: join(mainD, '.claude', 'worktrees', 'lc'), main: mainD };
      writeFileSync(holdFile(e), `${holdText('build-window: merge', mainD)}\n`);
      assert.ok((await stop(ew, false)).some(l => /^hold kept: set by .*main, not this worktree/.test(l)) && existsSync(holdFile(e)), 'worktree keeps main hold');
      writeFileSync(holdFile(e), 'build-window: old @ 2026-11-23T04:57:00Z\n');
      assert.ok((await stop(ew, false)).some(l => /^hold kept: set by the main checkout/.test(l)), 'no owner = main');
      assert.ok((await stop(ew, false, true)).some(l => /released hold/.test(l)) && !existsSync(holdFile(e)), '--force-hold');
      setHold(ew, 'build-window: lc'); assert.equal(holdOwner(read(holdFile(e))), ew.me);
      assert.ok((await stop(ew, false)).some(l => /released hold/.test(l)) && !existsSync(holdFile(e)), 'own hold released');
      setHold(ew, 'x');
      assert.ok((await stop({ ...ew, me: mainD }, false)).some(l => /released hold/.test(l)), 'main releases a worktree hold');
      // stop with a killed queue's Mods record: the job's package goes back to the start one (2026-11-08 dance repro)
      const pk = join(d, 'user', 'Mods', 'Packages'); mkdirSync(pk, { recursive: true }); writeFileSync(join(pk, 'ts3fixes.package'), 'release');
      snapshotMods(pk, join(d, 'mods-before')); writeFileSync(join(pk, 'ts3fixes.package'), 'build');
      const out2 = await stop(e, false);
      assert.ok(out2.some(l => /restored Mods packages/.test(l)) && readFileSync(join(pk, 'ts3fixes.package'), 'utf8') === 'release' && !modsPending(join(d, 'mods-before')));
      // wait-chain: returns once the chain and its queue are gone, not before
      ps = [{ pid: 20, ppid: 1, name: 'bash.exe', cmd: 'bash probe-out/queue/chain-a.sh' }, { pid: 21, ppid: 20, name: 'node.exe', cmd: 'node tools/harness/queue.ts x.json' }];
      let ticks = 0; const e2: Env = { ...e, procs: () => ps, sleepMs: 0 };
      const w = waitChain({ ...e2, procs: () => { if (++ticks === 3) ps = [ps[1]]; if (ticks === 5) ps = []; return ps; } }, 'probe-out/queue/chain-a.sh');
      assert.match(await w, /chain-a\.sh gone/); assert.equal(ticks, 5, 'waits for the orphaned queue too');
      // markers: one "<UTC>Z text" line each; wait-marker finds it, and gives up once the chain is gone without it
      assert.equal(writeMarker(d, 'chain-m', 'mono-1115 rest done (rc 0)', new Date('2026-11-16T05:00:01Z')), '2026-11-16T05:00:01Z mono-1115 rest done (rc 0)');
      assert.equal(readFileSync(join(d, 'chain-m.markers'), 'utf8'), '2026-11-16T05:00:01Z mono-1115 rest done (rc 0)\n');
      ps = [{ pid: 50, ppid: 1, name: 'bash.exe', cmd: 'bash probe-out/queue/chain-m.sh' }];
      assert.deepEqual(await waitMarker(e2, 'chain-m.sh', 'rest done'), { ok: true, line: 'marker: 2026-11-16T05:00:01Z mono-1115 rest done (rc 0)' });
      let polls = 0; const gone = waitMarker({ ...e2, procs: () => (++polls === 3 ? (ps = []) : ps) }, 'chain-m', 'rebaseline done');
      assert.deepEqual(await gone, { ok: false, line: 'chain-m is not running and wrote no marker "rebaseline done"' }); assert.equal(polls, 3);
      // 2026-11-27: a main-checkout chain whose queue.ts (owner: a worktree) runs under exec'd subshells (MSYS parent only): stop
      // from the main checkout takes the queue.ts too; --after-job kills only the children, TERMs the chain and checks its trap's work
      const wtQ = join(oMain, '.claude', 'worktrees', 'atlas');
      const chainTable = (): Proc[] => [
        { pid: 100, ppid: 1, name: 'bash.exe', cmd: 'bash probe-out/queue/chain-main20c-x.sh', start: '2026-11-27T08:00:00' },
        { pid: 101, ppid: 100, name: 'bash.exe', cmd: 'bash probe-out/queue/chain-main20c-x.sh', start: '2026-11-27T08:00:01' },
        { pid: 102, ppid: 999, mppid: 101, name: 'bash.exe', cmd: 'bash probe-out/queue/chain-main20c-x.sh', start: '2026-11-27T08:00:02' },
        { pid: 103, ppid: 102, name: 'timeout.exe', cmd: 'timeout 48m node --no-warnings tools/harness/queue.ts x.json', start: '2026-11-27T08:00:02' },
        { pid: 104, ppid: 103, name: 'node.exe', cmd: `node --no-warnings tools/harness/queue.ts ${wtQ.replace(/\\/g, '/')}/tools/harness/queues/x.json`, start: '2026-11-27T08:00:03' },
        { pid: 105, ppid: 100, name: 'node.exe', cmd: 'node old', start: '2026-11-26T01:00:00' }];   // reused pid: older than its "parent"
      const chainEnv = (k: number[], t: number[]): Env => ({ ...e, me: oMain, roots: oRoots, procs: () => ps, kill: pid => { k.push(pid); ps = ps.filter(p => p.pid !== pid); },
        term: pid => { t.push(pid); ps = ps.filter(p => p.pid !== pid); appendFileSync(join(d, 'chain-main20c-x.markers'), 'x 08:30 chain-main20c-x: Bin restored\nx 08:30 chain-main20c-x: CHAIN END\n'); rmSync(join(d, 'night-chain.lock'), { force: true }); return true; } });
      assert.deepEqual([...descendants(chainTable(), 100)].sort(), [101, 102, 103, 104], 'MSYS-parent subshell and its queue.ts are under the chain; reused pid is not');
      rmSync(join(d, 'owners'), { recursive: true, force: true }); mkdirSync(join(d, 'owners')); rmSync(join(d, 'job-running'), { force: true }); rmSync(holdFile(e), { force: true });
      ps = chainTable(); writeFileSync(join(d, 'owners', '100'), oMain); writeFileSync(join(d, 'owners', '104'), wtQ);
      assert.equal(procOwner(ps[4], new Map(ps.map(p => [p.pid, p])), readOwners(d), [...oRoots, wtQ]), wtQ, 'queue.ts owner is the worktree');
      const k3: number[] = [], t3: number[] = [];
      const hard = await stop(chainEnv(k3, t3), false);
      assert.ok(k3.includes(100) && k3.includes(104) && !k3.includes(105) && !t3.length, `no --after-job: chain and its worktree queue.ts killed (${k3})`);
      assert.ok(!hard.some(l => /not killed/.test(l)), hard.join(' | '));
      const chainOwners = () => { writeFileSync(join(d, 'owners', '100'), oMain); writeFileSync(join(d, 'owners', '104'), wtQ); };
      ps = chainTable(); chainOwners(); writeFileSync(join(d, 'night-chain.lock'), JSON.stringify({ night: 'main20c-x', pid: 100 }));
      const k4: number[] = [], t4: number[] = [];
      const soft = await stop(chainEnv(k4, t4), true);
      assert.ok(!k4.includes(100) && k4.includes(104) && k4.includes(102) && !k4.includes(105) && t4.join() === '100', `--after-job: children killed, chain TERMed not killed (kill ${k4}, term ${t4})`);
      assert.ok(soft.some(l => /chain children idle \(queue\.ts 104\)/.test(l)) && soft.some(l => /^chain-main20c-x\.sh\(100\) stopped by TERM, EXIT trap ran: Bin restored; lock free/.test(l)), soft.join(' | '));
      ps = chainTable(); chainOwners(); writeFileSync(join(d, 'night-chain.lock'), JSON.stringify({ night: 'main20c-x', pid: 100 }));
      const left = await stop({ ...chainEnv([], []), term: pid => { ps = ps.filter(p => p.pid !== pid); return true; } }, true);
      assert.ok(left.some(l => /no "Bin restored" marker .*; LOCK STILL HELD \(main20c-x\)/.test(l)), `trap work missing is reported: ${left.join(' | ')}`);
      // 2026-11-27: TERM first (trap runs while the children idle), kill the children after; a trap with no marker: track does its work
      ps = chainTable(); chainOwners(); writeFileSync(join(d, 'night-chain.lock'), JSON.stringify({ night: 'main20c-x', pid: 100 }));
      const seq: string[] = [], rescued: string[] = [];
      const res = await stop({ ...chainEnv([], []), kill: pid => { seq.push(`kill ${pid}`); ps = ps.filter(p => p.pid !== pid); },
        term: pid => { seq.push(`term ${pid}`); ps = ps.filter(p => p.pid !== pid); return true; }, rescue: n => (rescued.push(n), 'track restored Bin from the snapshot; lock released') }, true);
      assert.equal(seq[0], 'term 100', `TERM before any kill: ${seq}`);
      assert.ok(seq.includes('kill 101') && seq.includes('kill 104'), `children killed after the TERM: ${seq}`);
      assert.deepEqual(rescued, ['chain-main20c-x'], 'no marker: track rescues');
      assert.ok(res.some(l => /left no "Bin restored" marker: track restored Bin/.test(l)), res.join(' | '));
      rmSync(join(d, 'night-chain.lock'), { force: true });
      // real Git Bash: a TERM reaches the EXIT trap of a chain bash that waits on a while-loop pipeline, and a second TERM during
      // the trap's sleep does not cut it (the chain template's restore() starts with trap '' INT TERM HUP)
      if (process.platform === 'win32') {
        const tsh = join(d, 'trap.sh'), tf = join(d, 'trap.out');
        writeFileSync(tsh, [`restore() { trap '' INT TERM HUP; sleep 3; echo restored > '${tf.replace(/\\/g, '/')}'; }`, 'trap restore EXIT',
          'echo x | while read -r l; do sleep 60; done', ''].join('\n'));
        const b = spawn('bash', [tsh], { stdio: 'ignore' });
        const gone = new Promise(r => b.on('exit', r));
        await sleep(1500);
        assert.ok(realTerm(b.pid!), 'TERM sent');
        await sleep(1000);
        realTerm(b.pid!);   // second TERM inside the trap's sleep
        await Promise.race([gone, sleep(20000)]);
        assert.equal(existsSync(tf) && readFileSync(tf, 'utf8').trim(), 'restored', 'EXIT trap ran to its end under two TERMs');
        try { execFileSync('taskkill', ['/T', '/F', '/PID', String(b.pid)], { stdio: 'ignore' }); } catch { /* gone */ }
      }
      // the real start path: echo, tee and node lines of the chain all land in the .out (2026-11-16: echo and tee lines were lost)
      const sh = join(d, 'chain-echo.sh'), chOut = join(d, 'chain-echo.out');
      writeFileSync(sh, ['echo "06:44 mono-1115 rest start"', 'echo "tee line" | tee /dev/null', 'node -e "console.log(\'node line\')"',
        'node "$TRACK" marker "rest done"', 'echo "06:45 mono-1115 rest done (rc 0)"', ''].join('\n'));
      writeFileSync(chOut, 'earlier run\n');
      process.env.TRACK = join(import.meta.dirname, 'track.ts'); process.env.TS3_QUEUE_DIR = d;
      const ch = startChain(sh, chOut, d);
      await new Promise(r => ch.on('exit', r));
      assert.deepEqual(readFileSync(chOut, 'utf8').split(/\r?\n/).filter(Boolean), ['earlier run', '06:44 mono-1115 rest start', 'tee line', 'node line',
        readFileSync(join(d, 'chain-echo.markers'), 'utf8').trim().replace(/^/, 'marker: '), '06:45 mono-1115 rest done (rc 0)'], 'every line, appended');
      assert.match(readFileSync(join(d, 'chain-echo.markers'), 'utf8'), /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\dZ rest done\n$/, 'TS3_CHAIN names the chain');
    } finally { rmSync(d, { recursive: true, force: true }); }
    console.log('self-check ok');
  } else if (cmd === 'status') {
    const e = env(); console.log(`queue dir:   ${e.dir}`); for (const l of status(e)) console.log(l);
  } else if (cmd === 'stop' || (cmd === 'switch' && rest[0])) {
    const e = env();
    let script = '';
    if (cmd === 'switch') { script = resolve(rest[0]); if (!existsSync(script)) { console.error(`no such script: ${script}`); process.exit(2); } }
    for (const l of (await stop(e, afterJob, forceHold, everything)).slice(0, 10)) console.log(l);
    if (script) {
      const outF = join(e.dir, `${chainName(script)}.out`), ch = startChain(script, outF, dirname(dirname(e.dir)));
      ch.unref();
      console.log(`started ${basename(script)} pid ${ch.pid}, out ${outF}, markers ${join(e.dir, `${chainName(script)}.markers`)}`);
    }
  } else if (cmd === 'marker' && rest.length) {
    // never fails a chain: without a chain name the text still goes to stdout (the .out)
    const ci = rest.indexOf('--chain'), chain = ci >= 0 ? rest[ci + 1] : process.env.TS3_CHAIN;
    const text = (ci >= 0 ? rest.filter((_, i) => i !== ci && i !== ci + 1) : rest).join(' ');
    if (!chain) console.log(`marker: ${text} (WARN: no chain name, TS3_CHAIN unset and no --chain: not written to a .markers file)`);
    else console.log(`marker: ${writeMarker(env().dir, chain, text)}`);
  } else if (cmd === 'wait-marker' && rest.length >= 2) {
    const r = await waitMarker(env(), rest[0], rest.slice(1).join(' '));
    console.log(`${new Date().toISOString().slice(11, 19)} track: ${r.line}`); process.exit(r.ok ? 0 : 3);
  } else if (cmd === 'wait-chain' && rest[0]) {
    const e = env(); console.log(`${new Date().toISOString().slice(11, 19)} track: ${await waitChain(e, rest[0])}`);
  } else if (cmd === 'build-window' && rest.length) {
    const e = env();
    setHold(e, `build-window: ${rest.join(' ')}`);
    e.log(await waitJobEnd(e)); console.log('ok to build');
  } else if (cmd === 'build-done') {
    const e = env(), h = read(holdFile(e));
    if (h?.startsWith('build-window') && !mayReleaseHold(e, h, forceHold)) { console.log(`WARN: build-window hold ${keptLine(h)}`); process.exit(1); }
    else if (h?.startsWith('build-window')) { rmSync(holdFile(e), { force: true }); console.log(`released: ${h}`); }
    else console.log(`WARN: ${h ? `hold is not a build-window hold (${h}), left in place` : 'no hold set'}`);
  } else { console.error('usage: node tools/harness/track.ts status | stop [--after-job] [--force-hold] [--all] | switch <script.sh> [--after-job] [--force-hold] [--all] | wait-chain <script.sh> | build-window <reason> | build-done [--force-hold] | --self-check'); process.exit(2); }
}
