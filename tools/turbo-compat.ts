// Turbo Simulation Engine V3.4 live compat check next to Apex Radiance + ours (docs/263 §4; queue turbo-apex-1114): Bin states,
// a thread/working-set sampler for the running game, per-arm log collection, and a hash-checked restore.
//   node tools/turbo-compat.ts snapshot <dir>      hash every Bin file, back up the files the states touch and Apex's Documents
//                                                  folder into <dir>; refuses when <dir>/snapshot.json exists or TS3W.exe runs
//   node tools/turbo-compat.ts state <dir> <name>  Bin = snapshot + that state's files (read back by sha256); names in STATES; every state but base, turbo, apex-stock
//                                                  is our fork build (the install-copy output for FORK.sha) and refuses while it is missing or stale
//   node tools/turbo-compat.ts monitor <dir> <arm> [--all-on] [--f7] [--max-min 40]
//        waits for TS3W.exe, samples its threads (base priority, start address -> ts3native.asi RVA) and working set every 5 s
//        for 2 min, then every 30 s, until it exits; --all-on writes the all-on Turbo.txt once Turbo has rewritten its file
//        after the start (retail mode: the 2 s reload applies it, docs/263 §1.1-1.2); --f7 posts F7 down/up to the game window
//        at +6 and +7 min (PostMessage: never focuses the window). Output <dir>/<arm>/monitor.jsonl + monitor.txt.
//   node tools/turbo-compat.ts collect <dir> <arm> copy Turbo.txt, imgui.ini, bridge + Apex logs (Apex log from the state switch on)
//                                                  into <dir>/<arm>/ and grep the lines that matter into summary.txt
//   node tools/turbo-compat.ts restore <dir>       state base, Apex Documents folder back, Bin files not in the snapshot removed
//                                                  (known Apex/bridge/Turbo outputs only), whole-Bin sha256 compare; exit 1 on a diff
//   node tools/turbo-compat.ts install-copy [--build]  rewind arm (docs/270 §12): game-copy/rewind-bridge-1119/install = Apex release
//                                                  files + our fork's FORK branch (pair, conf, asi), rewindMode = True; state
//                                                  apex-rewind uses it; --build first checks out FORK.sha, rebuilds + tests the pair on one CPU
//                                                  and, when no green CI build of FORK.sha has the public asi, builds that asi locally
//   node tools/turbo-compat.ts --self-check
// TURBO_COMPAT_DRY=1 with TS3_BIN/TS3_USER on copies: dry run of snapshot/state/restore while a game runs.
// Windows-only by nature (game Bin, 32-bit Windows PowerShell for the WOW64 thread walk). Turbo files never come from the web
// here: game-copy/turbo-3.4 (engine rar only, sha256 pinned below). Apex: the latest GitHub release's Manual-install zip (asi + RTX Remix bridge), fetched at snapshot into game-copy/apex-<ver>.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { appendFileSync, copyFileSync, cpSync, existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, realpathSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { homedir, tmpdir } from 'node:os';
import { dirname, join, resolve } from 'node:path';
import { parseArgs } from 'node:util';
import { BIN, ensureVramSidecar, s3mmFiles } from './harness/game-exe.ts';
import { extraMarker, extraOwned, extraSweep, iniBackupRestore, LEGACY_MARKER } from './harness/extra-asi.ts';

const ROOT = resolve(import.meta.dirname, '..');
const USER = process.env.TS3_USER ?? join(homedir(), 'Documents', 'Electronic Arts', 'The Sims 3');
const APEX_DOCS = join(USER, 'Apex Radiance');
const GC = join(ROOT, 'game-copy');
const SRC = {
  turbo: { path: join(GC, 'turbo-3.4', 'Turbo.asi'), sha: '74f971bcaf1cc2c8ad9c9ec4af5f05a280a938bedd6d80cb9867aded5bae4326' },
  ual77: { path: join(GC, 'turbo-3.4', 'wininet.dll'), sha: '29fee0aa46414864ad1f65b2689b470bfb2cf2f09a94e2afd271e68f2e16743b' },
};
// Apex: always the latest GitHub release (Rodney 11-14), its "Manual install" zip, sha256 = the release asset digest
const APEX_REPO = 'loinyx/Sims3-ApexRadiance';
// files of the zip's "RTX Remix bridge" folder (= Bin relative paths, docs/259 §7); a file outside this list stops the fetch
// (restore would not know it)
const BRIDGE = ['d3d9.dll', 'bridge.conf', 'dxvk.conf', '.trex/NvRemixBridge.exe', '.trex/d3d9vk_x64.dll', '.trex/bridge.conf', 'Apex Radiance RTX Remix bridge.txt'];
// SlimMain / UdHeaderMain RVAs in ts3native.asi rc1111g = rel0212 (game-copy/ts3native.map.rc1111g, base 0x10000000)
const OUR_THREADS: Record<number, string> = { 0xAAE30: 'SlimMain (saveslim.cpp:592)', 0xB9230: 'UdHeaderMain (userdiag.cpp:559)' };
export const ALL_ON = ['# TS3 Turbo Engine Configuration (all on, turbo-apex-1114 arm 5)', 'Turbo = 1000', 'Patch = 1', 'Delay = 0', 'QpsVsync = 0',
  'Janeladeborda = 1', 'MenuKey = 118', 'AutoClean = 1', 'ThreadPriorityBoost = 1', 'CpuTopologyOpt = 1', 'SmartRamManager = 1', 'PreFaultMemory = 1',
  'InstalacaoValida = 1'];

