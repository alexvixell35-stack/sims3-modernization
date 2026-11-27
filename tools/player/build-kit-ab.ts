// Assembles a tester kit with the A/B mode (docs/314) from the repo's kit sources and pinned game-copy builds:
// tools/player/ts3-kit (TS3Kit.bat, tools/TS3Kit.ps1, READMEs, payload/S3SS-tunings.toml) + the kit's asi / ini / ts3fixes
// packages (sha256 from docs/data/builds.json, checked) + payload/ab/arms.json, then zips it. Windows / Ubuntu / macOS
// (zip through PowerShell's Compress-Archive on Windows, `zip` elsewhere).
//   node tools/player/build-kit-ab.ts k4|k4s|e7b --out <dir> [--bench-agent <ts3agent build>]   (writes <dir>/<kit folder> and its .<suffix>.zip)
// The benchmark runner (docs/315: Benchmark.cmd, tools/TS3Bench.ps1/.cs; arms = payload/ab/arms.json) always ships; it runs only
// with --bench-agent (payload/bench/ts3agent.package, a builds.json ts3agent row in game-copy).
//   node tools/player/build-kit-ab.ts --self-check
import { createHash } from 'node:crypto';
import { execFileSync } from 'node:child_process';
import { copyFileSync, existsSync, mkdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { atlasKitProblem } from '../release/atlas-kit.ts';

const ROOT = resolve(import.meta.dirname, '..', '..');
const KIT = join(ROOT, 'tools', 'player', 'ts3-kit');
const GC = join(ROOT, 'game-copy');

type Arm = { label: string; build?: string; off?: true };
type Spec = {
  folder: string; suffix: string; version: string; versionNote: string; edition: string; exe: string; asiName: string;
  asi: string; ini: string; diagIni: string; arms: { A: Arm & { build: string }; B: Arm }; readmeDir: string;
};
// k4 arm A: commodmapfast ON when commodmap-fast-1121-correct passed (docs/313 §5), else the shadow check
export const SPECS: Record<string, Spec> = {
  k4: {
    folder: 'TS3Kit-retail167-coremod-1121', suffix: 'k4', version: '1121.k4',
    versionNote: 'k4: A/B mode (docs/314): arm A commodmapfast ON, arm B OFF, switched per game session by option 6, asi / inis as k3, ts3fixes master m1121 + kit lines; k3: k2 + ts3fixes commodmapshadow ON',
    edition: 'retail 1.67', exe: 'TS3W.exe', asiName: 'ts3native-retail167.asi',
    asi: 'ts3native-retail167.asi.mkit2-1121', ini: 'ts3native-retail167.ini.k2normal-1121', diagIni: 'ts3native-retail167.ini.k2diag-1121',
    arms: { A: { label: "commodmapfast ON (our faster object-list update)", build: 'ts3fixes.package.kit4a-1121' }, B: { label: "commodmapfast OFF (game's own code)", build: 'ts3fixes.package.kit4b-1121' } },
    readmeDir: join(KIT, 'kits', 'k4'),
  },
  // k4 with the shadow check in arm A: the kit to send while commodmap-fast-1121-correct job 2 (many-mods) has not passed
  k4s: {
    folder: 'TS3Kit-retail167-coremod-1121', suffix: 'k4s', version: '1121.k4s',
    versionNote: 'k4s: A/B mode (docs/314): arm A commodmapshadow ON (the game keeps its own result, our faster path is checked against it and mismatches logged), arm B OFF, switched per game session by option 6, asi / inis as k3, ts3fixes master m1121 + kit lines',
    edition: 'retail 1.67', exe: 'TS3W.exe', asiName: 'ts3native-retail167.asi',
    asi: 'ts3native-retail167.asi.mkit2-1121', ini: 'ts3native-retail167.ini.k2normal-1121', diagIni: 'ts3native-retail167.ini.k2diag-1121',
    arms: { A: { label: "commodmapshadow ON (check only; game's own result kept)", build: 'ts3fixes.package.kit4s-1121' }, B: { label: "commodmapfast OFF (game's own code)", build: 'ts3fixes.package.kit4b-1121' } },
    readmeDir: join(KIT, 'kits', 'k4s'),
  },
  e7b: {
    folder: 'TS3Kit-ea169-diag-1121', suffix: 'e7b', version: '1121.e7b',
    versionNote: 'e7b: A/B mode (docs/314): arm A our mod ON (e6 asi / inis, ts3fixes master m1121 with swapinvokediag ON + kit lines), arm B our mod OFF (ts3native.asi and ts3fixes.package moved into the kit folder), switched per game session by option 6; e7: ts3fixes with swapinvokediag + PR #616, Turbo.asi moved into the backup',
    edition: 'EA app 1.69', exe: 'TS3.exe', asiName: 'ts3native-ea169.asi',
    asi: 'ts3native-ea169.asi.eaooa1-1120', ini: 'ts3native-ea169.ini.e6normal-1120', diagIni: 'ts3native-ea169.ini.e6diag-1120',
    arms: { A: { label: 'our mod ON (ts3native + ts3fixes; CAS error logging on)', build: 'ts3fixes.package.kit7a-1121' }, B: { label: 'our mod OFF (ts3native and ts3fixes not loaded)', off: true } },
    readmeDir: KIT,
  },
};

const sha = (p: string) => createHash('sha256').update(readFileSync(p)).digest('hex');
function pinned(name: string): string {
  const rows = readFileSync(join(ROOT, 'docs', 'data', 'builds.json'), 'utf8').split('\n').filter(l => l.includes(`"name":"${name}"`));
  const m = rows.length === 1 ? rows[0].match(/"sha256":"([0-9a-f]{64})"/) : null;
  if (!m) throw new Error(`builds.json: ${rows.length} rows for ${name}`);
  const p = join(GC, name);
  if (!existsSync(p) || sha(p) !== m[1]) throw new Error(`game-copy/${name} missing or not the builds.json sha ${m[1].slice(0, 12)}`);
  return p;
}
// the kit constants in TS3Kit.ps1: version, edition / exe / asi, the payload shas the installer checks
export function retarget(ps1: string, s: Spec, asiSha: string, iniSha: string, pkgSha: string): string {
  const rules: [RegExp, string][] = [
    [/^\$KitVersion = '[^']*'.*$/m, `$KitVersion = '${s.version}'  # ${s.versionNote}`],
    [/^\$KitEdition = '[^']*'; \$KitExe = '[^']*'; \$KitAsi = '[^']*'$/m, `$KitEdition = '${s.edition}'; $KitExe = '${s.exe}'; $KitAsi = '${s.asiName}'`],
    [/^\$RcAsiSha = '[0-9a-f]{64}'$/m, `$RcAsiSha = '${asiSha}'`],
    [/^\$RcIniSha = '[0-9a-f]{64}'$/m, `$RcIniSha = '${iniSha}'`],
    [/^\$RcPkgSha = '[0-9a-f]{64}'$/m, `$RcPkgSha = '${pkgSha}'`],
  ];
  for (const [re, to] of rules) { if (!re.test(ps1)) throw new Error(`TS3Kit.ps1: no line for ${re}`); ps1 = ps1.replace(re, () => to); }
  return ps1;
}

function build(name: string, outDir: string, benchAgent = '') {
  const s = SPECS[name]; if (!s) throw new Error(`unknown kit ${name} (k4, k4s, e7b)`);
  const dir = join(outDir, s.folder);
  rmSync(dir, { recursive: true, force: true });
  const put = (src: string, rel: string) => { const d = join(dir, rel); mkdirSync(dirname(d), { recursive: true }); copyFileSync(src, d); };
  const asi = pinned(s.asi), ini = pinned(s.ini), diagIni = pinned(s.diagIni), pkgA = pinned(s.arms.A.build);
  put(join(KIT, 'TS3Kit.bat'), 'TS3Kit.bat');
  put(join(KIT, 'Benchmark.cmd'), 'Benchmark.cmd');
  for (const f of ['TS3Bench.ps1', 'TS3Bench.cs']) put(join(KIT, 'tools', f), `tools/${f}`);
  if (benchAgent) put(pinned(benchAgent), 'payload/bench/ts3agent.package');
  put(join(KIT, 'payload', 'S3SS-tunings.toml'), 'payload/S3SS-tunings.toml');
  for (const r of ['README.txt', 'README-diag.txt']) put(join(s.readmeDir, r), r);
  put(asi, `payload/${s.asiName}`); put(ini, 'payload/ts3native.ini'); put(pkgA, 'payload/ts3fixes.package');
  put(asi, `payload/diag/${s.asiName}`); put(diagIni, 'payload/diag/ts3native.ini'); put(pkgA, 'payload/diag/ts3fixes.package');
  const arms: Record<string, unknown> = { A: { label: s.arms.A.label, package: 'ts3fixes.package', packageSha: sha(pkgA) } };
  if (s.arms.B.off) arms.B = { label: s.arms.B.label, off: true };
  else { const pkgB = pinned(s.arms.B.build!); put(pkgB, 'payload/ab/B-ts3fixes.package'); arms.B = { label: s.arms.B.label, package: 'ab\\B-ts3fixes.package', packageSha: sha(pkgB) }; }
  mkdirSync(join(dir, 'payload', 'ab'), { recursive: true });
  writeFileSync(join(dir, 'payload', 'ab', 'arms.json'), JSON.stringify({ kit: s.version, arms }, null, 2) + '\n');
  const ps1 = retarget(readFileSync(join(KIT, 'tools', 'TS3Kit.ps1'), 'utf8'), s, sha(asi), sha(ini), sha(pkgA));
  mkdirSync(join(dir, 'tools'), { recursive: true });
  writeFileSync(join(dir, 'tools', 'TS3Kit.ps1'), ps1);
  const zip = join(outDir, `${s.folder}.${s.suffix}.zip`);
  rmSync(zip, { force: true });
  if (process.platform === 'win32') execFileSync('powershell.exe', ['-NoProfile', '-Command', `Compress-Archive -Path '${dir}' -DestinationPath '${zip}'`], { stdio: 'inherit' });
  else execFileSync('zip', ['-qr', zip, s.folder], { cwd: outDir, stdio: 'inherit' });
  console.log(`${name}: ${dir}\n${zip} sha256 ${sha(zip)}`);
}

function selfCheck() {
  const ps1 = "$KitVersion = '1121.e7'  # e7: x\n# Kit target\n$KitEdition = 'EA app 1.69'; $KitExe = 'TS3.exe'; $KitAsi = 'ts3native-ea169.asi'\n$RcAsiSha = '" + 'a'.repeat(64) + "'\n$RcIniSha = '" + 'b'.repeat(64) + "'\n$RcPkgSha = '" + 'c'.repeat(64) + "'\n";
  const out = retarget(ps1, SPECS.k4, '1'.repeat(64), '2'.repeat(64), '3'.repeat(64));
  const ok = out.includes("$KitVersion = '1121.k4'  # k4:") && out.includes("$KitEdition = 'retail 1.67'; $KitExe = 'TS3W.exe'; $KitAsi = 'ts3native-retail167.asi'")
    && out.includes(`$RcAsiSha = '${'1'.repeat(64)}'`) && out.includes(`$RcPkgSha = '${'3'.repeat(64)}'`) && out.includes('# Kit target');
  let threw = false; try { retarget('$KitVersion = 1', SPECS.k4, '', '', ''); } catch { threw = true; }
  const labels = Object.values(SPECS).flatMap(s => [s.arms.A.label, s.arms.B.label]);
  // the labels go through build-fixes.ts --on (comma-separated) and the TNS text: no comma, ASCII only
  const clean = labels.every(l => !l.includes(',') && /^[\x20-\x7e]+$/.test(l));
  const atlas = atlasKitProblem(readFileSync(import.meta.filename, 'utf8')); // a kit that stages Atlas writes [Kit] Release=1
  console.log(ok && threw && clean && !atlas ? 'build-kit-ab self-check: 0 FAIL' : `build-kit-ab self-check: FAIL (retarget ${ok}, refuses ${threw}, labels ${clean}${atlas ? `, ${atlas}` : ''})`);
  if (!(ok && threw && clean && !atlas)) process.exit(1);
}

const a = process.argv.slice(2);
if (a[0] === '--self-check') selfCheck();
else {
  const o = a.indexOf('--out');
  if (!a[0] || o < 0 || !a[o + 1]) { console.error('usage: node tools/player/build-kit-ab.ts k4|k4s|e7b --out <dir> [--bench-agent <ts3agent build>] | --self-check'); process.exit(2); }
  const b = a.indexOf('--bench-agent');
  build(a[0], resolve(a[o + 1]), b >= 0 ? a[b + 1] ?? '' : '');
}
