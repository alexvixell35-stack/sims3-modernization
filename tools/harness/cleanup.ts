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
//   probe-out of the worktree has no raw/result file (-hitches.jsonl ..., *.jsonl) missing from docs/PLAN.md tokens (not dropped);
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
//   probe-out dumps (dumps rule below), *-frames dirs, probe-out/rewind-* dirs: older than --days, not referenced (as above); the newest
//   save per family (name without its trailing -N) always stays. Baseline/story saves never match.
// dumps: any .dmp under probe-out/dumps (any depth) and CrashDumps TS3W*/NvRemixBridge*: deleted once <name>-stacks.txt holds cdb stacks (marker line; extracted by
//   dump-stacks.ts first); no stacks and extraction fails: kept --days, then deleted. Kept: `keep` file in the folder, name in docs/ or the ledger.
//   Every delete logs its path relative to its dump dir + size; dry run unless --apply. Never through a junction/symlink, never outside these dirs.
// leftovers (docs/283 §6; in `all` and the queue.ts end sweep) = temp + probe (gamecopy only by name, Rodney 2026-11-23). Locked/AV-held files: skipped + counted.
//   temp: %TEMP% (TS3_TEMP) top-level entries whose prefix is in TEMP_CLEAN (frida-, ts3-publish-; creators scanned from
//     tools/ each run, creator listed) or TEMP_EXTRA (Frida), newest file > --temp-days (2), no link inside, not named by a process.
//   probe: RAW_OUT files in probe-out/{profile,queue} > --probe-days (14), not of a running queue, name / job stem not a token in
//     docs/ (any depth), PLAN.md, README.md. Results (<queue>-<date>.jsonl), logs, json, reports, gifs, scripts never match.
//   gamecopy: cc-synth/<set> unused > --gc-days (14) and not named by a queue json/chain written < --gc-days; compat-mods/<mod>
//     once every compat-matrix.csv row for it is 'no overlap' or 'overlap' with a status.
// lane --branch <b> (agent-merge finish): game-copy/<builds.json name> copies of that lane. dropped --branch <b>: + its worktree
//   even unmerged (clean, branch ref must exist and is kept) + its raw probe-out outputs at any age.
// --min-free-gb N (not gate): do nothing while free >= N GB. Env: TS3_CLEANUP_AUTO_GB, TS3_CLEANUP_MIN_GB, TS3_USER.
import assert from 'node:assert/strict';
import { execFileSync, spawnSync } from 'node:child_process';
import { appendFileSync, existsSync, lstatSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, realpathSync, rmSync, statfsSync, statSync, symlinkSync, utimesSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir, userInfo } from 'node:os';
import { basename, dirname, join, relative, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { CDB_MARK, extractStacks, hasStacks } from './dump-stacks.ts';
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
  tempDays: number; probeDays: number; gcDays: number; tempDir?: string; rm?: (p: string) => void;
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

// A worktree's own (git-ignored) probe-out holds queue results and raw dumps that die with it (11-26: nonsim-split-1122 lost its
// ME-1 band dumps). Digested = file name or job stem is a docs/PLAN token (same rule as doProbe). Returns first undigested file or ''.
export function undigested(wtPath: string, main: string): string {
  let docs: Set<string> | null = null;
  for (const d of ['queue', 'profile']) {
    let es: string[] = []; try { es = readdirSync(join(wtPath, 'probe-out', d)); } catch { continue; }
    for (const f of es) {
      if (!RAW_OUT.test(f) && !/.jsonl$/i.test(f)) continue;
      docs ??= docTokens(main); const lf = f.toLowerCase();
      if (!docs.has(lf) && !docs.has(rawStem(f)) && !docs.has(lf.replace(/.jsonl$/, ''))) return `${d}/${f}`;
    }
  }
  return '';
}

// Why a worktree must stay ('' = removable).
function blocker(w: Wt, x: Ctx, refs: string, named: boolean, dropped = false): string {
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
  if (!dropped) { const u = undigested(w.path, x.main); if (u) return `probe-out not digested: ${u} (name it in docs/ or PLAN.md, or use dropped)`; }
  // dropped design: unmerged is fine, the branch ref keeps every commit (never deleted here)
  if (dropped) return w.branch && gitOk(w.repo, 'rev-parse', '-q', '--verify', `refs/heads/${w.branch}`) ? '' : 'detached HEAD: no branch keeps its commits';
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

function doWorktrees(x: Ctx, branch?: string, dropped = false): number {
  const refs = references(x.main, x.procs); let freed = 0, bad = 0;
  for (const repo of repos(x.main)) {
    let wts: Wt[]; try { wts = worktrees(repo, repo === x.main); } catch (e) { x.log(`skip repo ${repo}: ${(e as Error).message.split('\n')[0]}`); continue; }
    if (branch) wts = wts.filter(w => w.branch === branch);
    for (const w of wts) {
      if (!existsSync(w.path)) { x.log(`${x.apply ? '' : 'would '}prune missing ${w.path}`); if (x.apply) git(repo, 'worktree', 'prune'); continue; }
      let why: string; try { why = blocker(w, x, refs, !!branch, dropped); } catch (e) { why = `check failed: ${(e as Error).message.split('\n')[0]}`; }
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

// Dump rule (docs/283): a .dmp under probe-out/dumps (any depth) or %LOCALAPPDATA%\CrashDumps (TS3W*/NvRemixBridge*) goes as soon as
// <name>-stacks.txt is non-empty (all .txt stay). No stacks: extract first (dump-stacks.ts); still none: kept < --days, then deleted with a log line.
// Kept: a `keep` file in its folder, the dump's name in docs/ or the data ledger, a running process/chain naming it, written < 10 min ago.
function dumpFiles(dir: string, out: string[] = []): string[] {
  let es; try { es = readdirSync(dir, { withFileTypes: true }); } catch { return out; }
  for (const e of es) { const f = join(dir, e.name); if (e.isSymbolicLink()) continue; if (e.isDirectory()) dumpFiles(f, out); else if (/\.dmp$/i.test(e.name)) out.push(f); }
  return out;
}
function docsText(main: string): string {
  let t = ''; const add = (d: string) => { try { for (const f of readdirSync(d)) if (/\.(md|json|jsonl)$/.test(f)) t += readFileSync(join(d, f), 'utf8').toLowerCase(); } catch { /* none */ } };
  add(join(main, 'docs')); add(join(main, 'docs', 'data')); add(join(main, 'docs', 'data', 'change-ledger')); return t;
}
// Worktree guard: a dump is touched only under one of the dump roots, with no junction/symlink from the root down to it
// (game-copy is a junction in worktrees; a link could point the delete at a real game or another checkout). Returns the root.
export function dumpRoot(p: string, roots: string[]): string | null {
  const f = resolve(p), r = roots.map(x => resolve(x)).find(x => low(f).startsWith(`${low(x).replace(/\/$/, '')}/`));
  if (!r) return null;
  for (let q = f; ; q = dirname(q)) { try { if (lstatSync(q).isSymbolicLink()) return null; } catch { return null; } if (low(q) === low(r) || dirname(q) === q) break; }
  return r;
}
export function doDumps(x: Ctx, refs: string, extra: string[] = []): number {
  const pr = join(x.main, 'probe-out'), po = join(pr, 'dumps'), cd = process.env.TS3_CRASHDUMPS ?? join(process.env.LOCALAPPDATA ?? join(homedir(), 'AppData', 'Local'), 'CrashDumps');
  const roots = [po, pr, cd], label = (r: string) => (r === po ? 'dumps' : r === pr ? 'probe-out' : 'CrashDumps');
  const dmps = [...dumpFiles(po), ...dumpFiles(cd).filter(f => /^(TS3W|NvRemixBridge)/i.test(basename(f))), ...extra]; let freed = 0, docs: string | null = null;
  for (const p of dmps) {
    const r = dumpRoot(p, roots);
    if (!r || !/\.dmp$/i.test(p)) { x.log(`skip ${p}: not a .dmp under probe-out/dumps, probe-out or CrashDumps, or behind a junction/symlink`); continue; }
    const s = size(p), name = basename(p).toLowerCase(), rel = `${label(r)}/${relative(r, p).replace(/\\/g, '/')}`;
    if (x.now - mtime(p) < 10 * 60e3 || referenced(refs, p)) continue;
    if (existsSync(join(dirname(p), 'keep'))) { x.log(`keep ${rel}: keep marker`); continue; }
    docs ??= docsText(x.main); if (docs.includes(name)) { x.log(`keep ${rel}: named in docs/ or the ledger`); continue; }
    let why = '';
    if (hasStacks(p)) why = 'stacks file exists';
    else if (x.apply && extractStacks(p)) why = 'stacks extracted';
    else if (!x.apply) { x.log(`would extract stacks of ${rel} ${(s / 2 ** 20).toFixed(1)} MB, then delete it`); continue; }
    else if (x.now - mtime(p) > x.days * 24 * H) why = `no stacks and extraction failed, > ${x.days} d`;
    else { x.log(`keep ${rel}: no stacks file yet, < ${x.days} d`); continue; }
    freed += s; x.log(`${x.apply ? 'deleted' : 'would delete'} ${rel} ${(s / 2 ** 20).toFixed(1)} MB (${why})`); if (x.apply) rmSync(p, { force: true });
  }
  return freed;
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
  freed += doDumps(x, refs, ls(po).filter(f => /\.dmp$/i.test(f)).map(f => join(po, f)));
  const cand = [...ls(po).filter(f => /^rewind-/.test(f)).map(f => join(po, f)),
    ...['queue', 'profile'].flatMap(d => ls(join(po, d)).filter(f => /-frames$/.test(f)).map(f => join(po, d, f)))];
  for (const p of cand) if (old(p) && !referenced(refs, p)) drop(p, `artifact > ${x.days} d`);
  x.log(`saves: ${x.apply ? 'freed' : 'would free'} ${gb(freed)}`);
  return 0;
}

// ---- leftovers: %TEMP%, probe-out raw outputs, game-copy experiment copies (docs/283 §6) ----
// Bytes, newest mtime and link count under p; unreadable parts count as 0 (Kaspersky / locked files).
function scan(p: string): { s: number; m: number; links: number } {
  let st; try { st = lstatSync(p); } catch { return { s: 0, m: 0, links: 0 }; }
  if (st.isSymbolicLink()) return { s: 0, m: 0, links: 1 };
  if (!st.isDirectory()) return { s: st.size, m: st.mtimeMs, links: 0 };
  const r = { s: 0, m: st.mtimeMs, links: 0 }; let es: string[] = []; try { es = readdirSync(p); } catch { /* locked */ }
  for (const e of es) { const k = scan(join(p, e)); r.s += k.s; r.m = Math.max(r.m, k.m); r.links += k.links; }
  return r;
}
// Delete; a locked or AV-held file (EPERM/EBUSY/EACCES) is skipped and counted, never fatal. Returns false when anything stayed.
type Rm = (p: string) => void;
const rmTree: Rm = p => rmSync(p, { recursive: true, force: true, maxRetries: 0 });
function tryRm(x: Ctx, p: string, held: { n: number }): boolean {
  try { (x.rm ?? rmTree)(p); return true; } catch (e) { if (/^(EPERM|EBUSY|EACCES|ENOTEMPTY)$/.test((e as any).code)) { held.n++; return false; } throw e; }
}

// %TEMP% prefixes our scripts create, read from the source (mkdtemp under tmpdir(), python tempfile prefix=, ps1 $env:TEMP).
export const TEMP_SRC = /(?:tmpdir\(\)|\$env:TEMP|process\.env\.TEMP\b[^,]{0,40}|gettempdir\(\))\s*,?\s*['"`]([A-Za-z][\w.]*-(?:[A-Za-z][\w.]*-)*)(?=['"`$])|mkdtemp\(\s*prefix\s*=\s*['"]([A-Za-z][\w.]*-(?:[A-Za-z][\w.]*-)*)(?=['"])/g;
// Made by tools we run, not by a literal in our source (creator in the value).
export const TEMP_EXTRA: Record<string, string> = {
  'frida-': 'Frida agent/helper dirs (tools/probe/*.py attach to TS3W; left when the game or probe dies)',
};
// Prefixes `temp` deletes (Rodney 2026-11-23: only these two; the source scan still names every creator for the log/docs)
export const TEMP_CLEAN = ['frida-', 'ts3-publish-'];
export function tempPrefixes(main: string): Map<string, string[]> {
  const out = new Map<string, string[]>(Object.entries(TEMP_EXTRA).map(([k, v]) => [k, [v]]));
  const walkSrc = (d: string) => { let es; try { es = readdirSync(d, { withFileTypes: true }); } catch { return; }
    for (const e of es) { const p = join(d, e.name);
      if (e.isDirectory() && !e.isSymbolicLink() && !/^(node_modules|\.git|bin|obj)$/.test(e.name)) walkSrc(p);
      else if (e.isFile() && /\.(ts|cjs|mjs|js|py|ps1)$/.test(e.name))
        for (const m of read(p).matchAll(TEMP_SRC)) { const k = (m[1] ?? m[2]).toLowerCase(); if (k.length < 4) continue; // 'x-' etc.: too generic
          const r = relative(main, p).replace(/\\/g, '/'); out.set(k, [...new Set([...(out.get(k) ?? []), r])]); } } };
  walkSrc(join(main, 'tools'));
  return out;
}
// Longest prefix that owns a %TEMP% entry name ('' = not ours). The rest must look like a mkdtemp/pid suffix.
export function tempOwner(name: string, prefixes: Iterable<string>): string {
  const n = name.toLowerCase(); let best = '';
  for (const k of prefixes) if (n.startsWith(k) && k.length > best.length && /^[\w.-]+$/.test(n.slice(k.length))) best = k;
  return best;
}
function doTemp(x: Ctx, refs: string): number {
  const dir = x.tempDir ?? process.env.TS3_TEMP ?? tmpdir(), all = tempPrefixes(x.main), pre = new Map([...all].filter(([k]) => TEMP_CLEAN.includes(k))), held = { n: 0 };
  const by = new Map<string, { n: number; s: number; kept: number; why: Set<string> }>(); let freed = 0;
  let es: string[] = []; try { es = readdirSync(dir); } catch { x.log(`temp: cannot list ${dir}`); return 0; }
  for (const e of es) {
    const k = tempOwner(e, pre.keys()); if (!k) continue;
    const p = join(dir, e), r = scan(p), row = by.get(k) ?? { n: 0, s: 0, kept: 0, why: new Set() }; by.set(k, row);
    const why = x.now - r.m < x.tempDays * 24 * H ? `newer than ${x.tempDays} d` : r.links ? 'contains a junction/symlink' : referenced(refs, p) ? 'named by a running process / chain' : '';
    if (why) { row.kept++; row.why.add(why); continue; }
    if (x.apply && !tryRm(x, p, held)) { row.kept++; row.why.add('locked (EPERM/EBUSY, AV?)'); continue; }
    row.n++; row.s += r.s; freed += r.s;
  }
  for (const [k, r] of [...by].sort((a, b) => b[1].s - a[1].s))
    x.log(`temp: ${x.apply ? 'deleted' : 'would delete'} ${r.n} x ${k}* ${gb(r.s)}${r.kept ? `, kept ${r.kept} (${[...r.why].join('; ')})` : ''} [${(pre.get(k) ?? []).join(', ')}]`);
  x.log(`temp: ${x.apply ? 'freed' : 'would free'} ${gb(freed)} in ${dir}${held.n ? `; ${held.n} locked entries skipped` : ''}`);
  return freed;
}

// Raw per-job data in probe-out/{profile,queue}; results (<queue>-<date>.jsonl), logs, reports, json, gifs, chain scripts stay.
export const RAW_OUT = /-(frames\.csv|hitches\.jsonl|seconds\.csv|freezes\.jsonl|simstack\.jsonl|activity\.jsonl|moved\.jsonl|shadow\.jsonl|autshadow\.jsonl|machine\.jsonl|samplers\.jsonl|probe\.log)$/i;
// Tokens of docs/, docs/data (any depth, results and crashes included), PLAN.md, README.md: a raw file whose name or job stem
// (name without the raw suffix) is a token is linked and stays. Tokens, not substring search: change-ledger/*.jsonl is ~50 MB a month.
export function docTokens(main: string): Set<string> {
  const t = new Set<string>();
  const add = (f: string) => { for (const m of read(f).toLowerCase().matchAll(/[a-z0-9][\w.-]{7,}/g)) t.add(m[0].replace(/[.-]+$/, '')); };
  const walkD = (d: string) => { let es; try { es = readdirSync(d, { withFileTypes: true }); } catch { return; }
    for (const e of es) { const p = join(d, e.name); if (e.isDirectory() && !e.isSymbolicLink()) walkD(p); else if (/\.(md|json|jsonl|csv|txt)$/i.test(e.name)) add(p); } };
  walkD(join(main, 'docs')); add(join(main, 'PLAN.md')); add(join(main, 'README.md'));
  return t;
}
export const rawStem = (f: string) => f.replace(RAW_OUT, '').toLowerCase();
// Queue names a running chain/queue/job-running uses: their outputs stay whatever their age.
const liveQueues = (refs: string) => [...new Set((refs.match(JSON_REF) ?? []).map(j => basename(j.replace(/\\/g, '/')).replace(/\.json$/i, '')))].filter(q => q.length >= 3);
function doProbe(x: Ctx, refs: string, lane?: RegExp): number {
  const docs = docTokens(x.main), live = liveQueues(refs), held = { n: 0 }; let freed = 0, n = 0, linked = 0, young = 0, running = 0;
  for (const d of ['profile', 'queue']) {
    const dir = join(x.main, 'probe-out', d); let es: string[] = []; try { es = readdirSync(dir); } catch { continue; }
    for (const f of es) {
      if (!RAW_OUT.test(f) || (lane && !lane.test(f.toLowerCase()))) continue;
      const p = join(dir, f), lf = f.toLowerCase();
      if (live.some(q => lf.startsWith(q.toLowerCase()))) { running++; continue; }
      if (!lane && x.now - mtime(p) < x.probeDays * 24 * H) { young++; continue; }
      if (docs.has(lf) || docs.has(rawStem(f))) { linked++; continue; }
      let st; try { st = lstatSync(p); } catch { continue; } if (!st.isFile()) continue;
      if (x.apply && !tryRm(x, p, held)) continue;
      n++; freed += st.size;
    }
  }
  x.log(`probe-out: ${x.apply ? 'deleted' : 'would delete'} ${n} raw files ${gb(freed)}${lane ? ` (lane ${lane.source})` : ` (> ${x.probeDays} d)`}; kept ${linked} linked from docs, ${young} younger, ${running} of a running queue${held.n ? `, ${held.n} locked` : ''}`);
  return freed;
}

// Queue jsons written in the last --gc-days (tools/harness/queues, probe-out/queue): a game-copy copy they name may still run.
function pendingRefs(x: Ctx): string {
  const t: string[] = [];
  for (const d of [join(x.main, 'tools', 'harness', 'queues'), join(x.main, 'probe-out', 'queue')]) {
    let es: string[] = []; try { es = readdirSync(d); } catch { continue; }
    for (const f of es) if (/\.(json|sh)$/.test(f) && x.now - mtime(join(d, f)) < x.gcDays * 24 * H) t.push(read(join(d, f)));
  }
  return low(t.join('\n'));
}
// compat-mods/<dir>: removable when every compat-matrix.csv row for it has a recorded verdict: 'no overlap', or 'overlap' with an in-game status.
export function compatDone(csv: string): Map<string, boolean> {
  const out = new Map<string, boolean>();
  for (const line of csv.split(/\r?\n/).slice(1)) {
    const c = [...`${line},`.matchAll(/(?:"((?:[^"]|"")*)"|([^,"]*)),/g)].map(m => (m[1] ?? m[2] ?? '').replace(/""/g, '"'));
    const dir = (c[1]?.match(/^compat-mods\/([^/]+)\//) || [])[1]; if (!dir) continue;
    const done = c[4] === 'no overlap' || (c[4] === 'overlap' && !!c[6]?.trim());
    out.set(dir, (out.get(dir) ?? true) && done);
  }
  return out;
}
function doGameCopy(x: Ctx, refs: string): number {
  const gc = join(x.main, 'game-copy'), pend = pendingRefs(x), held = { n: 0 }; let freed = 0;
  const ls = (d: string) => { try { return readdirSync(d, { withFileTypes: true }).filter(e => e.isDirectory() && !e.isSymbolicLink()).map(e => e.name); } catch { return []; } };
  const drop = (p: string, why: string) => { const r = scan(p); if (r.links) { x.log(`keep ${p}: contains a junction/symlink`); return; }
    if (x.apply && !tryRm(x, p, held)) { x.log(`keep ${p}: locked`); return; } freed += r.s; x.log(`${x.apply ? 'deleted' : 'would delete'} ${p} ${gb(r.s)} (${why})`); };
  const busy = (p: string) => referenced(refs, p) || refRe(basename(p)).test(pend);
  // cc-synth sets: generated by cc-gen*.ts (rerun with the same --seed to rebuild)
  for (const s of ls(join(gc, 'cc-synth'))) { const p = join(gc, 'cc-synth', s);
    if (busy(p)) { x.log(`keep ${p}: named by a running or recent (< ${x.gcDays} d) queue`); continue; }
    if (x.now - scan(p).m < x.gcDays * 24 * H) continue;
    drop(p, `generated CC set, unused > ${x.gcDays} d`); }
  // compat-mods: MTS downloads whose matrix verdict is recorded
  const done = compatDone(read(join(x.main, 'docs', 'data', 'compat-matrix.csv')));
  let noVerdict = 0;
  for (const m of ls(join(gc, 'compat-mods'))) { const p = join(gc, 'compat-mods', m);
    if (done.get(m) !== true) { noVerdict++; continue; }
    if (busy(p)) { x.log(`keep ${p}: named by a running or recent queue`); continue; }
    drop(p, 'compat verdict recorded in docs/data/compat-matrix.csv'); }
  x.log(`game-copy: ${x.apply ? 'freed' : 'would free'} ${gb(freed)}; compat-mods kept ${noVerdict} without a recorded verdict${held.n ? `; ${held.n} locked` : ''}`);
  return freed;
}

// Lane outputs (agent-merge finish after merge: builds; `dropped`: + unmerged worktree + raw probe-out outputs, any age).
export const laneOf = (branch: string) => branch.replace(/^.*\//, '').replace(/^\d+-/, '').toLowerCase();
export const laneBuild = (lane: string) => new RegExp(`\\.${lane.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?:[a-z]|-[\\w-]+)?$`, 'i');
// lane tag as a whole word in a probe-out file name ('-' counts as a separator there)
export const laneFile = (lane: string) => new RegExp(`(^|[^a-z0-9])${lane.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}([^a-z0-9]|$)`, 'i');
function doLane(x: Ctx, refs: string, branch: string, dropped: boolean): number {
  const lane = laneOf(branch), re = laneBuild(lane), pend = pendingRefs(x), held = { n: 0 }; let freed = 0, rc = 0;
  let names: string[] = []; try { names = JSON.parse(read(join(x.main, 'docs', 'data', 'builds.json'))).builds.map((b: any) => String(b.name)); } catch { /* none */ }
  for (const nm of names.filter(n => re.test(n))) {
    const p = join(x.main, 'game-copy', nm); let st; try { st = lstatSync(p); } catch { continue; }
    if (st.isSymbolicLink()) { x.log(`keep ${p}: a link`); continue; }
    if (referenced(refs, p) || refRe(nm).test(pend)) { x.log(`keep ${p}: named by a running or recent (< ${x.gcDays} d) queue`); continue; }
    const s = scan(p).s; if (x.apply && !tryRm(x, p, held)) { x.log(`keep ${p}: locked`); continue; }
    freed += s; x.log(`${x.apply ? 'deleted' : 'would delete'} ${p} ${gb(s)} (lane build ${lane}; hash stays in builds.json)`);
  }
  if (dropped) { rc |= doWorktrees(x, branch, true); freed += doProbe(x, refs, laneFile(lane)); }
  x.log(`lane ${lane}: ${x.apply ? 'freed' : 'would free'} ${gb(freed)}${dropped ? ' (dropped)' : ''}`);
  return rc;
}

function ctxNow(apply: boolean, v: Record<string, any>): Ctx {
  const main = mainCheckout(), day = new Date().toISOString().slice(0, 10), dir = join(main, 'probe-out', 'cleanup');
  mkdirSync(dir, { recursive: true });
  const log = (m: string) => { const l = `${new Date().toISOString().slice(11, 19)} cleanup: ${m}`; console.log(l); try { appendFileSync(join(dir, `${day}.log`), l + '\n'); } catch { /* disk full: console only */ } };
  const user = process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3');
  return { main, apply, days: Number(v.days ?? 3), recentH: 2, now: Date.now(), savesDir: join(user, 'Saves'), log, procs: procsNow(), gh: ghState, target: v.target,
    tempDays: Number(v['temp-days'] ?? 2), probeDays: Number(v['probe-days'] ?? 14), gcDays: Number(v['gc-days'] ?? 14) };
}
export const MODES = ['all', 'worktrees', 'builds', 'saves', 'temp', 'probe', 'gamecopy', 'leftovers', 'lane', 'dropped', 'gate'];
function runAll(x: Ctx, what: string, branch?: string): number {
  let rc = 0;
  if (what === 'lane' || what === 'dropped') { if (!branch) { x.log(`${what} needs --branch <b>`); return 2; } return doLane(x, references(x.main, x.procs), branch, what === 'dropped'); }
  if (what === 'all' || what === 'worktrees') rc |= doWorktrees(x, branch);
  if (branch) return rc;
  if (what === 'all' || what === 'builds') rc |= doBuilds(x);
  if (what === 'all' || what === 'saves') rc |= doSaves(x);
  const left = what === 'all' || what === 'leftovers', refs = left || ['temp', 'probe', 'gamecopy'].includes(what) ? references(x.main, x.procs) : '';
  if (left || what === 'temp') doTemp(x, refs);
  if (left || what === 'probe') doProbe(x, refs);
  if (what === 'gamecopy') doGameCopy(x, refs);
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
  const last = `${r.stdout ?? ''}`.trim().split(/\r?\n/).pop() ?? `cleanup saves exit ${r.status}`;
  // merged worktrees whose agent-merge finish was blocked by a running chain never got a retry, and the gate sweep only runs below 30 GB free
  const w = spawnSync(process.execPath, [join(import.meta.dirname, 'cleanup.ts'), 'worktrees', '--apply'], { encoding: 'utf8', timeout: 1800e3 });
  // %TEMP%, raw probe-out outputs, game-copy experiment copies (docs/283 §6); own child: the first %TEMP% pass walks ~165 GB
  const o = spawnSync(process.execPath, [join(import.meta.dirname, 'cleanup.ts'), 'leftovers', '--apply'], { encoding: 'utf8', timeout: 3600e3 });
  const ol = `${o.stdout ?? ''}`.trim().split(/\r?\n/).filter(l => /cleanup: (temp|probe-out|game-copy): .*fre/.test(l)).map(l => l.replace(/^.*?cleanup: /, '')).join('; ') || `leftovers exit ${o.status}`;
  return `${last}; ${`${w.stdout ?? ''}`.trim().split(/\r?\n/).filter(l => /worktrees: /.test(l)).pop() ?? `worktrees exit ${w.status}`}; ${ol}`;
}

function selfCheck(): number {
  process.env.TS3_CRASHDUMPS = join(tmpdir(), 'ts3-cleanup-no-crashdumps'); // fixtures never touch the real CrashDumps
  const d = realpathSync.native(mkdtempSync(join(tmpdir(), 'ts3-cleanup-check-'))), main = join(d, 'repo'), tgt = join(d, 'real-game-copy');
  const g = (cwd: string, ...a: string[]) => git(cwd, '-c', 'user.name=t', '-c', 'user.email=t@example.invalid', '-c', 'core.autocrlf=false', ...a);
  const lines: string[] = [];
  const ctx = (o: Partial<Ctx> = {}): Ctx => ({ main, apply: true, days: 3, recentH: 0, now: Date.now(), savesDir: join(d, 'Saves'), log: m => lines.push(m), procs: [], gh: () => '', tempDays: 2, probeDays: 14, gcDays: 14, ...o });
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
    for (const n of ['merged', 'unmerged', 'dirty', 'chained', 'reserved', 'lockedwt', 'rawout']) g(main, 'worktree', 'add', '-q', wt(n), '-b', `b/${n}`);
    symlinkSync(tgt, join(wt('merged'), 'game-copy'), 'junction');
    mkdirSync(join(wt('rawout'), 'probe-out', 'queue'), { recursive: true }); writeFileSync(join(wt('rawout'), 'probe-out', 'queue', 'qq-1126-hitches.jsonl'), 'band dump');
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
    for (const [n, re] of [['rawout', /probe-out not digested: queue\/qq-1126-hitches\.jsonl/], ['unmerged', /not merged/], ['dirty', /uncommitted/], ['chained', /referenced by a running chain/], ['reserved', /agent reservation doc-1/], ['lockedwt', /git-locked/]] as const)
      assert.ok(existsSync(wt(n)) && lines.some(l => l.includes(wt(n)) && re.test(l)), `kept ${n}`);
    // digested (named in docs) -> goes
    lines.length = 0; mkdirSync(join(main, 'docs'), { recursive: true }); writeFileSync(join(main, 'docs', 'r.md'), 'see qq-1126-hitches.jsonl'); runAll(ctx(), 'worktrees');
    assert.ok(!existsSync(wt('rawout')), 'digested probe-out: worktree removed');
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
    const D = join(main, 'probe-out', 'dumps'), put = (n: string, stacks: boolean, keep = false, aged = false) => { mkdirSync(join(D, n), { recursive: true }); const f = join(D, n, 'a.dmp');
      writeFileSync(f, 'd'); if (stacks) writeFileSync(join(D, n, 'a-stacks.txt'), n === 'lock-only' ? '# lock-wait text only' : `${CDB_MARK}
frames`); if (keep) writeFileSync(join(D, n, 'keep'), ''); if (aged) old(f); const t = new Date(Date.now() - 3600e3); if (!aged) utimesSync(f, t, t); };
    put('with-stacks', true); put('no-stacks-new', false); put('keep-marker', true, true); put('no-stacks-old', false, false, true); put('lock-only', true);
    // junction inside dumps (as game-copy is in worktrees) to a folder outside the repo, and an outside path passed in directly: never deleted
    const outside = join(d, 'outside-dumps'); mkdirSync(outside); writeFileSync(join(outside, 'b.dmp'), 'd'); writeFileSync(join(outside, 'b-stacks.txt'), `${CDB_MARK}\nframes`); old(join(outside, 'b.dmp'));
    symlinkSync(outside, join(D, 'linked'), 'junction');
    assert.equal(dumpRoot(join(D, 'linked', 'b.dmp'), [D]), null, 'path through a junction refused');
    assert.equal(dumpRoot(join(outside, 'b.dmp'), [D]), null, 'path outside the dump dirs refused');
    assert.equal(dumpRoot(join(D, 'with-stacks', 'a.dmp'), [D]), resolve(D), 'plain path under dumps allowed');
    // dry run (no --apply): logs relative path + size, deletes nothing
    lines.length = 0; doDumps(ctx({ apply: false }), '', [join(D, 'linked', 'b.dmp'), join(outside, 'b.dmp')]);
    assert.ok(existsSync(join(D, 'with-stacks', 'a.dmp')), 'dry run deletes nothing');
    assert.ok(lines.some(l => /^would delete dumps\/with-stacks\/a\.dmp 0\.0 MB \(stacks file exists\)$/.test(l)), 'dry run logs path relative to dumps + size');
    assert.equal(lines.filter(l => /^skip .*b\.dmp: /.test(l)).length, 2, 'junction and outside dumps skipped with a line');
    lines.length = 0; doSaves(ctx({ procs: chainProc }));
    assert.ok(lines.some(l => /^deleted dumps\/with-stacks\/a\.dmp 0\.0 MB/.test(l)), 'apply logs every deleted dump');
    assert.ok(existsSync(join(outside, 'b.dmp')), 'dump behind the junction kept');
    assert.ok(!existsSync(join(S, 'Repro-x-1200-1.sims3')) && existsSync(join(S, 'Repro-x-1200-2.sims3')), 'older family member deleted, newest kept');
    assert.ok(existsSync(join(S, 'Baseline-BP.sims3')), 'baseline never touched');
    assert.ok(existsSync(join(S, 'Matrix-BP-off-1.sims3')), 'save named by a running queue kept');
    assert.ok(existsSync(join(S, 'QuitCrash-0412.sims3')), 'sole family member kept');
    assert.ok(!existsSync(join(main, 'probe-out', 'dumps', 'old.dmp')) && existsSync(join(main, 'probe-out', 'dumps', 'new.dmp')), 'old dump deleted, new kept');
    assert.ok(!existsSync(join(D, 'with-stacks', 'a.dmp')) && existsSync(join(D, 'with-stacks', 'a-stacks.txt')), 'stacks present: dmp deleted, txt kept');
    assert.ok(existsSync(join(D, 'no-stacks-new', 'a.dmp')), 'no stacks, < 3 d: kept');
    assert.ok(existsSync(join(D, 'keep-marker', 'a.dmp')), 'keep marker: kept');
    assert.ok(existsSync(join(D, 'lock-only', 'a.dmp')), 'lock-wait text only (no cdb stacks): dmp kept');
    assert.ok(!existsSync(join(D, 'no-stacks-old', 'a.dmp')), 'no stacks, > 3 d: deleted');

    // leftovers regexes on real source lines / names
    const src = (t: string) => [...t.matchAll(TEMP_SRC)].map(m => m[1] ?? m[2]);
    assert.deepEqual(src("const d = mkdtempSync(join(tmpdir(), 'ts3-publish-'));"), ['ts3-publish-']);
    assert.deepEqual(src("d = tempfile.mkdtemp(prefix='f4b-test-')"), ['f4b-test-']);
    assert.deepEqual(src('$d = Join-Path $env:TEMP "ts3-diag-fixture-$PID"'), ['ts3-diag-fixture-']);
    assert.deepEqual(src('const f = join(tmpdir(), `wh-session-${process.pid}-b`)'), ['wh-session-']);
    assert.deepEqual(src("const lock = join(tmpdir(), 'mts-helper.lock'); mkdtempSync(join(tmp, 'x-')); join(tmpdir(), 'ts3-cleanup-no-crashdumps')"), [], 'lock file, non-tmpdir base, no trailing dash: not prefixes');
    const P = ['frida-', 'ts3-publish-', 'ts3-publish-check-', 'mts-root-'];
    assert.equal(tempOwner('frida-0000ca9d3e1a41a29f057091e6eaf717', P), 'frida-');
    assert.equal(tempOwner('ts3-publish-check-Ab12Cd', P), 'ts3-publish-check-', 'longest prefix wins');
    for (const n of ['claude', 'ts3-publish', 'remoteIpMoProxy_CimCmdlets_1.0.0.0_localhost_0075', 'mts-root-a b', 'Frida']) assert.equal(tempOwner(n, P), '', `not ours: ${n}`);
    for (const f of ['adaptdiag-kit-correct-1121-m1121b-2026-11-21-bp-adaptdiag-induced-600-frames.csv', 'q-2026-11-21-bp-shadow.jsonl', 'q-2026-11-21-bp-probe.log', 'q-x-hitches.jsonl']) assert.ok(RAW_OUT.test(f), `raw ${f}`);
    for (const f of ['q-2026-11-21.jsonl', 'q-2026-11-21-bp.log', 'q-x-hitches.jsonl.bak', 'q-report.txt', 'q-x-counters.json', 'chain-x.sh']) assert.ok(!RAW_OUT.test(f), `kept kind ${f}`);
    assert.equal(rawStem('q-2026-11-21-bp-600-frames.csv'), 'q-2026-11-21-bp-600');
    const cd = compatDone('mod,file,kind,overlap,verdict,evidence,status,note\n"A","compat-mods/1-a/A.zip","tuning","","no overlap","[scan, MTS 1] ","",""\n"B","compat-mods/2-b/B, v2.zip","script","x","overlap","e","",""\n'
      + '"C","compat-mods/3-c/C.zip","script","x","overlap","e","tested 11-22: ok",""\n"C2","compat-mods/3-c/C2.zip","script","","no overlap","","",""\n"D","compat-mods/4-d/D.zip","script","","unknown","","",""\n"P","player/p.package","script","","overlap","","",""\n');
    assert.deepEqual([...cd], [['1-a', true], ['2-b', false], ['3-c', true], ['4-d', false]], 'verdict recorded: no overlap, or overlap with a status');
    assert.equal(laneOf('feature/cleanup-auto-1123'), 'cleanup-auto-1123'); assert.equal(laneOf('feature/12-kebab-x'), 'kebab-x');
    assert.ok(laneBuild('compat-resolve-1122').test('ts3fixes.package.compat-resolve-1122h') && laneBuild('compat-resolve-1122').test('ApexRadiance.asi.compat-resolve-1122-dev'));
    assert.ok(laneFile('lanex-1123').test('lanex-1123-2026-11-23-a-frames.csv') && laneFile('lanex-1123').test('q-lanex-1123-x-shadow.jsonl'));
    assert.ok(!laneFile('lanex-1123').test('lanex-11234-a-frames.csv') && !laneFile('lanex-1123').test('xlanex-1123-a-frames.csv'));
    assert.ok(!laneBuild('compat-resolve-1122').test('ts3fixes.package.compat-resolve-11223') && !laneBuild('m1122').test('ts3fixes.package.xm1122') && !laneBuild('m1122').test('ts3fixes.package.m11220'));

    // temp sweep: ours + old + unreferenced goes; young, foreign, linked, named, locked stay (locked counted, not fatal)
    mkdirSync(join(main, 'tools'), { recursive: true });
    writeFileSync(join(main, 'tools', 'a.ts'), "mkdtempSync(join(tmpdir(), 'ts3-publish-')); mkdtempSync(join(tmpdir(), 'mts-root-')); mkdtempSync(join(tmpdir(), 'jit-replay-'));\n");
    const T = join(d, 'tmp'); mkdirSync(T);
    const tdir = (n: string, aged = true) => { mkdirSync(join(T, n)); writeFileSync(join(T, n, 'f.bin'), 'x'.repeat(10)); if (aged) { old(join(T, n, 'f.bin')); old(join(T, n)); } };
    for (const n of ['ts3-publish-old111', 'claude', 'ts3-publish-ref333', 'frida-lck444', 'jit-replay-old555', 'frida-abc']) tdir(n);
    tdir('ts3-publish-new222', false); symlinkSync(tgt, join(T, 'frida-abc', 'link'), 'junction'); old(join(T, 'frida-abc'));
    const tp: Proc[] = [{ pid: 999002, ppid: 1, name: 'chrome.exe', cmd: `chrome --user-data-dir=${join(T, 'ts3-publish-ref333')}` }];
    const epermRm = (p: string) => { if (p.endsWith('frida-lck444')) throw Object.assign(new Error('EPERM'), { code: 'EPERM' }); rmSync(p, { recursive: true, force: true }); };
    lines.length = 0; doTemp(ctx({ apply: false, tempDir: T }), references(main, tp));
    assert.ok(existsSync(join(T, 'ts3-publish-old111')) && lines.some(l => /would delete 1 x ts3-publish-\* .*\[tools\/a\.ts\]/.test(l)), 'temp dry run: plan with creator');
    lines.length = 0; doTemp(ctx({ tempDir: T, rm: epermRm }), references(main, tp));
    assert.ok(!existsSync(join(T, 'ts3-publish-old111')) && existsSync(join(T, 'ts3-publish-new222')), 'old ours deleted, young kept');
    assert.ok(existsSync(join(T, 'claude')) && existsSync(join(T, 'frida-abc')) && existsSync(join(tgt, 'Game', 'TS3W.exe')), 'foreign kept; linked entry kept; junction target intact');
    assert.ok(existsSync(join(T, 'ts3-publish-ref333')) && existsSync(join(T, 'frida-lck444')) && existsSync(join(T, 'jit-replay-old555')), 'named by a process kept; EPERM kept; prefix outside TEMP_CLEAN kept');
    assert.ok(lines.some(l => /1 locked entries skipped/.test(l)), 'locked entry counted');

    // probe-out raw outputs: old + unlinked goes; results, docs-linked, young, running queue stay
    const PP = join(main, 'probe-out', 'profile'), PQ = join(main, 'probe-out', 'queue'); mkdirSync(PP, { recursive: true });
    const pf = (dir: string, n: string, aged = true) => { writeFileSync(join(dir, n), 'r'); if (aged) old(join(dir, n)); };
    pf(PP, 'q1-2026-11-01-a-600-frames.csv'); pf(PP, 'q1-2026-11-01-a-hitches.jsonl'); pf(PP, 'q1-2026-11-01-b-frames.csv', false); pf(PQ, 'q1-2026-11-01.jsonl'); pf(PQ, 'q-x-2026-11-01-a-shadow.jsonl');
    mkdirSync(join(main, 'docs', 'data', 'results'), { recursive: true }); writeFileSync(join(main, 'docs', 'data', 'results', 'r.md'), 'see `probe-out/profile/q1-2026-11-01-a-hitches.jsonl`.\n');
    lines.length = 0; doProbe(ctx({ probeDays: 5 }), references(main, chainProc));
    assert.ok(!existsSync(join(PP, 'q1-2026-11-01-a-600-frames.csv')), 'old raw deleted');
    assert.ok(existsSync(join(PP, 'q1-2026-11-01-a-hitches.jsonl')) && existsSync(join(PP, 'q1-2026-11-01-b-frames.csv')) && existsSync(join(PQ, 'q1-2026-11-01.jsonl')) && existsSync(join(PQ, 'q-x-2026-11-01-a-shadow.jsonl')), 'linked, young, result, running queue kept');
    assert.ok(lines.some(l => /deleted 1 raw files .*kept 1 linked from docs, 1 younger, 1 of a running queue/.test(l)), 'probe summary line');

    // game-copy: old unused cc set + compat mods with a recorded verdict go
    const GC = join(main, 'game-copy'), cc = (n: string, aged = true) => { mkdirSync(join(GC, 'cc-synth', n), { recursive: true }); writeFileSync(join(GC, 'cc-synth', n, 'a.package'), 'p'); if (aged) { old(join(GC, 'cc-synth', n, 'a.package')); old(join(GC, 'cc-synth', n)); } };
    cc('cc-old'); cc('cc-pend'); cc('cc-new', false);
    mkdirSync(join(main, 'tools', 'harness', 'queues'), { recursive: true }); writeFileSync(join(main, 'tools', 'harness', 'queues', 'p.json'), JSON.stringify({ jobs: [{ modSet: 'game-copy/cc-synth/cc-pend' }] }));
    for (const m of ['1-a', '2-b', '3-c', '4-d', '5-none']) { mkdirSync(join(GC, 'compat-mods', m), { recursive: true }); writeFileSync(join(GC, 'compat-mods', m, 'm.zip'), 'z'); }
    writeFileSync(join(main, 'docs', 'data', 'compat-matrix.csv'), 'mod,file,kind,overlap,verdict,evidence,status,note\n"A","compat-mods/1-a/A.zip","t","","no overlap","","",""\n"B","compat-mods/2-b/B.zip","s","x","overlap","","",""\n"C","compat-mods/3-c/C.zip","s","x","overlap","","ok 11-22",""\n"D","compat-mods/4-d/D.zip","s","","unknown","","",""\n');
    lines.length = 0; doGameCopy(ctx({ gcDays: 5 }), '');
    assert.ok(!existsSync(join(GC, 'cc-synth', 'cc-old')) && existsSync(join(GC, 'cc-synth', 'cc-pend')) && existsSync(join(GC, 'cc-synth', 'cc-new')), 'cc: old unused deleted; queued, young kept');
    assert.deepEqual(['1-a', '2-b', '3-c', '4-d', '5-none'].map(m => existsSync(join(GC, 'compat-mods', m))), [false, true, false, true, true], 'compat: recorded verdicts deleted, open/unknown/no row kept');
    lines.length = 0; runAll(ctx({ apply: false, tempDir: T }), 'leftovers');
    assert.ok(!lines.some(l => /^game-copy: /.test(l)) && lines.some(l => /^probe-out: /.test(l)) && lines.some(l => /^temp: /.test(l)), 'leftovers = temp + probe, no gamecopy');

    // lane (merge finish): lane builds go, other lanes and the unmerged worktree stay; dropped: worktree too (branch ref kept) + raw outputs any age
    writeFileSync(join(main, 'docs', 'data', 'builds.json'), JSON.stringify({ builds: [{ name: 'ts3fixes.package.lanex-1123b' }, { name: 'ts3fixes.package.other-1123' }] }));
    for (const n of ['ts3fixes.package.lanex-1123b', 'ts3fixes.package.other-1123']) writeFileSync(join(GC, n), 'pkg');
    g(main, 'worktree', 'add', '-q', wt('lanex'), '-b', 'feature/lanex-1123'); writeFileSync(join(wt('lanex'), 'l.txt'), 'l'); g(wt('lanex'), 'add', '-A'); g(wt('lanex'), 'commit', '-qm', 'parked design');
    pf(PP, 'lanex-1123-2026-11-23-a-frames.csv', false);
    lines.length = 0; assert.equal(runAll(ctx(), 'lane', 'feature/lanex-1123'), 0);
    assert.ok(!existsSync(join(GC, 'ts3fixes.package.lanex-1123b')) && existsSync(join(GC, 'ts3fixes.package.other-1123')) && existsSync(wt('lanex')), 'lane: own build deleted only');
    assert.equal(runAll(ctx(), 'lane'), 2, 'lane without --branch refused');
    lines.length = 0; assert.equal(runAll(ctx(), 'dropped', 'feature/lanex-1123'), 0);
    assert.ok(!existsSync(wt('lanex')) && gitOk(main, 'rev-parse', '-q', '--verify', 'refs/heads/feature/lanex-1123'), 'dropped: unmerged worktree removed, branch ref keeps its commits');
    assert.ok(!existsSync(join(PP, 'lanex-1123-2026-11-23-a-frames.csv')), 'dropped: lane raw output deleted at any age');
    assert.equal(runAll(ctx(), 'dropped', 'b/dirty'), 1, 'dropped: dirty worktree refused');
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
    target: { type: 'string' }, 'min-free-gb': { type: 'string' }, 'auto-gb': { type: 'string' }, 'temp-days': { type: 'string' }, 'probe-days': { type: 'string' }, 'gc-days': { type: 'string' } } });
  if (v['self-check']) process.exit(selfCheck());
  const what = positionals[0] ?? 'all';
  if (!MODES.includes(what)) { console.error(`usage: node tools/harness/cleanup.ts [${MODES.join('|')}] [--apply] [--min-free-gb N] [--days N] [--temp-days N] [--probe-days N] [--gc-days N] [--branch b] [--target ref] | --self-check`); process.exit(2); }
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