// docs/270 §12 + probe-out/apex-latest-switch-1119.md: the test install (Bin state apex-rewind) = the Apex release files of
// REWIND_BUILT_ON, with the bridge pair, bridge/dxvk conf and public asi of our fork's integration branch (Apex main + our
// open bug-fix PRs; Rodney 11-19 "use our Apex fork so we don't crash"). Pair built locally in REWIND/apex-repo, asi = the CI
// artifact of that commit when one exists, else the Public asi built locally from that exact commit (the fork's CI only runs
// on PRs and main, so an integration merge has none). install-copy takes the latest release's files and refuses only when
// that release tag is not an ancestor of FORK.sha (git in REWIND/apex-repo); REWIND_BUILT_ON names the release the pin was
// last merged with (a newer merged release only logs). A new release not merged yet: merge it into the branch in the fork,
// update FORK.sha and REWIND_BUILT_ON, run install-copy --build.
const REWIND = join(GC, 'rewind-bridge-1119'), REWIND_BUILT_ON = 'v4.1.7';
export const FORK = { remote: 'https://github.com/rodneypantonial/Sims3-ApexRadiance.git', branch: 'ours/test-integration-1119', sha: 'bfafc850647da996f66e61eeefea20208e0e0def' };
const REWIND_UNZ = join(REWIND, 'install', 'apex-unz');
// Rodney 11-19 (restated 11-27): every game test runs on our fork's integration build, never stock Apex. The fork install
// (install-copy for FORK.sha) is ready when fork.json names FORK.sha and its asi, bridge pair and ts3wrap.exe still hash to the
// sha256 values it recorded; '' = ready, else why not (every state but STOCK_STATES then refuses: no fallback to stock)
export const FORK_FILES: [string, string][] = [['ApexRadiance.asi', 'asi'], ['d3d9.dll', 'd3d9'], ['.trex/NvRemixBridge.exe', 'server'], ['ts3wrap.exe', 'ts3wrap']];
export function forkInstallProblemOf(fj: Record<string, any> | null, forkSha: string, fileSha: (rel: string) => string | null): string {
  if (!fj) return 'no fork.json (install-copy never ran)';
  if (fj.sha !== forkSha) return `fork.json is for ${String(fj.sha).slice(0, 8)}, FORK.sha is ${forkSha.slice(0, 8)}`;
  const bad = FORK_FILES.filter(([rel, k]) => !fj[k] || fileSha(rel) !== fj[k]).map(([rel, k]) => fj[k] ? `${rel} changed` : `no ${k} sha256 in fork.json (older install-copy)`);
  return bad.length ? bad.join(', ') : '';
}
export const forkInstallProblem = () => {
  let fj = null; try { fj = JSON.parse(readFileSync(join(REWIND, 'install', 'fork.json'), 'utf8')); } catch { /* no install copy */ }
  return forkInstallProblemOf(fj, FORK.sha, rel => existsSync(join(REWIND_UNZ, rel)) ? sha(join(REWIND_UNZ, rel)) : null);
};
// states that never need the fork install: no Apex, or stock Apex as an explicit opt-in (comparison, reporter repros)
export const STOCK_STATES = new Set(['base', 'turbo', 'apex-stock']);
// a run's <dir>/apex-unz = the fork install with rewindMode taken out of both bridge.conf (rewind is its own opt-in: apex-rewind)
export const noRewindMode = (conf: string) => conf.replace(/^[ \t]*rewindMode[ \t]*=.*(\r?\n|$)/gim, '');
function forkUnz(dir: string) {
  const why = forkInstallProblem();
  if (why) throw new Error(`fork install not ready for FORK.sha ${FORK.sha.slice(0, 8)} (${why}): run node tools/turbo-compat.ts install-copy (stock Apex only as state apex-stock)`);
  const unz = join(dir, 'apex-unz');
  rmSync(unz, { recursive: true, force: true }); cpSync(REWIND_UNZ, unz, { recursive: true });
  for (const c of ['bridge.conf', '.trex/bridge.conf']) if (existsSync(join(unz, c))) writeFileSync(join(unz, c), noRewindMode(readFileSync(join(unz, c), 'utf8')));
  copyFileSync(join(REWIND, 'install', 'fork.json'), join(dir, 'fork.json'));
  const fj = JSON.parse(readFileSync(join(dir, 'fork.json'), 'utf8'));
  for (const [rel, k] of FORK_FILES) if (sha(join(unz, rel)) !== fj[k]) throw new Error(`fork install copy: ${rel} read-back differs`);
}
type Src = string | null; // file to copy into Bin, null = absent
export const STATES: Record<string, (unz: string) => Record<string, Src>> = {
  base: () => ({}),
  // a run's <dir>/apex-unz: the fork install of FORK.sha (forkUnz, at every state switch) with its restart host ts3wrap.exe
  apex: unz => ({ 'ApexRadiance.asi': join(unz, 'ApexRadiance.asi'), ...Object.fromEntries(BRIDGE.filter(k => !unz || existsSync(join(unz, k))).map(k => [k, join(unz, k)])),
    ...(unz && existsSync(join(unz, 'ts3wrap.exe')) ? { 'ts3wrap.exe': join(unz, 'ts3wrap.exe') } : {}) }),
  // stock Apex (the latest release, fetched at snapshot into <dir>/apex-stock-unz): explicit opt-in only
  'apex-stock': unz => STATES.apex(join(dirname(unz), 'apex-stock-unz')),
  'apex-turbo': unz => ({ ...STATES.apex(unz), 'Turbo.asi': SRC.turbo.path }),
  'apex-turbo-ual77': unz => ({ ...STATES['apex-turbo'](unz), 'wininet.dll': SRC.ual77.path }),
  turbo: () => ({ 'Turbo.asi': SRC.turbo.path }),
  // docs/270 §12: the install copy with our patched bridge pair and rewindMode = True (made by `install-copy`)
  // apex + the install copy's bridge.conf files (rewindMode = True)
  'apex-rewind': unz => ({ ...STATES.apex(unz), 'bridge.conf': join(REWIND_UNZ, 'bridge.conf'), '.trex/bridge.conf': join(REWIND_UNZ, '.trex', 'bridge.conf') }),
  // docs/305: the latest Apex release with a fork build's ApexRadiance.asi and ts3wrap.exe (the restart host that runs the
  // out-of-process services); a chain copies the build under test into game-copy/apex-oop-test first
  // docs/309 §2: the latest Apex release with the fork build of feature/async-rt-reads-1120 (asi + bridge patches 0017/0018:
  // d3d9.dll, .trex/NvRemixBridge.exe, bridge.conf with client.asyncReadback); a chain copies them into game-copy/apex-asyncrt-test
  'apex-asyncrt': unz => ({ ...STATES.apex(unz), ...Object.fromEntries(['ApexRadiance.asi', 'd3d9.dll', '.trex/NvRemixBridge.exe', 'bridge.conf'].map(k => [k, join(GC, 'apex-asyncrt-test', k)])) }),
  'apex-oop': unz => ({ ...STATES.apex(unz), 'ApexRadiance.asi': join(GC, 'apex-oop-test', 'ApexRadiance.asi'), 'ts3wrap.exe': join(GC, 'apex-oop-test', 'ts3wrap.exe') }),
  // boundless-atlas docs/025: the fork build (apex: asi + ts3wrap.exe with the 64-bit plugin host #158) plus the Atlas region
  // store plugin, which the host loads from its own folder only (the chain stages it in game-copy/apex-oop-test)
  'apex-oop-atlas': unz => ({ ...STATES.apex(unz), 'atlas-oop.dll': join(GC, 'apex-oop-test', 'atlas-oop.dll') }),
  // docs/260 §9: the latest Apex release with one fork build's ApexRadiance.asi (an Apex bug fix under test); a chain copies the
  // build under test into game-copy/apex-fork-asi-test first
  'apex-fork-asi': unz => ({ ...STATES.apex(unz), 'ApexRadiance.asi': join(GC, 'apex-fork-asi-test', 'ApexRadiance.asi') }),
};
// every path a state or a run can change; backed up at snapshot when present, reset to the snapshot before each state
export const MANAGED = [...new Set([...Object.keys(STATES['apex-turbo-ual77']('')), 'ts3wrap.exe', 'atlas-oop.dll', 'Turbo.txt', 'imgui.ini', 'ts3native.asi', 'ts3native.ini'])];
// Bin files absent from the snapshot that restore may delete (run outputs of Apex, the bridge, Turbo, the game)
export const EXTRA_OK = /^(rtx-remix\/|\.trex\/|NvRemixBridge[^/]*$|bridge\.conf$|Turbo\.(asi|txt)$|ApexRadiance[^/]*$|ts3wrap\.exe$|atlas-oop\.dll$|Apex Radiance RTX Remix bridge\.txt$|imgui\.ini$|pccheck(-summary)?\.(txt|csv)$|[^/]*\.(log|jsonl|dmp)$)/i;
// runtime outputs, changed by every run: game and mod logs, the bridge logs, the pccheck report (the sha check stays strict for every other file)
const IGNORE_DIFF = /^([^/]*\.(log|jsonl|dmp)|rtx-remix\/logs\/[^/]+\.log|pccheck(-summary)?\.(txt|csv))$/i;
// docs/284: the DxDiagCache sidecar is a per-machine cache, kept across states and restores (never compared, never removed)
export const KEEP_CACHE = /^ts3native-vram\.ini$/i;

const sha = (p: string) => createHash('sha256').update(readFileSync(p)).digest('hex');
const now = () => new Date(Date.now() + 8 * 3600e3).toISOString().slice(11, 19) + ' PHT';
const say = (s: string) => console.log(`${now()} ${s}`);
const sleep = (ms: number) => Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, ms);
const walk = (d: string, pre = ''): string[] => readdirSync(d, { withFileTypes: true }).flatMap(e =>
  e.isDirectory() ? walk(join(d, e.name), `${pre}${e.name}/`) : [`${pre}${e.name}`]);
const hashTree = (d: string) => Object.fromEntries(walk(d).map(f => [f, sha(join(d, f))]));
const DRY = process.env.TURBO_COMPAT_DRY === '1'; // test runs on a fake TS3_BIN/TS3_USER while a game runs: skip the "TS3W.exe runs" guard
const gamePids = (): number[] => {
  const out = execFileSync('tasklist', ['/FI', 'IMAGENAME eq TS3W.exe', '/FO', 'CSV', '/NH'], { encoding: 'utf8' });
  return [...out.matchAll(/^"TS3W\.exe","(\d+)"/gim)].map(m => Number(m[1]));
};
function verify(s: { path: string; sha: string }) {
  if (!existsSync(s.path)) throw new Error(`missing ${s.path}`);
  const h = sha(s.path);
  if (h !== s.sha) throw new Error(`${s.path}: sha256 ${h.slice(0, 12)}, expected ${s.sha.slice(0, 12)}`);
}

// ------------------------------------------------------------------------------------------------ latest Apex
export const pickZip = (assets: { name: string; digest?: string; browser_download_url: string }[]) =>
  assets.find(a => /Manual-install\.zip$/i.test(a.name) && /^sha256:[0-9a-f]{64}$/.test(a.digest ?? ''));

