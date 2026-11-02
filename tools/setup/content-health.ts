// Read-only content health check for a Sims 3 user folder (docs/17 candidate 2). Never deletes or moves anything.
//   node tools/setup/content-health.ts [--user <path>] [--json] [--self-test]
// Default user folder: TS3_USER or Documents\Electronic Arts\The Sims 3 (same rule as install-fixes.ts).
// Cache caps come from Default.ini in TS3_BIN (or the Steam default path), with EA's shipped values as fallback.
import assert from 'node:assert/strict';
import { existsSync, readdirSync, readFileSync, statSync } from 'node:fs';
import { homedir } from 'node:os';
import { join, relative, sep } from 'node:path';
import { parseArgs } from 'node:util';

// Copied from install-fixes.ts (importing it would run its CLI).
const BIN = process.env.TS3_BIN ?? 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\The Sims 3\\Game\\Bin';
const DEFAULT_USER = process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3');

const MB = 1024 * 1024, GB = 1024 * MB;
const EXCLUDED_REPACKS = [/store\s*fixes/i, /store\s*updates/i]; // G4TW repacks, docs/07 §2.6
const CONFLICT_DIRS = ['', 'Thumbnails', 'DCCache', 'WorldCaches', 'SigsCache', 'FeaturedItems'];

type Entry = { path: string; size: number };

function walk(dir: string): Entry[] {
  if (!existsSync(dir)) return [];
  return readdirSync(dir, { withFileTypes: true }).flatMap(e => {
    const p = join(dir, e.name);
    return e.isDirectory() ? walk(p) : [{ path: p, size: statSync(p).size }];
  });
}
const sum = (es: Entry[]) => es.reduce((a, e) => a + e.size, 0);
const mb = (n: number) => Math.round((n / MB) * 100) / 100;
const posix = (p: string) => p.split(sep).join('/');

// Resource.cfg: the PackedFile patterns (relative to Mods). '*' matches within one path segment only.
// shortcut: DirectoryFiles lines are ignored (no .package mount); add them if a mod set relies on them.
export function packedPatterns(cfg: string): string[] {
  return cfg.split(/\r?\n/).map(l => l.trim().match(/^PackedFile\s+(.+)$/i)?.[1].trim()).filter((p): p is string => !!p);
}
export function globMatch(pattern: string, rel: string): boolean {
  const ps = pattern.replace(/\\/g, '/').split('/'), rs = rel.replace(/\\/g, '/').split('/');
  if (ps.length !== rs.length) return false;
  return ps.every((seg, i) => new RegExp('^' + seg.split('*').map(s => s.replace(/[.+?^${}()|[\]\\]/g, '\\$&')).join('[^/]*') + '$', 'i').test(rs[i]));
}

// OneDrive-style conflict copies: "<name>-<PC>[.ext]" next to "<name>[.ext]" (PC names may contain '-').
export function conflictBase(name: string, siblings: Set<string>): string | null {
  const dot = name.lastIndexOf('.');
  const stem = dot > 0 ? name.slice(0, dot) : name, ext = dot > 0 ? name.slice(dot) : '';
  for (let i = stem.indexOf('-'); i > 0; i = stem.indexOf('-', i + 1)) {
    const base = stem.slice(0, i) + ext;
    if (siblings.has(base.toLowerCase())) return base;
  }
  return null;
}

function iniNumbers(path: string): Record<string, number> {
  const out: Record<string, number> = {};
  if (!existsSync(path)) return out;
  for (const m of readFileSync(path, 'latin1').matchAll(/^\s*(\w+)\s*=\s*(\d+)/gm)) out[m[1]] = Number(m[2]);
  return out;
}

