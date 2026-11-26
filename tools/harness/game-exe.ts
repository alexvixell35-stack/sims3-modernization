// Which game edition the harness drives. Defaults = the Steam edition; the EA app edition needs only environment settings:
//   TS3_BIN          Game\Bin folder (default: Steam library path)
//   TS3_EXE          game process/file name (default TS3W.exe; EA app: TS3.exe or Sims3Launcher.exe)
//   TS3_LAUNCH       direct (default: start <TS3_BIN>\<TS3_EXE>) | ea (EALauncher.exe with TS3_EA_URI, then wait for the TS3_EXE process)
//   TS3_EA_URI, TS3_EA_LAUNCHER   EA launch URI and EALauncher.exe path (defaults below)
//   TS3_MACHINE      machine tag (default main): result rows/files of any other machine carry it and never mix with the main machine's (docs/TESTING.md)
//   node tools/harness/game-exe.ts --self-check   no process is started
//   node tools/harness/game-exe.ts --installed    base game + EP/SP folders found under the game root
//   node tools/harness/game-exe.ts --vram         write or confirm <TS3_BIN>\ts3native-vram.ini (dxdiag-vram.exe, docs/284)
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { basename, dirname, join, resolve } from 'node:path';

export const MACHINE = process.env.TS3_MACHINE || 'main';
export const machineTag = (m = MACHINE) => (m === 'main' ? '' : m); // '' on the main machine: its file names and labels stay unchanged

export const GAME_EXE = process.env.TS3_EXE ?? 'TS3W.exe';
export const GAME_NAME = GAME_EXE.replace(/\.exe$/i, ''); // PowerShell Get-Process / Windows counters name
export const BIN = process.env.TS3_BIN ?? 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\The Sims 3\\Game\\Bin';
// We never install Sims3MonoModder (S3MM): its profiler is not compatible with ts3native (docs/291). Test runs refuse a Bin that holds it;
// the installer refuses to copy a file with such a name. Folders: Bin and the ASI loader's plugin folders.
export const S3MM_RE = /monomodder|s3mm/i;
export const s3mmFiles = (bin = BIN): string[] => ['', 'Plugins', 'plugins', 'asi', 'scripts'].flatMap(d => {
  const p = join(bin, d);
  return existsSync(p) ? readdirSync(p).filter(f => S3MM_RE.test(f)).map(f => join(p, f)) : [];
}).filter((f, i, a) => a.findIndex(x => x.toLowerCase() === f.toLowerCase()) === i); // Windows: Plugins and plugins are one folder
// Installed products from the game root (<TS3_BIN>/../..), same on Steam and the EA app: base game + every EPnn/SPnn folder that holds GameData.
// The "Installed" line in ScriptError xml is written by the game itself (not by the harness) from its own check; the HKLM Uninstall keys list the same set.
// Retail (disc) installs put each pack in a sibling folder of the game root instead ("<root> World Adventures" next to
// "<root>", docs/273): those count too, named by the pack code below.
export const gameRoot = (bin = BIN) => resolve(bin, '..', '..');
// shortcut: name keys of the retail sibling folders, not checked on a real disc install; an unknown name is listed as the folder name
const PACK_NAMES: [RegExp, string][] = [[/world adventures/i, 'EP01'], [/ambitions/i, 'EP02'], [/late night/i, 'EP03'], [/generations/i, 'EP04'],
  [/pets/i, 'EP05'], [/showtime/i, 'EP06'], [/supernatural/i, 'EP07'], [/seasons/i, 'EP08'], [/university/i, 'EP09'], [/island paradise/i, 'EP10'],
  [/into the future/i, 'EP11'], [/high.?end loft/i, 'SP01'], [/fast lane/i, 'SP02'], [/outdoor living/i, 'SP03'], [/town life/i, 'SP04'],
  [/master suite/i, 'SP05'], [/katy perry|sweet treats/i, 'SP06'], [/diesel/i, 'SP07'], [/70s|80s|90s/i, 'SP08'], [/movie/i, 'SP09']];
