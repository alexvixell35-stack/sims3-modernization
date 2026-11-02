// Copy the local-only files (everything .gitignore keeps out of git) to a USB drive, so a second machine can pick them up.
// Incremental: a file is copied when it is missing on the drive or its size/mtime differ; nothing is ever deleted there.
//   node tools/usb-backup.ts --dest <drive or folder> [--sets saves,mods,probe,gamecopy,downloads,memory] [--dry-run] [--rar] [--exfat]
//   node tools/usb-backup.ts --self-check
// Sets (all by default):
//   saves      <Documents>/Electronic Arts/The Sims 3/Saves (test saves) + the *.ini settings in that folder
//   mods       <Documents>/Electronic Arts/The Sims 3/Mods (test mods, installed ts3fixes/agent)
//   probe      probe-out/ (queue logs, profiles, results, handoffs)
//   gamecopy   game-copy/ without the per-agent Ghidra copies (ghidra-<x>/; the main ghidra/ project is kept)
//   downloads  ~/Downloads/ts3-fixes (installer cache: MonoPatcher, S3PP, NRaas, ...)
//   memory     Claude Code's memory for this project (~/.claude/projects/<project>/memory)
// The drive keeps EA game bytes (Ghidra project, decompiled code, pinned builds): personal use only, never share it.
// Close Ghidra before copying game-copy/ghidra (its lock files are skipped; a database in use may copy half-written).
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readdirSync, rmSync, statSync, statfsSync, utimesSync, writeFileSync } from 'node:fs';
import { execFileSync } from 'node:child_process';
import { homedir, tmpdir } from 'node:os';
import { dirname, join, relative, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..');
const DOCS = join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3');
const FAT32_MAX = 4 * 1024 ** 3 - 1;

type Set = { name: string; src: string; skip?: (rel: string, isDir: boolean) => boolean; filesOnly?: RegExp };
const SETS: Set[] = [
  { name: 'saves', src: join(DOCS, 'Saves') },
  { name: 'settings', src: DOCS, filesOnly: /\.ini$/i },
  { name: 'mods', src: join(DOCS, 'Mods') },
  { name: 'probe', src: join(ROOT, 'probe-out') },
  { name: 'gamecopy', src: join(ROOT, 'game-copy'), skip: skipGameCopy },
  { name: 'downloads', src: join(homedir(), 'Downloads', 'ts3-fixes') },
  { name: 'memory', src: join(homedir(), '.claude', 'projects', ROOT.replace(/[:\\/]/g, '-'), 'memory') },
];

// per-agent Ghidra copies (ghidra-<x>/) duplicate the main project; lock files belong to a running Ghidra
export function skipGameCopy(rel: string, isDir: boolean): boolean {
  const top = rel.split(/[\\/]/)[0];
  if (isDir && rel === top && /^ghidra-/.test(top)) return true;
  return /\.lock~?$/i.test(rel);
}

type Plan = { files: { from: string; to: string; size: number; mtime: number }[]; skipped: string[]; bytes: number };
export function plan(set: Set, destRoot: string, fat32: boolean): Plan {
  const p: Plan = { files: [], skipped: [], bytes: 0 };
  const walk = (dir: string) => {
    let entries;
    try { entries = readdirSync(dir, { withFileTypes: true }); } catch (e) { p.skipped.push(`${dir}: ${(e as Error).message}`); return; }
    for (const ent of entries) {
      const from = join(dir, ent.name), rel = relative(set.src, from);
      const isDir = ent.isDirectory();
      if (set.skip?.(rel, isDir)) continue;
      if (isDir) { if (!set.filesOnly) walk(from); continue; }
      if (!ent.isFile() || (set.filesOnly && !set.filesOnly.test(ent.name))) continue;
      const st = statSync(from), to = join(destRoot, set.name, rel);
      if (fat32 && st.size > FAT32_MAX) { p.skipped.push(`${rel}: ${st.size} bytes, over the FAT32 4 GB limit`); continue; }
      const old = existsSync(to) ? statSync(to) : null;
      // FAT/exFAT keep mtimes at 2 s resolution
      if (old && old.size === st.size && Math.abs(old.mtimeMs - st.mtimeMs) < 2000) continue;
      p.files.push({ from, to, size: st.size, mtime: st.mtimeMs });
      p.bytes += st.size;
    }
  };
  if (existsSync(set.src)) walk(set.src); else p.skipped.push(`${set.src}: not found`);
  return p;
}

const gb = (n: number) => (n / 1024 ** 3).toFixed(2) + ' GB';

function main() {
  const arg = (k: string) => { const i = process.argv.indexOf(k); return i > 0 ? process.argv[i + 1] : undefined; };
  const dest = arg('--dest');
  if (!dest) { console.log('usage: node tools/usb-backup.ts --dest <drive or folder> [--sets a,b] [--dry-run] | --self-check'); process.exit(2); }
  const want = arg('--sets')?.split(',');
  const dry = process.argv.includes('--dry-run');
  const destRoot = join(dest, 'ts3-modernization-backup');
  mkdirSync(destRoot, { recursive: true });
  const fs = statfsSync(destRoot);
  // FAT32 reports type 0x4d44 on Linux; on Windows there is no type, so assume FAT32 limits unless --exfat
  const fat32 = !process.argv.includes('--exfat') && (process.platform === 'win32' || fs.type === 0x4d44);
  const sets = SETS.filter(s => !want || want.includes(s.name) || (s.name === 'settings' && want.includes('saves')));
  const plans = sets.map(s => ({ s, p: plan(s, destRoot, fat32) }));
  const need = plans.reduce((a, x) => a + x.p.bytes, 0), free = fs.bavail * fs.bsize;
  for (const { s, p } of plans) console.log(`${s.name.padEnd(9)} ${String(p.files.length).padStart(7)} files to copy, ${gb(p.bytes)}${p.skipped.length ? `, ${p.skipped.length} skipped` : ''}`);
  console.log(`total ${gb(need)} to copy, ${gb(free)} free on ${dest}${fat32 ? ' (FAT32 4 GB file limit applied; pass --exfat if the drive is exFAT/NTFS)' : ''}`);
  for (const { p } of plans) for (const s of p.skipped) console.log(`  skipped: ${s}`);
  if (need > free) { console.log('STOP: not enough free space; pick fewer sets with --sets'); process.exit(1); }
  if (dry) { console.log('dry run: nothing copied'); return; }
  if (process.argv.includes('--rar')) return rarSets(sets, destRoot);
  let done = 0, failed = 0;
  for (const { s, p } of plans) {
    for (const f of p.files) {
      try {
        mkdirSync(dirname(f.to), { recursive: true });
        copyFileSync(f.from, f.to);
        utimesSync(f.to, new Date(), new Date(f.mtime));
        done += f.size;
      } catch (e) { failed++; console.log(`  FAILED ${relative(s.src, f.from)}: ${(e as Error).message}`); }
    }
    console.log(`${s.name}: done (${gb(done)} copied so far)`);
  }
  writeFileSync(join(destRoot, 'BACKUP.txt'), `ts3-modernization local-only backup\nlast run ${new Date().toISOString()}\nsets: ${sets.map(s => s.name).join(', ')}\n`
    + 'Contains EA game bytes (Ghidra project, decompiled code, builds): personal use only, never share or upload.\n'
    + 'Restore: copy each folder back to its source (see the header of tools/usb-backup.ts).\n');
  console.log(`done: ${gb(done)} copied, ${failed} failed`);
  if (failed) process.exitCode = 1;
}

// --rar: one RAR archive per set instead of a file copy (smaller: logs and the Ghidra database shrink most, packages and
// saves little). Volumes of 3,900 MB fit FAT32; low priority (-ri1) and 2 threads so a running game is less disturbed.
// shortcut: full archive every run (no incremental update of multi-volume archives); use the plain copy for frequent runs.
export function rarArgs(set: Set, destRoot: string): string[] {
  const a = ['a', '-r', '-m3', '-mt2', '-ri1', '-v3900m', '-ep1', '-y', '-idq'];
  if (set.name === 'gamecopy') a.push('-x*\\ghidra-*\\*', '-x*.lock');
  const src = set.filesOnly ? join(set.src, '*.ini') : join(set.src, '*');
  if (set.filesOnly) a.splice(1, 1);
  return [...a, join(destRoot, `${set.name}.rar`), src];
}

function rarSets(sets: Set[], destRoot: string) {
  const rar = ['C:\\Program Files\\WinRAR\\Rar.exe', 'C:\\Program Files (x86)\\WinRAR\\Rar.exe'].find(p => existsSync(p)) ?? 'rar';
  for (const s of sets) {
    if (!existsSync(s.src)) { console.log(`${s.name}: skipped, ${s.src} not found`); continue; }
    console.log(`${s.name}: archiving...`);
    try { execFileSync(rar, rarArgs(s, destRoot), { stdio: 'inherit' }); console.log(`${s.name}: done`); }
    catch (e) { process.exitCode = 1; console.log(`${s.name}: FAILED ${(e as Error).message}`); }
  }
}

function selfCheck() {
  let k = 0;
  const ok = (c: boolean, what: string) => { k++; if (!c) { console.log(`FAIL ${what}`); process.exitCode = 1; } };
  ok(skipGameCopy('ghidra-core1', true) && !skipGameCopy('ghidra', true) && !skipGameCopy('ghidra-apply.log', false), 'agent ghidra copies skipped, main project and logs kept');
  ok(skipGameCopy(join('ghidra', 'ts3.lock'), false) && !skipGameCopy(join('ghidra', 'ts3.rep', 'x.gbf'), false), 'lock files skipped');
  const tmp = mkdtempSync(join(tmpdir(), 'usb-backup-'));
  try {
    const src = join(tmp, 'src'), dst = join(tmp, 'dst');
    mkdirSync(join(src, 'ghidra-a'), { recursive: true }); mkdirSync(join(src, 'ghidra'), { recursive: true });
    writeFileSync(join(src, 'ghidra-a', 'x'), 'x'); writeFileSync(join(src, 'ghidra', 'p.gbf'), 'p'); writeFileSync(join(src, 'a.txt'), 'a');
    const set: Set = { name: 'gamecopy', src, skip: skipGameCopy };
    const p1 = plan(set, dst, true);
    ok(p1.files.length === 2 && !p1.files.some(f => f.from.includes('ghidra-a')), 'plan picks files, skips agent copies');
    for (const f of p1.files) { mkdirSync(dirname(f.to), { recursive: true }); copyFileSync(f.from, f.to); utimesSync(f.to, new Date(), new Date(f.mtime)); }
    ok(plan(set, dst, true).files.length === 0, 'second run copies nothing (incremental)');
    writeFileSync(join(src, 'a.txt'), 'changed');
    ok(plan(set, dst, true).files.length === 1, 'changed file is copied again');
    const ini: Set = { name: 'settings', src, filesOnly: /\.ini$/i };
    writeFileSync(join(src, 'Options.ini'), 'o');
    ok(plan(ini, dst, true).files.length === 1, 'settings set takes only top-level ini files');
  } finally { rmSync(tmp, { recursive: true, force: true }); }
  const ra = rarArgs({ name: 'gamecopy', src: 'G', skip: skipGameCopy }, 'D');
  ok(ra.includes('-v3900m') && ra.includes('-x*\\ghidra-*\\*') && ra.at(-2) === join('D', 'gamecopy.rar'), 'rar args: FAT32 volumes, agent copies excluded');
  console.log(`usb-backup self-check: ${k} checks${process.exitCode ? ', FAILED' : ', ok'}`);
}

if (process.argv.includes('--self-check')) selfCheck(); else main();