// latest release -> game-copy/apex-<ver>/<zip> (downloaded once, sha256 = the asset digest) -> <dir>/apex-unz with
// ApexRadiance.asi + the bridge files at their Bin paths. No network: the newest game-copy/apex-*/apex.json zip, with a WARN.
async function fetchApex(dir: string, name = 'apex-unz') {
  let pin: { tag: string; zip: string; sha: string; from: string } | undefined;
  try {
    const r = await fetch(`https://api.github.com/repos/${APEX_REPO}/releases/latest`, { headers: { 'User-Agent': 'turbo-compat', Accept: 'application/vnd.github+json' }, signal: AbortSignal.timeout(30000) });
    if (!r.ok) throw new Error(`GitHub API ${r.status}`);
    const rel = await r.json(), a = pickZip(rel.assets ?? []);
    if (!a) throw new Error(`release ${rel.tag_name}: no "Manual-install.zip" asset with a sha256 digest`);
    const gd = join(GC, `apex-${String(rel.tag_name).replace(/^v/, '')}`), zip = join(gd, a.name), want = a.digest!.slice(7);
    mkdirSync(gd, { recursive: true });
    if (!existsSync(zip) || sha(zip) !== want) {
      say(`apex: downloading ${a.name} (${rel.tag_name})`);
      const d = await fetch(a.browser_download_url, { headers: { 'User-Agent': 'turbo-compat' }, signal: AbortSignal.timeout(300000) });
      if (!d.ok) throw new Error(`download ${d.status}`);
      writeFileSync(zip, Buffer.from(await d.arrayBuffer()));
    }
    pin = { tag: rel.tag_name, zip, sha: want, from: 'GitHub latest' };
    writeFileSync(join(gd, 'apex.json'), JSON.stringify(pin, null, 1));
  } catch (e) {
    const known = existsSync(GC) ? readdirSync(GC).filter(d => /^apex-\d/.test(d) && existsSync(join(GC, d, 'apex.json'))).map(d => JSON.parse(readFileSync(join(GC, d, 'apex.json'), 'utf8')))
      .sort((x, y) => y.tag.localeCompare(x.tag, undefined, { numeric: true })) : [];
    if (!known.length) throw new Error(`apex: latest release not reachable (${(e as Error).message}) and no earlier fetch in game-copy`);
    pin = { ...known[0], from: `WARN: GitHub not reachable (${(e as Error).message}): newest earlier fetch` };
  }
  verify({ path: pin.zip, sha: pin.sha });
  const tmp = join(dir, 'apex-zip'), unz = join(dir, name);
  for (const d of [tmp, unz]) { rmSync(d, { recursive: true, force: true }); mkdirSync(d, { recursive: true }); }
  // Windows bsdtar reads zip; Git Bash's GNU tar would take "C:" for a remote host
  execFileSync(join(process.env.SystemRoot ?? 'C:\\Windows', 'System32', 'tar.exe'), ['-xf', pin.zip, '-C', tmp]);
  const files = walk(tmp), asi = files.find(f => /(^|\/)ApexRadiance\.asi$/i.test(f));
  const bdir = files.map(f => /^(.*?RTX Remix bridge\/)/i.exec(f)?.[1]).find(Boolean);
  if (!asi || !bdir) throw new Error(`apex zip ${pin.zip}: no ApexRadiance.asi or "RTX Remix bridge" folder`);
  copyFileSync(join(tmp, asi), join(unz, 'ApexRadiance.asi'));
  const bfiles = files.filter(f => f.startsWith(bdir)).map(f => f.slice(bdir.length));
  const unknown = bfiles.filter(f => !BRIDGE.includes(f)), missing = BRIDGE.filter(f => !bfiles.includes(f) && f !== 'Apex Radiance RTX Remix bridge.txt');
  if (unknown.length || missing.length) throw new Error(`apex ${pin.tag} bridge folder changed: new ${unknown.join(', ') || '-'}, missing ${missing.join(', ') || '-'} (update BRIDGE)`);
  for (const f of bfiles) { mkdirSync(dirname(join(unz, f)), { recursive: true }); copyFileSync(join(tmp, bdir + f), join(unz, f)); }
  rmSync(tmp, { recursive: true, force: true });
  if (!readFileSync(join(unz, 'bridge.conf'), 'utf8').includes('TS3W.exe')) say('apex: WARN bridge.conf does not name TS3W.exe (Steam needs client.targetProcess = TS3W.exe)');
  const out = { ...pin, asiSha: sha(join(unz, 'ApexRadiance.asi')), bridge: bfiles };
  writeFileSync(join(dir, 'apex.json'), JSON.stringify(out, null, 1));
  say(`apex ${pin.tag} (${pin.from}): zip sha256 ${pin.sha.slice(0, 12)} = release digest, ApexRadiance.asi ${out.asiSha.slice(0, 12)}, bridge ${bfiles.length} files; ts3wrap.exe not installed (optional restart host)`);
}

// ------------------------------------------------------------------------------------------------ snapshot / state / restore
// --build: clone/checkout FORK.sha in REWIND/apex-repo, fresh bridge source, build.cmd + test-rewind.cmd pinned to one CPU
// (a timing chain may run), log REWIND/build-<sha8>.log. Windows only (MSVC, cmd).
function buildFork() {
  const repo = join(REWIND, 'apex-repo'), rb = join(repo, 'native', 'remix-bridge'), venv = join(REWIND, 'venv', 'Scripts');
  const git = (...a: string[]) => execFileSync('git', ['-C', repo, ...a], { encoding: 'utf8', timeout: 300000 }).trim();
  if (!existsSync(join(repo, '.git'))) execFileSync('git', ['clone', '-q', '--filter=blob:limit=2m', '-b', FORK.branch, FORK.remote, repo], { timeout: 600000 });
  git('fetch', '-q', FORK.remote, FORK.branch);
  git('checkout', '-q', '--detach', FORK.sha);
  rmSync(join(rb, 'src'), { recursive: true, force: true }); // patches may have changed: refetch + apply
  if (!existsSync(join(venv, 'meson.exe'))) {
    execFileSync('python', ['-m', 'venv', join(REWIND, 'venv')], { timeout: 120000 });
    execFileSync(join(venv, 'python.exe'), ['-m', 'pip', 'install', '-q', '--disable-pip-version-check', 'meson', 'ninja'], { timeout: 300000 });
  }
  // the server test on DXVK needs Apex's DXVK next to the package; the release copy has it
  const dxvk = join(REWIND, 'install', 'apex-unz', '.trex', 'd3d9vk_x64.dll');
  if (existsSync(dxvk)) { mkdirSync(join(rb, 'out', 'package', '.trex'), { recursive: true }); copyFileSync(dxvk, join(rb, 'out', 'package', '.trex', 'd3d9vk_x64.dll')); }
  const log = join(REWIND, `build-${FORK.sha.slice(0, 8)}.log`);
  say(`build: ${FORK.branch} ${FORK.sha.slice(0, 8)} -> ${rb}\\out (one CPU, log ${log})`);
  // start /affinity 1: ninja's jobs share one core, so a running timing job keeps its CPUs. .\ because cmd does not look in
  // the current folder when NoDefaultCurrentDirectoryInExePath is set (agent shells set it): 'build.cmd' is not recognized
  execFileSync('cmd', ['/d', '/c', `start "" /wait /low /affinity 1 cmd /d /c "cd /d "${rb}" && set "PATH=${venv};%PATH%" && call .\\build.cmd && call .\\test-rewind.cmd > "${log}" 2>&1"`],
    { stdio: 'inherit', timeout: 3600000, windowsVerbatimArguments: true });
  if (!/rewind tests passed/.test(readFileSync(log, 'utf8'))) throw new Error(`build: offline rewind tests did not pass, see ${log}`);
  say('build: pair built, rewind tests passed');
}

// The CI's public asi of FORK.sha (Apex's "Windows build and CPU checks" artifact; upstream runs it on main, the fork on PRs
// and main), or null when neither repo has a green build of it. Never starts or waits on CI.
function ciAsi(): string | null {
  const d = join(REWIND, `asi-${FORK.sha.slice(0, 8)}`), f = join(d, `ApexRadiance-public-${FORK.sha}`, 'Public', 'ApexRadiance.asi');
  if (existsSync(f)) return f;
  const env = { ...process.env, ...(process.env.PERSONAL_GITHUB_TOKEN ? { GH_TOKEN: process.env.PERSONAL_GITHUB_TOKEN } : {}) };
  for (const r of [APEX_REPO, 'rodneypantonial/Sims3-ApexRadiance']) {
    let id = '';
    // a repo without the workflow on its default branch (the fork: HTTP 404) has no CI build: next repo, then the local build
    try { id = execFileSync('gh', ['run', 'list', '-R', r, '--commit', FORK.sha, '--workflow', 'build.yml', '--status', 'success', '-L', '1', '--json', 'databaseId', '--jq', '.[0].databaseId // ""'], { encoding: 'utf8', env, timeout: 60000, stdio: ['ignore', 'pipe', 'pipe'] }).trim(); }
    catch (e: any) { say(`asi: no CI build list for ${r} (${String(e.stderr ?? e).split('\n')[0].slice(0, 120)})`); }
    if (!id) continue;
    execFileSync('gh', ['run', 'download', id, '-R', r, '-n', `ApexRadiance-public-${FORK.sha}`, '-D', join(d, `ApexRadiance-public-${FORK.sha}`)], { env, timeout: 300000 });
    if (existsSync(f)) return f;
  }
  return null;
}

