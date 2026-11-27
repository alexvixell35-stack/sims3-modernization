// Night plan (Rodney 2026-11-24, the night-all-1124 post-mortem): the night-run rules as code. A plan names the queues and their
// worktrees; --check refuses with one list before the night starts, --write generates the chain script, --start runs it detached,
// --watch is the orchestrating session's wake call, --triage is the chain's first-pass classifier after each queue.
//
//   node tools/harness/night-plan.ts --check <plan.json>        preflight only (creates a missing game-copy junction)
//   node tools/harness/night-plan.ts --write <plan.json>        preflight + probe-out/queue/chain-<night>.sh (+ its --check)
//   node tools/harness/night-plan.ts --start <plan.json>        preflight + write + nohup bash ... & disown (detached)
//   node tools/harness/night-plan.ts --watch <night> [--timeout 3600]   exits 0 on a new FAIL/STOP/DONE wake line (exit 2 = timeout: re-arm)
//   node tools/harness/night-plan.ts --triage --night <n> --name <q> --out <file> --est <min> --minutes <m> --rc <rc>   (chain only)
//   node tools/harness/night-plan.ts --lock <night> --pid <pid> | --unlock <night>   (chain only: one chain at a time)
//   node tools/harness/night-plan.ts --bin-state <queue.json> --wt <worktree> [--stage]   (chain only: the queue's Bin state)
//   node tools/harness/night-plan.ts --self-check
//
// Bin state per queue (2026-11-27): queue key "binState" names a tools/turbo-compat.ts state (unknown names refused), optional
// "binStage" { "game-copy/<dest>": { "from": "game-copy/<src>", "sha256": "<hex>" } } the files the chain copies into that state's
// source folder first (game-copy of this checkout, where turbo-compat.ts reads; each read back by sha256).
// Without binState: apex-rewind when the queue has rewind auto and the worktree has the rewind tool, its own or (sources sha256
// equal) the main checkout's, which --stage copies in; else apex. apex and every state but base / turbo / apex-stock are our fork
// build (turbo-compat.ts): --check and --bin-state refuse them while the fork install of FORK.sha is missing or stale (no stock
// fallback), and --check notes the asi each queue gets. The chain sets the state before a queue (and its rerun), apex after it.
//
// Chain lock (2026-11-27): two chains started with --start could both pass nogame / wait-job-end in the gap between one
// chain's queues and run together. Each chain takes probe-out/queue/night-chain.lock (O_EXCL create) before its checks and
// holds it to its EXIT trap; a second chain waits for it. A lock whose owner pid is dead is taken over.
//
// Plan file (JSON): { "night": "night-1125", "cutoff": "07:45", "rgbOffUntil": "09:00",
//   "runs": [ { "worktree": "agent-x" | ".", "queue": "lc7-desc-correct-1124", "est": 30 }, { "branch": "bugfix/y", "queue": "..." } ] }
// worktree: a .claude/worktrees/<name> dir or "." (main checkout); branch: looked up in `git worktree list`. queue: a name in the
// worktree's tools/harness/queues/, or a path (relative to the worktree, then the main checkout). est (min): else the sum of the
// jobs' "[est a-b min]" notes (upper bound), else refused. Times are machine-local (PHT on the test machine).
// gate: true (a smoke the later runs depend on): when that queue does not pass (after its one rerun), the chain stops there.
//
// Preflight (each a refusal line): worktree + game-copy junction; every pinned file (asi, fixesPackage, agent) passes
// `builds.ts check`; the agent package per queue (queue/job key agentPackage, else the newest builds.json ts3agent entry built on
// the worktree's branch, else the newest one in its history; a missing file fails, never a guess); a clean queue.ts --dry-run
// (also covers modSet set.json files lists and regexCases parsing); slow repro steps without timeoutS (SLOW_STEPS); every
// logRequire regex has regexCases that pass; notes that say a job FAILs by design need expectFail; the disk gate; the total est
// fits before the cutoff; the generated script has no rm/del/elevation and feeds every child /dev/null.
import { execFileSync, spawn } from 'node:child_process';
import { appendFileSync, copyFileSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, symlinkSync, unlinkSync, utimesSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, isAbsolute, join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { loadDb, packageTuning } from '../builds.ts';
import { FORK, forkInstallProblem, STATES, STOCK_STATES } from '../turbo-compat.ts';
import { queueRewind } from './queue-rewind.ts';
import { pickRewindExe } from './rewind-smoke.ts';

// the main checkout (also when this runs from a worktree): the chain, probe-out and game-copy live there
const M = resolve(execFileSync('git', ['rev-parse', '--path-format=absolute', '--git-common-dir'], { cwd: import.meta.dirname, encoding: 'utf8', timeout: 30000 }).trim(), '..');
const QOUT = join(M, 'probe-out', 'queue');
// this checkout: turbo-compat.ts (imported) resolves its state sources here, so binStage reads and writes here too
const OWN = resolve(import.meta.dirname, '..', '..');
const { values } = parseArgs({ allowPositionals: true, options: {
  check: { type: 'string' }, write: { type: 'string' }, start: { type: 'string' }, watch: { type: 'string' }, timeout: { type: 'string', default: '3600' },
  triage: { type: 'boolean' }, night: { type: 'string' }, name: { type: 'string' }, out: { type: 'string' }, est: { type: 'string' }, minutes: { type: 'string' }, rc: { type: 'string' }, 'rerun-ids': { type: 'string' },
  lock: { type: 'string' }, unlock: { type: 'string' }, pid: { type: 'string' },
  'bin-state': { type: 'string' }, wt: { type: 'string' }, stage: { type: 'boolean' },
  'self-check': { type: 'boolean' } } });

type Run = { worktree?: string; branch?: string; queue: string; est?: number; gate?: boolean };
type Plan = { night: string; cutoff: string; rgbOffUntil?: string; runs: Run[] };
type Resolved = { wt: string; q: string; name: string; est: number; agent: string; agentWhy: string; expectFail: string[]; gate?: boolean };

// ---------------------------------------------------------------- pure parts (covered by --self-check)
// repro steps whose reply can outlast the 8 s default (profile-world.ts ag): place_objects (vbowner 11-24), travel (Rodney); the
// others: most of their repros.json steps already set timeoutS [verified: counted 2026-11-24, e.g. place_objects 62/99]
export const SLOW_STEPS = /^(place_objects|remove_objects|wear_parts|restore_outfit|travel_\w+|load_save|load_world|fresh_world)$/;
export function slowStepsWithoutTimeout(entry: unknown): string[] {
  const out: string[] = [];
  const walk = (o: any) => { if (Array.isArray(o)) o.forEach(walk); else if (o && typeof o === 'object') { if (typeof o.cmd === 'string' && SLOW_STEPS.test(o.cmd) && !o.timeoutS) out.push(o.cmd); Object.values(o).forEach(walk); } };
  walk(entry);
  return out;
}
export const jobsOf = (q: any): any[] => Array.isArray(q) ? q : q?.jobs ?? [];
export const reprosOf = (j: any): string[] => [j?.run?.repro, j?.run?.profile?.repro, ...((j?.run?.profile?.repros ?? []) as any[]).map(r => typeof r === 'string' ? r : r?.repro)].filter((x): x is string => typeof x === 'string');
// every logRequire / apexLogRequire regex needs a case (job or queue level) whose match lines all match and noMatch lines none
export function regexCaseProblems(q: any): string[] {
  const out: string[] = [];
  for (const j of jobsOf(q)) for (const k of ['logRequire', 'apexLogRequire']) for (const re of Object.keys(j[k] ?? {})) {
    const c = j.regexCases?.[re] ?? q?.regexCases?.[re];
    if (!c) { out.push(`${j.id}: ${k} /${re}/ has no regexCases`); continue; }
    let r: RegExp; try { r = new RegExp(re); } catch (e) { out.push(`${j.id}: /${re}/ does not compile`); continue; }
    const bad = [...(c.match ?? []).filter((l: string) => !r.test(l)).map((l: string) => `misses "${l.slice(0, 80)}"`), ...(c.noMatch ?? []).filter((l: string) => r.test(l)).map((l: string) => `matches noMatch "${l.slice(0, 80)}"`)];
    if (!c.match?.length) bad.push('no match lines');
    if (bad.length) out.push(`${j.id}: /${re}/ ${bad.join('; ')}`);
  }
  return out;
}
// a note that says the job FAILs by design (tuneisolate OFF 11-24) without expectFail: true
export const EXPECT_FAIL_HINT = /\bexpect(?:s|ed)?\s+(?:to\s+)?FAIL|\bFAILs? (?:is |are )?(?:expected|by design)|\bshould FAIL\b/i;
export const expectFailUnmarked = (q: any): string[] => jobsOf(q).filter(j => !j.expectFail && EXPECT_FAIL_HINT.test(String(j.note ?? ''))).map(j => j.id);
export const expectFailIds = (q: any): string[] => jobsOf(q).filter(j => j.expectFail === true).map(j => j.id);
// "[est 8-12 min]" / "[est 30 min]" per job note (upper bound); null when a job has none
export function estFromNotes(q: any): number | null {
  let sum = 0;
  for (const j of jobsOf(q)) { const m = String(j.note ?? '').match(/\[est (\d+)(?:\s*-\s*(\d+))? min\]/); if (!m) return null; sum += Number(m[2] ?? m[1]); }
  return jobsOf(q).length ? sum : null;
}
// queue keys binState / binStage (header): state null = not declared (the rewind rule decides)
type Stage = { to: string; from: string; sha256: string };
const GC_FILE = /^game-copy\/[\w.\/ -]+$/;
export function binStateOf(q: any, states = Object.keys(STATES)): { state: string | null; stage: Stage[]; problems: string[] } {
  const problems: string[] = [], stage: Stage[] = [], o = Array.isArray(q) ? {} : q ?? {}, s = o.binState, st = o.binStage;
  if (s !== undefined && !(typeof s === 'string' && states.includes(s))) problems.push(`binState ${JSON.stringify(s)} is not a turbo-compat state (${states.join(', ')})`);
  if (st !== undefined) {
    if (s === undefined) problems.push('binStage without binState');
    if (!st || typeof st !== 'object' || Array.isArray(st)) problems.push('binStage must be an object { "game-copy/<dest>": { from, sha256 } }');
    else for (const [to, v] of Object.entries<any>(st)) {
      const ok = (p: unknown) => typeof p === 'string' && GC_FILE.test(p) && !p.split('/').includes('..');
      if (!ok(to) || !ok(v?.from) || !/^[0-9a-f]{64}$/.test(v?.sha256 ?? '')) problems.push(`binStage ${to}: dest and from must be game-copy/ files (no ..), sha256 64 lowercase hex`);
      else stage.push({ to, from: v.from, sha256: v.sha256 });
    }
  }
  return { state: problems.length ? null : s ?? null, stage, problems };
}
const fileSha = (p: string) => createHash('sha256').update(readFileSync(p)).digest('hex');
// the state a queue gets; problem: it needs the fork install (every state but STOCK_STATES, Rodney 11-19: tests run on our fork
// build) and that is not ready (forkProblem from turbo-compat); warn: rewind auto without a rewind tool (fork build, fresh starts)
export function binChoice(declared: string | null, rewind: 'auto' | 'off', rewindExe: { from: 'own' | 'main' | 'none'; why: string }, forkProblem: string): { state: string; why: string; warn: string; problem: string } {
  const c = declared ? { state: declared, why: 'binState', warn: '' }
    : rewind !== 'auto' ? { state: 'apex', why: 'no binState, rewind off', warn: '' }
    : rewindExe.from === 'none' ? { state: 'apex', why: 'rewind auto', warn: `rewind auto but no rewind tool (${rewindExe.why}): apex, every job starts fresh` }
    : { state: 'apex-rewind', why: `rewind auto, rewind tool ${rewindExe.from === 'own' ? 'built here' : rewindExe.why}`, warn: '' };
  return { ...c, problem: !STOCK_STATES.has(c.state) && forkProblem ? `Bin state ${c.state} needs the fork install of FORK.sha, not ready (${forkProblem}): run node tools/turbo-compat.ts install-copy` : '' };
}
// the Apex asi a state puts in Bin: the fork install's (fork.json asi), stock Apex, or none
function apexAsiOf(state: string): string {
  if (state === 'base' || state === 'turbo') return 'no Apex';
  if (state === 'apex-stock') return 'stock Apex (latest release)';
  if (state === 'apex-oop') return 'PR158 asi (game-copy/apex-oop-test)';
  try { const fj = JSON.parse(readFileSync(join(OWN, 'game-copy', 'rewind-bridge-1119', 'install', 'fork.json'), 'utf8')); return `ApexRadiance.asi ${String(fj.asi).slice(0, 12)} (fork ${String(fj.sha).slice(0, 8)})`; } catch { return 'fork install missing'; }
}
const rewindDir = (root: string) => join(root, 'tools', 'harness', 'rewind');
function resolveBin(q: any, wt: string) {
  const exe = pickRewindExe(rewindDir(wt), rewindDir(M));
  return { ...binChoice(binStateOf(q).state, queueRewind(q), exe, forkInstallProblem()), exe };
}
export const queueTimeout = (est: number) => Math.max(30, 2 * est);
// next local HH:MM after `now`
export function nextAt(hhmm: string, now: Date): Date {
  const m = hhmm.match(/^(\d\d):(\d\d)$/); if (!m) throw new Error(`time must be HH:MM: ${hhmm}`);
  const d = new Date(now); d.setHours(Number(m[1]), Number(m[2]), 0, 0); if (d <= now) d.setDate(d.getDate() + 1); return d;
}
// generated-script lint: no deletes, no elevation, every child of the chain reads /dev/null
export function scriptProblems(text: string): string[] {
  const out: string[] = [], code = text.split('\n').filter(l => !/^\s*#/.test(l));
  for (const [i, l] of code.entries()) {
    if (/(^|[\s;&|(`])(rm|rmdir|del|Remove-Item|rd|sudo|runas|gsudo)\s/.test(l) || /-Verb\s+RunAs/i.test(l)) out.push(`line ${i + 1}: delete or elevation: ${l.trim().slice(0, 100)}`);
    if (/node[^|;]*tools\/harness\/queue\.ts/.test(l) && !/<\s*\/dev\/null/.test(l)) out.push(`line ${i + 1}: queue.ts without < /dev/null`);
  }
  return out;
}
// chain lock: one O_EXCL file per machine; owner = { night, pid } (Windows pid of the chain's bash on Windows)
export const LOCK = join(QOUT, 'night-chain.lock');
export const pidAlive = (pid: number) => { try { process.kill(pid, 0); return true; } catch (e: any) { return e.code === 'EPERM'; } };
export function tryLock(file: string, night: string, pid: number, alive = pidAlive): { ok: boolean; owner?: { night: string; pid: number } } {
  const me = JSON.stringify({ night, pid, at: new Date().toISOString() });
  for (let i = 0; i < 2; i++) {
    try { writeFileSync(file, me, { flag: 'wx' }); return { ok: true }; } catch (e: any) { if (e.code !== 'EEXIST') throw e; }
    let owner: { night: string; pid: number } | undefined;
    try { owner = JSON.parse(readFileSync(file, 'utf8')); } catch { /* half-written or unreadable: treat as stale */ }
    if (owner && owner.pid === pid) return { ok: true };
    if (owner && Number.isInteger(owner.pid) && alive(owner.pid)) return { ok: false, owner };
    try { unlinkSync(file); } catch { /* another chain took it over first: retry the create */ }
  }
  return { ok: false };
}
export function unlock(file: string, night: string): boolean {
  try { if (JSON.parse(readFileSync(file, 'utf8')).night !== night) return false; } catch { return false; }
  unlinkSync(file); return true;
}
// job ids of FAIL / VOID / CRASH rows in queue.ts output ("HH:MM:SS queue: <id>: FAIL in 3 min ...")
export const failedJobs = (out: string): string[] => [...new Set([...out.matchAll(/^\d\d:\d\d:\d\d queue: ([\w.-]+): (?:FAIL|VOID|CRASH) in /gm)].map(m => m[1]))];
// first-pass triage: known failure patterns of a queue's output -> class + suggested fix (the night-all-1124 failures)
export const CLASSES: { cls: string; re: RegExp; fix: (m: RegExpMatchArray) => string }[] = [
  { cls: 'setup: build file missing', re: /(asi missing|agentPackage missing|ts3fixes\.package missing|\*\* MISSING[^*]*\*\*)[^\n]{0,80}/, fix: m => `${m[0].slice(0, 120)}: a worktree without its game-copy junction or an unbuilt file; night-plan.ts --check creates the junction, builds.ts check names the file` },
  { cls: 'step timeout', re: /no reply to '(\w+)'/, fix: m => `repro step ${m[1]} hit the 8 s default reply timeout: give it "timeoutS" in repros.json` },
  { cls: 'early log miss', re: /FAIL early: ([^\n;]{0,160})/, fix: m => `${m[1]}: the repro did not produce the line (or the rule belongs to checkAt "end")` },
  { cls: 'regex miss (events not fired)', re: /events not fired: ([^\n|]{0,160})/, fix: m => `${m[1]}: test the rule on the real ts3native.log lines (split lines, arm counts, version string) and fix the regex or its count` },
  { cls: 'game never started', re: /no ts3native\.log copy/, fix: () => 'no ts3native.log copy: the game never started (asi refused, Steam, launcher): read the first FAIL line of the job log' },
  { cls: 'crash', re: /: CRASH in |game died at|exception 0x[0-9A-Fa-f]{8}|\bC0000\w{3}\b/, fix: () => 'ts3-crash-triage before any rerun (crash cause before fix)' },
  { cls: 'harness error', re: /(Cannot read properties of undefined[^\n]{0,60}|TypeError: [^\n]{0,80}|SyntaxError: [^\n]{0,80})/, fix: m => `${m[1]}: a harness bug: fix on a bugfix branch, rerun with --only` },
  { cls: 'refused', re: /(refusing to start[^\n]{0,120}|dry-run refused[^\n]{0,80})/, fix: m => `${m[1]}` },
];
export function triage(out: string, o: { est: number; minutes: number; rc: number }): { classes: string[]; lines: string[]; fastFail: boolean; failed: string[]; expected: string[] } {
  const failed = failedJobs(out), expected = [...out.matchAll(/^\d\d:\d\d:\d\d queue: ([\w.-]+): PASS in [^\n]*failed as expected/gm)].map(m => m[1]);
  const lines: string[] = [], classes: string[] = [];
  for (const c of CLASSES) { const m = out.match(c.re); if (m && (failed.length || o.rc !== 0)) { classes.push(c.cls); lines.push(`${c.cls}: ${c.fix(m)}`); } }
  if (o.rc === 124) { classes.push('queue timeout'); lines.push(`queue timeout at ${queueTimeout(o.est)} min (2x est): est too low or a hung job; the next queue started`); }
  if ((failed.length || o.rc !== 0) && !classes.length) { classes.push('unknown'); lines.push('no known pattern: hand it to ts3-fail-triage'); }
  const fastFail = (failed.length > 0 || o.rc !== 0) && o.minutes < 0.25 * o.est;
  return { classes, lines, fastFail, failed, expected };
}

// ---------------------------------------------------------------- machine parts
const git = (cwd: string, ...a: string[]) => execFileSync('git', a, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'], timeout: 60000 }).trim();
function worktreeOf(r: Run): string {
  if (r.worktree) return r.worktree === '.' ? M : join(M, '.claude', 'worktrees', r.worktree);
  const list = git(M, 'worktree', 'list', '--porcelain').split(/\r?\n\r?\n/);
  const hit = list.find(b => b.split(/\r?\n/).includes(`branch refs/heads/${r.branch}`));
  return hit ? resolve(hit.split(/\r?\n/)[0].replace(/^worktree /, '')) : '';
}
function queueFileOf(wt: string, q: string): string {
  if (/^[\w.-]+$/.test(q) && !q.endsWith('.json')) return join(wt, 'tools', 'harness', 'queues', `${q}.json`);
  return isAbsolute(q) ? q : [join(wt, q), join(M, q)].find(existsSync) ?? join(wt, q);
}
// agent per queue: key, else builds.json (branch build, else newest in history); never a guess
function agentOf(wt: string, q: any): { agent: string; why: string } {
  const keys = [...new Set([q?.agentPackage, ...jobsOf(q).map(j => j.agentPackage)].filter(Boolean))];
  if (keys.length > 1) return { agent: '', why: `jobs name ${keys.length} agentPackages: one per queue (${keys.join(', ')})` };
  if (keys.length) return { agent: resolve(wt, keys[0]), why: 'queue key agentPackage' };
  const db = JSON.parse(readFileSync(join(wt, 'docs', 'data', 'builds.json'), 'utf8'));
  const agents = (Array.isArray(db) ? db : db.builds).filter((b: any) => b.kind === 'ts3agent' && b.commit).sort((a: any, b: any) => String(b.created).localeCompare(String(a.created)));
  const head = new Set(git(wt, 'rev-list', '-n', '3000', 'HEAD').split(/\s+/)), master = new Set(git(wt, 'rev-list', '-n', '3000', 'master').split(/\s+/));
  const inSet = (s: Set<string>, c: string) => [...s].some(h => h.startsWith(c));
  const own = agents.find((b: any) => inSet(head, b.commit) && !inSet(master, b.commit)), any = agents.find((b: any) => inSet(head, b.commit));
  const b = own ?? any;
  return b ? { agent: join(wt, 'game-copy', b.name), why: `builds.json ${b.name} (${own ? 'built on this branch' : 'newest in its history'})` } : { agent: '', why: 'no agentPackage key and no ts3agent entry in builds.json' };
}
// a pinned file must be a builds.json entry of its worktree (asi / agent by sha256, ts3fixes by tuning digest: packages are not
// byte-reproducible) and pass `builds.ts check --expect <that entry>`; file names need not match entry names (cullrace1 vs -1124)
const buildsCheck = (wt: string, file: string): string => {
  const db = loadDb(join(wt, 'docs', 'data', 'builds.json')), buf = readFileSync(file), sha = createHash('sha256').update(buf).digest('hex');
  let hit = db.find(b => b.sha256 === sha);
  if (!hit && /ts3fixes/.test(basename(file))) { try { const d = packageTuning(buf).digest; hit = db.find(b => (b as any).tuningDigest === d); } catch { /* not a package */ } }
  if (!hit) return `not registered in builds.json (sha ${sha.slice(0, 12)})`;
  try { execFileSync(process.execPath, ['tools/builds.ts', 'check', file, '--expect', hit.name], { cwd: wt, stdio: ['ignore', 'pipe', 'pipe'], timeout: 120000 }); return ''; }
  catch (e: any) { return `${String(e.stdout ?? '')}${String(e.stderr ?? '')}`.trim().split('\n')[0] || 'builds.ts check failed'; }
};

const CHAIN_DIR = '\0dir', CHAIN_UNZ = join(CHAIN_DIR, 'apex-unz');
export function preflight(plan: Plan, now = new Date()): { problems: string[]; notes: string[]; runs: Resolved[] } {
  const problems: string[] = [], notes: string[] = [], runs: Resolved[] = [];
  if (!/^[\w.-]+$/.test(plan.night ?? '')) problems.push('plan.night must be letters, digits, . _ -');
  if (!Array.isArray(plan.runs) || !plan.runs.length) problems.push('plan.runs must be a non-empty list');
  for (const r of plan.runs ?? []) {
    const at = `${r.worktree ?? r.branch ?? '?'}|${r.queue}`, wt = worktreeOf(r);
    if (!wt || !existsSync(wt)) { problems.push(`${at}: worktree missing (${wt || `no worktree for branch ${r.branch}`})`); continue; }
    const gc = join(wt, 'game-copy');
    if (wt !== M && !existsSync(gc)) {
      let dangling = false; try { lstatSync(gc); dangling = true; } catch { /* absent */ }
      if (dangling) { problems.push(`${at}: ${gc} is a dangling link: relink it by hand`); continue; }
      try { symlinkSync(join(M, 'game-copy'), gc, 'junction'); notes.push(`${at}: game-copy junction created`); }
      catch (e) { problems.push(`${at}: game-copy junction missing and not created: ${(e as Error).message}`); continue; }
    }
    const qf = queueFileOf(wt, r.queue);
    if (!existsSync(qf)) { problems.push(`${at}: queue file missing: ${qf}`); continue; }
    let q: any; try { q = JSON.parse(readFileSync(qf, 'utf8')); } catch (e) { problems.push(`${at}: queue is not JSON: ${(e as Error).message}`); continue; }
    const name = basename(qf, '.json'), est = r.est ?? estFromNotes(q);
    if (!est) problems.push(`${at}: no est (plan "est" or "[est N min]" in every job note)`);
    const { agent, why } = agentOf(wt, q);
    if (!agent) problems.push(`${at}: agent package unresolved: ${why}`);
    else if (!existsSync(agent)) problems.push(`${at}: agent package ${agent} missing (${why})`);
    else { const bc = buildsCheck(wt, agent); if (bc) problems.push(`${at}: agent ${basename(agent)}: ${bc}`); }
    const pinned = new Set<string>();
    for (const j of jobsOf(q)) for (const f of [j.asi, j.fixesPackage]) if (typeof f === 'string' && /^game-copy\//.test(f)) pinned.add(f);
    for (const f of pinned) { if (!existsSync(join(wt, f))) problems.push(`${at}: pinned ${f} missing`); else { const bc = buildsCheck(wt, join(wt, f)); if (bc) problems.push(`${at}: ${f}: ${bc}`); } }
    const repros: any[] = existsSync(join(wt, 'tools', 'harness', 'repros.json')) ? JSON.parse(readFileSync(join(wt, 'tools', 'harness', 'repros.json'), 'utf8')) : [];
    for (const rp of new Set(jobsOf(q).flatMap(reprosOf))) {
      const entry = repros.find(x => x?.id === rp);
      if (!entry) { problems.push(`${at}: repro ${rp} not in repros.json`); continue; }
      const slow = slowStepsWithoutTimeout(entry);
      if (slow.length) problems.push(`${at}: repro ${rp}: ${slow.length} slow step(s) without timeoutS: ${[...new Set(slow)].join(', ')}`);
    }
    for (const p of regexCaseProblems(q)) problems.push(`${at}: ${p}`);
    const bs = binStateOf(q);
    for (const p of bs.problems) problems.push(`${at}: ${p}`);
    for (const f of bs.stage) if (!existsSync(join(OWN, f.from))) problems.push(`${at}: binStage from ${f.from} missing`); else if (fileSha(join(OWN, f.from)) !== f.sha256) problems.push(`${at}: binStage from ${f.from} sha256 ${fileSha(join(OWN, f.from)).slice(0, 12)}, want ${f.sha256.slice(0, 12)}`);
    if (bs.state) { // the state's own sources (the chain's apex-unz comes at snapshot; staged files at the queue start)
      const staged = new Set(bs.stage.map(f => resolve(OWN, f.to)));
      // chain-dir paths (apex-unz, and apex-stock-unz next to it, fetched at snapshot) are skipped: '\0dir' marks them
      for (const src of Object.values(STATES[bs.state](CHAIN_UNZ))) if (src && !src.startsWith(CHAIN_DIR) &&!staged.has(resolve(src)) && !existsSync(src)) problems.push(`${at}: Bin state ${bs.state}: ${src} missing`);
      if (bs.stage.length) notes.push(`${at}: binStage ${bs.stage.length} file(s)`);
    }
    if (!bs.problems.length) {
      const rb = resolveBin(q, wt);
      if (rb.problem) problems.push(`${at}: ${rb.problem}`);
      if (rb.warn) notes.push(`${at}: WARN ${rb.warn}`);
      notes.push(`${at}: Bin state ${rb.state} (${rb.why}): ${apexAsiOf(rb.state)}`);
    }
    const unmarked = expectFailUnmarked(q);
    if (unmarked.length) problems.push(`${at}: note says FAIL by design but no expectFail: true: ${unmarked.join(', ')}`);
    try {
      const out = execFileSync(process.execPath, ['tools/harness/queue.ts', qf, '--dry-run'], { cwd: wt, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], timeout: 180000 });
      const bad = out.split(/\r?\n/).filter(l => /\*\*[^*]*(MISSING|REFUSED|refuses)[^*]*\*\*/.test(l));
      if (bad.length) problems.push(`${at}: dry-run: ${bad.length} refusal line(s): ${bad[0].trim().slice(0, 160)}`);
    } catch (e: any) { problems.push(`${at}: dry-run refused: ${String(e.stderr ?? e.stdout ?? e.message).trim().split('\n').slice(-1)[0].slice(0, 200)}`); }
    if (r.gate !== undefined && typeof r.gate !== 'boolean') problems.push(`${at}: gate is true, false or left out`);
    runs.push({ wt, q: qf, name, est: est ?? 0, agent, agentWhy: why, expectFail: expectFailIds(q), gate: r.gate === true });
  }
  const dup = runs.map(r => r.name).filter((n, i, a) => a.indexOf(n) !== i);
  if (dup.length) problems.push(`queue names twice (their .out files would mix): ${[...new Set(dup)].join(', ')}`);
  try { execFileSync(process.execPath, ['tools/harness/cleanup.ts', 'gate'], { cwd: M, stdio: ['ignore', 'pipe', 'pipe'], timeout: 600000 }); }
  catch (e: any) { problems.push(`disk gate: ${String(e.stdout ?? e.message).trim().split('\n').slice(-1)[0]}`); }
  if (plan.cutoff) {
    const cut = nextAt(plan.cutoff, now), total = runs.reduce((s, r) => s + r.est, 0), end = new Date(now.getTime() + total * 60000);
    notes.push(`total est ${total} min, ends ~${end.toTimeString().slice(0, 5)}, cutoff ${plan.cutoff}`);
    if (end > cut) { let t = now.getTime(); const late = runs.filter(r => (t += r.est * 60000) > cut.getTime()).map(r => r.name); problems.push(`total est ${total} min ends after the ${plan.cutoff} cutoff; would not fit: ${late.join(', ')}`); }
  } else problems.push('plan.cutoff (HH:MM) missing');
  return { problems, notes, runs };
}

const posix = (p: string) => process.platform === 'win32' ? p.replace(/\\/g, '/').replace(/^([A-Za-z]):/, (_, d) => `/${d.toLowerCase()}`) : p;
export function chainScript(plan: Plan, runs: Resolved[], now = new Date(), alienfx = ''): string {
  const c = `chain-${plan.night}`, cut = Math.floor(nextAt(plan.cutoff, now).getTime() / 1000);
  const rgbUntil = plan.rgbOffUntil && alienfx ? Math.floor(nextAt(plan.rgbOffUntil, now).getTime() / 1000) : 0;
  const rows = runs.map(r => [posix(r.wt), posix(r.q), posix(r.agent), r.est, queueTimeout(r.est), r.expectFail.join(',') || '-', r.gate ? 1 : 0].join('|')).join('\n');
  return `#!/bin/bash
# ${plan.night}: generated by tools/harness/night-plan.ts (${now.toISOString()}); do not edit, regenerate with --write.
# Fields: worktree|queue|agent|est min|timeout min (2x est, >= 30)|expectFail ids. Bin restore if killed: node tools/turbo-compat.ts restore probe-out/${c}
M=${posix(M)}
cd $M || exit 1
export TS3_CHAIN=${c} TS3_NIGHT=1 TS3_AGENT_WAIT_S=900
unset TIMING
C=${c}; N=${plan.night}; O=$M/probe-out/queue; D=$M/probe-out/$C; L=$O/$N.log; W=$O/$N-wake.log
RUNS="${rows}"
CUT=${cut}
ts() { date +%H:%M; }
mk() { node --no-warnings tools/harness/track.ts marker "$(ts) $C: $*" </dev/null >/dev/null 2>&1; echo "$(ts) $*" | tee -a $L; }
wake() { echo "$(ts) $*" >> $W; }
TC="node --no-warnings tools/turbo-compat.ts"
nogame() { while tasklist 2>/dev/null </dev/null | grep -qi TS3W.exe; do sleep 20; done; }
${rgbUntil ? `RGB="${posix(alienfx)}"
( while [ $(date +%s) -lt ${rgbUntil} ]; do "$RGB" lowlevel setall=0,0,0 setdim=0 </dev/null >/dev/null 2>&1; sleep 120; done ) &
RGBPID=$!` : 'RGBPID='}
NP="node --no-warnings tools/harness/night-plan.ts"
WP=$(cat /proc/$$/winpid 2>/dev/null || echo $$)
mk "$N start; waiting for the chain lock, TS3W.exe to exit and the running job to end"
$NP --lock $N --pid $WP </dev/null >> $L 2>&1 || { mk "CHAIN STOP: chain lock failed"; wake "STOP chain lock"; exit 1; }
trap '$NP --unlock $N </dev/null >/dev/null 2>&1' EXIT
nogame; node --no-warnings tools/harness/hold.ts wait-job-end </dev/null >/dev/null 2>&1
node --no-warnings tools/harness/cleanup.ts gate </dev/null >> $L 2>&1 || { mk "CHAIN STOP: disk gate"; wake "STOP disk gate"; exit 1; }
[ "$(git rev-parse --abbrev-ref HEAD)" = master ] || { mk "CHAIN STOP: main checkout is not on master"; wake "STOP not on master"; exit 1; }
[ -e "$D" ] && { mk "CHAIN STOP: $D already exists"; wake "STOP snapshot dir exists"; exit 1; }
$TC snapshot $D > $O/$C-snapshot.out 2>&1 < /dev/null || { mk "CHAIN STOP: snapshot failed, Bin unchanged"; wake "STOP snapshot"; exit 1; }
restore() { [ -n "$RGBPID" ] && kill $RGBPID 2>/dev/null; nogame; sleep 5; $TC restore $D > $O/$C-restore.out 2>&1 < /dev/null && mk "Bin restored" || mk "WARN: Bin restore failed";
  $NP --unlock $N </dev/null >/dev/null 2>&1; mk "CHAIN END"; wake "DONE $N"; }
trap restore EXIT
agentin() { # worktree agent tag: back up the worktree's build/ts3agent.package once, install the queue's agent
  local b=$1/build/ts3agent.package bak=$D/agent-$3.bak
  mkdir -p $1/build
  [ -f "$b" ] && cp -p "$b" "$bak"
  cp "$2" "$b" && cmp -s "$b" "$2"; }
agentout() { local b=$1/build/ts3agent.package bak=$D/agent-$2.bak; [ -f "$bak" ] && cp -p "$bak" "$b" && mk "agent put back in $1"; }
runq() { # worktree queue agent est timeout tag [--only ids]
  local w=$1 q=$2 a=$3 e=$4 to=$5 tag=$6 name=$(basename $2 .json); shift 6
  local st; st=$($NP --bin-state $q --wt $w --stage < /dev/null 2>&1) || { mk "SKIPPED $name$tag: Bin state refused ($(echo "$st" | tail -1 | cut -c1-140))"; return 1; }; st=$(echo "$st" | tail -1)
  $TC state $D $st > $O/$C-state-$name$tag.out 2>&1 < /dev/null || { mk "SKIPPED $name$tag: Bin state $st failed"; return 1; }
  agentin $w $a $name || { mk "SKIPPED $name$tag: agent install failed ($a)"; return 1; }
  mk "queue $name$tag start ($(basename $w), agent $(basename $a), est \${e}m, timeout \${to}m, Bin $st)"
  local t0=$(date +%s)
  (cd $w && timeout \${to}m node --no-warnings tools/harness/queue.ts $q "$@" > $O/$C-$name$tag.out 2>&1 < /dev/null)
  local rc=$? min=$(( ($(date +%s) - t0) / 60 ))
  [ $rc -ne 0 ] && { taskkill //F //T //IM TS3W.exe < /dev/null > /dev/null 2>&1; nogame; }
  agentout $w $name
  [ "$st" != apex ] && { nogame; $TC state $D apex > $O/$C-state-$name$tag-back.out 2>&1 < /dev/null && mk "Bin back to apex after $name$tag" || mk "WARN: Bin back to apex failed after $name$tag (the next queue sets its own state)"; }
  mk "queue $name$tag exited $rc in \${min}m: $(grep -E 'queue: \\S+: (PASS|FAIL|N/A|VOID|CRASH|KILLED) in' $O/$C-$name$tag.out | cut -c1-110 | tr '\\n' ';')"
  node --no-warnings tools/harness/night-plan.ts --triage --night $N --name $name$tag --out $O/$C-$name$tag.out --est $e --minutes $min --rc $rc < /dev/null >> $L 2>&1
  return $rc
}
echo "$RUNS" | while IFS='|' read -r w q a e to xf g; do
  name=$(basename $q .json)
  [ $(date +%s) -ge $CUT ] && { mk "SKIPPED $name: past the ${plan.cutoff} cutoff"; continue; }
  [ $(( $(date +%s) + e * 60 )) -gt $(( CUT + 1800 )) ] && { mk "SKIPPED $name: est \${e}m runs > 30 min past the cutoff"; continue; }
  (cd $w && timeout 180 node --no-warnings tools/harness/queue.ts $q --dry-run < /dev/null > $O/$C-dry-$name.out 2>&1) || { mk "SKIPPED $name: dry-run refused ($(tail -1 $O/$C-dry-$name.out | cut -c1-100))"; wake "FAIL $name dry-run refused"; continue; }
  runq $w $q $a $e $to ""; rc=$?
  # one rerun of the FAIL/VOID/CRASH jobs only (expectFail ids never), never the whole queue
  ids=$(node --no-warnings tools/harness/night-plan.ts --triage --night $N --name $name --out $O/$C-$name.out --est $e --minutes 0 --rc 0 --rerun-ids "$xf" < /dev/null 2>/dev/null | tail -1)
  if [ -n "$ids" ] && [ "$ids" != "-" ] && [ $(date +%s) -lt $CUT ]; then
    mk "rerun $name once: --only $ids"
    runq $w $q $a $e $to -rerun --only $ids; rc=$?
  fi
  if [ "$g" = 1 ] && [ $rc -ne 0 ]; then mk "CHAIN STOP: gate $name did not pass (rc $rc): the runs after it are not started"; wake "STOP gate $name"; exit 3; fi
done
[ \${PIPESTATUS[1]} -eq 3 ] && exit 1
mk "$N DONE"
`;
}

// ---------------------------------------------------------------- triage + watch (chain side and session side)
function triageCmd() {
  const n = values.night!, out = values.out!, txt = existsSync(out) ? readFileSync(out, 'latin1') : '';
  if (values['rerun-ids'] !== undefined) { // rerun list only: failed ids minus expectFail ids ("-" = none)
    const skip = new Set(values['rerun-ids'].split(',').filter(x => x && x !== '-'));
    console.log(failedJobs(txt).filter(id => !skip.has(id)).join(',') || '-');
    return;
  }
  const t = triage(txt, { est: Number(values.est), minutes: Number(values.minutes), rc: Number(values.rc) });
  const md = join(QOUT, `${n}-triage.md`), wake = join(QOUT, `${n}-wake.log`), hh = new Date().toTimeString().slice(0, 5);
  if (!existsSync(md)) writeFileSync(md, `# ${n} first-pass triage (night-plan.ts --triage, live)\n\nOne block per queue end. Classes are pattern matches, not causes: confirm before a fix (ts3-fail-triage).\n`);
  const ok = !t.failed.length && Number(values.rc) === 0;
  appendFileSync(md, `\n## ${hh} ${values.name}: ${ok ? 'PASS' : `FAIL (${t.failed.join(', ') || `rc ${values.rc}`})`}${t.fastFail ? ' **FAST FAIL: likely setup**' : ''}\n` +
    `est ${values.est} min, ran ${values.minutes} min, rc ${values.rc}; out: ${out}\n` +
    (t.expected.length ? `- expectFail jobs failed as expected: ${t.expected.join(', ')}\n` : '') + t.lines.map(l => `- ${l}\n`).join(''));
  if (!ok) appendFileSync(wake, `${hh} FAIL ${values.name}: ${t.fastFail ? 'FAST FAIL: likely setup; ' : ''}${t.classes.join(', ')}\n`);
  console.log(`${hh} triage ${values.name}: ${ok ? 'PASS' : t.classes.join(', ')}${t.fastFail ? ' (FAST FAIL: likely setup)' : ''}`);
}
// session side: exit 0 with the new wake lines (FAIL / STOP / DONE), exit 2 at --timeout. The cursor file keeps the read offset,
// so a restarted watcher reports only what came after the last wake
async function watchCmd(n: string) {
  const wake = join(QOUT, `${n}-wake.log`), cur = join(QOUT, `${n}-wake.cursor`), end = Date.now() + Number(values.timeout) * 1000;
  let from = existsSync(cur) ? Number(readFileSync(cur, 'utf8')) || 0 : 0;
  for (;;) {
    const size = existsSync(wake) ? statSync(wake).size : 0;
    if (size < from) from = 0;
    if (size > from) {
      const lines = readFileSync(wake, 'utf8').slice(from).split(/\r?\n/).filter(Boolean);
      writeFileSync(cur, String(size));
      console.log(lines.join('\n'));
      console.log(lines.some(l => /\bDONE\b/.test(l)) ? 'night done: triage, then stop watching' : `triage now: ${join(QOUT, `${n}-triage.md`)}; then restart this watcher`);
      process.exit(0);
    }
    if (Date.now() >= end) { console.log(`no wake in ${values.timeout} s: re-arm`); process.exit(2); }
    await new Promise(r => setTimeout(r, 20000));
  }
}

function selfCheck() {
  // repro lint: the vbowner 11-24 miss (place_objects, no timeoutS) and a travel step
  assert.deepEqual(slowStepsWithoutTimeout({ steps: [{ cmd: 'place_objects', args: {} }, { cmd: 'place_objects', timeoutS: 120 }, { cmd: 'time' }], loop: { steps: [{ cmd: 'travel_home' }] } }), ['place_objects', 'travel_home']);
  assert.deepEqual(reprosOf({ run: { profile: { repro: 'a', repros: ['b', { repro: 'c' }] } } }), ['a', 'b', 'c']);
  // regexCases: the compat 11-24 split-line marker (a case line the rule misses), a missing case, a clean pair
  const q = { regexCases: { '^ts3fixes: script assemblies \\(\\d+\\): .*Moar': { match: ['ts3fixes: script assemblies (61): NRaas'], noMatch: ['x'] } },
    jobs: [{ id: 'a', logRequire: { '^ts3fixes: script assemblies \\(\\d+\\): .*Moar': 1 } }, { id: 'b', logRequire: { '^ok$': 1 }, regexCases: { '^ok$': { match: ['ok'], noMatch: ['ok2'] } } }, { id: 'c', logRequire: { '^z': 1 } }] };
  const rp = regexCaseProblems(q);
  assert.equal(rp.length, 2); assert.match(rp[0], /^a: .*misses "ts3fixes: script assemblies \(61\)/); assert.match(rp[1], /^c: .*no regexCases/);
  // expectFail hint: the tuneisolate OFF note shape
  assert.deepEqual(expectFailUnmarked({ jobs: [{ id: 'off', note: 'OFF arm: expected to FAIL (the bug shows)' }, { id: 'on', note: 'ON arm must PASS' }, { id: 'm', note: 'FAIL expected', expectFail: true }] }), ['off']);
  assert.deepEqual(expectFailIds({ jobs: [{ id: 'm', expectFail: true }, { id: 'n' }] }), ['m']);
  assert.equal(estFromNotes({ jobs: [{ note: '[est 8-12 min] x' }, { note: '[est 30 min]' }] }), 42);
  assert.equal(estFromNotes({ jobs: [{ note: '[est 8 min]' }, { note: 'none' }] }), null);
  assert.equal(queueTimeout(10), 30); assert.equal(queueTimeout(45), 90);
  const now = new Date(2026, 10, 24, 23, 0);
  assert.equal(nextAt('07:45', now).getDate(), 25); assert.equal(nextAt('23:30', now).getDate(), 24); assert.throws(() => nextAt('7:45', now));
  // script lint
  assert.deepEqual(scriptProblems('# rm in a comment is fine\ncp a b\n'), []);
  assert.equal(scriptProblems('rm -f x\n').length, 1); assert.equal(scriptProblems('x; del y\n').length, 1); assert.equal(scriptProblems('Start-Process x -Verb RunAs\n').length, 1);
  assert.equal(scriptProblems('(cd w && node tools/harness/queue.ts q >> o 2>&1)\n').length, 1, 'queue.ts without /dev/null');
  // the generated script itself passes its own lint and bash -n (when bash is there)
  const runs: Resolved[] = [{ wt: M, q: join(M, 'tools/harness/queues/x.json'), name: 'x', est: 20, agent: join(M, 'game-copy/ts3agent.package.t'), agentWhy: 'k', expectFail: ['off'] }];
  const sh = chainScript({ night: 'night-test', cutoff: '07:45', rgbOffUntil: '09:00', runs: [] }, runs, now, 'C:\\x\\alienfx-cli.exe');
  assert.deepEqual(scriptProblems(sh), []);
  { // gate: its row carries 1, a failed gate (after its rerun) stops the loop and the script, no DONE line
    const g = chainScript({ night: 'night-test', cutoff: '07:45', runs: [] }, [{ ...runs[0], gate: true }], now, '');
    assert.match(g, /RUNS=".*\|20\|40\|off\|1"/); assert.match(g, /if \[ "\$g" = 1 \] && \[ \$rc -ne 0 \]; then mk "CHAIN STOP: gate/);
    assert.ok(g.includes('exit 3; fi\ndone\n[ $' + '{PIPESTATUS[1]} -eq 3 ] && exit 1\nmk ') && g.includes('-rerun --only $ids; rc=$?'), 'gate after the rerun, script exits');
    assert.deepEqual(scriptProblems(g), []);
  }
  // Bin state per queue: declared, unknown, staged, undeclared; the chain asks night-plan and puts apex back after a queue
  const ST = ['base', 'apex', 'apex-rewind', 'apex-oop-atlas'], H = 'a'.repeat(64);
  assert.deepEqual(binStateOf({ jobs: [] }, ST), { state: null, stage: [], problems: [] }, 'undeclared: the rewind rule decides');
  assert.deepEqual(binStateOf([{ id: 'x' }], ST).state, null, 'array queue');
  assert.equal(binStateOf({ binState: 'apex-oop-atlas' }, ST).state, 'apex-oop-atlas');
  assert.match(binStateOf({ binState: 'apex-oop' }, ST).problems[0], /binState "apex-oop" is not a turbo-compat state/, 'unknown state refused');
  assert.equal(binStateOf({ binState: 'apex-oop' }, ST).state, null);
  assert.match(binStateOf({ binState: 7 }, ST).problems[0], /is not a turbo-compat state/);
  assert.deepEqual(binStateOf({ binState: 'apex-oop-atlas', binStage: { 'game-copy/apex-oop-test/atlas-oop.dll': { from: 'game-copy/atlas-main13/atlas-oop.dll', sha256: H } } }, ST).stage,
    [{ to: 'game-copy/apex-oop-test/atlas-oop.dll', from: 'game-copy/atlas-main13/atlas-oop.dll', sha256: H }]);
  for (const bad of [{ 'game-copy/../x.dll': { from: 'game-copy/a', sha256: H } }, { 'C:/Bin/x.dll': { from: 'game-copy/a', sha256: H } }, { 'game-copy/x': { from: 'game-copy/a', sha256: 'abc' } }, { 'game-copy/x': { from: 'tools/a', sha256: H } }])
    assert.equal(binStateOf({ binState: 'apex', binStage: bad }, ST).problems.length, 1, `bad binStage ${JSON.stringify(bad)}`);
  assert.match(binStateOf({ binStage: {} }, ST).problems[0], /binStage without binState/);
  assert.ok(['apex', 'apex-rewind', 'apex-oop-atlas'].every(k => k in STATES), 'turbo-compat names');
  // which state; the fork install is required (no stock fallback); rewind auto without a tool = fork apex, fresh starts
  const own = { from: 'own' as const, why: 'built' }, main = { from: 'main' as const, why: "the main checkout's" }, none = { from: 'none' as const, why: 'not built' };
  assert.deepEqual([binChoice(null, 'auto', own, '').state, binChoice(null, 'auto', main, '').state, binChoice(null, 'off', main, '').state], ['apex-rewind', 'apex-rewind', 'apex'], 'worktree borrows the main exe');
  assert.match(binChoice(null, 'auto', none, '').warn, /rewind auto but no rewind tool \(not built\): apex, every job starts fresh/);
  assert.equal(binChoice(null, 'off', none, '').warn, '', 'rewind off: apex (the fork build) by design');
  assert.match(binChoice(null, 'off', none, 'no fork.json').problem, /^Bin state apex needs the fork install of FORK.sha, not ready \(no fork.json\): run node tools\/turbo-compat.ts install-copy$/, 'no stock fallback');
  assert.ok(binChoice('apex-oop-atlas', 'off', none, 'x').problem && binChoice('apex-rewind', 'off', none, 'x').problem);
  assert.equal(binChoice('apex-stock', 'off', none, 'x').problem, '', 'stock only as the opt-in state');
  for (const st of ['apex', 'apex-stock']) assert.ok(Object.values(STATES[st](CHAIN_UNZ)).every(s => !s || s.startsWith(CHAIN_DIR) || !s.includes('-unz')), `${st}: its unz sources sit in the chain dir (preflight skips them)`);
  assert.deepEqual(binChoice('apex-rewind', 'off', none, ''), { state: 'apex-rewind', why: 'binState', warn: '', problem: '' });
  assert.ok(FORK.sha.length === 40);
  assert.match(sh, /st=\$\(\$NP --bin-state \$q --wt \$w --stage < \/dev\/null 2>&1\) \|\| \{ mk "SKIPPED/, 'state from night-plan, refused = skipped');
  assert.ok(!/case "\$st"/.test(sh), 'no apex|apex-rewind whitelist forcing apex');
  assert.ok(sh.indexOf('$TC state $D $st') < sh.indexOf('queue.ts $q "$@"') && sh.indexOf('queue.ts $q "$@"') < sh.indexOf('$TC state $D apex >'), 'state before the queue, apex after');
  assert.match(sh, /RUNS=".*\|20\|40\|off\|0"/); assert.match(sh, /setall=0,0,0 setdim=0/); assert.match(sh, /trap restore EXIT/); assert.match(sh, /--rerun-ids "\$xf"/); assert.ok(sh.includes('queue.ts $q "$@" > $O/$C-$name$tag.out 2>'), 'fresh .out per run: a rerun chain of the same night must not rerun old FAIL ids');
  // chain lock: taken before nogame / wait-job-end, released by the EXIT traps
  assert.ok(sh.indexOf('$NP --lock $N') > 0 && sh.indexOf('$NP --lock $N') < sh.indexOf('\nnogame;'), 'lock before the nogame wait');
  assert.match(sh, /restore\(\) \{[^}]*\$NP --unlock \$N/);
  const td = mkdtempSync(join(tmpdir(), 'night-lock-')), lf = join(td, 'night-chain.lock'), live = new Set([101, 102]);
  try {
    const al = (p: number) => live.has(p);
    assert.deepEqual(tryLock(lf, 'night-a', 101, al), { ok: true });
    assert.deepEqual(tryLock(lf, 'night-a', 101, al), { ok: true }, 'own lock again');
    const busy = tryLock(lf, 'night-b', 102, al);
    assert.equal(busy.ok, false); assert.equal(busy.owner?.night, 'night-a', 'second live chain waits');
    assert.equal(unlock(lf, 'night-b'), false, 'only the owner unlocks'); assert.ok(existsSync(lf));
    assert.equal(unlock(lf, 'night-a'), true); assert.ok(!existsSync(lf));
    assert.deepEqual(tryLock(lf, 'night-b', 102, al), { ok: true });
    live.delete(102);
    assert.deepEqual(tryLock(lf, 'night-c', 101, al), { ok: true }, 'dead owner: taken over');
    assert.equal(JSON.parse(readFileSync(lf, 'utf8')).night, 'night-c');
    writeFileSync(lf, '{half');
    assert.deepEqual(tryLock(lf, 'night-d', 101, al), { ok: true }, 'unreadable lock: taken over');
    assert.ok(pidAlive(process.pid));
  } finally { rmSync(td, { recursive: true, force: true }); }
  try { execFileSync('bash', ['-n'], { input: sh, stdio: ['pipe', 'ignore', 'pipe'], timeout: 30000 }); } catch (e: any) { if (e.code !== 'ENOENT') assert.fail(`bash -n: ${e.stderr}`); }
  // triage: the night-all-1124 lines
  assert.deepEqual(failedJobs('18:42:44 queue: bp-cullrace-smoke: FAIL in 0 min | x\n18:44:48 queue: ip-c: PASS in 3 min\n21:12:13 queue: bp-l: CRASH in 11 min | y\n'), ['bp-cullrace-smoke', 'bp-l']);
  const cls = (out: string, o = { est: 30, minutes: 20, rc: 1 }) => triage(out, o);
  assert.deepEqual(cls('18:42:44 queue: bp-c: FAIL in 0 min | FAIL: asi missing: game-copy/ts3native.asi.cullrace1 (refused before touching the game)').classes, ['setup: build file missing']);
  assert.deepEqual(cls("18:37:07 queue: bp-v: FAIL in 2 min | FAIL: repro step place_objects failed: no reply to 'place_objects' (game paused, loading or blocked?)").classes, ['step timeout']);
  assert.match(cls("x: FAIL in 2 min\nno reply to 'place_objects'").lines[0], /place_objects.*timeoutS/);
  assert.deepEqual(cls('18:49:59 queue: ip-x: FAIL in 3.4 min | FAIL: events not fired: log /^ts3fixes: script assemblies \\(/ 0 lines').classes, ['regex miss (events not fired)']);
  assert.ok(cls('18:42:44 queue: bp-c: FAIL in 0 min | FAIL: UNKNOWN: no ts3native.log copy, log rules not checked').classes.includes('game never started'));
  assert.ok(cls('21:12:13 queue: bp-l: CRASH in 11 min | FAIL: game died at 21:12:10 (TS3W.exe process gone)').classes.includes('crash'));
  assert.deepEqual(cls("TypeError: Cannot read properties of undefined (reading 'map')", { est: 30, minutes: 1, rc: 1 }).classes, ['harness error']);
  assert.ok(cls('x: FAIL in 1 min | FAIL: FAIL early: log /^a/ 0 lines (want >= 1) at the repro end').classes.includes('early log miss'));
  assert.deepEqual(cls('', { est: 30, minutes: 60, rc: 124 }).classes, ['queue timeout']);
  assert.ok(cls('q: FAIL in 1 min | odd', { est: 40, minutes: 2, rc: 1 }).fastFail, 'under 25% of est: FAST FAIL');
  assert.ok(!cls('q: FAIL in 1 min | odd', { est: 40, minutes: 20, rc: 1 }).fastFail);
  assert.deepEqual(cls('17:00:00 queue: a: PASS in 3 min', { est: 30, minutes: 3, rc: 0 }), { classes: [], lines: [], fastFail: false, failed: [], expected: [] }, 'a PASS queue: nothing');
  assert.deepEqual(cls('16:51:03 queue: ip-off: PASS in 15 min | PASS: failed as expected (expectFail): FAIL: x', { est: 30, minutes: 15, rc: 0 }).expected, ['ip-off']);
  console.log('self-check ok');
}

// ---------------------------------------------------------------- main
if (values['self-check']) { selfCheck(); process.exit(0); }
if (values.triage) { triageCmd(); process.exit(0); }
if (values.unlock) { console.log(unlock(LOCK, values.unlock) ? `chain lock of ${values.unlock} released` : `chain lock not held by ${values.unlock}`); process.exit(0); }
if (values.lock) { // chain side: wait (no time limit, like nogame) until this chain holds the lock
  const pid = Number(values.pid);
  if (!Number.isInteger(pid) || pid <= 0) { console.error('--lock needs --pid <pid>'); process.exit(2); }
  let said = '';
  for (;;) {
    const r = tryLock(LOCK, values.lock, pid);
    if (r.ok) { console.log(`${new Date().toTimeString().slice(0, 5)} chain lock taken by ${values.lock} (pid ${pid})`); process.exit(0); }
    const who = r.owner ? `${r.owner.night} (pid ${r.owner.pid})` : 'another chain';
    if (who !== said) { console.log(`${new Date().toTimeString().slice(0, 5)} chain lock held by ${who}: waiting`); said = who; }
    await new Promise(res => setTimeout(res, 20000));
  }
}
if (values['bin-state']) { // chain side: print the queue's Bin state (last line); --stage copies binStage first; exit 1 = refused
  const qf = values['bin-state'], wt = values.wt ?? M, b = binStateOf(JSON.parse(readFileSync(qf, 'utf8')));
  const refuse = (why: string) => { console.log(`refused: ${why}`); process.exit(1); };
  if (b.problems.length) refuse(b.problems.join('; '));
  const rb = resolveBin(JSON.parse(readFileSync(qf, 'utf8')), wt);
  if (rb.problem) refuse(rb.problem);
  if (rb.warn) console.log(`WARN ${rb.warn}`);
  // a worktree without the rewind tool gets the main checkout's (same sources): copied in, so queue.ts of any branch finds it
  if (values.stage && rb.state === 'apex-rewind' && rb.exe.from === 'main') {
    const to = join(rewindDir(wt), 'out', 'rewind-spike.exe'), want = fileSha(rb.exe.exe);
    mkdirSync(dirname(to), { recursive: true }); copyFileSync(rb.exe.exe, to);
    if (fileSha(to) !== want) refuse(`rewind tool copy ${to}: read-back differs`);
    utimesSync(to, new Date(), new Date()); // newer than the worktree's sources: queue.ts's stale check
    console.log(`rewind tool from the main checkout (${want.slice(0, 12)}) -> ${to}`);
  }
  if (values.stage) for (const f of b.stage) {
    const from = join(OWN, f.from), to = join(OWN, f.to);
    if (!existsSync(from) || fileSha(from) !== f.sha256) refuse(`binStage from ${f.from} missing or not sha256 ${f.sha256.slice(0, 12)}`);
    mkdirSync(dirname(to), { recursive: true }); copyFileSync(from, to);
    if (fileSha(to) !== f.sha256) refuse(`binStage ${f.to}: read-back differs`);
    console.log(`staged ${f.to} (${f.sha256.slice(0, 12)})`);
  }
  console.log(rb.state); process.exit(0);
}
if (values.watch) await watchCmd(values.watch);
const pf = values.check ?? values.write ?? values.start;
if (!pf) { console.error('usage: night-plan.ts --check|--write|--start <plan.json> | --watch <night> | --self-check'); process.exit(2); }
const plan: Plan = JSON.parse(readFileSync(pf, 'utf8'));
const { problems, notes, runs } = preflight(plan);
for (const n of notes) console.log(`note: ${n}`);
for (const r of runs) console.log(`run: ${r.name} (${r.wt === M ? '.' : basename(r.wt)}) est ${r.est} min, timeout ${queueTimeout(r.est)} min, agent ${basename(r.agent)} [${r.agentWhy}]${r.expectFail.length ? `, expectFail ${r.expectFail.join(',')}` : ''}`);
if (problems.length) { console.log(`REFUSED: ${problems.length} problem(s):`); for (const p of problems) console.log(`  - ${p}`); process.exit(1); }
console.log('preflight ok');
if (values.check) process.exit(0);
const fx = process.platform === 'win32' && process.env.LOCALAPPDATA ? join(process.env.LOCALAPPDATA, 'AlienFX Tools', 'alienfx-cli.exe') : '';
if (plan.rgbOffUntil && !existsSync(fx)) console.log(`note: rgbOffUntil set but ${fx || 'alienfx-cli.exe'} missing: no RGB loop`);
const sh = chainScript(plan, runs, new Date(), existsSync(fx) ? fx : '');
const lint = scriptProblems(sh);
if (lint.length) { console.log(`REFUSED: generated script: ${lint.join('; ')}`); process.exit(1); }
const file = join(QOUT, `chain-${plan.night}.sh`);
writeFileSync(file, sh);
try { execFileSync('bash', ['-n', file], { stdio: ['ignore', 'ignore', 'pipe'], timeout: 30000 }); } catch (e: any) { console.log(`REFUSED: bash -n ${file}: ${e.stderr}`); process.exit(1); }
console.log(`wrote ${file}`);
if (values.start) {
  const log = join(QOUT, `${plan.night}.log`);
  try { const o = JSON.parse(readFileSync(LOCK, 'utf8')); if (pidAlive(o.pid)) console.log(`note: chain ${o.night} (pid ${o.pid}) holds the chain lock: ${plan.night} waits for it to end`); } catch { /* no lock */ }
  // nohup + disown inside a detached bash, stdin /dev/null: the chain outlives this process and the session's shell
  spawn('bash', ['-c', `nohup bash '${posix(file)}' < /dev/null >> '${posix(log)}' 2>&1 & disown`], { detached: true, stdio: 'ignore' }).unref();
  console.log(`started detached; log ${log}\nwatch: node tools/harness/night-plan.ts --watch ${plan.night}   (run_in_background; restart after each wake)`);
}
