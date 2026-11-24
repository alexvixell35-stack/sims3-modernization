// The repo's main branch for "latest" reads: `master` in the private repo, `main` in a public clone (Lenovo 2026-11-23: auto-publish
// ran `git rev-parse master` there). master if it exists, else main, else what origin/HEAD points at, else master (the old default).
//   node tools/default-branch.ts [--self-check]
import { execFileSync } from 'node:child_process';
import { mkdtempSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';

export function defaultBranch(cwd: string, git: (...a: string[]) => string = (...a) => execFileSync('git', a, { cwd, encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim()): string {
  const has = (r: string) => { try { git('rev-parse', '-q', '--verify', `${r}^{commit}`); return true; } catch { return false; } };
  for (const b of ['master', 'main']) if (has(b)) return b;
  try { const h = git('symbolic-ref', '-q', '--short', 'refs/remotes/origin/HEAD'); if (h && has(h)) return h; } catch { /* no origin/HEAD */ }
  return 'master';
}

if (process.argv[1] === fileURLToPath(import.meta.url)) {
  if (process.argv.includes('--self-check')) {
    const assert = (await import('node:assert')).strict;
    const mk = (b: string) => { const d = mkdtempSync(join(tmpdir(), 'defbr-')); const g = (...a: string[]) => execFileSync('git', ['-c', 'user.name=t', '-c', 'user.email=t@t', ...a], { cwd: d, stdio: 'ignore' }); g('init', '-q', '-b', b); g('commit', '-q', '--allow-empty', '-m', 'x'); return { d, g }; };
    const m = mk('master'), n = mk('main'), o = mk('trunk');
    try {
      assert.equal(defaultBranch(m.d), 'master'); assert.equal(defaultBranch(n.d), 'main');
      m.g('branch', 'main'); assert.equal(defaultBranch(m.d), 'master', 'master wins when both exist');
      assert.equal(defaultBranch(o.d), 'master', 'nothing known: old default');
      o.g('update-ref', 'refs/remotes/origin/trunk', 'HEAD'); o.g('symbolic-ref', 'refs/remotes/origin/HEAD', 'refs/remotes/origin/trunk');
      assert.equal(defaultBranch(o.d), 'origin/trunk', 'origin/HEAD last');
    } finally { for (const x of [m, n, o]) rmSync(x.d, { recursive: true, force: true }); }
    console.log('default-branch self-check: OK');
  } else console.log(defaultBranch(process.cwd()));
}