export const installedProducts = (bin = BIN): string[] => {
  const root = gameRoot(bin);
  if (!existsSync(root)) return [];
  const parent = dirname(root), base = basename(root) + ' ';
  const siblings = readdirSync(parent).filter(d => d.startsWith(base) && existsSync(join(parent, d, 'GameData')))
    .map(d => PACK_NAMES.find(([re]) => re.test(d.slice(base.length)))?.[1] ?? d);
  const own = readdirSync(root).filter(d => /^(EP|SP)\d+$/i.test(d) && existsSync(join(root, d, 'GameData')));
  return ['BaseGame', ...new Set([...own, ...siblings].sort())];
};
export const LAUNCH = process.env.TS3_LAUNCH ?? 'direct';
export const EA_URI = process.env.TS3_EA_URI ?? 'origin2://game/launch/?offerIds=sims3_dd,1002441,70384_CE,70384_Unrevised';
export const EA_LAUNCHER = process.env.TS3_EA_LAUNCHER ?? 'C:\\Program Files\\Electronic Arts\\EA Desktop\\EA Desktop\\EALauncher.exe';
export const isSteamGame = (exe = GAME_EXE) => exe.toLowerCase() === 'ts3w.exe';
// PIDs of GAME_EXE seen by gameRunning() in this process: crash pickup takes only reports whose dump names one of them (crash-ledger.ts --pids),
// so an old xcpt or another process's crash is never blamed on this run. Callers clear it when their run starts.
export const seenPids = new Set<number>();
// Game processes the harness did not launch or track. The old rule "a GAME_EXE with -test is EA's crash relaunch" was wrong:
// every xcpt on the EA laptop on 2026-11-02 shows `-test`, the harness's own launches too (docs/236 #20). An untracked game
// process is logged with its command line, never killed and never assumed to be a relaunch. Steam: no-op.
export const untrackedPids = (psOut: string, known: Set<number>): { pid: number; cmd: string }[] =>
  [...psOut.matchAll(/^(\d+) (.*)$/gm)].map(m => ({ pid: Number(m[1]), cmd: m[2].trim() })).filter(p => !known.has(p.pid));