function check(user: string) {
  const warnings: string[] = [];
  const ini = iniNumbers(join(BIN, 'Default.ini'));
  const caps = {
    'compositorCache.package': ini.CompositorCacheSize ?? 100 * MB,
    'simCompositorCache.package': ini.SimCompositorCacheSize ?? 500 * MB,
  };
  const dbCacheMaxMB = ini.DBCacheMaxSizeMB ?? 200;

  // DCCache
  const dcFiles = walk(join(user, 'DCCache'));
  const ofExt = (x: string) => dcFiles.filter(e => e.path.toLowerCase().endsWith(x));
  const dbc = ofExt('.dbc'), ebc = ofExt('.ebc'), ent = dcFiles.find(e => e.path.toLowerCase().endsWith(`${sep}dcc.ent`));
  const dccache = {
    totalMB: mb(sum(dcFiles)), dccEntMB: ent ? mb(ent.size) : null,
    dbc: { count: dbc.length, MB: mb(sum(dbc)) }, ebc: { count: ebc.length, MB: mb(sum(ebc)) }, dbCacheMaxSizeMB: dbCacheMaxMB,
  };
  if (sum(dbc) + sum(ebc) > dbCacheMaxMB * MB)
    warnings.push(`DCCache bundles ${mb(sum(dbc) + sum(ebc))} MB exceed DBCacheMaxSizeMB ${dbCacheMaxMB} (community: launcher slows, bundles split)`);

  // Downloads leftovers
  const packs = walk(join(user, 'Downloads')).filter(e => e.path.toLowerCase().endsWith('.sims3pack'));
  const downloads = { sims3pack: packs.length, MB: mb(sum(packs)) };
  if (packs.length) warnings.push(`${packs.length} .sims3pack files (${downloads.MB} MB) left in Downloads`);

  // Conflict copies
  const conflicts: { path: string; of: string; MB: number }[] = [];
  for (const d of CONFLICT_DIRS) {
    const dir = join(user, d);
    if (!existsSync(dir) || !statSync(dir).isDirectory()) continue; // an empty FeaturedItems file is a known store-ads blocker
    const names = readdirSync(dir);
    const set = new Set(names.map(n => n.toLowerCase()));
    for (const n of names) {
      const base = conflictBase(n, set);
      if (!base) continue;
      const p = join(dir, n), st = statSync(p);
      conflicts.push({ path: posix(join(d, n)), of: base, MB: mb(st.isDirectory() ? sum(walk(p)) : st.size) });
    }
  }
  if (conflicts.length) warnings.push(`${conflicts.length} sync conflict copies (${mb(conflicts.reduce((a, c) => a + c.MB * MB, 0))} MB), not read by the game`);

  // Caches at their caps
  const cacheFiles = Object.entries(caps).map(([name, cap]) => {
    const p = join(user, name), size = existsSync(p) ? statSync(p).size : 0;
    return { name, MB: mb(size), capMB: mb(cap), atCap: size >= cap * 0.95 };
  });
  for (const c of cacheFiles) if (c.atCap) warnings.push(`${c.name} ${c.MB} MB is at its ${c.capMB} MB cap`);

  // Mods
  const mods = join(user, 'Mods'), cfgPath = join(mods, 'Resource.cfg');
  const cfg = existsSync(cfgPath) ? readFileSync(cfgPath, 'latin1') : null;
  const patterns = cfg ? packedPatterns(cfg) : [];
  const dccacheLine = patterns.some(p => /dccache/i.test(p));
  if (dccacheLine) warnings.push('Mods/Resource.cfg mounts DCCache (docs/07 §4.1: remove the DCCache line)');
  if (!cfg) warnings.push('no Mods/Resource.cfg: no package in Mods is loaded');
  const pkgs = walk(mods).filter(e => e.path.toLowerCase().endsWith('.package')).map(e => ({ ...e, rel: posix(relative(mods, e.path)) }));
  const byFolder = new Map<string, { count: number; MB: number }>();
  for (const p of pkgs) {
    const folder = p.rel.split('/').slice(0, -1).slice(0, 2).join('/') || '.';
    const f = byFolder.get(folder) ?? { count: 0, MB: 0 };
    f.count++; f.MB += p.size;
    byFolder.set(folder, f);
  }
  const folders = [...byFolder].map(([folder, f]) => ({ folder, count: f.count, MB: mb(f.MB) })).sort((a, b) => b.MB - a.MB);
  const notLoaded = pkgs.filter(p => !patterns.some(pt => globMatch(pt, p.rel))).map(p => p.rel);
  if (notLoaded.length) warnings.push(`${notLoaded.length} packages not matched by any Resource.cfg PackedFile line (never loaded)`);
  const byName = new Map<string, string[]>();
  for (const p of pkgs) { const k = p.rel.split('/').pop()!.toLowerCase(); byName.set(k, [...(byName.get(k) ?? []), p.rel]); }
  const duplicates = [...byName.values()].filter(v => v.length > 1);
  if (duplicates.length) warnings.push(`${duplicates.length} package file names appear more than once`);
  const large = pkgs.filter(p => p.size > 2 * GB).map(p => ({ path: p.rel, GB: Math.round((p.size / GB) * 100) / 100, overFormatCap: p.size >= 4 * GB }));
  for (const l of large) warnings.push(`${l.path} is ${l.GB} GB${l.overFormatCap ? ' (over the 4 GB package format cap: unreadable)' : ' (> 2 GB)'}`);
  const repacks = pkgs.filter(p => EXCLUDED_REPACKS.some(r => r.test(p.rel.split('/').pop()!))).map(p => p.rel);
  if (repacks.length) warnings.push(`${repacks.length} excluded third-party store repacks in Mods (docs/07 §2.6): flag only, nothing changed`);

  const dirMB = (d: string) => (existsSync(join(user, d)) ? mb(sum(walk(join(user, d)))) : null);
  return {
    user, defaultIni: existsSync(join(BIN, 'Default.ini')) ? join(BIN, 'Default.ini') : null,
    dccache, downloads, conflicts, cacheFiles,
    mods: { resourceCfg: !!cfg, dccacheLine, packages: pkgs.length, MB: mb(sum(pkgs)), folders, notLoaded, duplicates, large, repacks },
    sigsCacheMB: dirMB('SigsCache'), dcBackupMB: dirMB('DCBackup'),
    warnings,
  };
}

