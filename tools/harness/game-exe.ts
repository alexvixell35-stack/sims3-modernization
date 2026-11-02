// Which game edition the harness drives. Defaults = the Steam edition; the EA app edition needs only environment settings:
//   TS3_BIN          Game\Bin folder (default: Steam library path)
//   TS3_EXE          game process/file name (default TS3W.exe; EA app: TS3.exe or Sims3Launcher.exe)
//   TS3_LAUNCH       direct (default: start <TS3_BIN>\<TS3_EXE>) | ea (EALauncher.exe with TS3_EA_URI, then wait for the TS3_EXE process)
//   TS3_EA_URI, TS3_EA_LAUNCHER   EA launch URI and EALauncher.exe path (defaults below)
//   TS3_MACHINE      machine tag (default main): result rows/files of any other machine carry it and never mix with the main machine's (docs/TESTING.md)
//   node tools/harness/game-exe.ts --self-check   no process is started
//   node tools/harness/game-exe.ts --installed    base game + EP/SP folders found under the game root
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';

export const MACHINE = process.env.TS3_MACHINE || 'main';
export const machineTag = (m = MACHINE) => (m === 'main' ? '' : m); // '' on the main machine: its file names and labels stay unchanged

export const GAME_EXE = process.env.TS3_EXE ?? 'TS3W.exe';
export const GAME_NAME = GAME_EXE.replace(/\.exe$/i, ''); // PowerShell Get-Process / Windows counters name
export const BIN = process.env.TS3_BIN ?? 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\The Sims 3\\Game\\Bin';
// Installed products from the game root (<TS3_BIN>/../..), same on Steam and the EA app: base game + every EPnn/SPnn folder that holds GameData.
// The "Installed" line in ScriptError xml is written by the game itself (not by the harness) from its own check; the HKLM Uninstall keys list the same set.
export const gameRoot = (bin = BIN) => resolve(bin, '..', '..');
export const installedProducts = (bin = BIN): string[] => {
  const root = gameRoot(bin);
  if (!existsSync(root)) return [];
  return ['BaseGame', ...readdirSync(root).filter(d => /^(EP|SP)\d+$/i.test(d) && existsSync(join(root, d, 'GameData'))).sort()];
};
export const LAUNCH = process.env.TS3_LAUNCH ?? 'direct';
export const EA_URI = process.env.TS3_EA_URI ?? 'origin2://game/launch/?offerIds=sims3_dd,1002441,70384_CE,70384_Unrevised';
export const EA_LAUNCHER = process.env.TS3_EA_LAUNCHER ?? 'C:\\Program Files\\Electronic Arts\\EA Desktop\\EA Desktop\\EALauncher.exe';
export const isSteamGame = (exe = GAME_EXE) => exe.toLowerCase() === 'ts3w.exe';
// EA Desktop restarts a crashed game as `<GAME_EXE> -test` about 2 min after the crash (2026-11-02 13:07 crash, 13:09 relaunch).
// Called once the game is confirmed gone and nothing is launching: any new GAME_EXE with -test in that window is the relaunch. Steam: no-op.
export const relaunchPids = (psOut: string): string[] => [...psOut.matchAll(/^(\d+) .*\s-test\b/gm)].map(m => m[1]);
export async function killEaRelaunch(log: (l: string) => void, seconds = 150) {
  if (LAUNCH !== 'ea') return;
  for (let t = 0; t < seconds; t += 5) {
    await new Promise(r => setTimeout(r, 5000));
    let out = '';
    try { out = execFileSync('powershell', ['-NoProfile', '-Command', `Get-CimInstance Win32_Process -Filter "Name='${GAME_EXE}'" | % { "$($_.ProcessId) $($_.CommandLine)" }`], { timeout: 20000 }).toString(); } catch { continue; }
    for (const pid of relaunchPids(out)) {
      try { execFileSync('taskkill', ['/F', '/T', '/PID', pid], { stdio: 'ignore' }); } catch { /* gone */ }
      log(`EA app relaunch killed: ${GAME_EXE} PID ${pid} (-test) ${t + 5} s after the crash`);
    }
  }
}
export const gameRunning = () => { try { return execFileSync('tasklist', ['/NH', '/FI', `IMAGENAME eq ${GAME_EXE}`]).toString().includes(GAME_EXE); } catch { return true; } }; // tasklist failed: no verdict

if (import.meta.main) {
  if (process.argv[2] === '--self-check') {
    const assert = (await import('node:assert')).strict;
    assert.ok(isSteamGame('TS3W.exe') && isSteamGame('ts3w.EXE') && !isSteamGame('TS3.exe') && !isSteamGame('Sims3Launcher.exe'));
    assert.ok(GAME_EXE.length > 0 && GAME_NAME === GAME_EXE.replace(/\.exe$/i, '') && !/\.exe$/i.test(GAME_NAME));
    assert.ok(['direct', 'ea'].includes(LAUNCH), `TS3_LAUNCH "${LAUNCH}": use direct or ea`);
    assert.match(EA_URI, /^origin2:\/\/game\/launch\/\?offerIds=/);
    assert.equal(machineTag('main'), ''); assert.equal(machineTag('ea-laptop'), 'ea-laptop');
    assert.deepEqual(relaunchPids('28624 "C:/EA Games/The Sims 3/Game/Bin/Sims3Launcher.exe"  -test\r\n100 "C:/x/Sims3Launcher.exe"\r\n\r\n'), ['28624']);
    assert.deepEqual(relaunchPids(''), []);
    const tmp = mkdtempSync(join(tmpdir(), 'ts3-game-')); // Steam (<lib>/steamapps/common/The Sims 3) and EA (Program Files/EA Games/The Sims 3) layouts: same shape under different roots
    try {
      for (const root of ['steam/The Sims 3', 'ea/The Sims 3']) {
        mkdirSync(join(tmp, root, 'Game', 'Bin'), { recursive: true });
        for (const d of ['EP01', 'EP11', 'SP08']) mkdirSync(join(tmp, root, d, 'GameData'), { recursive: true });
        mkdirSync(join(tmp, root, 'EP02'), { recursive: true }); mkdirSync(join(tmp, root, 'Support'), { recursive: true }); // no GameData / not a pack
        assert.deepEqual(installedProducts(join(tmp, root, 'Game', 'Bin')), ['BaseGame', 'EP01', 'EP11', 'SP08'], root);
      }
      assert.deepEqual(installedProducts(join(tmp, 'none', 'Game', 'Bin')), [], 'missing root');
    } finally { rmSync(tmp, { recursive: true, force: true }); }
    console.log('self-check ok');
  } else if (process.argv[2] === '--installed') console.log(`${gameRoot()}: ${installedProducts().join(' ')}`);
  else { console.error('usage: node tools/harness/game-exe.ts --self-check | --installed'); process.exit(2); }
}
