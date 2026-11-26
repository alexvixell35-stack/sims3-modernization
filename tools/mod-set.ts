// Named Mods sets for the many-mods jobs (docs/302 §6.2): builds a flat folder of mod packages from a set manifest
// (docs/data/<set>.json), outside the game, for the queue key modSet (tools/harness/queue.ts) to move in and out of
// Mods/Packages/mod-set. Sources are local only: game-copy/compat-mods (MTS downloads, docs/data/compat-matrix.csv) and the
// drop folder game-copy/mod-sets/downloads (what Rodney fetched by hand from the official page: NRaas sits behind a browser
// check and asks for no automated downloads). Never downloads, never re-hosts, never S3MM.
//   node tools/mod-set.ts build docs/data/many-mods-s.json [--root <checkout>]   (re)build the set dir (sources and out under the
//        checkout the queues run from, default this one); refused while the set is installed
//   node tools/mod-set.ts status docs/data/many-mods-s.json    what is in, what waits for a download, what is left out
//   node tools/mod-set.ts --self-check
// Manifest: { set, out, sources: { <name>: dir }, mods: [{ name, page, version, status, from?, files?, why? }] }
//   status include = in the set; in-test-install = already in the test Mods (not copied: modSet refuses a second copy);
//   needs-download = Rodney fetches it from `page` into the drop folder, then it is in the set; excluded = left out (`why`).
//   files (from a source dir): "<dir>/<x>.package" copied; "<dir>/<x>.zip" every .package inside; "<dir>/<x>.zip!<name>.package"
//   that one. from "downloads": a name prefix; the newest file named <prefix>_* or <prefix>.* (.zip or .package) is used.
// Output: <out>/*.package (flat; Resource.cfg loads Mods/Packages/*/ subfolders) + <out>/set.json (file, mod, bytes, sha256).
import { execFileSync, spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { copyFileSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, renameSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { basename, join, normalize, resolve } from 'node:path';

const ROOT = resolve(import.meta.dirname, '..');
export const MOD_SET_RE = /^game-copy\/mod-sets\/[\w.-]+$/;
// the ts3fixes world-load line (docs/188 §2) in ts3native.log
export const ZIP_PICK_RE = /^(.+?\.zip)!([^!\\/]+\.package)$/i;
export const SCRIPT_ASM_RE = /^ts3fixes: script assemblies \(\d+\): /;
type Mod = { name: string; page: string; version: string; status: 'include' | 'in-test-install' | 'needs-download' | 'excluded'; from?: string; files?: string[]; why?: string };
type Manifest = { set: string; out: string; sources: Record<string, string>; mods: Mod[] };
type SetFile = { file: string; mod: string; bytes: number; sha256: string };

const sha = (p: string) => createHash('sha256').update(readFileSync(p)).digest('hex');
// shortcut: zip extraction needs bsdtar (Windows 10+ System32\tar.exe, macOS tar; Linux: libarchive-tools bsdtar); rar/7z are read
// from the .unpacked folders next to them in compat-mods
const TAR = process.platform === 'win32' ? join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'tar.exe') : process.platform === 'darwin' ? 'tar' : 'bsdtar';
// GNU tar cannot read or write zip: only a libarchive tar counts (the self-check skips its zip cases without one)
const ZIP_TAR_RE = /bsdtar|libarchive/i;
const hasZipTar = () => { const r = spawnSync(TAR, ['--version'], { encoding: 'utf8', timeout: 10000 }); return r.status === 0 && ZIP_TAR_RE.test(r.stdout ?? ''); };
const packagesIn = (dir: string): string[] => readdirSync(dir, { recursive: true, withFileTypes: true })
  .filter(e => e.isFile() && e.name.toLowerCase().endsWith('.package')).map(e => join(e.parentPath, e.name));

// set files whose name is already under Mods/Packages outside the set's own folder (a second copy of one mod loads twice)
export function modClash(pkgs: string, files: string[], own: string): string[] {
  if (!existsSync(pkgs)) return [];
  const o = normalize(own).toLowerCase();
  const have = new Set(readdirSync(pkgs, { recursive: true, withFileTypes: true })
    .filter(e => e.isFile() && !normalize(e.parentPath).toLowerCase().startsWith(o)).map(e => e.name.toLowerCase()));
  return files.filter(f => have.has(basename(f).toLowerCase()));
}

export const scriptAsmLine = (log: string) => log.split(/\r?\n/).find(l => SCRIPT_ASM_RE.test(l)) ?? '';

// job inventory text (queue.ts writes it into the job's -modset.txt)
export function inventory(set: string, dir: string, files: string[], scan: string): string {
  const meta = existsSync(join(dir, 'set.json')) ? JSON.parse(readFileSync(join(dir, 'set.json'), 'utf8')) as { built?: string; files: SetFile[] } : { files: [] };
  meta.files ??= []; // queue.ts modSetProblem refuses such a set first; this keeps the inventory readable
  const by = new Map(meta.files.map(f => [f.file.toLowerCase(), f]));
  const rows = files.map(f => { const m = by.get(f.toLowerCase()); return `${f}\t${m?.mod ?? '?'}\t${m?.bytes ?? '?'}\t${m?.sha256.slice(0, 8) ?? '?'}`; });
  return [`modSet ${set}: ${files.length} packages (set.json built ${meta.built ?? '?'})`, 'file\tmod\tbytes\tsha256', ...rows, '',
    'offload-override-scan.ts --summary (live Mods, set installed):', scan.trim(), ''].join('\n');
}

function resolveMod(m: Mod, mf: Manifest, tmp: string, root: string): { files: string[]; missing: string[] } {
  if (!m.from || !mf.sources[m.from]) throw new Error(`${m.name}: from "${m.from}" is not in sources`);
  const src = resolve(root, mf.sources[m.from]);
  const files: string[] = [], missing: string[] = [];
  for (const spec of m.files ?? []) {
    let path = '', pick = '';
    if (m.from === 'downloads') {
      const pre = spec.toLowerCase();
      const hits = existsSync(src) ? readdirSync(src).filter(f => { const l = f.toLowerCase(); return l.startsWith(pre) && /^[_.]/.test(l.slice(pre.length)) && /\.(zip|package)$/.test(l); })
        .sort((a, b) => statSync(join(src, b)).mtimeMs - statSync(join(src, a)).mtimeMs) : [];
      if (!hits.length) { missing.push(spec); continue; }
      path = join(src, hits[0]);
    } else {
      const zp = ZIP_PICK_RE.exec(spec); // folder names may hold '!' ("!!_install_one_package_only_!!"): only <x>.zip!<name> picks
      [path, pick] = zp ? [zp[1], zp[2]] : [spec, ''];
      path = join(src, path);
      if (!existsSync(path)) { missing.push(spec); continue; }
    }
    if (!/\.zip$/i.test(path)) { if (/\.package$/i.test(path)) files.push(path); else missing.push(`${spec} (not a .package or .zip)`); continue; }
    const x = mkdtempSync(join(tmp, 'x-'));
    execFileSync(TAR, ['-xf', path, '-C', x], { stdio: 'pipe', timeout: 120000 });
    const inner = packagesIn(x).filter(p => !pick || basename(p).toLowerCase() === pick.toLowerCase());
    if (!inner.length) missing.push(`${spec} (no ${pick || '.package'} inside)`);
    files.push(...inner);
  }
  return { files, missing };
}

export function build(mf: Manifest, opts: { installed?: string; root?: string } = {}): { out: string; files: SetFile[]; waiting: string[]; problems: string[] } {
  if (!MOD_SET_RE.test(mf.out)) throw new Error(`out must be game-copy/mod-sets/<set>, is ${mf.out}`);
  if (opts.installed && existsSync(join(opts.installed, 'manifest.json'))) throw new Error(`a mod set is installed (${opts.installed}\\manifest.json): run node tools/harness/queue.ts --restore-content first`);
  const root = opts.root ?? ROOT, out = resolve(root, mf.out), next = `${out}.next`, tmp = mkdtempSync(join(tmpdir(), 'mod-set-'));
  const files: SetFile[] = [], waiting: string[] = [], problems: string[] = [];
  try {
    rmSync(next, { recursive: true, force: true }); mkdirSync(next, { recursive: true });
    for (const m of mf.mods.filter(x => x.status === 'include' || x.status === 'needs-download')) {
      const r = resolveMod(m, mf, tmp, root);
      if (r.missing.length) { (m.status === 'needs-download' ? waiting : problems).push(`${m.name}: ${r.missing.join(', ')}${m.status === 'needs-download' ? ` (${m.page})` : ''}`); if (m.status === 'needs-download') continue; }
      for (const p of r.files) {
        const name = basename(p), dup = files.find(f => f.file.toLowerCase() === name.toLowerCase());
        if (dup) { problems.push(`${name}: in ${dup.mod} and ${m.name}`); continue; }
        copyFileSync(p, join(next, name));
        files.push({ file: name, mod: m.name, bytes: statSync(p).size, sha256: sha(join(next, name)) });
      }
    }
    writeFileSync(join(next, 'set.json'), JSON.stringify({ set: mf.set, built: new Date().toISOString(), files, waiting, problems }, null, 1));
    if (problems.length) throw new Error(`set not built: ${problems.join('; ')}`);
    rmSync(out, { recursive: true, force: true }); renameSync(next, out);
    return { out, files, waiting, problems };
  } finally { rmSync(tmp, { recursive: true, force: true }); rmSync(next, { recursive: true, force: true }); }
}

if (import.meta.main) {
  const [cmd, arg] = process.argv.slice(2), ri = process.argv.indexOf('--root'), root = ri > 0 ? resolve(process.argv[ri + 1]) : ROOT;
  if (cmd === '--self-check') {
    const assert = (await import('node:assert')).strict;
    assert.ok(MOD_SET_RE.test('game-copy/mod-sets/many-mods-s')); assert.ok(!MOD_SET_RE.test('game-copy/mod-sets/../x')); assert.ok(!MOD_SET_RE.test('game-copy/cc-synth/a'));
    // regexCases: real line (probe-out ts3native.log, 2026-11-20) and non-matches
    assert.ok(SCRIPT_ASM_RE.test('ts3fixes: script assemblies (15): StoreObjectsZoe  46796e9c, NRaasErrorTrap  40c77ec4'));
    assert.ok(!SCRIPT_ASM_RE.test('ts3fixes: settings on: kX, kY=4'));
    assert.ok(!SCRIPT_ASM_RE.test('  ts3fixes: script assemblies (15): x'));
    assert.deepEqual(ZIP_PICK_RE.exec('a b/Gardening Modifiers.zip!JJ_GardeningIsFun.package')?.slice(1), ['a b/Gardening Modifiers.zip', 'JJ_GardeningIsFun.package']);
    assert.equal(ZIP_PICK_RE.exec('579548-x/k.7z.unpacked/!!_install_one_package_only_!!/k.package'), null);
    assert.equal(ZIP_PICK_RE.exec('1-a/bc.zip'), null);
    assert.ok(ZIP_TAR_RE.test('bsdtar 3.8.9 - libarchive 3.8.9 zlib/1.2.13.1-motley liblzma/5.8.3 bz2lib/1.0.8'));
    assert.ok(!ZIP_TAR_RE.test('tar (GNU tar) 1.35'));
    assert.equal(scriptAsmLine('a\r\nts3fixes: script assemblies (2): A  1, B  2\r\nb'), 'ts3fixes: script assemblies (2): A  1, B  2');
    const t = mkdtempSync(join(tmpdir(), 'mod-set-check-'));
    const name = `selfcheck-${process.pid}`, out = `game-copy/mod-sets/${name}`; // under the temp root, never the checkout
    try {
      const comp = join(t, 'compat'), dl = join(t, 'dl'), pk = join(t, 'Packages');
      mkdirSync(join(comp, '1-a'), { recursive: true }); mkdirSync(dl); mkdirSync(join(pk, 'sub'), { recursive: true }); mkdirSync(join(pk, 'mod-set'));
      writeFileSync(join(comp, '1-a', 'A.package'), 'a');
      mkdirSync(join(t, 'z')); writeFileSync(join(t, 'z', 'B.package'), 'b'); writeFileSync(join(t, 'z', 'C.package'), 'c');
      if (hasZipTar()) {
      execFileSync(TAR, ['-a', '-cf', join(comp, '1-a', 'bc.zip'), '-C', join(t, 'z'), 'B.package', 'C.package']);
      writeFileSync(join(dl, 'NRaas_Thing_V12.package'), 'n'); writeFileSync(join(dl, 'NRaas_ThingElse_V1.package'), 'x');
      const mf: Manifest = { set: name, out, sources: { compat: comp, downloads: dl }, mods: [
        { name: 'A', page: 'p', version: '1', status: 'include', from: 'compat', files: ['1-a/A.package', '1-a/bc.zip!C.package'] },
        { name: 'NT', page: 'https://example.invalid/NT', version: '1', status: 'needs-download', from: 'downloads', files: ['NRaas_Thing'] },
        { name: 'W', page: 'https://example.invalid/W', version: '1', status: 'needs-download', from: 'downloads', files: ['NRaas_Waiting'] },
        { name: 'X', page: 'p', version: '1', status: 'excluded', why: 'conflict' }] };
      const r = build(mf, { root: t });
      assert.deepEqual(r.files.map(f => f.file).sort(), ['A.package', 'C.package', 'NRaas_Thing_V12.package'], 'zip pick, prefix match not ThingElse');
      assert.equal(r.waiting.length, 1); assert.match(r.waiting[0], /NRaas_Waiting \(https:\/\/example\.invalid\/W\)/);
      assert.ok(existsSync(join(t, out, 'set.json')));
      // whole zip, then a name clash between two mods refuses and keeps the old set
      mf.mods[0].files = ['1-a/bc.zip']; mf.mods.push({ name: 'A2', page: 'p', version: '1', status: 'include', from: 'compat', files: ['1-a/bc.zip!B.package'] });
      assert.throws(() => build(mf, { root: t }), /B\.package: in A and A2/);
      assert.ok(existsSync(join(t, out, 'A.package')), 'failed build keeps the previous set');
      mf.mods.pop(); mf.mods[0].files = ['1-a/missing.package'];
      assert.throws(() => build(mf, { root: t }), /A: 1-a\/missing\.package/, 'a missing include is a problem, not a wait');
      writeFileSync(join(pk, 'mod-set', 'manifest.json'), '{}');
      assert.throws(() => build(mf, { installed: join(pk, 'mod-set'), root: t }), /is installed/);
      const inv = inventory(name, join(t, out), ['A.package', 'Q.package'], '{"packages": 3}');
      assert.match(inv, /^modSet selfcheck-\d+: 2 packages/); assert.match(inv, /A\.package\tA\t1\t/); assert.match(inv, /Q\.package\t\?\t\?/); assert.match(inv, /"packages": 3/);
      } else console.log(`skip zip/build cases: no libarchive tar (${TAR})`);
      // clash: same name elsewhere under Packages counts, the set's own folder does not
      writeFileSync(join(pk, 'sub', 'a.PACKAGE'), 'x'); writeFileSync(join(pk, 'mod-set', 'C.package'), 'c');
      assert.deepEqual(modClash(pk, ['A.package', 'C.package'], join(pk, 'mod-set')), ['A.package']);
      assert.deepEqual(modClash(join(t, 'none'), ['A.package'], join(pk, 'mod-set')), []);
    } finally { rmSync(t, { recursive: true, force: true }); }
    console.log('self-check ok');
  } else if ((cmd === 'build' || cmd === 'status') && arg) {
    const mf = JSON.parse(readFileSync(resolve(arg), 'utf8')) as Manifest;
    const user = process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3');
    for (const s of ['include', 'in-test-install', 'needs-download', 'excluded'] as const) {
      const ms = mf.mods.filter(m => m.status === s);
      console.log(`${s} (${ms.length}): ${ms.map(m => m.name + (m.why ? ` [${m.why}]` : '')).join('; ')}`);
    }
    if (cmd === 'build') {
      const r = build(mf, { installed: join(user, 'Mods', 'Packages', 'mod-set'), root });
      console.log(`built ${r.out}: ${r.files.length} packages, ${(r.files.reduce((s, f) => s + f.bytes, 0) / 1e6).toFixed(1)} MB`);
      for (const w of r.waiting) console.log(`  waiting for a download: ${w}`);
    }
  } else { console.error('usage: node tools/mod-set.ts build|status <manifest.json> | --self-check'); process.exit(2); }
}
