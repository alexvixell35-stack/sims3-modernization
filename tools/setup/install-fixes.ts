// Installs the known community fixes for The Sims 3 from tools/setup/fixes.json in one go (see docs/07).
// Downloads only from the official sources (never re-hosts), verifies SHA-256 when the manifest has one,
// backs up every file it overwrites and records what it did, so `uninstall` restores the exact previous state.
//
//   node tools/setup/install-fixes.ts plan      [--set default|all] [--only id,id] [--skip id,id]   (also prints content-health warnings)
//   node tools/setup/install-fixes.ts install   [same options]
//   node tools/setup/install-fixes.ts uninstall [--only id,id]
//   node tools/setup/install-fixes.ts status
//   node tools/setup/install-fixes.ts install --only cache-budget --cache-budget-mb 50   (offload phase 0)
//
// Manual downloads (NRaas, ModTheSims, Nexus need a browser): put the files in --downloads
// (default ~/Downloads/ts3-fixes); `plan` lists which ones are missing and where to get them.
// Windows-only in practice (the game is), but uses only Node built-ins plus the system `tar` (bsdtar: zip, tar.gz, 7z).
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { homedir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';
import { parseArgs } from 'node:util';

type FileSpec = string | { from: string; to: string };
type Fix = {
  id: string; name: string; version: string | null; default: boolean;
  source: { kind: 'github-release' | 'direct' | 'manual'; url: string | null; asset?: string | null; page?: string | null };
  sha256?: string | null; install: { target: 'bin' | 'mods' | 'overrides' | 'world' | 'config'; files?: FileSpec[] };
  config?: any; requires: string[]; conflictsWith: string[]; notes?: string;
};
type Installed = { id: string; version: string | null; at: string; files: { path: string; backup: string | null }[] };

const ROOT = resolve(import.meta.dirname, '..', '..');
const BIN = process.env.TS3_BIN ?? 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\The Sims 3\\Game\\Bin';
const USER = process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3');
const HOME = join(process.env.LOCALAPPDATA ?? join(homedir(), '.local', 'share'), 'ts3-fixes');
const STATE = join(HOME, 'state.json');
// Windows' own bsdtar reads zip/7z/tar.gz; a GNU tar from Git Bash on PATH would treat "C:" as a remote host.
const TAR = process.platform === 'win32' ? join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'tar.exe') : (process.env.TS3_TAR ?? 'bsdtar');  // TS3_TAR: another bsdtar (Linux: GNU tar cannot read zip/7z/rar)

const { positionals, values } = parseArgs({ allowPositionals: true, options: {
  set: { type: 'string', default: 'default' }, only: { type: 'string' }, skip: { type: 'string' },
  downloads: { type: 'string', default: join(homedir(), 'Downloads', 'ts3-fixes') },
  'cache-budget-mb': { type: 'string' }, // value for the cache-budget fix (offload phase 0: 200, 50, 0)
} });
const cmd = positionals[0] ?? 'plan';
const fixes: Fix[] = JSON.parse(readFileSync(join(import.meta.dirname, 'fixes.json'), 'utf8'));
const byId = new Map(fixes.map(f => [f.id, f]));
const state: Record<string, Installed> = existsSync(STATE) ? JSON.parse(readFileSync(STATE, 'utf8')) : {};
const saveState = () => { mkdirSync(HOME, { recursive: true }); writeFileSync(STATE, JSON.stringify(state, null, 1)); };
const list = (s?: string) => (s ? s.split(',').map(x => x.trim()).filter(Boolean) : []);

// ---------- selection: chosen set + requirements, in dependency order, conflicts refused
function select(): Fix[] {
  const only = list(values.only), skip = new Set(list(values.skip));
  for (const id of [...only, ...skip]) if (!byId.has(id)) throw new Error(`unknown fix id '${id}'`);
  const wanted = only.length ? only : fixes.filter(f => values.set === 'all' || f.default).map(f => f.id);
  const order: Fix[] = [], seen = new Set<string>();
  const visit = (id: string) => {
    if (seen.has(id) || skip.has(id)) return;
    seen.add(id);
    const f = byId.get(id)!;
    f.requires.forEach(visit);
    order.push(f);
  };
  wanted.forEach(visit);
  for (const f of order) for (const c of f.conflictsWith) if (seen.has(c)) throw new Error(`'${f.id}' conflicts with '${c}'; pick one (--skip)`);
  return order;
}