export async function logUntrackedGame(log: (l: string) => void, seconds = 150) {
  if (LAUNCH !== 'ea') return;
  const logged = new Set<number>();
  for (let t = 0; t < seconds; t += 5) {
    await new Promise(r => setTimeout(r, 5000));
    let out = '';
    try { out = execFileSync('powershell', ['-NoProfile', '-Command', `Get-CimInstance Win32_Process -Filter "Name='${GAME_EXE}'" | % { "$($_.ProcessId) $($_.CommandLine)" }`], { timeout: 20000 }).toString(); } catch { continue; }
    for (const p of untrackedPids(out, seenPids)) if (!logged.has(p.pid)) { logged.add(p.pid); log(`untracked ${GAME_EXE} PID ${p.pid} ${t + 5} s after the game left: ${p.cmd} (not launched or seen by this run; not killed, not assumed a relaunch)`); }
  }
}
// docs/236 open 6: a PID that first shows up while none of the PIDs already seen is alive is a relaunch (EA's crash relaunch), not this run's
// game. It goes to `relaunched` and stays out of seenPids, so crash pickup never blames it on this run and logUntrackedGame reports it.
export const relaunched = new Set<number>();
export function noteSeen(seen: Set<number>, rel: Set<number>, alive: number[]) {
  const fresh = alive.filter(p => !seen.has(p) && !rel.has(p)), anyOld = alive.some(p => seen.has(p));
  for (const p of fresh) (seen.size && !anyOld ? rel : seen).add(p);
}
export const csvPids = (csv: string): number[] => [...csv.matchAll(/^"[^"]*","(\d+)"/gm)].map(m => Number(m[1]));
export const gameRunning = () => {
  try {
    const out = execFileSync('tasklist', ['/NH', '/FO', 'CSV', '/FI', `IMAGENAME eq ${GAME_EXE}`]).toString();
    noteSeen(seenPids, relaunched, csvPids(out));
    return out.includes(GAME_EXE);
  } catch { return true; } // tasklist failed: no verdict
};

// After a failed or hung launch: the game and, on EA, the per-launch helpers go. Never the EA app itself (EADesktop.exe: the sign-in and
// the entitlement the next launch needs). Returns what was running, for the log (docs/236 round 4: launcher-stage hangs).
export const LEFTOVER_EXES = (launch = LAUNCH, game = GAME_EXE) => [game, ...(launch === 'ea' ? ['EALauncher.exe', 'TSLHelper.exe'] : [])];
export function killLeftovers(): string[] {
  const found: string[] = [];
  for (const n of LEFTOVER_EXES()) {
    let out = '';
    try { out = execFileSync('tasklist', ['/NH', '/FO', 'CSV', '/FI', `IMAGENAME eq ${n}`]).toString(); } catch { continue; }
    const pids = csvPids(out);
    if (!pids.length) continue;
    found.push(`${n} ${pids.join(',')}`);
    try { execFileSync('taskkill', ['/F', '/T', '/IM', n], { stdio: 'ignore' }); } catch { /* gone meanwhile */ }
  }
  return found;
}

// docs/284: [Startup] DxDiagCache's sidecar <Bin>\ts3native-vram.ini, written once per machine key by dxdiag-vram.exe (one DxDiag
// query outside the game, ~60 s, no game launch); a current sidecar returns at once. Never throws: without it the first launch
// with DxDiagCache=1 probes and writes it itself. Returns the helper's last line (or why it did not run) for the caller's log.
export const VRAM_HELPER = join(import.meta.dirname, '..', '..', 'native', 'ts3native', 'out', 'dxdiag-vram.exe');
export function ensureVramSidecar(bin = BIN, helper = VRAM_HELPER): string {
  if (!existsSync(helper)) return `dxdiag-vram: ${helper} not built (native\\ts3native\\build.cmd), the first DxDiagCache=1 launch probes`;
  try { return execFileSync(helper, [bin], { timeout: 300_000, stdio: ['ignore', 'pipe', 'pipe'] }).toString().trim().split(/\r?\n/).pop() ?? ''; }
  catch (e: any) { return `dxdiag-vram: failed (${e.status ?? e.code ?? e}): ${String(e.stdout ?? '').trim().split(/\r?\n/).pop() ?? ''}`; }
}

if (import.meta.main) {
  if (process.argv[2] === '--self-check') {
    { const r = ensureVramSidecar(BIN, join(tmpdir(), 'no-such-dxdiag-vram.exe')); if (!/not built/.test(r)) throw new Error(`ensureVramSidecar without helper: ${r}`); }
    const assert = (await import('node:assert')).strict;
    assert.deepEqual(LEFTOVER_EXES('ea', 'Sims3Launcher.exe'), ['Sims3Launcher.exe', 'EALauncher.exe', 'TSLHelper.exe']);
    assert.deepEqual(LEFTOVER_EXES('direct', 'TS3W.exe'), ['TS3W.exe']);
    assert.ok(!LEFTOVER_EXES('ea').some(n => /EADesktop/i.test(n)), 'never the EA app');
    assert.ok(isSteamGame('TS3W.exe') && isSteamGame('ts3w.EXE') && !isSteamGame('TS3.exe') && !isSteamGame('Sims3Launcher.exe'));
    assert.ok(GAME_EXE.length > 0 && GAME_NAME === GAME_EXE.replace(/\.exe$/i, '') && !/\.exe$/i.test(GAME_NAME));
    assert.ok(['direct', 'ea'].includes(LAUNCH), `TS3_LAUNCH "${LAUNCH}": use direct or ea`);
    assert.match(EA_URI, /^origin2:\/\/game\/launch\/\?offerIds=/);
    assert.equal(machineTag('main'), ''); assert.equal(machineTag('ea-laptop'), 'ea-laptop');
    // docs/236 #20: only PIDs the run did not see are reported, whatever their command line; -test alone means nothing
    const ps = '28624 "C:/EA Games/The Sims 3/Game/Bin/Sims3Launcher.exe"  -test\r\n100 "C:/x/Sims3Launcher.exe" -test\r\n\r\n';
    assert.deepEqual(untrackedPids(ps, new Set([100])), [{ pid: 28624, cmd: '"C:/EA Games/The Sims 3/Game/Bin/Sims3Launcher.exe"  -test' }]);
    assert.deepEqual(untrackedPids(ps, new Set([100, 28624])), []);
    assert.deepEqual(untrackedPids('', new Set()), []);
    { const seen = new Set<number>(), rel = new Set<number>(); // first PID is the run's; a PID appearing after it is gone is a relaunch
      noteSeen(seen, rel, [10]); noteSeen(seen, rel, [10, 11]); noteSeen(seen, rel, [12]); noteSeen(seen, rel, [12, 10]);
      assert.deepEqual([[...seen], [...rel]], [[10, 11], [12]]); }
    // the running game is found by GAME_EXE/GAME_NAME only (7d: a soak loop watched TS3W on the EA laptop, where the game is Sims3Launcher.exe)
    const hard = /Get-Process\s+'?TS3W\b|IMAGENAME eq TS3W|frida\.attach\('TS3W|'-n', 'TS3W|\/IM', 'TS3W|Name='TS3W/;
    for (const f of readdirSync(import.meta.dirname).filter(f => /\.(ts|mjs|js)$/.test(f) && f !== 'game-exe.ts')) {
      const bad = readFileSync(join(import.meta.dirname, f), 'utf8').split('\n').findIndex(l => !/^\s*\/\//.test(l) && hard.test(l));
      assert.equal(bad, -1, `${f}:${bad + 1}: hard-coded TS3W process name; use GAME_EXE / GAME_NAME from game-exe.ts`);
    }
    assert.deepEqual(csvPids('"Sims3Launcher.exe","14068","Console","1","2,000 K"\r\n"Sims3Launcher.exe","7","Console","1","1 K"\r\n'), [14068, 7]);
    assert.deepEqual(csvPids('INFO: No tasks are running which match the specified criteria.\r\n'), []);
    const tmp = mkdtempSync(join(tmpdir(), 'ts3-game-')); // Steam (<lib>/steamapps/common/The Sims 3) and EA (Program Files/EA Games/The Sims 3) layouts: same shape under different roots
    try {
      for (const root of ['steam/The Sims 3', 'ea/The Sims 3']) {
        mkdirSync(join(tmp, root, 'Game', 'Bin'), { recursive: true });
        for (const d of ['EP01', 'EP11', 'SP08']) mkdirSync(join(tmp, root, d, 'GameData'), { recursive: true });
        mkdirSync(join(tmp, root, 'EP02'), { recursive: true }); mkdirSync(join(tmp, root, 'Support'), { recursive: true }); // no GameData / not a pack
        assert.deepEqual(installedProducts(join(tmp, root, 'Game', 'Bin')), ['BaseGame', 'EP01', 'EP11', 'SP08'], root);
      }
      assert.deepEqual(installedProducts(join(tmp, 'none', 'Game', 'Bin')), [], 'missing root');
      // retail (disc): each pack a sibling folder of the base game's, with its own GameData (Game\Bin alone is not a pack)
      const ret = join(tmp, 'retail');
      mkdirSync(join(ret, 'The Sims 3', 'Game', 'Bin'), { recursive: true });
      for (const d of ['The Sims 3 World Adventures', 'The Sims 3 Into the Future', 'The Sims 3 70s, 80s, & 90s Stuff', 'The Sims 3 Unknown Pack'])
        mkdirSync(join(ret, d, 'GameData'), { recursive: true });
      mkdirSync(join(ret, 'The Sims 3 Ambitions', 'Game', 'Bin'), { recursive: true });
      mkdirSync(join(ret, 'The Sims 2', 'GameData'), { recursive: true });
      assert.deepEqual(installedProducts(join(ret, 'The Sims 3', 'Game', 'Bin')), ['BaseGame', 'EP01', 'EP11', 'SP08', 'The Sims 3 Unknown Pack'], 'retail siblings');
    } finally { rmSync(tmp, { recursive: true, force: true }); }
    { const b = mkdtempSync(join(tmpdir(), 'ts3-s3mm-')); // S3MM guard: Bin and plugin folders, any case; other ASIs pass
      try {
        mkdirSync(join(b, 'Plugins')); for (const f of ['ts3native.asi', 'Sims3SettingsSetter.asi', 'd3d9.dll']) writeFileSync(join(b, f), '');
        assert.deepEqual(s3mmFiles(b), []);
        writeFileSync(join(b, 'Plugins', 'Sims3MonoModder.asi'), ''); writeFileSync(join(b, 'S3MM.toml'), '');
        assert.deepEqual(s3mmFiles(b).map(f => basename(f)).sort(), ['S3MM.toml', 'Sims3MonoModder.asi']);
        assert.ok(S3MM_RE.test('sims3monomodder.ASI') && !S3MM_RE.test('S3SS.asi') && !S3MM_RE.test('Sims3SettingsSetter.asi'));
      } finally { rmSync(b, { recursive: true, force: true }); } }
    console.log('self-check ok');
  } else if (process.argv[2] === '--installed') console.log(`${gameRoot()}: ${installedProducts().join(' ')}`);
  else if (process.argv[2] === '--vram') console.log(ensureVramSidecar());
  else { console.error('usage: node tools/harness/game-exe.ts --self-check | --installed | --vram'); process.exit(2); }
}