function selfTest() {
  const cfg = 'Priority 500\r\nPackedFile Packages/*.package\r\nPackedFile Packages/*/*.package\r\n  packedfile DCCache/*.dbc\r\nDirectoryFiles Files/... autoupdate\r\n';
  assert.deepEqual(packedPatterns(cfg), ['Packages/*.package', 'Packages/*/*.package', 'DCCache/*.dbc']);
  assert.ok(globMatch('Packages/*/*.package', 'Packages/CC/hair.package'));
  assert.ok(globMatch('Packages/*/*.package', 'packages/My CC/Hair.PACKAGE'));
  assert.ok(!globMatch('Packages/*/*.package', 'Packages/a/b/hair.package'));
  assert.ok(!globMatch('Packages/*.package', 'hair.package'));
  assert.ok(!globMatch('Packages/*.package', 'Packages/hair.package.bak'));
  const sib = new Set(['compositorcache.package', 'compositorcache-laptop-g3.package', 'featureditems', 'featureditems-laptop-m15']);
  assert.equal(conflictBase('compositorCache-LAPTOP-G3.package', sib), 'compositorCache.package');
  assert.equal(conflictBase('FeaturedItems-<pc>', sib), 'FeaturedItems');
  assert.equal(conflictBase('simCompositorCache.package', sib), null);
  console.log('self-test ok');
}

const { values } = parseArgs({ options: { user: { type: 'string' }, json: { type: 'boolean' }, 'self-test': { type: 'boolean' } } });
if (values['self-test']) selfTest();
else {
  const user = values.user ?? DEFAULT_USER;
  if (!existsSync(user)) { console.error(`user folder not found: ${user}`); process.exit(2); }
  const r = check(user);
  if (values.json) console.log(JSON.stringify(r, null, 2));
  else {
    const list = (xs: string[], n = 5) => xs.slice(0, n).map(x => `    ${x}`).concat(xs.length > n ? [`    ... ${xs.length - n} more (--json for all)`] : []);
    console.log([
      `Sims 3 content health: ${r.user}`,
      `  DCCache ${r.dccache.totalMB} MB: dcc.ent ${r.dccache.dccEntMB ?? 'absent'}${r.dccache.dccEntMB !== null ? ' MB' : ''}, ${r.dccache.dbc.count} .dbc (${r.dccache.dbc.MB} MB), ${r.dccache.ebc.count} .ebc (${r.dccache.ebc.MB} MB); DBCacheMaxSizeMB ${r.dccache.dbCacheMaxSizeMB}`,
      `  Downloads: ${r.downloads.sims3pack} .sims3pack (${r.downloads.MB} MB)`,
      `  Caches: ${r.cacheFiles.map(c => `${c.name} ${c.MB}/${c.capMB} MB`).join(', ')}`,
      `  Mods: ${r.mods.packages} packages, ${r.mods.MB} MB; Resource.cfg ${r.mods.resourceCfg ? 'present' : 'MISSING'}${r.mods.dccacheLine ? ' (with DCCache line)' : ''}`,
      ...r.mods.folders.slice(0, 8).map(f => `    ${f.folder}: ${f.count} packages, ${f.MB} MB`),
      `  SigsCache ${r.sigsCacheMB ?? 'absent'}${r.sigsCacheMB !== null ? ' MB' : ''}, DCBackup ${r.dcBackupMB ?? 'absent'}${r.dcBackupMB !== null ? ' MB' : ''}`,
      ...(r.conflicts.length ? ['  Conflict copies:', ...list(r.conflicts.map(c => `${c.path} (${c.MB} MB, copy of ${c.of})`))] : []),
      ...(r.mods.notLoaded.length ? ['  Not loaded (no PackedFile match):', ...list(r.mods.notLoaded)] : []),
      ...(r.mods.duplicates.length ? ['  Duplicate names:', ...list(r.mods.duplicates.map(d => d.join('  |  ')))] : []),
      ...(r.mods.repacks.length ? ['  Excluded store repacks:', ...list(r.mods.repacks)] : []),
      r.warnings.length ? `WARN (${r.warnings.length}):\n${r.warnings.map(w => `  - ${w}`).join('\n')}` : 'OK: nothing to flag',
      'Read-only: nothing was changed.',
    ].join('\n'));
  }
}
