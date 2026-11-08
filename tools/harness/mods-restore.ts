// Mods/Packages files the harness installs (cycle.ts --restart copies build/ts3agent, ts3-nointro and ts3fixes; fixesPackage
// jobs copy a shipped ts3fixes over it) are recorded at queue start and put back at queue end, like the Bin pair (2026-11-08:
// after a dance repro job Mods held build/ts3fixes.package 55df1786 instead of the release v0.2.10 c373cd87; Bin was restored).
// The record is <dir>/manifest.json (name -> sha256, or null = absent) plus a copy of each present file, manifest written last;
// it stays until a restore read back clean, so a killed queue is restored by the next queue start or track.ts stop.
// Between jobs the packages stay as cycle.ts installs them; only the queue end (and the kill paths) put the start state back.
//   node tools/harness/mods-restore.ts restore [dir]   put back from <dir> (default <main checkout>/probe-out/queue/mods-before); game must be closed
//   node tools/harness/mods-restore.ts --self-check    temp dirs only
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';

// shortcut: fixed list of the packages our tools install; add a name here when a tool installs another package into Mods/Packages
export const OURS = ['ts3fixes.package', 'ts3agent.package', 'ts3-nointro.package'];
type Manifest = { time: string; pkgs: string; files: Record<string, string | null> };
const sha = (p: string) => createHash('sha256').update(readFileSync(p)).digest('hex');
const mf = (dir: string) => join(dir, 'manifest.json');
const short = (s: string | null) => (s ? s.slice(0, 8) : 'absent');
export const pending = (dir: string) => existsSync(mf(dir));

// records OURS in pkgs (<user>/Mods/Packages); refuses over a manifest that was not restored yet (restore it first)
export function snapshotMods(pkgs: string, dir: string): string {
  if (pending(dir)) throw new Error(`Mods record from an unfinished run exists: ${mf(dir)} (restore it first)`);
  mkdirSync(dir, { recursive: true });
  const files: Record<string, string | null> = {};
  for (const n of OURS) {
    const p = join(pkgs, n);
    files[n] = existsSync(p) ? sha(p) : null;
    if (files[n]) { copyFileSync(p, join(dir, n)); if (sha(join(dir, n)) !== files[n]) throw new Error(`Mods record: copy of ${n} differs after the copy`); }
    else rmSync(join(dir, n), { force: true });
  }
  writeFileSync(mf(dir), JSON.stringify({ time: new Date().toISOString(), pkgs, files } satisfies Manifest, null, 1));
  return `Mods packages at start: ${OURS.map(n => `${n} ${short(files[n])}`).join(', ')} (copies in ${dir})`;
}

// puts back what the manifest recorded, reads every file back by sha; changed anything = scriptCache.package removed (a stale
// cache runs the old scripts); clean read-back removes the manifest, any problem keeps it (and the copies) for a rerun
export function restoreMods(dir: string): { line: string; problems: string[]; changed: string[] } {
  if (!pending(dir)) return { line: `Mods packages: no record in ${dir}, nothing to restore`, problems: [], changed: [] };
  const m = JSON.parse(readFileSync(mf(dir), 'utf8')) as Manifest;
  const problems: string[] = [], changed: string[] = [];
  for (const [n, want] of Object.entries(m.files)) {
    const p = join(m.pkgs, n), cur = existsSync(p) ? sha(p) : null;
    if (cur === want) continue;
    try {
      if (want) copyFileSync(join(dir, n), p); else rmSync(p, { force: true });
      changed.push(`${n} ${short(cur)} -> ${short(want)}`);
    } catch (e) { problems.push(`${n}: ${String(e).split('\n')[0]}`); continue; }
    const back = existsSync(p) ? sha(p) : null;
    if (back !== want) problems.push(`read-back: ${n} is ${short(back)}, want ${short(want)}`);
  }
  if (changed.length) rmSync(join(dirname(dirname(m.pkgs)), 'scriptCache.package'), { force: true });
  if (!problems.length) rmSync(mf(dir), { force: true });
  const state = Object.entries(m.files).map(([n, w]) => `${n} ${short(w)}`).join(', ');
  const line = problems.length ? `Mods RESTORE FAILED: ${problems.join('; ')} (record kept in ${dir}; node tools/harness/mods-restore.ts restore)`
    : `restored Mods packages from the start (sha read back): ${state}${changed.length ? `; changed: ${changed.join(', ')}; scriptCache cleared` : '; nothing had changed'}`;
  return { line, problems, changed };
}

if (import.meta.main) {
  const [cmd, arg] = process.argv.slice(2);
  if (cmd === '--self-check') {
    const assert = (await import('node:assert')).strict;
    const t = mkdtempSync(join(tmpdir(), 'mods-restore-')), user = join(t, 'user'), pkgs = join(user, 'Mods', 'Packages'), rec = join(t, 'rec');
    const put = (n: string, s: string) => writeFileSync(join(pkgs, n), s), rd = (n: string) => (existsSync(join(pkgs, n)) ? readFileSync(join(pkgs, n), 'utf8') : null);
    try {
      mkdirSync(pkgs, { recursive: true });
      put('ts3fixes.package', 'release'); put('ts3agent.package', 'agent-old'); // ts3-nointro absent at start
      assert.match(snapshotMods(pkgs, rec), /ts3-nointro\.package absent/);
      assert.throws(() => snapshotMods(pkgs, rec), /unfinished run/, 'no second record over an unrestored one');
      // job: cycle.ts installs build/ packages (all three), clears nothing we track
      put('ts3fixes.package', 'build'); put('ts3agent.package', 'agent-new'); put('ts3-nointro.package', 'ni'); writeFileSync(join(user, 'scriptCache.package'), 'cache');
      const r = restoreMods(rec);
      assert.deepEqual(r.problems, []); assert.equal(r.changed.length, 3);
      assert.equal(rd('ts3fixes.package'), 'release'); assert.equal(rd('ts3agent.package'), 'agent-old'); assert.equal(rd('ts3-nointro.package'), null, 'absent at start = removed');
      assert.ok(!existsSync(join(user, 'scriptCache.package')), 'stale script cache removed');
      assert.ok(!pending(rec), 'clean restore drops the record'); assert.match(restoreMods(rec).line, /nothing to restore/);
      // unchanged: no copy, cache kept
      snapshotMods(pkgs, rec); writeFileSync(join(user, 'scriptCache.package'), 'cache');
      const u = restoreMods(rec); assert.deepEqual(u.changed, []); assert.match(u.line, /nothing had changed/); assert.ok(existsSync(join(user, 'scriptCache.package')));
      // lost backup copy: problem reported, record kept for a rerun
      snapshotMods(pkgs, rec); put('ts3fixes.package', 'build'); rmSync(join(rec, 'ts3fixes.package'));
      const f = restoreMods(rec); assert.equal(f.problems.length, 1); assert.match(f.line, /Mods RESTORE FAILED/); assert.ok(pending(rec), 'record kept on failure');
    } finally { rmSync(t, { recursive: true, force: true }); }
    console.log('self-check ok');
  } else if (cmd === 'restore') {
    const { gameRunning, GAME_EXE } = await import('./game-exe.ts');
    if (gameRunning()) { console.error(`${GAME_EXE} is running: close the game first`); process.exit(2); }
    const r = restoreMods(resolve(arg ?? join(import.meta.dirname, '..', '..', 'probe-out', 'queue', 'mods-before')));
    console.log(r.line); process.exit(r.problems.length ? 1 : 0);
  } else { console.error('usage: node tools/harness/mods-restore.ts restore [dir] | --self-check'); process.exit(2); }
}
