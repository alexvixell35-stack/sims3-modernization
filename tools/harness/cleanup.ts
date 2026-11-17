// Mandatory disk cleanup (docs/283). Drive C: hit 0 bytes on 2026-11-17 (~120 bridge worktrees, agent worktrees, build
// intermediates, harness saves, dumps) and broke builds and game runs. Dry run unless --apply; every line also goes to
// probe-out/cleanup/<date>.log (main checkout).
//   node tools/harness/cleanup.ts [all|worktrees|builds|saves] [--apply] [--min-free-gb N] [--days 3] [--branch <b>] [--target <ref>]
//   node tools/harness/cleanup.ts gate [--auto-gb 30] [--min-free-gb 15]   free < auto: all --apply; still < min: exit 3 (REFUSED)
//   node tools/harness/cleanup.ts --self-check
// worktrees: agent worktrees of this repo (.claude/worktrees) and every worktree of the separate repos under game-copy
//   (Apex, GI, DXVK, remix: game-copy/*/.git, game-copy/*/*/.git; each repo's own git). Removed only when ALL hold: branch
//   merged into a target (this repo: master; others: feature/realtime-gi, main, master, origin/*; --target overrides) or its
//   PR is merged/closed (gh, GH_TOKEN=$PERSONAL_GITHUB_TOKEN); `git status` clean; no commit outside the remotes and targets;
//   path or name not in a running process's command line, a running chain script (probe-out/queue/*.sh), a queue json those
//   name, or job-running; not git-locked; no agent reservation (docs/data/registry-reservations.json) younger than 24 h and no
//   commit/checkout in the last 2 h (a fresh worktree looks merged: the 11-17 one-off sweep removed one). --branch <b> names
//   the worktree (agent-merge.cjs finish): skips the 24 h / 2 h guards, and any other blocker is exit 1 with the reason.
//   Removal is tools/worktree-clean.ts removeWorktree: game-copy link unlinked first (never followed), then git worktree remove; prune.
// builds: in every worktree (and main checkout) with no process naming it and no intermediate newer than 30 min, delete only
//   git-IGNORED rebuildable intermediates: dirs obj (beside a .csproj/.vcxproj/.sln), *.tlog, meson *.p (beside build.ninja),
//   files .obj .o .iobj .ipdb .pch .ilk .idb. Outputs (.asi .dll .exe .pdb .package) stay. Never follows links, never enters
//   a nested repo, game-copy or probe-out.
// saves: Saves/ harness temp saves (ApexRunner-*, Matrix-*, QuitCrash-*, QuitStop-*, Repro-<tag>-HHMM*; .sims3 + .backup),
//   probe-out dumps (*.dmp), *-frames dirs, probe-out/rewind-* dirs: older than --days, not referenced (as above); the newest
//   save per family (name without its trailing -N) always stays. Baseline/story saves never match.
// --min-free-gb N (not gate): do nothing while free >= N GB. Env: TS3_CLEANUP_AUTO_GB, TS3_CLEANUP_MIN_GB, TS3_USER.
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { appendFileSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, realpathSync, rmSync, statfsSync, statSync, symlinkSync, utimesSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir, userInfo } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { removeWorktree } from '../worktree-clean.ts';
import { classify, realProcs, type Proc } from './track.ts';

const win = process.platform === 'win32';
const H = 3600e3, GB = 1024 ** 3;
const low = (p: string) => p.replace(/\\/g, '/').toLowerCase();
const gb = (b: number) => `${(b / GB).toFixed(1)} GB`;
const git = (cwd: string, ...a: string[]) => execFileSync('git', a, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], timeout: 300e3 }).trim();
const gitOk = (cwd: string, ...a: string[]) => spawnSync('git', a, { cwd, stdio: 'ignore', timeout: 300e3 }).status === 0;
const mtime = (p: string) => { try { return lstatSync(p).mtimeMs; } catch { return 0; } };
const read = (f: string) => { try { return readFileSync(f, 'utf8'); } catch { return ''; } };
export const freeBytes = (p: string) => { const s = statfsSync(p); return s.bavail * s.bsize; };

// regexes (self-check has real match / non-match cases)
export const TEMP_SAVE = /^(ApexRunner-|Matrix-|QuitCrash-|QuitStop-|Repro-.*-\d{4}(?=[-.]|$))/;
export const GH_REPO = /github\.com[:/]([\w.-]+\/[\w.-]+?)(?:\.git)?\/?$/;
export const JSON_REF = /[\w.$/\\-]+\.json\b/g;
export const INTER_FILE = /\.(obj|o|iobj|ipdb|pch|ilk|idb)$/i;
export const refRe = (name: string) => new RegExp(`(^|[^\\w-])${name.toLowerCase().replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}([^\\w-]|$)`, 'm');