// ---------- sources
const sha256 = (file: string) => createHash('sha256').update(readFileSync(file)).digest('hex');

function findManual(f: Fix): string | null {
  if (!existsSync(values.downloads!)) return null;
  const want = (f.source.asset ?? '').toLowerCase();
  const names = readdirSync(values.downloads!);
  // ModTheSims prefixes downloads ("MTS_<author>_<id>_<name>"), so match on the end of the name
  const hit = want ? names.find(n => n.toLowerCase().endsWith(want)) : null;
  // shortcut: without an asset name, match on the fix id's words; good enough for a hand-filled folder
  const loose = hit ?? names.find(n => f.id.split('-').every(w => n.toLowerCase().includes(w)));
  return loose ? join(values.downloads!, loose) : null;
}

async function fetchSource(f: Fix): Promise<string | null> {
  if (f.source.kind === 'manual') return findManual(f);
  if (!f.source.url) return null; // generated by this tool
  const cache = join(HOME, 'cache', f.id);
  const file = join(cache, f.source.asset ?? basename(new URL(f.source.url).pathname));
  if (!existsSync(file)) {
    mkdirSync(cache, { recursive: true });
    const r = await fetch(f.source.url, { redirect: 'follow', signal: AbortSignal.timeout(300000) });
    if (!r.ok) throw new Error(`${f.id}: download failed, HTTP ${r.status} for ${f.source.url}`);
    writeFileSync(file, Buffer.from(await r.arrayBuffer()));
  }
  const got = sha256(file);
  if (f.sha256 && got !== f.sha256) { rmSync(file); throw new Error(`${f.id}: SHA-256 mismatch (got ${got}); file deleted`); }
  if (!f.sha256) console.log(`  ${f.id}: no pinned hash in fixes.json; downloaded file has ${got}`);
  return file;
}

function unpack(f: Fix, file: string): string {
  if (!/\.(zip|7z|tar\.gz|tgz|rar)$/i.test(file)) return dirname(file);
  const dir = join(HOME, 'unpacked', f.id);
  rmSync(dir, { recursive: true, force: true });
  mkdirSync(dir, { recursive: true });
  execFileSync(TAR, ['-xf', file, '-C', dir]);
  return dir;
}

function walk(dir: string): string[] {
  return readdirSync(dir, { withFileTypes: true }).flatMap(e => (e.isDirectory() ? walk(join(dir, e.name)) : [join(dir, e.name)]));
}

function pick(tree: string, pattern: string): string[] {
  const norm = (p: string) => p.replace(/\\/g, '/').toLowerCase();
  const rx = new RegExp('(^|/)' + norm(pattern).replace(/[.+^${}()|[\]]/g, '\\$&').replace(/\*/g, '[^/]*') + '$');
  return walk(tree).filter(p => rx.test(norm(p)));
}