// The locally built Public asi of FORK.sha: REWIND/asi-<sha8>/local/ApexRadiance.asi + build.json { sha, asiSha }, made by
// buildLocalAsi from REWIND/apex-repo checked out at FORK.sha (by buildFork).
const localAsiDir = () => join(REWIND, `asi-${FORK.sha.slice(0, 8)}`, 'local');
function buildLocalAsi() {
  const repo = join(REWIND, 'apex-repo'), d = localAsiDir(), log = join(REWIND, `build-asi-${FORK.sha.slice(0, 8)}.log`);
  const head = execFileSync('git', ['-C', repo, 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
  if (head !== FORK.sha) throw new Error(`build: ${repo} is at ${head.slice(0, 8)}, not FORK.sha ${FORK.sha.slice(0, 8)}`);
  // the repo's own recipe (CONTRIBUTING.md): setup.ps1 once (pinned vcpkg deps), then build.ps1 -Flavor Public. Deps of
  // another Apex clone in game-copy with the same vcpkg baseline are linked (junction) instead of rebuilt on one CPU.
  const deps = (d: string) => existsSync(join(d, 'vcpkg_installed', 'x86-windows-static', 'lib', 'detours.lib'));
  if (!deps(repo) && !existsSync(join(repo, 'vcpkg_installed'))) {
    const cands = readdirSync(GC, { withFileTypes: true }).filter(e => e.isDirectory()).map(e => join(GC, e.name))
      .map(dir => ({ dir, baseline: vcpkgBaseline(dir), hasDeps: deps(dir) }));
    const donor = pickVcpkgDonor(vcpkgBaseline(repo), cands);
    if (donor) {
      const target = realpathSync(join(donor, 'vcpkg_installed')); // a donor's own junction: link its target, not a chain
      execFileSync('cmd', ['/d', '/c', 'mklink', '/J', join(repo, 'vcpkg_installed'), target], { stdio: 'ignore' });
      say(`build: vcpkg deps linked from ${target} (same baseline)`);
    }
  }
  const setup = deps(repo) ? '' : `pwsh -NoProfile -File tools\\development\\setup.ps1 && `;
  say(`build: no CI asi for ${FORK.sha.slice(0, 8)}: Public asi from ${repo} (one CPU, log ${log})`);
  execFileSync('cmd', ['/d', '/c', `start "" /wait /low /affinity 1 cmd /d /c "cd /d "${repo}" && ${setup}pwsh -NoProfile -File tools\\development\\build.ps1 -Flavor Public > "${log}" 2>&1"`],
    { stdio: 'inherit', timeout: 3600000, windowsVerbatimArguments: true });
  const asi = join(repo, 'Public', 'ApexRadiance.asi');
  if (!existsSync(asi) || !/SHA256/.test(readFileSync(log, 'utf8'))) throw new Error(`build: Public asi build failed, see ${log}`);
  rmSync(d, { recursive: true, force: true }); mkdirSync(d, { recursive: true });
  copyFileSync(asi, join(d, 'ApexRadiance.asi'));
  const tw = join(repo, 'Public', 'ts3wrap.exe'); // the same Public build makes the restart host
  if (existsSync(tw)) copyFileSync(tw, join(d, 'ts3wrap.exe'));
  writeFileSync(join(d, 'build.json'), JSON.stringify({ sha: FORK.sha, asiSha: sha(join(d, 'ApexRadiance.asi')), ts3wrapSha: existsSync(tw) ? sha(join(d, 'ts3wrap.exe')) : null, log }, null, 1));
  say(`build: local Public asi ${sha(join(d, 'ApexRadiance.asi')).slice(0, 12)}`);
}

const vcpkgBaseline = (dir: string): string | null => {
  try { return JSON.parse(readFileSync(join(dir, 'vcpkg.json'), 'utf8'))['builtin-baseline'] ?? null; } catch { return null; }
};
// an Apex clone whose installed vcpkg deps were built from the same pinned baseline (none when the baseline is unknown)
export const pickVcpkgDonor = (baseline: string | null, cands: { dir: string; baseline: string | null; hasDeps: boolean }[]) =>
  baseline ? cands.find(c => c.hasDeps && c.baseline === baseline)?.dir ?? null : null;

// CI asi first; else the local build of exactly forkSha (build.json names the commit, the file must still hash to it)
export function chooseAsi(ci: string | null, local: { sha: string; asiSha: string } | null, forkSha: string, localFile: string, fileSha: (p: string) => string | null): { path: string; source: 'ci' | 'local' } {
  if (ci) return { path: ci, source: 'ci' };
  if (!local) throw new Error(`install-copy: no green CI build of ${forkSha.slice(0, 8)} with a public asi and no local build: run install-copy --build`);
  if (local.sha !== forkSha) throw new Error(`install-copy: the local asi was built from ${local.sha.slice(0, 8)}, FORK.sha is ${forkSha.slice(0, 8)}: run install-copy --build`);
  if (fileSha(localFile) !== local.asiSha) throw new Error(`install-copy: ${localFile} does not hash to its build.json: run install-copy --build`);
  return { path: localFile, source: 'local' };
}
function forkAsi(build: boolean) {
  const ci = ciAsi(), d = localAsiDir(), bj = join(d, 'build.json'), f = join(d, 'ApexRadiance.asi');
  const readLocal = () => existsSync(bj) ? JSON.parse(readFileSync(bj, 'utf8')) as { sha: string; asiSha: string } : null;
  const pick = () => chooseAsi(ci, readLocal(), FORK.sha, f, p => existsSync(p) ? sha(p) : null);
  if (!ci && build) { try { return pick(); } catch { buildLocalAsi(); } }
  return pick();
}

// the fork's ts3wrap.exe (restart host + 64-bit plugin host, #158): the first candidate that exists and hashes to its want
// (no want = any); null = none, the install has no host and apex-oop-atlas falls back to the PR158 pair
export const chooseTs3wrap = (cands: { path: string; want?: string | null }[], fileSha: (p: string) => string | null): string | null =>
  cands.find(c => { const s = fileSha(c.path); return !!s && (!c.want || s === c.want); })?.path ?? null;

// is release tag <tag> an ancestor of (or equal to) commit sha in git repo <repo>? false when the tag is unknown there
export function releaseInFork(repo: string, tag: string, sha: string): boolean {
  try { execFileSync('git', ['-C', repo, 'merge-base', '--is-ancestor', `refs/tags/${tag}^{commit}`, sha], { stdio: 'ignore', timeout: 60000 }); return true; }
  catch { return false; }
}

// game-copy/rewind-bridge-1119/install/apex-unz: the Apex release files, our fork's pair, conf and asi, rewindMode = True in both
// bridge.conf. Apex's zip and anything Apex installed stay untouched.
async function installCopy(build: boolean) {
  const dir = join(REWIND, 'install'), unz = join(dir, 'apex-unz'), rb = join(REWIND, 'apex-repo', 'native', 'remix-bridge'), out = join(rb, 'out');
  const pair = ['d3d9.dll', '.trex/NvRemixBridge.exe'];
  if (build) buildFork();
  const head = execFileSync('git', ['-C', join(REWIND, 'apex-repo'), 'rev-parse', 'HEAD'], { encoding: 'utf8' }).trim();
  if (head !== FORK.sha) throw new Error(`install-copy: ${REWIND}/apex-repo is at ${head.slice(0, 8)}, FORK.sha is ${FORK.sha.slice(0, 8)}: run install-copy --build`);
  for (const p of pair) if (!existsSync(join(out, p))) throw new Error(`install-copy: ${join(out, p)} missing: run install-copy --build`);
  const { path: asi, source: asiSource } = forkAsi(build);
  mkdirSync(dir, { recursive: true });
  await fetchApex(dir);
  const tag = JSON.parse(readFileSync(join(dir, 'apex.json'), 'utf8')).tag;
  const repo = join(REWIND, 'apex-repo');
  try { execFileSync('git', ['-C', repo, 'fetch', '-q', '--no-tags', `https://github.com/${APEX_REPO}.git`, `+refs/tags/${tag}:refs/tags/${tag}`], { stdio: 'ignore', timeout: 120000 }); }
  catch { say(`install-copy: WARN could not fetch tag ${tag} from ${APEX_REPO}; using the local tag if present`); }
  if (!releaseInFork(repo, tag, FORK.sha)) throw new Error(`install-copy: the latest Apex release ${tag} is not merged into FORK.sha ${FORK.sha.slice(0, 8)}: merge it into ${FORK.branch} in the fork, then update FORK.sha (and REWIND_BUILT_ON)`);
  if (tag !== REWIND_BUILT_ON) say(`install-copy: release ${tag} is merged into ${FORK.sha.slice(0, 8)} (REWIND_BUILT_ON ${REWIND_BUILT_ON} is older: update it with the next pin)`);
  for (const p of pair) copyFileSync(join(out, p), join(unz, p));
  copyFileSync(asi, join(unz, 'ApexRadiance.asi'));
  // next to the chosen asi (CI artifact; local build: its build.json sha256), else apex-repo's Public output (HEAD = FORK.sha above)
  // shortcut: the Public fallback is not proven built from FORK.sha (an older build.json has no ts3wrapSha); fixed by install-copy --build
  const bj = join(localAsiDir(), 'build.json'), lj = existsSync(bj) ? JSON.parse(readFileSync(bj, 'utf8')) : null;
  const tw = chooseTs3wrap([{ path: join(dirname(asi), 'ts3wrap.exe'), want: asiSource === 'local' ? lj?.ts3wrapSha ?? '-' : null }, { path: join(repo, 'Public', 'ts3wrap.exe') }], p => existsSync(p) ? sha(p) : null);
  if (tw) copyFileSync(tw, join(unz, 'ts3wrap.exe')); else say('install-copy: WARN no fork ts3wrap.exe: the install has no restart host (apex-oop-atlas falls back to the PR158 pair)');
  copyFileSync(join(rb, 'conf', 'dxvk.conf'), join(unz, 'dxvk.conf'));
  for (const c of ['bridge.conf', '.trex/bridge.conf']) { copyFileSync(join(rb, 'conf', 'bridge.conf'), join(unz, c)); appendFileSync(join(unz, c), '\r\nrewindMode = True\r\n'); }
  const twSha = tw ? sha(join(unz, 'ts3wrap.exe')) : null;
  writeFileSync(join(dir, 'fork.json'), JSON.stringify({ ...FORK, release: tag, asiSource, asi: sha(join(unz, 'ApexRadiance.asi')), d3d9: sha(join(unz, pair[0])), server: sha(join(unz, pair[1])), ts3wrap: twSha }, null, 1));
  say(`install-copy: ${unz} = Apex ${tag} files + fork ${FORK.branch} ${FORK.sha.slice(0, 8)}: asi (${asiSource}) ${sha(join(unz, 'ApexRadiance.asi')).slice(0, 12)}, d3d9.dll ${sha(join(unz, pair[0])).slice(0, 12)}, NvRemixBridge.exe ${sha(join(unz, pair[1])).slice(0, 12)}, ts3wrap.exe ${twSha?.slice(0, 12) ?? 'none'}, rewindMode = True`);
}

async function snapshot(dir: string) {
  if (existsSync(join(dir, 'snapshot.json'))) throw new Error(`${dir}/snapshot.json exists: restore first (or remove it after a verified restore)`);
  if (!DRY && gamePids().length) throw new Error('TS3W.exe runs');
  for (const s of Object.values(SRC)) verify(s);
  await fetchApex(dir, 'apex-stock-unz'); // stock: state apex-stock only; the fork install comes in at each state switch
  const fw = forkInstallProblem();
  if (fw) say(`snapshot: WARN fork install not ready for FORK.sha ${FORK.sha.slice(0, 8)} (${fw}): every state but ${[...STOCK_STATES].join(', ')} refuses until install-copy`);
  const bak = join(dir, 'bin-bak');
  mkdirSync(bak, { recursive: true });
  const present: string[] = [];
  for (const f of MANAGED) if (existsSync(join(BIN, f))) { mkdirSync(dirname(join(bak, f)), { recursive: true }); copyFileSync(join(BIN, f), join(bak, f)); present.push(f); }
  if (existsSync(APEX_DOCS)) cpSync(APEX_DOCS, join(dir, 'apex-docs'), { recursive: true });
  const snap = { at: new Date().toISOString(), bin: BIN, present, files: hashTree(BIN), apexDocs: existsSync(APEX_DOCS) ? hashTree(APEX_DOCS) : null };
  for (const f of present) assert.equal(sha(join(bak, f)), snap.files[f], `backup of ${f}`);
  writeFileSync(join(dir, 'snapshot.json'), JSON.stringify(snap, null, 1));
  say(`snapshot: ${Object.keys(snap.files).length} Bin files hashed, backed up ${present.join(', ')}; Apex Documents folder ${snap.apexDocs ? 'backed up' : 'absent'}; wininet ${snap.files['wininet.dll']?.slice(0, 12)}, d3d9 ${snap.files['d3d9.dll']?.slice(0, 12)}, ts3native.asi ${snap.files['ts3native.asi']?.slice(0, 12)}`);
}

const loadSnap = (dir: string) => JSON.parse(readFileSync(join(dir, 'snapshot.json'), 'utf8')) as { files: Record<string, string>; present: string[]; apexDocs: Record<string, string> | null };

function setState(dir: string, name: string) {
  if (!STATES[name]) throw new Error(`unknown state ${name} (${Object.keys(STATES).join(', ')})`);
  if (!DRY && gamePids().length) throw new Error('TS3W.exe runs');
  if (s3mmFiles().length) throw new Error(`Sims3MonoModder (S3MM) in the game folder: ${s3mmFiles().join(', ')}; we never install it (docs/291), remove it first`);
  if (!STOCK_STATES.has(name)) forkUnz(dir); // refuses when the fork install is missing or stale
  const snap = loadSnap(dir), want = STATES[name](join(dir, 'apex-unz'));
  for (const f of MANAGED) { // back to the snapshot first
    const p = join(BIN, f);
    if (snap.present.includes(f)) { if (!existsSync(p) || sha(p) !== snap.files[f]) copyFileSync(join(dir, 'bin-bak', f), p); }
    else rmSync(p, { force: true });
  }
  for (const [f, src] of Object.entries(want)) {
    const p = join(BIN, f);
    if (src === null) rmSync(p, { force: true });
    else { mkdirSync(dirname(p), { recursive: true }); copyFileSync(src, p); }
  }
  const bad: string[] = [];
  for (const f of MANAGED) {
    const p = join(BIN, f), src = f in want ? want[f] : snap.present.includes(f) ? join(dir, 'bin-bak', f) : null;
    if (src === null ? existsSync(p) : !existsSync(p) || sha(p) !== sha(src)) bad.push(f);
  }
  if (bad.length) throw new Error(`state ${name}: read-back differs for ${bad.join(', ')}`);
  const apexLog = join(APEX_DOCS, 'ApexRadiance_LOG.txt');
  writeFileSync(join(dir, 'state.json'), JSON.stringify({ name, at: new Date().toISOString(), apexLogSize: existsSync(apexLog) ? statSync(apexLog).size : 0 }));
  if (!DRY) say(`state ${name}: ${ensureVramSidecar()}`);  // docs/284: every Bin state, so no DxDiagCache=1 test launch probes
  say(`state ${name}: ${Object.keys(want).length} file(s) over the snapshot (${Object.keys(want).join(', ') || 'none'}); every managed file read back by sha256`);
}

function restore(dir: string): boolean {
  setState(dir, 'base');
  const problems: string[] = [];
  // a killed queue.ts job (keys extraAsi / defaultIni): exactly the Bin files its marker names are removed (no blanket *.asi rule), and the
  // Default.ini it found goes back from Default.ini.queue-bak; a file the snapshot holds is never deleted here
  // the marker is Bin/extra-asi.queue.json (any checkout's queue); the old probe-out marker of this checkout too, except DRY (a fake Bin, the real one is not its record)
  const legacy = DRY ? '' : LEGACY_MARKER, snap = loadSnap(dir), kept = [extraMarker(BIN), legacy].filter(Boolean).flatMap(m => extraOwned(m)).filter(f => f in snap.files);
  if (kept.length) problems.push(`extra-asi marker names snapshot files, left alone: ${kept.join(', ')}`);
  else { const why = extraSweep(BIN, true, legacy); if (why) problems.push(`extraAsi files of a killed queue: ${why}`); }
  { const why = iniBackupRestore(join(BIN, 'Default.ini')); if (why) problems.push(`Default.ini.queue-bak: ${why}`); }
  const cur = hashTree(BIN);
  for (const f of Object.keys(cur)) if (!(f in snap.files) && !KEEP_CACHE.test(f)) {
    if (EXTRA_OK.test(f)) rmSync(join(BIN, f), { force: true }); else problems.push(`new file left in place: ${f}`);
  }
  for (const d of ['rtx-remix', '.trex']) if (existsSync(join(BIN, d)) && !Object.keys(snap.files).some(f => f.startsWith(`${d}/`))) rmSync(join(BIN, d), { recursive: true, force: true });
  const after = hashTree(BIN);
  for (const [f, h] of Object.entries(snap.files)) if (!IGNORE_DIFF.test(f) && !KEEP_CACHE.test(f) && after[f] !== h) problems.push(`${f}: ${after[f] ? 'sha differs' : 'missing'}`);
  for (const f of Object.keys(after)) if (!(f in snap.files) && !KEEP_CACHE.test(f) && !problems.some(p => p.endsWith(f))) problems.push(`new file: ${f}`);
  if (snap.apexDocs) { // put Apex's Documents folder back as it was
    for (const f of existsSync(APEX_DOCS) ? walk(APEX_DOCS) : []) if (!(f in snap.apexDocs)) rmSync(join(APEX_DOCS, f), { force: true });
    cpSync(join(dir, 'apex-docs'), APEX_DOCS, { recursive: true });
    const d = hashTree(APEX_DOCS);
    for (const [f, h] of Object.entries(snap.apexDocs)) if (d[f] !== h) problems.push(`Apex Documents ${f}: differs after restore`);
  } else if (existsSync(APEX_DOCS)) rmSync(APEX_DOCS, { recursive: true, force: true });
  const n = Object.keys(snap.files).filter(f => !IGNORE_DIFF.test(f)).length;
  if (problems.length) { say(`restore: ${problems.length} problem(s):\n  ${problems.join('\n  ')}`); return false; }
  say(`restore OK: ${n} Bin files equal the snapshot by sha256 (logs aside), no Turbo/Apex/bridge file left; wininet ${after['wininet.dll']?.slice(0, 12)}, d3d9 ${after['d3d9.dll']?.slice(0, 12)}, ts3native pair ${after['ts3native.asi']?.slice(0, 12)}/${after['ts3native.ini']?.slice(0, 12)}; Apex Documents ${snap.apexDocs ? 'restored' : 'absent'}`);
  return true;
}

// ------------------------------------------------------------------------------------------------ monitor
// 32-bit Windows PowerShell: Process.Modules of a WOW64 game lists its 32-bit modules only from a 32-bit process; thread start
// addresses through NtQueryInformationThread(ThreadQuerySetWin32StartAddress) (ProcessThread.StartAddress returned 0 for every thread)
const PS32 = join(process.env.SystemRoot ?? 'C:\\Windows', 'SysWOW64', 'WindowsPowerShell', 'v1.0', 'powershell.exe');
const SAMPLE_PS = (pid: number) => `$ErrorActionPreference='Stop'; $p=Get-Process -Id ${pid}
$b=0; $z=0; try { $m=$p.Modules | Where-Object { $_.ModuleName -eq 'ts3native.asi' } | Select-Object -First 1; if ($m) { $b=[int64]$m.BaseAddress; $z=$m.ModuleMemorySize } } catch {}
Add-Type -Namespace T -Name N -MemberDefinition '[DllImport("kernel32.dll")] public static extern System.IntPtr OpenThread(int a, bool i, int id); [DllImport("ntdll.dll")] public static extern int NtQueryInformationThread(System.IntPtr h, int c, out System.IntPtr v, int l, System.IntPtr r); [DllImport("kernel32.dll")] public static extern bool CloseHandle(System.IntPtr h);'
$th=@(foreach ($x in $p.Threads) { $s=0; $h=[T.N]::OpenThread(0x40, $false, $x.Id); if ($h -ne [System.IntPtr]::Zero) { $v=[System.IntPtr]::Zero; if ([T.N]::NtQueryInformationThread($h, 9, [ref]$v, 4, [System.IntPtr]::Zero) -eq 0) { $s=[int64][BitConverter]::ToUInt32([BitConverter]::GetBytes($v.ToInt32()), 0) }; [void][T.N]::CloseHandle($h) }; $bp=-1; try { $bp=$x.BasePriority } catch {}; @{id=$x.Id; b=$bp; s=$s} })
$mn=0; $mx=0; try { $mn=[int64]$p.MinWorkingSet; $mx=[int64]$p.MaxWorkingSet } catch {}
@{ws=$p.WorkingSet64; pv=$p.PrivateMemorySize64; mn=$mn; mx=$mx; cls="$($p.PriorityClass)"; nb=$b; nz=$z; resp=$p.Responding; th=$th} | ConvertTo-Json -Compress -Depth 3`;
const F7_PS = (pid: number) => `Add-Type -Namespace T -Name W -MemberDefinition '[DllImport("user32.dll")] public static extern bool PostMessageW(System.IntPtr h, uint m, System.IntPtr w, System.IntPtr l);'
$h=(Get-Process -Id ${pid}).MainWindowHandle; if ($h -eq [System.IntPtr]::Zero) { 'no game window: nothing posted'; exit }; $a=[T.W]::PostMessageW($h, 0x100, [System.IntPtr]0x76, [System.IntPtr]0x00410001); Start-Sleep -Milliseconds 80
$c=[T.W]::PostMessageW($h, 0x101, [System.IntPtr]0x76, [System.IntPtr][int32]-1069481983); "hwnd $h down $a up $c"`;
type Sample = { t: number; ws: number; pv: number; mn: number; mx: number; cls: string; nb: number; nz: number; resp: boolean; th: { id: number; b: number; s: number }[] };

// one sample -> priority histogram and every thread that starts inside ts3native.asi (named when it is one of the two background threads)
export function digest(s: Sample) {
  const hist: Record<number, number> = {};
  for (const x of s.th) hist[x.b] = (hist[x.b] ?? 0) + 1;
  const ours = s.th.filter(x => s.nb && x.s >= s.nb && x.s < s.nb + s.nz).map(x => `${OUR_THREADS[x.s - s.nb] ?? `ts3native+0x${(x.s - s.nb).toString(16)}`} tid ${x.id} base ${x.b}`);
  return { hist: Object.entries(hist).sort((a, b) => Number(b[0]) - Number(a[0])).map(([k, v]) => `${k}:${v}`).join(' '), ours };
}

// Turbo's rewritten file is UTF-16 (BOM FF FE) or 8-bit: write ours in the same encoding
export function encodeLike(old: Buffer, lines: string[]): Buffer {
  const text = lines.join('\r\n') + '\r\n';
  return old.length >= 2 && old[0] === 0xFF && old[1] === 0xFE ? Buffer.concat([Buffer.from([0xFF, 0xFE]), Buffer.from(text, 'utf16le')]) : Buffer.from(text, 'utf8');
}

function monitor(dir: string, arm: string, o: { allOn: boolean; f7: boolean; maxMin: number }) {
  const out = join(dir, arm); mkdirSync(out, { recursive: true });
  const jl = join(out, 'monitor.jsonl'), txt = join(out, 'monitor.txt');
  const note = (s: string) => { appendFileSync(txt, `${now()} ${s}\n`); say(`monitor ${arm}: ${s}`); };
  const seen = new Set(gamePids()), t0 = Date.now();
  note(`waiting for a new TS3W.exe (running now: ${[...seen].join(',') || 'none'})${o.allOn ? '; all-on Turbo.txt after Turbo rewrites its file' : ''}${o.f7 ? '; F7 at +6/+7 min' : ''}`);
  // a start that ends within 3 min (cycle.ts restart) re-arms for the next TS3W.exe, up to 3 starts
  for (let round = 0; round < 3; round++) {
    let pid = 0;
    while (!pid) {
      if (Date.now() - t0 > 30 * 60000) { note('no new TS3W.exe within 30 min: stop'); return; }
      pid = gamePids().find(p => !seen.has(p)) ?? 0;
      if (!pid) sleep(2000);
    }
    seen.add(pid);
    if (watch(pid) >= 180000) return;
    note(`pid ${pid} lived < 3 min: waiting for the next start`);
  }

  function watch(pid: number): number {
    const start = Date.now(), turboTxt = join(BIN, 'Turbo.txt');
    note(`TS3W.exe pid ${pid}`);
    let wrote = false, f7 = 0, last: Sample | undefined, wsMax = 0, drops = 0, at60 = '', hist = '';
    const oursSeen = new Map<string, number[]>();
    while (gamePids().includes(pid) && Date.now() - start < o.maxMin * 60000) {
      const sec = Math.round((Date.now() - start) / 1000);
      try {
        const s: Sample = { t: sec, ...JSON.parse(execFileSync(PS32, ['-NoProfile', '-NonInteractive', '-Command', SAMPLE_PS(pid)], { encoding: 'utf8', timeout: 30000 })) };
        appendFileSync(jl, JSON.stringify({ pid, ...s }) + '\n');
        const d = digest(s);
        if (d.hist !== hist) { note(`+${sec}s base priorities (priority:threads) ${d.hist}, class ${s.cls}`); hist = d.hist; }
        for (const x of d.ours) { const k = x.replace(/ base -?\d+$/, ''); oursSeen.set(k, [...(oursSeen.get(k) ?? []), Number(x.split(' base ')[1])]); }
        if (!at60 && sec >= 60) at60 = `+${sec}s class ${s.cls}, base priorities ${d.hist}; our threads: ${d.ours.join(', ') || 'none'} (UdHeaderMain ends ~15-20 s after start, SlimMain only runs during a save)`;
        if (last && s.ws < last.ws * 0.6 && last.ws > 300e6) { drops++; note(`working set drop at +${sec}s: ${(last.ws / 1e6).toFixed(0)} -> ${(s.ws / 1e6).toFixed(0)} MB`); }
        if (!s.resp) note(`+${sec}s window not responding`);
        wsMax = Math.max(wsMax, s.ws); last = s;
      } catch (e) { if (gamePids().includes(pid)) note(`+${sec}s sample failed: ${String((e as Error).message).split('\n')[0]}`); }
      if (o.allOn && !wrote && existsSync(turboTxt) && statSync(turboTxt).mtimeMs >= start - 5000) {
        sleep(3000);
        const old = readFileSync(turboTxt);
        writeFileSync(join(out, 'Turbo.txt.as-rewritten'), old);
        writeFileSync(turboTxt, encodeLike(old, ALL_ON));
        wrote = true; note(`Turbo.txt rewritten by Turbo after the start (${old[0] === 0xFF ? 'UTF-16' : '8-bit'}); all-on file written (Patch=1, ThreadPriorityBoost=1, SmartRamManager=1, ...)`);
      }
      if (o.f7 && f7 < 2 && Date.now() - start >= (6 + f7) * 60000) {
        try { note(`F7 ${f7 ? 'close' : 'open'} posted: ${execFileSync(PS32, ['-NoProfile', '-NonInteractive', '-Command', F7_PS(pid)], { encoding: 'utf8', timeout: 30000 }).trim()}`); } catch (e) { note(`F7 post failed: ${(e as Error).message.split('\n')[0]}`); }
        f7++;
      }
      sleep(Date.now() - start < 120000 ? 5000 : 30000);
    }
    const life = Date.now() - start;
    if (life < 180000) return life;
    if (o.allOn && !wrote) note('all-on Turbo.txt NOT written (Turbo never rewrote Turbo.txt after the start: Turbo not loaded?)');
    note(`end pid ${pid} after ${(life / 60000).toFixed(1)} min: ${at60 || 'no sample at 60 s'}`);
    for (const [k, v] of oursSeen) note(`${k}: base priority over its samples ${[...new Set(v)].join(',')} (${v.length} samples)`);
    note(`working set: max ${(wsMax / 1e6).toFixed(0)} MB, last ${last ? (last.ws / 1e6).toFixed(0) : '?'} MB, min/max WS limits ${last ? `${(last.mn / 1e6).toFixed(0)}/${(last.mx / 1e6).toFixed(0)} MB` : '?'}, ${drops} drop(s) > 40%`);
    return life;
  }
}

// ------------------------------------------------------------------------------------------------ collect
function collect(dir: string, arm: string) {
  const out = join(dir, arm); mkdirSync(out, { recursive: true });
  const st = existsSync(join(dir, 'state.json')) ? JSON.parse(readFileSync(join(dir, 'state.json'), 'utf8')) : { apexLogSize: 0, name: '?' };
  const lines: string[] = [`arm ${arm}, Bin state ${st.name}`];
  for (const f of ['Turbo.txt', 'imgui.ini', 'NvRemixBridge_d3d9.log']) if (existsSync(join(BIN, f))) copyFileSync(join(BIN, f), join(out, f));
  const rl = join(BIN, 'rtx-remix', 'logs');
  const bridgeLogs = existsSync(rl) ? readdirSync(rl).filter(f => /\.log$/i.test(f)) : [];
  for (const f of bridgeLogs) copyFileSync(join(rl, f), join(out, f));
  lines.push(`bridge logs: ${bridgeLogs.join(', ') || 'none'}`);
  for (const f of bridgeLogs) {
    const t = readFileSync(join(rl, f), 'utf8');
    lines.push(`  ${f}: ${t.split('\n').length} lines; error/reset/lost lines:`, ...t.split('\n').filter(l => /error|fail|reset|lost|exception|timeout/i.test(l)).slice(0, 15).map(l => `    ${l.trim().slice(0, 240)}`));
  }
  const al = join(APEX_DOCS, 'ApexRadiance_LOG.txt');
  if (existsSync(al)) {
    const buf = readFileSync(al), from = buf.length >= st.apexLogSize ? st.apexLogSize : 0;
    const t = buf.subarray(from).toString('utf8');
    writeFileSync(join(out, 'ApexRadiance_LOG.txt'), t);
    lines.push(`Apex log since the state switch: ${t.split('\n').length} lines; platform/bridge/device lines:`,
      ...t.split('\n').filter(l => /Platform:|RTX Remix|VulkanDriverGuard|\bReset\b|First Present|No Present|Game device|\[(ERROR|FATAL)\]|crash|exception/i.test(l)).slice(0, 30).map(l => `    ${l.trim().slice(0, 240)}`));
  } else lines.push('Apex log: none');
  if (existsSync(join(BIN, 'Turbo.txt'))) lines.push('Turbo.txt at the end:', ...readFileSync(join(BIN, 'Turbo.txt')).toString(readFileSync(join(BIN, 'Turbo.txt'))[0] === 0xFF ? 'utf16le' : 'utf8').split(/\r?\n/).filter(Boolean).map(l => `    ${l.replace(/^\uFEFF/, '')}`));
  writeFileSync(join(out, 'summary.txt'), lines.join('\n') + '\n');
  say(`collect ${arm}: ${out}`);
}

// ------------------------------------------------------------------------------------------------ cli
function selfCheck() {
  assert.ok(MANAGED.includes('wininet.dll') && MANAGED.includes('d3d9.dll') && MANAGED.includes('.trex/NvRemixBridge.exe') && MANAGED.includes('ts3native.asi'));
  assert.deepEqual(Object.keys(STATES.turbo('u')), ['Turbo.asi']);
  assert.ok(Object.keys(STATES['apex-rewind']('u')).every(k => MANAGED.includes(k)), 'restore knows every file of the rewind arm');
  assert.ok(STATES['apex-rewind']('u')['bridge.conf']!.includes('rewind-bridge-1119'), 'the rewind arm takes its rewindMode confs from the install copy');
  assert.equal(STATES['apex-turbo-ual77']('u')['wininet.dll'], SRC.ual77.path);
  assert.ok(Object.keys(STATES['apex-asyncrt']('u')).every(k => MANAGED.includes(k)) && STATES['apex-asyncrt']('u')['.trex/NvRemixBridge.exe']!.includes('apex-asyncrt-test'), 'restore knows the bridge pair of the async read-back arm');
  assert.ok(STATES['apex-fork-asi']('u')['ApexRadiance.asi']!.includes('apex-fork-asi-test') && STATES['apex-fork-asi']('u')['d3d9.dll'] === STATES.apex('u')['d3d9.dll'], 'fork asi state: our asi, the release bridge');
  assert.ok(Object.keys(STATES['apex-oop']('u')).every(k => MANAGED.includes(k)) && STATES['apex-oop']('u')['ts3wrap.exe']!.includes('apex-oop-test'), 'restore knows the restart host of the oop arm');
  assert.ok(Object.keys(STATES['apex-oop-atlas']('u')).every(k => MANAGED.includes(k)) && STATES['apex-oop-atlas']('u')['atlas-oop.dll']!.includes('apex-oop-test') && EXTRA_OK.test('atlas-oop.dll'), 'restore knows the Atlas store plugin');
  assert.ok(!('wininet.dll' in STATES['apex-turbo']('u')), 'arm 2 keeps our loader');
  // fork install: ready only for FORK.sha with every recorded sha256 matching; rewind is opt-in; stock only as apex-stock
  const F = 'f'.repeat(40), ok = { sha: F, asi: 'a', d3d9: 'd', server: 's', ts3wrap: 't' }, files: Record<string, string> = { 'ApexRadiance.asi': 'a', 'd3d9.dll': 'd', '.trex/NvRemixBridge.exe': 's', 'ts3wrap.exe': 't' };
  const fsx = (m: Record<string, string>) => (rel: string) => m[rel] ?? null;
  assert.equal(forkInstallProblemOf(ok, F, fsx(files)), '');
  assert.match(forkInstallProblemOf(null, F, fsx(files)), /no fork.json/);
  assert.match(forkInstallProblemOf({ ...ok, sha: 'a69a1bbc' }, F, fsx(files)), /fork.json is for a69a1bbc, FORK.sha is ffffffff/);
  assert.match(forkInstallProblemOf({ ...ok, ts3wrap: undefined }, F, fsx(files)), /no ts3wrap sha256 in fork.json \(older install-copy\)/);
  assert.match(forkInstallProblemOf(ok, F, fsx({ ...files, 'ApexRadiance.asi': 'x' })), /ApexRadiance.asi changed/);
  assert.equal(noRewindMode('a = 1\r\nrewindMode = True\r\nb = 2\r\n\r\nrewindMode = True\r\n'), 'a = 1\r\nb = 2\r\n\r\n');
  assert.equal(noRewindMode('# rewindMode = True\n'), '# rewindMode = True\n', 'a comment stays');
  assert.ok(['apex', 'apex-rewind', 'apex-oop-atlas', 'apex-turbo'].every(k => !STOCK_STATES.has(k)) && STOCK_STATES.has('apex-stock'), 'only base, turbo, apex-stock skip the fork');
  assert.equal(STATES['apex-stock']('r/apex-unz')['ApexRadiance.asi'], join('r', 'apex-stock-unz', 'ApexRadiance.asi'));
  assert.equal(STATES.apex('r/apex-unz')['ApexRadiance.asi'], join('r/apex-unz', 'ApexRadiance.asi'), 'apex = the run copy of the fork install');
  const rw = STATES['apex-rewind']('r/apex-unz');
  assert.ok(rw['bridge.conf']!.includes('rewind-bridge-1119') && rw['.trex/bridge.conf']!.includes('rewind-bridge-1119') && rw['ApexRadiance.asi'] === STATES.apex('r/apex-unz')['ApexRadiance.asi'], 'apex-rewind = apex + the rewindMode confs');
  const oa = STATES['apex-oop-atlas']('r/apex-unz');
  assert.ok(oa['ApexRadiance.asi'] === STATES.apex('r/apex-unz')['ApexRadiance.asi'] && oa['atlas-oop.dll']!.includes('apex-oop-test'), 'apex-oop-atlas = the fork build + the plugin');
  const T = 't'.repeat(64);
  const has = (m: Record<string, string>) => (p: string) => m[p] ?? null;
  assert.equal(chooseTs3wrap([{ path: 'l', want: T }, { path: 'p' }], has({ l: T, p: 'y' })), 'l');
  assert.equal(chooseTs3wrap([{ path: 'l', want: '-' }, { path: 'p' }], has({ l: T, p: 'y' })), 'p', 'local build without a recorded sha: the Public output');
  assert.equal(chooseTs3wrap([{ path: 'l', want: T }, { path: 'p' }], has({})), null, 'none: no host');
  assert.equal(chooseTs3wrap([{ path: 'c', want: null }], has({ c: 'z' })), 'c', 'CI artifact: any');
  const dg = `sha256:${'a'.repeat(64)}`, url = 'https://x/y';
  assert.equal(pickZip([{ name: 'ApexRadianceSetup.exe', digest: dg, browser_download_url: url }, { name: 'Apex-Radiance-3.2.1-Manual-install.zip', digest: dg, browser_download_url: url }])?.name, 'Apex-Radiance-3.2.1-Manual-install.zip');
  assert.equal(pickZip([{ name: 'Apex-Radiance-3.2.1-Manual-install.zip', browser_download_url: url }]), undefined, 'no digest = no pin');
  assert.equal(pickZip([{ name: 'ApexRadiance.asi', digest: dg, browser_download_url: url }]), undefined);
  for (const f of ['rtx-remix/logs/bridge32.log', '.trex/x.dll', 'NvRemixBridge_d3d9.log', 'Turbo.txt', 'ts3native.log', 'imgui.ini', 'ApexRadiance_Crash.dmp', 'ts3wrap.exe']) assert.ok(EXTRA_OK.test(f), f);
  for (const f of ['TS3W.exe', 'Default.ini', 'Sims3Performance.ini', 'Gameplay/x.package', 'sub/ts3native.log', 'ts3wrapXexe', 'sub/ts3wrap.exe']) assert.ok(!EXTRA_OK.test(f), f);
  assert.ok(KEEP_CACHE.test('ts3native-vram.ini') && !KEEP_CACHE.test('ts3native.ini') && !KEEP_CACHE.test('sub/ts3native-vram.ini') && !EXTRA_OK.test('ts3native-vram.ini'));
  for (const f of ['ts3native.log', 'rtx-remix/logs/bridge32.log', 'rtx-remix/logs/bridge64.log', 'pccheck-summary.txt', 'pccheck.csv']) assert.ok(IGNORE_DIFF.test(f) && EXTRA_OK.test(f), f);
  for (const f of ['wininet.dll', 'TS3W.exe', 'bridge.conf', 'rtx-remix/d3d9.dll', 'rtx-remix/logs/x.dll', 'sub/pccheck.csv', 'Default.ini']) assert.ok(!IGNORE_DIFF.test(f), f);
  const s: Sample = { t: 60, ws: 1, pv: 1, mn: 0, mx: 0, cls: 'High', nb: 0x10000000, nz: 0x200000, resp: true,
    th: [{ id: 1, b: 13, s: 0x401000 }, { id: 2, b: 4, s: 0x100B9230 }, { id: 3, b: 13, s: 0x10001000 }, { id: 5, b: 13, s: 0x10400000 }, { id: 4, b: 14, s: 0x100AAE30 }] };
  const d = digest(s);
  assert.equal(d.hist, '14:1 13:3 4:1');
  assert.deepEqual(d.ours, ['UdHeaderMain (userdiag.cpp:559) tid 2 base 4', 'ts3native+0x1000 tid 3 base 13', 'SlimMain (saveslim.cpp:592) tid 4 base 14']);
  assert.deepEqual(digest({ ...s, nb: 0 }).ours, []);
  const u16 = encodeLike(Buffer.from([0xFF, 0xFE, 0x41, 0]), ['A=1']);
  assert.equal(u16.subarray(2).toString('utf16le'), 'A=1\r\n');
  assert.equal(encodeLike(Buffer.from('x'), ['A=1']).toString('utf8'), 'A=1\r\n');
  assert.ok(ALL_ON.slice(1).every(l => /^\w+ = -?\d+$/.test(l)), 'integers only (docs/263 §1.1: stoi)');
  // asi pick: CI wins; else only the local build of exactly FORK.sha whose file still hashes to its build.json
  const fs1 = 'f'.repeat(40), loc = { sha: fs1, asiSha: 'h1' }, hashOk = () => 'h1';
  assert.deepEqual(chooseAsi('ci.asi', loc, fs1, 'l.asi', hashOk), { path: 'ci.asi', source: 'ci' });
  assert.deepEqual(chooseAsi('ci.asi', null, fs1, 'l.asi', () => null), { path: 'ci.asi', source: 'ci' }, 'CI needs no local build');
  assert.deepEqual(chooseAsi(null, loc, fs1, 'l.asi', hashOk), { path: 'l.asi', source: 'local' });
  assert.throws(() => chooseAsi(null, null, fs1, 'l.asi', hashOk), /no local build/);
  assert.throws(() => chooseAsi(null, { ...loc, sha: 'e'.repeat(40) }, fs1, 'l.asi', hashOk), /built from eeeeeeee/);
  assert.throws(() => chooseAsi(null, loc, fs1, 'l.asi', () => 'h2'), /does not hash/);
  assert.throws(() => chooseAsi(null, loc, fs1, 'l.asi', () => null), /does not hash/, 'a missing file is no local build');
  // vcpkg deps donor: same baseline and deps present only
  const bl = 'd'.repeat(40);
  assert.equal(pickVcpkgDonor(bl, [{ dir: 'a', baseline: 'e'.repeat(40), hasDeps: true }, { dir: 'b', baseline: bl, hasDeps: false }, { dir: 'c', baseline: bl, hasDeps: true }]), 'c');
  assert.equal(pickVcpkgDonor(bl, [{ dir: 'a', baseline: 'e'.repeat(40), hasDeps: true }, { dir: 'b', baseline: null, hasDeps: true }]), null, 'other or unknown baseline: no donor');
  assert.equal(pickVcpkgDonor(null, [{ dir: 'a', baseline: null, hasDeps: true }]), null, 'unknown repo baseline: setup.ps1');
  assert.match(REWIND_BUILT_ON,/^v\d+\.\d+\.\d+$/);
  // release check on a throwaway repo: main = A (tag v1) - B (merge target); side branch C (tag v2) not merged
  const gr = mkdtempSync(join(tmpdir(), 'tc-rel-')), g = (...a: string[]) => execFileSync('git', ['-C', gr, '-c', 'user.name=t', '-c', 'user.email=t@t', ...a], { encoding: 'utf8' }).trim();
  try {
    g('init', '-q'); g('commit', '-q', '--allow-empty', '-m', 'A'); g('tag', '-a', 'v1', '-m', 'v1');
    g('commit', '-q', '--allow-empty', '-m', 'B'); const b = g('rev-parse', 'HEAD');
    g('checkout', '-q', '-b', 'side', 'v1'); g('commit', '-q', '--allow-empty', '-m', 'C'); g('tag', 'v2');
    assert.ok(releaseInFork(gr, 'v1', b), 'an older release merged in passes (annotated tag)');
    assert.ok(releaseInFork(gr, 'v2', g('rev-parse', 'HEAD')), 'the pin at the release itself passes');
    assert.ok(!releaseInFork(gr, 'v2', b), 'a release not merged into the pin is refused');
    assert.ok(!releaseInFork(gr, 'v9', b), 'an unknown tag is refused');
  } finally { rmSync(gr, { recursive: true, force: true }); }
  console.log('turbo-compat self-check OK');
}

// imported by night-plan.ts for STATES: the CLI runs only when this file is the entry point
if (import.meta.main) {
const { values: v, positionals: [cmd, dir, arg] } = parseArgs({ allowPositionals: true, options: {
  'all-on': { type: 'boolean' }, build: { type: 'boolean' }, f7: { type: 'boolean' }, 'max-min': { type: 'string' }, 'self-check': { type: 'boolean' } } });
if (v['self-check']) selfCheck();
else if (cmd === 'snapshot' && dir) await snapshot(resolve(dir));
else if (cmd === 'install-copy') await installCopy(!!v.build);
else if (cmd === 'state' && dir && arg) setState(resolve(dir), arg);
else if (cmd === 'monitor' && dir && arg) monitor(resolve(dir), arg, { allOn: !!v['all-on'], f7: !!v.f7, maxMin: Number(v['max-min'] ?? 40) });
else if (cmd === 'collect' && dir && arg) collect(resolve(dir), arg);
else if (cmd === 'restore' && dir) process.exit(restore(resolve(dir)) ? 0 : 1);
else { console.error('usage: node tools/turbo-compat.ts snapshot|restore <dir> | state <dir> <name> (apex = our fork build of FORK.sha; apex-stock = stock Apex, opt-in; names in STATES) | install-copy [--build] | monitor <dir> <arm> [--all-on] [--f7] | collect <dir> <arm> | --self-check'); process.exit(2); }
}