type Ctx = {
  main: string; apply: boolean; days: number; recentH: number; now: number; savesDir: string; log: (m: string) => void;
  procs: Proc[]; gh: (repo: string, branch: string) => string; target?: string;
};
type Wt = { repo: string; path: string; head: string; branch: string; locked: string | null; ours: boolean };

function mainCheckout(): string {
  if (process.env.TS3_REPO) return resolve(process.env.TS3_REPO);
  return dirname(git(import.meta.dirname, 'rev-parse', '--path-format=absolute', '--git-common-dir'));
}
function procsNow(): Proc[] {
  let all: Proc[];
  if (win) all = realProcs();
  else {
    const out = execFileSync('ps', ['-eo', 'pid=,ppid=,comm=,args='], { encoding: 'utf8', timeout: 30e3 });
    all = out.split('\n').map(l => l.trim().match(/^(\d+)\s+(\d+)\s+(\S+)\s+(.*)$/)).filter(Boolean).map(m => ({ pid: +m![1], ppid: +m![2], name: basename(m![3]), cmd: m![4] }));
  }
  return notSelf(all, process.pid);
}
// Our own process and its callers (`agent-merge.cjs finish <branch>`, the shell that ran it) name the worktree in their
// command lines: they are not users of it (every finish was refused as "referenced by a running ... process"). Git Bash
// forks break the parent chain on Windows, so a command line that runs the cleaner itself counts as self too.
const CLEANER = /agent-merge\.cjs["']?\s+finish\s|cleanup\.ts["']?\s+(worktrees|all|builds|saves)\b/;
export function notSelf(all: Proc[], pid: number): Proc[] {
  const by = new Map(all.map(p => [p.pid, p])), self = new Set<number>();
  for (let p = by.get(pid); p && !self.has(p.pid); p = by.get(p.ppid)) self.add(p.pid);
  return all.filter(p => !self.has(p.pid) && !CLEANER.test(p.cmd));
}
function ghState(repo: string, branch: string): string {
  const env = { ...process.env, ...(process.env.PERSONAL_GITHUB_TOKEN ? { GH_TOKEN: process.env.PERSONAL_GITHUB_TOKEN } : {}) };
  const r = spawnSync('gh', ['pr', 'list', '--repo', repo, '--head', branch, '--state', 'all', '--limit', '1', '--json', 'state', '--jq', '.[0].state // ""'], { env, encoding: 'utf8', timeout: 60e3 });
  return r.status === 0 ? r.stdout.trim() : '';
}

// Lowercased, /-separated text of everything a running chain or queue may still use.
export function references(main: string, procs: Proc[]): string {
  const c = classify(procs), qd = join(main, 'probe-out', 'queue'), texts = procs.map(p => p.cmd);
  const scripts = c.chains.map(ch => read(join(qd, ch.script)));
  const jsons = [...scripts, ...c.queues.map(q => q.cmd)].flatMap(t => t.match(JSON_REF) ?? []).map(j => basename(j.replace(/\\/g, '/')));
  for (const j of new Set(jsons)) texts.push(read(join(main, 'tools', 'harness', 'queues', j)), read(join(qd, j)));
  texts.push(...scripts, read(join(qd, 'job-running')));
  return low(texts.join('\n'));
}
const referenced = (refs: string, p: string) => refs.includes(low(p)) || refRe(basename(p)).test(refs);

function size(p: string): number {
  let st; try { st = lstatSync(p); } catch { return 0; }
  if (st.isSymbolicLink() || !st.isDirectory()) return st.isSymbolicLink() ? 0 : st.size;
  let n = 0; for (const e of readdirSync(p)) n += size(join(p, e)); return n;
}

function repos(main: string): string[] {
  const out = [main], gc = join(main, 'game-copy');
  const isRepo = (d: string) => { try { return statSync(join(d, '.git')).isDirectory() && !lstatSync(d).isSymbolicLink(); } catch { return false; } };
  const kids = (d: string) => { try { return readdirSync(d, { withFileTypes: true }).filter(e => e.isDirectory()).map(e => join(d, e.name)); } catch { return []; } };
  for (const a of kids(gc)) { if (isRepo(a)) out.push(a); else for (const b of kids(a)) if (isRepo(b)) out.push(b); }
  return out;
}
function worktrees(repo: string, ours: boolean): Wt[] {
  const root = low(join(repo, '.claude', 'worktrees')) + '/';
  return git(repo, 'worktree', 'list', '--porcelain').split(/\r?\n\r?\n/).slice(1).map(b => ({
    repo, path: resolve((b.match(/^worktree (.+)$/m) || [])[1] ?? ''), head: (b.match(/^HEAD (\w+)/m) || [])[1] ?? '',
    branch: (b.match(/^branch refs\/heads\/(.+)$/m) || [])[1] ?? '', locked: /^locked/m.test(b) ? ((b.match(/^locked ?(.*)$/m) || [])[1] || 'locked') : null, ours,
  })).filter(w => w.head && (!ours || low(w.path).startsWith(root)));
}

// Why a worktree must stay ('' = removable).
function blocker(w: Wt, x: Ctx, refs: string, named: boolean): string {
  if (w.locked) return `git-locked (${w.locked}): git worktree unlock when the agent is done`;
  if (referenced(refs, w.path)) return 'referenced by a running chain / queue / process';
  if (!named) {
    const user = userInfo().username, rs = (() => { try { return JSON.parse(read(join(x.main, 'docs', 'data', 'registry-reservations.json'))).reservations ?? []; } catch { return []; } })();
    const r = rs.find((r: any) => r.worktree && low(String(r.worktree).replace('<user>', user)) === low(w.path) && x.now - Date.parse(r.at) < 24 * H);
    if (r) return `agent reservation ${r.id} by ${r.by} (< 24 h)`;
    const gd = (read(join(w.path, '.git')).match(/^gitdir: (.+)$/m) || [])[1]?.trim();
    if (gd && x.now - Math.max(mtime(join(gd, 'logs', 'HEAD')), mtime(join(gd, 'HEAD'))) < x.recentH * H) return `commit/checkout < ${x.recentH} h ago (fresh worktrees look merged)`;
  }
  if (git(w.path, 'status', '--porcelain')) return 'uncommitted changes';
  const cands = x.target ? [x.target] : w.ours ? ['master'] : ['feature/realtime-gi', 'main', 'master'].flatMap(b => [b, `origin/${b}`]);
  const targets = cands.filter(t => gitOk(w.repo, 'rev-parse', '-q', '--verify', `${t}^{commit}`));
  let done = targets.some(t => gitOk(w.repo, 'merge-base', '--is-ancestor', w.head, t)), how = 'merged';
  if (!done && w.branch) {
    const url = spawnSync('git', ['remote', 'get-url', 'origin'], { cwd: w.repo, encoding: 'utf8', timeout: 300e3 }).stdout ?? '';
    const repo = (url.trim().match(GH_REPO) || [])[1];
    const st = repo ? x.gh(repo, w.branch) : '';
    done = st === 'MERGED' || st === 'CLOSED'; how = `PR ${st.toLowerCase()}`;
  }
  if (!done) return `not merged into ${targets.join(' / ') || '(no target ref)'} and no merged/closed PR`;
  const ahead = +git(w.repo, 'rev-list', '--count', w.head, '--not', '--remotes', ...targets);
  if (ahead) return `${ahead} commit(s) not on any remote or target (${how})`;
  return '';
}

function doWorktrees(x: Ctx, branch?: string): number {
  const refs = references(x.main, x.procs); let freed = 0, bad = 0;
  for (const repo of repos(x.main)) {
    let wts: Wt[]; try { wts = worktrees(repo, repo === x.main); } catch (e) { x.log(`skip repo ${repo}: ${(e as Error).message.split('\n')[0]}`); continue; }
    if (branch) wts = wts.filter(w => w.branch === branch);
    for (const w of wts) {
      if (!existsSync(w.path)) { x.log(`${x.apply ? '' : 'would '}prune missing ${w.path}`); if (x.apply) git(repo, 'worktree', 'prune'); continue; }
      let why: string; try { why = blocker(w, x, refs, !!branch); } catch (e) { why = `check failed: ${(e as Error).message.split('\n')[0]}`; }
      if (why) { x.log(`keep ${w.path} [${w.branch || 'detached'}]: ${why}`); if (branch) bad++; continue; }
      const sz = size(w.path);
      if (!x.apply) { x.log(`would remove ${w.path} [${w.branch || 'detached'}] ${gb(sz)}`); freed += sz; continue; }
      const r = removeWorktree(repo, w, true);
      if (r === 'ok') { freed += sz; x.log(`removed ${w.path} ${gb(sz)}`); } else { x.log(`REFUSED ${w.path} (${r})`); bad++; if (r === 'stop') return 1; }
    }
    if (x.apply) try { git(repo, 'worktree', 'prune'); } catch { /* next run */ }
  }
  x.log(`worktrees: ${x.apply ? 'freed' : 'would free'} ${gb(freed)}${branch ? ` (branch ${branch})` : ''}`);
  return bad ? 1 : 0;
}

// Bytes and newest mtime under p (links not followed).
function walk(p: string): { s: number; m: number } {
  let st; try { st = lstatSync(p); } catch { return { s: 0, m: 0 }; }
  if (st.isSymbolicLink()) return { s: 0, m: 0 };
  if (!st.isDirectory()) return { s: st.size, m: st.mtimeMs };
  const r = { s: 0, m: 0 }; for (const e of readdirSync(p)) { const k = walk(join(p, e)); r.s += k.s; r.m = Math.max(r.m, k.m); } return r;
}
const isProjObj = (p: string) => basename(p).toLowerCase() === 'obj' && readdirSync(dirname(p)).some(f => /\.(csproj|vcxproj|sln)$/i.test(f));
// Rebuildable intermediates under an ignored path, not following links or entering nested repos.
function collect(p: string, out: string[], ninja = false): void {
  let st; try { st = lstatSync(p); } catch { return; }
  if (st.isSymbolicLink()) return;
  if (!st.isDirectory()) { if (INTER_FILE.test(p)) out.push(p); return; }
  const b = basename(p);
  if (isProjObj(p) || /\.tlog$/i.test(b) || (ninja && /\.p$/.test(b))) { out.push(p); return; }
  if (/^(\.git|node_modules)$/.test(b) || existsSync(join(p, '.git'))) return; // nested repo / worktree: its own entry
  const kids = readdirSync(p), nj = kids.includes('build.ninja');
  for (const k of kids) collect(join(p, k), out, nj);
}
function doBuilds(x: Ctx): number {
  const busy = low(x.procs.map(p => p.cmd).join('\n')); let freed = 0;
  for (const repo of repos(x.main)) {
    let paths: string[]; try { paths = [repo, ...worktrees(repo, repo === x.main).map(w => w.path)]; } catch { continue; }
    for (const wt of paths.filter(existsSync)) {
      if (busy.includes(low(wt) + '/') || busy.includes(low(wt) + '"') || busy.endsWith(low(wt))) { x.log(`builds: skip ${wt}: a process names it`); continue; }
      let ign: string[]; try { ign = git(wt, 'ls-files', '-o', '-i', '--exclude-standard', '--directory').split(/\r?\n/).filter(Boolean); } catch { continue; }
      const hits: string[] = [];
      for (const rel of ign) if (!/^(game-copy|probe-out)(\/|$)/i.test(rel) && !/(^|\/)\.claude\/worktrees/.test(rel)) {
        const p = join(wt, rel.replace(/\/$/, '')); collect(p, hits, existsSync(join(dirname(p), 'build.ninja')));
      }
      const w = hits.map(walk), n = w.reduce((a, k) => a + k.s, 0), m = Math.max(0, ...w.map(k => k.m));
      if (!n) continue;
      if (x.now - m < 0.5 * H) { x.log(`builds: skip ${wt}: intermediate written < 30 min ago (build running?)`); continue; }
      if (x.apply) for (const h of hits) rmSync(h, { recursive: true, force: true });
      if (n >= 10 * 1024 ** 2) x.log(`builds: ${x.apply ? 'stripped' : 'would strip'} ${gb(n)} in ${wt}`); // smaller: total only
      freed += n;
    }
  }
  x.log(`builds: ${x.apply ? 'freed' : 'would free'} ${gb(freed)}`);
  return 0;
}

function doSaves(x: Ctx): number {
  const refs = references(x.main, x.procs), old = (p: string) => x.now - mtime(p) > x.days * 24 * H; let freed = 0;
  const drop = (p: string, why: string) => { const s = size(p); freed += s; x.log(`${x.apply ? 'deleted' : 'would delete'} ${p} ${gb(s)} (${why})`); if (x.apply) rmSync(p, { recursive: true, force: true }); };
  const ls = (d: string) => { try { return readdirSync(d); } catch { return []; } };
  // Saves: newest per family stays
  const fam = new Map<string, string[]>();
  for (const f of ls(x.savesDir)) { const m = f.match(/^(.*?)\.sims3(\.backup)?$/); if (m && TEMP_SAVE.test(m[1])) { const k = m[1].replace(/-\d+$/, ''); fam.set(k, [...(fam.get(k) ?? []), f]); } }
  for (const fs of fam.values()) {
    const newestBase = fs.map(f => f.replace(/\.backup$/, '')).sort((a, b) => mtime(join(x.savesDir, b)) - mtime(join(x.savesDir, a)))[0];
    for (const f of fs) { const p = join(x.savesDir, f), base = f.replace(/\.sims3(\.backup)?$/, '');
      if (f.replace(/\.backup$/, '') !== newestBase && old(p) && !referenced(refs, base)) drop(p, `harness save > ${x.days} d`); }
  }
  // probe-out artifacts
  const po = join(x.main, 'probe-out');
  const cand = [...ls(join(po, 'dumps')).filter(f => /\.dmp$/i.test(f)).map(f => join(po, 'dumps', f)),
    ...ls(po).filter(f => /^rewind-/.test(f) || /\.dmp$/i.test(f)).map(f => join(po, f)),
    ...['queue', 'profile'].flatMap(d => ls(join(po, d)).filter(f => /-frames$/.test(f)).map(f => join(po, d, f)))];
  for (const p of cand) if (old(p) && !referenced(refs, p)) drop(p, `artifact > ${x.days} d`);
  x.log(`saves: ${x.apply ? 'freed' : 'would free'} ${gb(freed)}`);
  return 0;
}

function ctxNow(apply: boolean, v: Record<string, any>): Ctx {
  const main = mainCheckout(), day = new Date().toISOString().slice(0, 10), dir = join(main, 'probe-out', 'cleanup');
  mkdirSync(dir, { recursive: true });
  const log = (m: string) => { const l = `${new Date().toISOString().slice(11, 19)} cleanup: ${m}`; console.log(l); try { appendFileSync(join(dir, `${day}.log`), l + '\n'); } catch { /* disk full: console only */ } };
  const user = process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3');
  return { main, apply, days: Number(v.days ?? 3), recentH: 2, now: Date.now(), savesDir: join(user, 'Saves'), log, procs: procsNow(), gh: ghState, target: v.target };
}
function runAll(x: Ctx, what: string, branch?: string): number {
  let rc = 0;
  if (what === 'all' || what === 'worktrees') rc |= doWorktrees(x, branch);
  if (!branch && (what === 'all' || what === 'builds')) rc |= doBuilds(x);
  if (!branch && (what === 'all' || what === 'saves')) rc |= doSaves(x);
  return rc;
}

// Free-space gate for queue.ts / apex-run.ts / night-run.ts: runs this script as a child (own PID, timeout 30 min).
export function diskGate(): { ok: boolean; line: string } {
  const r = spawnSync(process.execPath, [join(import.meta.dirname, 'cleanup.ts'), 'gate'], { encoding: 'utf8', timeout: 1800e3 });
  const out = `${r.stdout ?? ''}${r.stderr ?? ''}`.trim().split(/\r?\n/).filter(l => / gate|REFUSED/.test(l)).pop() ?? `cleanup gate exit ${r.status}`;
  return { ok: r.status === 0, line: out };
}
export function sweepSaves(): string {
  const r = spawnSync(process.execPath, [join(import.meta.dirname, 'cleanup.ts'), 'saves', '--apply'], { encoding: 'utf8', timeout: 600e3 });
  return `${r.stdout ?? ''}`.trim().split(/\r?\n/).pop() ?? `cleanup saves exit ${r.status}`;
}

function selfCheck(): number {
  const d = realpathSync.native(mkdtempSync(join(tmpdir(), 'ts3-cleanup-check-'))), main = join(d, 'repo'), tgt = join(d, 'real-game-copy');
  const g = (cwd: string, ...a: string[]) => git(cwd, '-c', 'user.name=t', '-c', 'user.email=t@example.invalid', '-c', 'core.autocrlf=false', ...a);
  const lines: string[] = [];
  const ctx = (o: Partial<Ctx> = {}): Ctx => ({ main, apply: true, days: 3, recentH: 0, now: Date.now(), savesDir: join(d, 'Saves'), log: m => lines.push(m), procs: [], gh: () => '', ...o });
  const old = (p: string) => { const t = new Date(Date.now() - 10 * 24 * H); utimesSync(p, t, t); };
  try {
    // regexes on real names / lines
    for (const s of ['Repro-stove-wall-player-1107-0930-stove-wall-player-r2-1', 'Repro-jit-verify-1115-2210', 'Matrix-NewDowntownWorld-ow-1', 'QuitCrash-0412', 'QuitStop-sv-2', 'ApexRunner-town'])
      assert.ok(TEMP_SAVE.test(s), `temp save ${s}`);
    for (const s of ['Baseline-BP-Spring', 'Repro-family-v021', 'ApexTest-Town', 'Story-BP-s1', 'Validation-SV-fixes']) assert.ok(!TEMP_SAVE.test(s), `not temp ${s}`);
    assert.equal(('https://github.com/loinyx/Sims3-ApexRadiance.git'.match(GH_REPO) || [])[1], 'loinyx/Sims3-ApexRadiance');
    { // finish's own caller chain is not a user of the worktree; a sibling chain is
      const ps: Proc[] = [{ pid: 1, ppid: 0, name: 'bash.exe', cmd: 'bash' }, { pid: 2, ppid: 1, name: 'node.exe', cmd: 'node tools/agent-merge.cjs finish bugfix/x-1117' },
        { pid: 3, ppid: 2, name: 'node.exe', cmd: 'node cleanup.ts worktrees --branch bugfix/x-1117' }, { pid: 4, ppid: 0, name: 'bash.exe', cmd: 'bash probe-out/queue/chain-x.sh' },
        { pid: 5, ppid: 9, name: 'bash.exe', cmd: 'bash.exe -c "cd /c/r && node tools/agent-merge.cjs finish bugfix/x-1117"' }];
      assert.deepEqual(notSelf(ps, 3).map(p => p.pid), [4], 'own process + ancestors dropped, others kept');
    }
    assert.equal(('git@github.com:rodneypantonial/dxvk-remix.git'.match(GH_REPO) || [])[1], 'rodneypantonial/dxvk-remix');
    assert.equal('https://gitlab.com/a/b.git'.match(GH_REPO), null);
    assert.deepEqual('node tools/harness/queue.ts $QD/gc-idle-gate-correct-1117-master-1117b.json --only bp'.match(JSON_REF), ['$QD/gc-idle-gate-correct-1117-master-1117b.json']);
    assert.ok(refRe('apex-gi').test(low('async-pick off / on (apex-gi = feature/realtime-gi b74d192')));
    assert.ok(refRe('jit-hot-reserve-1117').test('ws=$w/jit-hot-reserve-1117/tools'));
    assert.ok(!refRe('apex-gi').test('apex-gi-lamps worktree') && !refRe('apex-gi').test('my-apex-gi'));
    assert.ok(INTER_FILE.test('d3d9.cpp.obj') && INTER_FILE.test('x.ipdb') && !INTER_FILE.test('ts3native.asi') && !INTER_FILE.test('ApexRadiance.pdb'));

    // repo + agent worktrees
    mkdirSync(join(tgt, 'Game'), { recursive: true }); writeFileSync(join(tgt, 'Game', 'TS3W.exe'), 'keep me');
    mkdirSync(main); g(main, 'init', '-q', '-b', 'master'); writeFileSync(join(main, '.gitignore'), 'game-copy\nprobe-out\nbuild/\n.claude/worktrees/\n');
    mkdirSync(join(main, 'assets')); writeFileSync(join(main, 'assets', 'mesh.obj'), 'tracked model'); g(main, 'add', '-A'); g(main, 'commit', '-qm', 'base'); g(main, 'remote', 'add', 'origin', 'https://github.com/o/r.git');
    const wt = (n: string) => join(main, '.claude', 'worktrees', n);
    for (const n of ['merged', 'unmerged', 'dirty', 'chained', 'reserved', 'lockedwt']) g(main, 'worktree', 'add', '-q', wt(n), '-b', `b/${n}`);
    symlinkSync(tgt, join(wt('merged'), 'game-copy'), 'junction');
    writeFileSync(join(wt('unmerged'), 'u.txt'), 'u'); g(wt('unmerged'), 'add', '-A'); g(wt('unmerged'), 'commit', '-qm', 'not in master');
    writeFileSync(join(wt('dirty'), 'wip.txt'), 'wip');
    g(main, 'worktree', 'lock', '--reason', 'agent', wt('lockedwt'));
    mkdirSync(join(main, 'probe-out', 'queue'), { recursive: true });
    writeFileSync(join(main, 'probe-out', 'queue', 'chain-x.sh'), `W=$M/.claude/worktrees\nnode tools/harness/queue.ts $QD/q-x.json\n`);
    writeFileSync(join(main, 'probe-out', 'queue', 'q-x.json'), JSON.stringify({ jobs: [{ id: 'a', asi: 'chained/out.asi', save: 'Matrix-BP-off-1' }] }));
    mkdirSync(join(main, 'docs', 'data'), { recursive: true });
    writeFileSync(join(main, 'docs', 'data', 'registry-reservations.json'), JSON.stringify({ reservations: [{ id: 'doc-1', kind: 'doc', by: 'b/reserved', worktree: wt('reserved'), at: new Date().toISOString() }] }));
    const chainProc: Proc[] = [{ pid: 999001, ppid: 1, name: 'bash.exe', cmd: 'bash probe-out/queue/chain-x.sh' }];
    // a separate repo under game-copy (Apex-like) with a merged worktree
    const apex = join(main, 'game-copy', 'rb', 'apex-repo'); mkdirSync(apex, { recursive: true });
    g(apex, 'init', '-q', '-b', 'feature/realtime-gi'); writeFileSync(join(apex, 'a.cpp'), 'x'); g(apex, 'add', '-A'); g(apex, 'commit', '-qm', 'a');
    g(apex, 'worktree', 'add', '-q', join(main, 'game-copy', 'rb', 'apex-done'), '-b', 'fix/done');

    // dry run changes nothing
    assert.equal(runAll(ctx({ apply: false, procs: chainProc }), 'worktrees'), 0);
    assert.ok(existsSync(wt('merged')) && lines.some(l => /would remove .*merged/.test(l)), 'dry run: plan only');
    lines.length = 0;
    // recency guard (default 2 h): every fixture is fresh
    runAll(ctx({ recentH: 2, procs: chainProc }), 'worktrees');
    assert.ok(existsSync(wt('merged')) && lines.some(l => /keep .*merged.*fresh worktrees look merged/.test(l)), 'fresh worktree kept');
    lines.length = 0;
    assert.equal(runAll(ctx({ procs: chainProc }), 'worktrees'), 0);
    assert.ok(!existsSync(wt('merged')), 'merged clean worktree removed');
    assert.ok(existsSync(join(tgt, 'Game', 'TS3W.exe')), 'junction target intact');
    assert.ok(!existsSync(join(main, 'game-copy', 'rb', 'apex-done')), 'separate-repo merged worktree removed with its own git');
    for (const [n, re] of [['unmerged', /not merged/], ['dirty', /uncommitted/], ['chained', /referenced by a running chain/], ['reserved', /agent reservation doc-1/], ['lockedwt', /git-locked/]] as const)
      assert.ok(existsSync(wt(n)) && lines.some(l => l.includes(wt(n)) && re.test(l)), `kept ${n}`);
    // chain gone: chained worktree goes
    lines.length = 0; runAll(ctx(), 'worktrees'); assert.ok(!existsSync(wt('chained')), 'unreferenced once the chain ended');
    // PR closed per gh: unmerged commit pushed? no remote -> still blocked by unpushed commits
    lines.length = 0; runAll(ctx({ gh: () => 'MERGED' }), 'worktrees');
    assert.ok(existsSync(wt('unmerged')) && lines.some(l => /unmerged.*1 commit\(s\) not on any remote/.test(l)), 'PR merged but commit only local: kept');
    // --branch strict: blocker = exit 1 with reason; named skips the reservation guard
    lines.length = 0; assert.equal(runAll(ctx(), 'worktrees', 'b/unmerged'), 1);
    assert.equal(runAll(ctx(), 'worktrees', 'b/reserved'), 0); assert.ok(!existsSync(wt('reserved')), '--branch ignores the reservation guard');
    assert.equal(runAll(ctx(), 'worktrees', 'b/nothere'), 0, 'no worktree for the branch: nothing to do');

    // builds: only ignored intermediates; outputs, tracked files and junction targets stay
    const b = join(main, 'build'); mkdirSync(join(b, 'Release'), { recursive: true }); mkdirSync(join(b, 'proj', 'obj'), { recursive: true });
    writeFileSync(join(b, 'proj', 'p.csproj'), ''); writeFileSync(join(b, 'proj', 'obj', 'x.dll'), 'i'.repeat(100));
    writeFileSync(join(b, 'Release', 'ts3native.asi'), 'out'); writeFileSync(join(b, 'Release', 'a.obj'), 'o'); writeFileSync(join(b, 'Release', 'a.pch'), 'p');
    writeFileSync(join(b, 'build.ninja'), ''); mkdirSync(join(b, 'd3d9.dll.p')); writeFileSync(join(b, 'd3d9.dll.p', 'x.o'), 'o'); writeFileSync(join(b, 'd3d9.dll'), 'out');
    writeFileSync(join(tgt, 'keep.obj'), 'target'); symlinkSync(tgt, join(b, 'linked'), 'junction');
    for (const f of [join(b, 'Release', 'a.obj'), join(b, 'Release', 'a.pch'), join(b, 'd3d9.dll.p', 'x.o'), join(b, 'proj', 'obj', 'x.dll')]) old(f);
    lines.length = 0; doBuilds(ctx());
    assert.ok(!existsSync(join(b, 'Release', 'a.obj')) && !existsSync(join(b, 'Release', 'a.pch')) && !existsSync(join(b, 'd3d9.dll.p')) && !existsSync(join(b, 'proj', 'obj')), 'intermediates gone');
    assert.ok(existsSync(join(b, 'Release', 'ts3native.asi')) && existsSync(join(b, 'd3d9.dll')) && existsSync(join(main, 'assets', 'mesh.obj')), 'outputs + tracked .obj kept');
    assert.ok(existsSync(join(tgt, 'keep.obj')), 'junction never followed');
    writeFileSync(join(b, 'Release', 'b.obj'), 'fresh'); lines.length = 0; doBuilds(ctx());
    assert.ok(existsSync(join(b, 'Release', 'b.obj')) && lines.some(l => /< 30 min/.test(l)), 'fresh intermediate = build running: skipped');

    // saves + artifacts
    const S = join(d, 'Saves'); mkdirSync(S);
    for (const n of ['Repro-x-1200-1.sims3', 'Repro-x-1200-2.sims3', 'Baseline-BP.sims3', 'Matrix-BP-off-1.sims3', 'Matrix-BP-off-1.sims3.backup', 'QuitCrash-0412.sims3']) { mkdirSync(join(S, n)); old(join(S, n)); }
    const t2 = new Date(Date.now() - 5 * 24 * H); utimesSync(join(S, 'Repro-x-1200-2.sims3'), t2, t2);
    mkdirSync(join(main, 'probe-out', 'dumps')); writeFileSync(join(main, 'probe-out', 'dumps', 'old.dmp'), 'd'); old(join(main, 'probe-out', 'dumps', 'old.dmp'));
    writeFileSync(join(main, 'probe-out', 'dumps', 'new.dmp'), 'd');
    lines.length = 0; doSaves(ctx({ procs: chainProc }));
    assert.ok(!existsSync(join(S, 'Repro-x-1200-1.sims3')) && existsSync(join(S, 'Repro-x-1200-2.sims3')), 'older family member deleted, newest kept');
    assert.ok(existsSync(join(S, 'Baseline-BP.sims3')), 'baseline never touched');
    assert.ok(existsSync(join(S, 'Matrix-BP-off-1.sims3')), 'save named by a running queue kept');
    assert.ok(existsSync(join(S, 'QuitCrash-0412.sims3')), 'sole family member kept');
    assert.ok(!existsSync(join(main, 'probe-out', 'dumps', 'old.dmp')) && existsSync(join(main, 'probe-out', 'dumps', 'new.dmp')), 'old dump deleted, new kept');
    console.log('ok   cleanup self-check');
    return 0;
  } catch (e) { console.log('FAIL', (e as Error).message); console.log(lines.join('\n')); return 1; }
  finally {
    for (const p of [join(main, 'build', 'linked')]) try { rmSync(p); } catch { /* gone */ }
    // unlink every junction left in fixtures before the recursive delete (never through a link)
    const unlinkAll = (dir: string) => { let es; try { es = readdirSync(dir, { withFileTypes: true }); } catch { return; } for (const e of es) { const p = join(dir, e.name); if (e.isSymbolicLink() || lstatSync(p).isSymbolicLink()) { try { rmSync(p); } catch { try { execFileSync('cmd', ['/c', 'rmdir', p]); } catch { /* */ } } } else if (e.isDirectory()) unlinkAll(p); } };
    unlinkAll(d); rmSync(d, { recursive: true, force: true });
  }
}

if (import.meta.main) {
  const { values: v, positionals } = parseArgs({ allowPositionals: true, options: {
    apply: { type: 'boolean' }, 'dry-run': { type: 'boolean' }, 'self-check': { type: 'boolean' }, days: { type: 'string' }, branch: { type: 'string' },
    target: { type: 'string' }, 'min-free-gb': { type: 'string' }, 'auto-gb': { type: 'string' } } });
  if (v['self-check']) process.exit(selfCheck());
  const what = positionals[0] ?? 'all';
  if (!['all', 'worktrees', 'builds', 'saves', 'gate'].includes(what)) { console.error('usage: node tools/harness/cleanup.ts [all|worktrees|builds|saves|gate] [--apply] [--min-free-gb N] [--days N] [--branch b] [--target ref] | --self-check'); process.exit(2); }
  if (what === 'gate') {
    const auto = Number(v['auto-gb'] ?? process.env.TS3_CLEANUP_AUTO_GB ?? 30), min = Number(v['min-free-gb'] ?? process.env.TS3_CLEANUP_MIN_GB ?? 15);
    const x = ctxNow(true, v); let free = freeBytes(x.main);
    if (free < auto * GB) { x.log(`gate: free ${gb(free)} < ${auto} GB: cleanup --apply`); runAll(x, 'all'); free = freeBytes(x.main); }
    if (free < min * GB) { x.log(`REFUSED: free ${gb(free)} < ${min} GB after cleanup: free space by hand (node tools/harness/cleanup.ts shows what is left), then start again`); process.exit(3); }
    x.log(`gate ok: free ${gb(free)} (auto cleanup < ${auto} GB, refuse < ${min} GB)`); process.exit(0);
  }
  const x = ctxNow(!!v.apply && !v['dry-run'], v), free = freeBytes(x.main);
  if (v['min-free-gb'] && free >= Number(v['min-free-gb']) * GB) { x.log(`free ${gb(free)} >= ${v['min-free-gb']} GB: nothing to do`); process.exit(0); }
  x.log(`${x.apply ? 'APPLY' : 'dry run'} ${what}${v.branch ? ` --branch ${v.branch}` : ''}: free ${gb(free)}`);
  const rc = runAll(x, what, v.branch);
  if (x.apply) x.log(`free now ${gb(freeBytes(x.main))}`);
  process.exit(rc);
}
