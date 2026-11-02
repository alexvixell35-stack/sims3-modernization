// Is the game window in front? Runs are measured with the game NOT focused (Rodney, 2026-10-17: the game takes focus
// when it launches; a focused run is not comparable with unfocused ones). saveForeground() keeps the window that was in
// front before the queue started the game; giveFocusBack() puts it in front again if the game took focus.
import { execFileSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { GAME_NAME } from './game-exe.ts';

const STATE = join(resolve(import.meta.dirname, '..', '..'), 'probe-out', 'foreground.json');
const FG = "Add-Type 'using System; using System.Runtime.InteropServices; public static class Fg {" +
  " [DllImport(\"user32.dll\")] public static extern IntPtr GetForegroundWindow();" +
  " [DllImport(\"user32.dll\")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint p);" +
  " [DllImport(\"user32.dll\")] public static extern bool SetForegroundWindow(IntPtr h);" +
  " [DllImport(\"user32.dll\")] public static extern bool IsWindow(IntPtr h);" +
  " [DllImport(\"user32.dll\")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra); }'";
const ps = (script: string): string => {
  try { return execFileSync('powershell', ['-NoProfile', '-NonInteractive', '-Command', script], { timeout: 15000 }).toString().trim(); } catch { return ''; }
};

export function focusGame(): boolean {
  return ps(`$p = Get-Process '${GAME_NAME}' -ErrorAction SilentlyContinue | Select-Object -First 1; if (-not $p) { 'no game'; exit }; ` + FG +
    "; $id = 0; [void][Fg]::GetWindowThreadProcessId([Fg]::GetForegroundWindow(), [ref]$id); [bool]($id -eq $p.Id)") === 'True';
}

// Before the game starts: remember the foreground window unless it is the game itself.
export function saveForeground(): void {
  const out = ps(FG + "; $h = [Fg]::GetForegroundWindow(); $id = 0; [void][Fg]::GetWindowThreadProcessId($h, [ref]$id);" +
    " $n = (Get-Process -Id $id -ErrorAction SilentlyContinue).ProcessName; \"$([int64]$h) $n\"");
  const [hwnd, name] = out.split(' ');
  if (!hwnd || name === GAME_NAME) return;
  mkdirSync(dirname(STATE), { recursive: true });
  writeFileSync(STATE, JSON.stringify({ hwnd: Number(hwnd), name }));
}

// If the game is in front, put the saved window back (Alt tap first: Windows only lets a background process change the
// foreground after an input event). Returns what happened, for the run log.
export function giveFocusBack(): string {
  if (!focusGame()) return 'game not in front';
  if (!existsSync(STATE)) return 'game in front; no saved window to return to';
  const { hwnd, name } = JSON.parse(readFileSync(STATE, 'utf8'));
  ps(FG + `; $h = [IntPtr]${hwnd}; if ([Fg]::IsWindow($h)) { [Fg]::keybd_event(0x12, 0, 0, [UIntPtr]::Zero); [Fg]::keybd_event(0x12, 0, 2, [UIntPtr]::Zero); [void][Fg]::SetForegroundWindow($h) }`);
  return focusGame() ? `game still in front (could not return to ${name})` : `focus returned to ${name}`;
}

if (import.meta.main) {
  const a = process.argv[2];
  if (a === 'save') { saveForeground(); console.log('saved', existsSync(STATE) ? readFileSync(STATE, 'utf8') : '(game was in front, nothing saved)'); }
  else if (a === 'back') console.log(giveFocusBack());
  else console.log(focusGame() ? 'in front' : 'not in front');
}