// ---------- targets
function targetPath(f: Fix, rel: string): string {
  if (rel.startsWith('%BIN%')) return join(BIN, rel.slice('%BIN%'.length).replace(/^[\\/]/, ''));
  if (rel.includes('%DOCUMENTS%')) return rel.replace('%DOCUMENTS%/Electronic Arts/The Sims 3', USER).replace(/\//g, process.platform === 'win32' ? '\\' : '/');
  switch (f.install.target) {
    case 'bin': return join(BIN, rel);
    case 'overrides': return join(USER, 'Mods', 'Overrides', basename(rel));
    case 'mods': return rel.includes('/') || rel === 'Resource.cfg' ? join(USER, 'Mods', rel) : join(USER, 'Mods', 'Packages', rel);
    default: return join(USER, rel);
  }
}

function place(f: Fix, rec: Installed, dest: string, write: () => void) {
  let backup: string | null = null;
  if (existsSync(dest) && !rec.files.some(x => x.path === dest)) {
    backup = join(HOME, 'backup', f.id, basename(dest) + '.' + Date.now());
    mkdirSync(dirname(backup), { recursive: true });
    copyFileSync(dest, backup);
  }
  mkdirSync(dirname(dest), { recursive: true });
  write();
  if (!rec.files.some(x => x.path === dest)) rec.files.push({ path: dest, backup }); // re-installs must not record a file twice
  state[f.id] = rec; // record now: a later failure in this fix must still let uninstall restore the backup (at stays '' until the fix succeeds)
  if (backup) saveState(); // on disk at once only when a player's file was replaced (a kill skips the catch); not per file: content fixes place thousands
  console.log(`  ${f.id}: ${dest}`);
}

// ---------- generated entries (config owned by this tool)
function toml(c: any): string {
  const out: string[] = [];
  const val = (v: any) => (typeof v === 'string' ? JSON.stringify(v) : String(v));
  for (const [name, p] of Object.entries<any>({ ...c.patches, ...(c.optionalPatches ?? {}) })) {
    out.push(`[patches.${name}]`, ...Object.entries(p).map(([k, v]) => `${k} = ${val(v)}`), '');
  }
  for (const [name, q] of Object.entries<any>(c.qol ?? {})) out.push(`[qol.${name}]`, ...Object.entries(q).map(([k, v]) => `${k} = ${val(v)}`), '');
  return out.join('\n');
}

function setIni(text: string, set: Record<string, any>): string {
  const lines = text.split(/\r?\n/);
  for (const [k, v] of Object.entries(set)) {
    const i = lines.findIndex(l => l.trim().toLowerCase().replace(/\s*=.*/, '') === k.toLowerCase() && l.includes('='));
    if (i >= 0) lines[i] = `${k} = ${v}`; else lines.push(`${k} = ${v}`);
  }
  return lines.join('\r\n');
}

function generated(f: Fix, rec: Installed): boolean {
  const c = f.config ?? {};
  switch (f.id) {
    case 'laa-check': {
      const exe = readFileSync(join(BIN, 'TS3W.exe'));
      const laa = (exe.readUInt16LE(exe.readUInt32LE(0x3c) + 22) & 0x20) !== 0;
      if (!laa) throw new Error('TS3W.exe has no Large Address Aware flag; restore it before installing (docs/02 step 2.5)');
      console.log('  laa-check: TS3W.exe is Large Address Aware');
      return true;
    }
    case 'dxvk-conf':
      place(f, rec, join(BIN, 'dxvk.conf'), () => writeFileSync(join(BIN, 'dxvk.conf'), c.lines.join('\r\n') + '\r\n'));
      return true;
    case 'graphicsrules-vram': {
      // only the "textureMemory == 0" fallback; detected values are left alone
      const p = join(BIN, 'GraphicsRules.sgr');
      const re = /(if \(\$textureMemory == 0\)\s*seti textureMemory\s+)\d+/;
      const text = readFileSync(p, 'latin1');
      if (!re.test(text)) throw new Error('graphicsrules-vram: fallback line not found in GraphicsRules.sgr');
      place(f, rec, p, () => writeFileSync(p, text.replace(re, `$1${c.textureMemoryMB}`), 'latin1'));
      return true;
    }
    case 's3ss-config': {
      const p = targetPath(f, f.install.files![0] as string);
      place(f, rec, p, () => writeFileSync(p, toml(c)));
      return true;
    }
    case 'resource-cfg': {
      const p = join(USER, 'Mods', 'Resource.cfg');
      place(f, rec, p, () => writeFileSync(p, c.content.join('\r\n') + '\r\n'));
      return true;
    }
    case 'options-ini': {
      const p = join(USER, 'Options.ini');
      if (!existsSync(p)) { console.log('  options-ini: no Options.ini yet (start the game once first); skipped'); return true; }
      const next = setIni(readFileSync(p, 'latin1'), c.set);
      place(f, rec, p, () => writeFileSync(p, next, 'latin1'));
      return true;
    }
    case 'nointro': {
      execFileSync(process.execPath, [join(ROOT, 'tools', 'harness', 'nointro.ts')], { stdio: ['ignore', 'ignore', 'inherit'] });
      const p = join(USER, 'Mods', 'Packages', 'ts3-nointro.package');
      place(f, rec, p, () => copyFileSync(join(ROOT, 'build', 'ts3-nointro.package'), p));
      return true;
    }
    case 'overwatch-no-stuckcheck': {
      execFileSync(process.execPath, [join(ROOT, 'tools', 'setup', 'build-overwatch-tuning.ts')], { stdio: ['ignore', 'ignore', 'inherit'] });
      const p = join(USER, 'Mods', 'Packages', 'ts3-overwatch-nostuckcheck.package');
      place(f, rec, p, () => copyFileSync(join(ROOT, 'build', 'ts3-overwatch-nostuckcheck.package'), p));
      return true;
    }
    case 'overwatch-no-invcleanup': {
      execFileSync(process.execPath, [join(ROOT, 'tools', 'setup', 'build-overwatch-tuning.ts'), 'inventory'], { stdio: ['ignore', 'ignore', 'inherit'] });
      const p = join(USER, 'Mods', 'Packages', 'ts3-overwatch-noinvcleanup.package');
      place(f, rec, p, () => copyFileSync(join(ROOT, 'build', 'ts3-overwatch-noinvcleanup.package'), p));
      return true;
    }
    case 'native-patches': {
      execFileSync('cmd', ['/c', join(ROOT, 'native', 'ts3native', 'build.cmd')], { stdio: 'ignore' });
      for (const name of ['ts3native.asi', 'ts3native.ini']) {
        const p = join(BIN, name);
        place(f, rec, p, () => copyFileSync(join(ROOT, 'native', 'ts3native', 'out', name), p));
      }
      return true;
    }
    case 'stutter-fixes': {
      execFileSync(process.execPath, [join(ROOT, 'tools', 'setup', 'build-fixes.ts')], { stdio: ['ignore', 'ignore', 'inherit'] });
      const p = join(USER, 'Mods', 'Packages', 'ts3fixes.package');
      place(f, rec, p, () => copyFileSync(join(ROOT, 'build', 'ts3fixes.package'), p));
      return true;
    }
    case 'portalplan-typepatch-test': {
      // test only (docs/94 §20 job 17); build-fixes.ts builds it once monopatcher (a requires, installed first) is unpacked
      execFileSync(process.execPath, [join(ROOT, 'tools', 'setup', 'build-fixes.ts')], { stdio: ['ignore', 'ignore', 'inherit'] });
      const built = join(ROOT, 'build', 'ts3-portalplan-test.package');
      if (!existsSync(built)) throw new Error('portalplan-typepatch-test: build/ts3-portalplan-test.package not built (needs the monopatcher download)');
      const p = join(USER, 'Mods', 'Packages', 'ts3-portalplan-test.package');
      place(f, rec, p, () => copyFileSync(built, p));
      return true;
    }
    case 'nraas-upstream-base':
    case 'nraas-upstream-v2': {
      // test only: prebuilt by build-nraas-upstream.ts (needs an NRaas clone), so no build here
      for (const name of f.install.files as string[]) {
        const built = join(ROOT, 'build', 'nraas-upstream', c.variant, name);
        if (!existsSync(built)) throw new Error(`${f.id}: ${built} not built (node tools/setup/build-nraas-upstream.ts <NRaas clone>)`);
        const p = join(USER, 'Mods', 'Packages', name);
        place(f, rec, p, () => copyFileSync(built, p));
      }
      return true;
    }
    case 'cache-budget': {
      // the generic ResourceCache budget (bytes) comes from GraphicsRules.sgr `setProp $ResourceGroup CacheBudget`
      // (the ctor 0x732910 reads the wide "CacheBudget" property); Default.ini's CacheBudget is ignored [verified 2026-10-12].
      // Re-installing with another --cache-budget-mb keeps the first backup, so uninstall restores the previous file.
      const p = join(BIN, 'GraphicsRules.sgr');
      const mb = Number(values['cache-budget-mb'] ?? c.mb);
      if (!Number.isInteger(mb) || mb < 0 || mb > 476) throw new Error('cache-budget: --cache-budget-mb must be 0..476 (engine debug var max 500,000,000 B)');
      const re = /^(setProp \$ResourceGroup CacheBudget )\S+/m;
      const text = readFileSync(p, 'latin1');
      if (!re.test(text)) throw new Error('cache-budget: CacheBudget setProp not found in GraphicsRules.sgr');
      place(f, rec, p, () => writeFileSync(p, text.replace(re, `$1${mb * 1024 * 1024}`), 'latin1'));
      return true;
    }
    case 'cache-overflow-budget': {
      // the ResourceCache cold list (released resources kept for reuse, docs/190 M7 rcCold) is capped by
      // `setProp $ResourceGroup CacheOverflowBudget`, which EA's file never sets (cold list ~10 MB); added after the CacheBudget line
      const p = join(BIN, 'GraphicsRules.sgr');
      const mb = Number(c.mb);
      // shortcut: only 64 was measured (2026-11-04 A/B); 256 is a guessed ceiling for the 32-bit address space
      if (!Number.isInteger(mb) || mb < 1 || mb > 256) throw new Error('cache-overflow-budget: config.mb must be 1..256');
      const text = readFileSync(p, 'latin1');
      const line = `setProp $ResourceGroup CacheOverflowBudget ${mb * 1024 * 1024}`;
      const own = /^setProp \$ResourceGroup CacheOverflowBudget \S+/m, anchor = /^setProp \$ResourceGroup CacheBudget \S+(\r?\n)/m;
      if (!own.test(text) && !anchor.test(text)) throw new Error('cache-overflow-budget: CacheBudget setProp not found in GraphicsRules.sgr');
      const next = own.test(text) ? text.replace(own, () => line) : text.replace(anchor, (m, nl) => m + line + nl);
      place(f, rec, p, () => writeFileSync(p, next, 'latin1'));
      return true;
    }
    case 'dxvk-texture-memory': {
      // virtual memory DXVK keeps mapped for locked D3D9 textures (default 100); 0 = no limit, so not allowed here
      const p = join(BIN, 'dxvk.conf');
      const mb = Number(c.mb);
      // shortcut: only 64 was measured (2026-11-04 old-save runs); 16 is a guessed floor
      if (!Number.isInteger(mb) || mb < 16 || mb > 100) throw new Error('dxvk-texture-memory: config.mb must be 16..100');
      const next = setIni(existsSync(p) ? readFileSync(p, 'latin1') : '', { 'd3d9.textureMemory': mb });
      place(f, rec, p, () => writeFileSync(p, next, 'latin1'));
      return true;
    }
    case 'user-cc':
    case 'store-repacks-test': {
      // Rodney's CC from the backup, relative paths kept (they fit Resource.cfg's folder depth); exclusions in fixes.json
      const files = (c.include as string[]).flatMap(inc => readdirSync(join(c.source, inc), { recursive: true }).map(String)
        .filter(n => n.toLowerCase().endsWith('.package')).map(n => `${inc}/${n.replace(/\\/g, '/')}`))
        .filter(rel => !(c.exclude as string[]).some(x => rel.startsWith(x)));
      for (const rel of files) {
        const dest = join(USER, 'Mods', rel);
        place(f, rec, dest, () => copyFileSync(join(c.source, rel), dest));
      }
      console.log(`  user-cc: ${files.length} packages`);
      return true;
    }
    case 'cache-clear':
      clearCaches(f);
      return true;
  }
  return false;
}

function clearCaches(f: Fix) {
  const keep = new Set<string>((f.config?.keep ?? []).map((k: string) => k.toLowerCase()));
  for (const spec of f.install.files as string[]) {
    const p = join(USER, spec.replace(/\/\*$/, ''));
    if (!existsSync(p)) continue;
    // a player may block the Store ads with a 0-byte FeaturedItems file where the game wants a folder: nothing to clear
    if (spec.endsWith('/*') && !statSync(p).isDirectory()) continue;
    if (spec.endsWith('/*')) for (const e of readdirSync(p)) {
      if (!keep.has(`${spec.slice(0, -1)}${e}`.toLowerCase())) rmSync(join(p, e), { recursive: true, force: true });
    }
    else rmSync(p, { force: true });
  }
  console.log('  cache-clear: game caches deleted (the game rebuilds them)');
}

// ---------- commands
async function install(order: Fix[]) {
  const missing: Fix[] = [];
  for (const f of order) {
    if (f.id === 'cache-clear') continue; // runs last, after everything is in place
    const rec: Installed = state[f.id] ?? { id: f.id, version: f.version, at: '', files: [] };
    try {
      if (!generated(f, rec)) {
        const src = await fetchSource(f);
        if (!src) { missing.push(f); continue; }
        const tree = unpack(f, src);
        // no file list: every .package in the download (single-package mods, per-expansion sets)
        for (const spec of f.install.files ?? ['*.package']) {
          const from = typeof spec === 'string' ? spec : spec.from;
          const hits = /\.(zip|7z|tar\.gz|tgz|rar)$/i.test(src) ? pick(tree, from) : [src];
          if (!hits.length) throw new Error(`${f.id}: '${from}' not found in ${basename(src)}`);
          for (const h of hits) {
            const dest = targetPath(f, typeof spec === 'string' ? basename(h) : spec.to);
            place(f, rec, dest, () => copyFileSync(h, dest));
          }
        }
        // key overrides in a shipped Bin ini (e.g. s3pp patches that S3SS already does); the copy above is the backup point
        for (const [file, set] of Object.entries<Record<string, any>>(f.config?.ini ?? {})) {
          const p = join(BIN, file);
          writeFileSync(p, setIni(readFileSync(p, 'utf8'), set));
        }
      }
      rec.at = new Date().toISOString();
      rec.version = f.version;
      if (rec.files.length || f.id === 'laa-check') state[f.id] = rec;
      saveState();
    } catch (e) {
      saveState();
      throw e;
    }
  }
  if (order.some(f => f.id === 'cache-clear')) clearCaches(byId.get('cache-clear')!);
  if (missing.length) {
    console.log(`\n${missing.length} manual download(s) missing; put them in ${values.downloads} and run install again:`);
    for (const f of missing) console.log(`  ${f.id}: ${f.source.asset ?? f.name} <- ${f.source.page}`);
  }
}

function uninstall(ids: string[]) {
  for (const id of ids) {
    const rec = state[id];
    if (!rec) continue;
    const kept: typeof rec.files = [];
    for (const file of [...rec.files].reverse()) {
      if (file.backup && !existsSync(file.backup)) { // never delete the player's file when its backup is gone
        console.warn(`  ${id}: WARNING: backup ${file.backup} is missing; left ${file.path} as it is (record kept)`);
        kept.push(file); process.exitCode = 1; continue;
      }
      if (file.backup) copyFileSync(file.backup, file.path);
      else rmSync(file.path, { force: true });
      console.log(`  ${id}: ${file.backup ? 'restored' : 'removed'} ${file.path}`);
    }
    if (kept.length) state[id] = { ...rec, files: kept.reverse() }; else delete state[id];
    saveState();
  }
}

// Read-only content-health report (docs/17 §7). Findings only warn; they never block an install.
function contentHealth() {
  try {
    const out = execFileSync(process.execPath, [join(import.meta.dirname, 'content-health.ts'), '--json', '--user', USER],
      { encoding: 'utf8', maxBuffer: 256 * 1024 * 1024, stdio: ['ignore', 'pipe', 'pipe'] });
    const { warnings } = JSON.parse(out) as { warnings: string[] };
    console.log(warnings.length ? `\ncontent health: ${warnings.length} warning(s) (details: node tools/setup/content-health.ts)` : '\ncontent health: OK');
    for (const w of warnings) console.log(`  warn: ${w}`);
  } catch (e: any) {
    console.log(`\ncontent health: not run (${String(e.stderr || e.message).trim().split('\n')[0]})`);
  }
}

if (cmd === 'plan') {
  for (const f of select()) {
    const src = f.source.kind === 'manual' ? (findManual(f) ? 'manual: found' : `manual: MISSING (${f.source.page})`)
      : f.source.url ? `${f.source.kind}${f.sha256 ? ' + sha256' : ' (no hash)'}` : 'generated';
    console.log(`${state[f.id]?.at ? 'installed' : 'pending  '}  ${f.id.padEnd(26)} ${String(f.version ?? '').padEnd(14)} ${src}`);
  }
  contentHealth();
} else if (cmd === 'install') {
  await install(select());
} else if (cmd === 'uninstall') {
  const ids = list(values.only).length ? list(values.only) : Object.keys(state);
  uninstall(ids.reverse());
} else if (cmd === 'status') {
  for (const r of Object.values(state)) console.log(`${r.id.padEnd(26)} ${String(r.version ?? '').padEnd(14)} ${r.at || 'incomplete (failed install)'}  ${r.files.length} file(s)`);
} else {
  console.error('usage: node tools/setup/install-fixes.ts plan|install|uninstall|status [--set default|all] [--only ids] [--skip ids]');
  process.exit(2);
}
