// Removes agent worktrees under <main checkout>/.claude/worktrees without touching the real game copy: a `game-copy`
// junction/symlink is unlinked first (the LINK only, checked with lstat and, on Windows, fsutil reparsepoint), then
// `git worktree remove --force`. A worktree with a real game-copy directory or any other link inside is refused.
//   node tools/worktree-clean.ts                 list agent worktrees and whether their branch is merged
//   node tools/worktree-clean.ts <path> [--yes]  one worktree (merged or not)
//   node tools/worktree-clean.ts --all-merged [--yes] [--base master]   every worktree whose HEAD is in the base branch
// Dry run unless --yes. Branches are kept (delete them with `git branch -d`).   --self-check: temp repo + temp junction.
import { execFileSync, spawnSync } from 'node:child_process';
import { existsSync, lstatSync, mkdirSync, mkdtempSync, readdirSync, realpathSync, rmdirSync, rmSync, symlinkSync, unlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve, sep } from 'node:path';

const win = process.platform === 'win32';
const norm = (p: string) => { const r = resolve(p); return win ? r.toLowerCase() : r; };
const lexists = (p: string) => { try { lstatSync(p); return true; } catch { return false; } };
const git = (cwd: string, ...a: string[]) => execFileSync('git', a, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();

function isLink(p: string): boolean {
  if (!lstatSync(p).isSymbolicLink()) return false; // Node reports junctions as symbolic links too
  return !win || spawnSync('fsutil', ['reparsepoint', 'query', p], { stdio: 'ignore' }).status === 0;
}
// Links under dir (not followed), and how many real files sit beside them.
function scan(dir: string, out = { links: [] as string[], files: 0 }) {
  for (const e of readdirSync(dir, { withFileTypes: true })) {
    const p = join(dir, e.name);
    if (lstatSync(p).isSymbolicLink()) out.links.push(p);
    else if (e.isDirectory()) scan(p, out); else out.files++;
  }
  return out;
}

function run(args: string[], cwd = process.cwd()): number {
  const yes = args.includes('--yes'), all = args.includes('--all-merged');
  const bi = args.indexOf('--base'), pathArg = args.find((a, i) => !a.startsWith('--') && args[i - 1] !== '--base');
  const top = git(cwd, 'rev-parse', '--show-toplevel'), common = resolve(top, git(cwd, 'rev-parse', '--git-common-dir'));
  const main = common.endsWith('.git') ? dirname(common) : top, root = norm(join(main, '.claude', 'worktrees')) + sep;
  const base = bi >= 0 ? args[bi + 1] : (spawnSync('git', ['rev-parse', '-q', '--verify', 'master'], { cwd: main }).status === 0 ? 'master' : 'main');
  const wts = git(main, 'worktree', 'list', '--porcelain').split(/\r?\n\r?\n/).map(b => ({
    path: resolve((b.match(/^worktree (.+)$/m) || [])[1] ?? ''), head: (b.match(/^HEAD (\w+)/m) || [])[1], branch: (b.match(/^branch refs\/heads\/(.+)$/m) || [])[1] ?? '(detached)',
    locked: /^locked/m.test(b),
  })).filter(w => w.head && norm(w.path).startsWith(root));
  const merged = (w: { head?: string }) => spawnSync('git', ['merge-base', '--is-ancestor', w.head!, base], { cwd: main }).status === 0;
  let targets = wts;
  if (pathArg) {
    const want = existsSync(pathArg) ? realpathSync.native(pathArg) : pathArg;  // expands 8.3 short names (TEMP is C:\Users\<user> on some machines); git lists long ones
    targets = wts.filter(w => norm(w.path) === norm(want));
    if (!targets.length) { console.error(`not an agent worktree under ${root}: ${pathArg}`); return 1; }
  } else if (all) targets = wts.filter(merged);
  else { for (const w of wts) console.log(`${merged(w) ? 'merged  ' : 'unmerged'}  ${w.branch}  ${w.path}`); return 0; }
  let fail = 0;
  for (const w of targets) {
    const here = norm(top) === norm(w.path) || norm(cwd).startsWith(norm(w.path) + sep);
    if (here) { console.log(`skip ${w.path}: current directory`); continue; }
    if (!existsSync(w.path)) { console.log(`${yes ? '' : 'would '}prune missing ${w.path}`); if (yes) git(main, 'worktree', 'prune'); continue; }
    // shortcut: a running agent's fresh worktree with no edits yet still looks merged; name worktrees by path when agents are running
    if (all && (w.locked || git(w.path, 'status', '--porcelain'))) { console.log(`skip ${w.path}: ${w.locked ? 'locked' : 'uncommitted changes'} (name it by path to remove anyway)`); continue; }
    const gc = join(w.path, 'game-copy');
    // game-copy is either a link, or a real directory holding only links (e.g. assemblies -> main game-copy)
    const kind = (() => { try { return lstatSync(gc).isSymbolicLink() ? 'link' : 'dir'; } catch { return 'none'; } })();
    let gcLinks: string[] = [];
    if (kind === 'link') gcLinks = [gc];
    else if (kind === 'dir') {
      const s = scan(gc);
      if (s.files) { console.log(`REFUSED ${w.path}: game-copy is a real directory with ${s.files} real files (move them out by hand)`); fail++; continue; }
      gcLinks = s.links;
    }
    const unverified = gcLinks.filter(p => !isLink(p));
    if (unverified.length) { console.log(`REFUSED ${w.path}: not a verified reparse point: ${unverified.join(', ')}`); fail++; continue; }
    const other = scan(w.path).links.filter(p => !gcLinks.some(g => norm(g) === norm(p)));
    if (other.length) { console.log(`REFUSED ${w.path}: other links inside (${other.join(', ')})`); fail++; continue; }
    const targets = gcLinks.map(l => { try { return realpathSync(l); } catch { return ''; } }); // '' = broken link
    if (!yes) { console.log(`would ${gcLinks.map((l, i) => `unlink ${l} (-> ${targets[i] || 'broken'}), `).join('')}${kind === 'dir' ? `remove empty ${gc}, ` : ''}git worktree remove --force ${w.path}  [${w.branch}]`); continue; }
    for (const [i, l] of gcLinks.entries()) {
      try { unlinkSync(l); } catch { rmdirSync(l); } // both remove only the link entry, never the target
      if (lexists(l) || (targets[i] && !existsSync(targets[i]))) { console.log(`STOP ${w.path}: removing ${l} did not leave its target alone`); return 1; }
      console.log(`unlinked ${l} (target ${targets[i] || 'broken'} kept)`);
    }
    if (kind === 'dir') {
      const s = scan(gc);
      if (s.files || s.links.length) { console.log(`STOP ${w.path}: ${gc} not empty after unlinking`); return 1; }
      rmSync(gc, { recursive: true });
    }
    git(main, 'worktree', 'remove', '--force', w.path);
    console.log(`removed ${w.path}  [${w.branch}]`);
  }
  return fail ? 1 : 0;
}

function selfCheck(): number {
  const d = mkdtempSync(join(tmpdir(), 'wt-clean-check-')), repo = join(d, 'repo'), target = join(d, 'real-game-copy');
  let bad = 0; const ok = (c: unknown, m: string) => { console.log(c ? 'ok  ' : 'FAIL', m); if (!c) bad++; };
  const g = (cwd: string, ...a: string[]) => git(cwd, '-c', 'user.name=t', '-c', 'user.email=t@example.invalid', ...a);
  const quiet = (a: string[]) => { const l = console.log; const out: string[] = []; console.log = (...x) => out.push(x.join(' ')); try { return { rc: run(a, repo), out: out.join('\n') }; } finally { console.log = l; } };
  try {
    mkdirSync(join(target, 'Game'), { recursive: true }); writeFileSync(join(target, 'Game', 'TS3W.exe'), 'keep me');
    mkdirSync(repo); g(repo, 'init', '-q', '-b', 'master'); writeFileSync(join(repo, 'a.txt'), 'a'); writeFileSync(join(repo, '.gitignore'), 'game-copy\ndocs-link\n'); g(repo, 'add', '-A'); g(repo, 'commit', '-qm', 'base');
    const wt = (n: string) => join(repo, '.claude', 'worktrees', n);
    for (const n of ['merged', 'unmerged', 'realdir', 'otherlink', 'dirty', 'linkdir']) g(repo, 'worktree', 'add', '-q', wt(n), '-b', n);
    writeFileSync(join(wt('dirty'), 'wip.txt'), 'uncommitted');
    writeFileSync(join(wt('unmerged'), 'b.txt'), 'b'); g(wt('unmerged'), 'add', '-A'); g(wt('unmerged'), 'commit', '-qm', 'not in master');
    for (const n of ['merged', 'unmerged', 'otherlink']) symlinkSync(target, join(wt(n), 'game-copy'), 'junction');
    mkdirSync(join(wt('realdir'), 'game-copy')); writeFileSync(join(wt('realdir'), 'game-copy', 'x'), 'x');
    mkdirSync(join(wt('linkdir'), 'game-copy')); symlinkSync(join(target, 'Game'), join(wt('linkdir'), 'game-copy', 'assemblies'), 'junction');
    symlinkSync(target, join(wt('otherlink'), 'docs-link'), 'junction');
    const dry = quiet(['--all-merged']);
    ok(dry.out.includes('would unlink') && existsSync(wt('merged')) && existsSync(join(wt('merged'), 'game-copy')), 'dry run changes nothing');
    const r = quiet(['--all-merged', '--yes']);
    ok(!existsSync(wt('merged')), 'merged worktree with a game-copy junction removed');
    ok(existsSync(join(target, 'Game', 'TS3W.exe')), 'junction target and its files survive');
    ok(existsSync(wt('unmerged')), '--all-merged keeps the unmerged worktree');
    ok(existsSync(join(wt('realdir'), 'game-copy', 'x')) && /REFUSED .*realdir.*real directory/.test(r.out), 'real game-copy directory refused');
    ok(!existsSync(wt('linkdir')) && existsSync(join(target, 'Game', 'TS3W.exe')), 'game-copy dir holding only a junction: link unlinked, worktree removed, target kept');
    ok(existsSync(wt('otherlink')) && /REFUSED .*otherlink.*other links/.test(r.out) && r.rc === 1, 'worktree with another link refused');
    ok(existsSync(join(wt('dirty'), 'wip.txt')) && /skip .*dirty.*uncommitted/.test(r.out), '--all-merged skips a worktree with uncommitted changes');
    const one = quiet([wt('unmerged'), '--yes']);
    ok(one.rc === 0 && !existsSync(wt('unmerged')) && existsSync(join(target, 'Game', 'TS3W.exe')), 'named unmerged worktree removed, target kept');
    ok(g(repo, 'branch', '--list', 'unmerged') !== '', 'branch kept');
  } finally {
    for (const n of ['otherlink']) for (const l of ['game-copy', 'docs-link']) try { unlinkSync(join(repo, '.claude', 'worktrees', n, l)); } catch { /* gone */ }
    rmSync(d, { recursive: true, force: true });
  }
  return bad ? 1 : 0;
}

const argv = process.argv.slice(2);
process.exit(argv.includes('--self-check') ? selfCheck() : run(argv));
