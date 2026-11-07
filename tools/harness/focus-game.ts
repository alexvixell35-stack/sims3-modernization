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
  " [DllImport(\"user32.dll\")] public static extern bool AllowSetForegroundWindow(int pid);" +
  " [DllImport(\"user32.dll\")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);" +
  " [DllImport(\"kernel32.dll\")] public static extern uint GetCurrentThreadId(); }'";
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

// If the game is in front, put the saved window back. No synthetic keys (an Alt tap or space could reach the user's app,
// e.g. pause a video): AllowSetForegroundWindow + AttachThreadInput to the foreground thread, then SetForegroundWindow.
// One try per call; callers never loop. Returns what happened, for the run log.
export const returnFocusPs = (hwnd: number): string => FG + `; $h = [IntPtr]${hwnd}; if ([Fg]::IsWindow($h)) { $id = 0; $t = [Fg]::GetWindowThreadProcessId([Fg]::GetForegroundWindow(), [ref]$id); $me = [Fg]::GetCurrentThreadId(); [void][Fg]::AllowSetForegroundWindow(-1); [void][Fg]::AttachThreadInput($me, $t, $true); [void][Fg]::SetForegroundWindow($h); [void][Fg]::AttachThreadInput($me, $t, $false) }`;
export function giveFocusBack(): string {
  if (!focusGame()) return 'game not in front';
  if (!existsSync(STATE)) return 'game in front; no saved window to return to';
  const { hwnd, name } = JSON.parse(readFileSync(STATE, 'utf8'));
  ps(returnFocusPs(hwnd));
  return focusGame() ? `game still in front (could not return to ${name})` : `focus returned to ${name}`;
}

if (import.meta.main) {
  const a = process.argv[2];
  if (a === '--self-check') {
    const s = returnFocusPs(1);
    console.assert(!/keybd_event|SendKeys|SendInput/.test(s), 'focus return must not synthesize keys');
    console.assert(s.includes('AttachThreadInput') && s.includes('[IntPtr]1;'), 'attach method + hwnd');
    console.log('focus-game self-check ok'); process.exit(0);
  }
  if (a === 'save') { saveForeground(); console.log('saved', existsSync(STATE) ? readFileSync(STATE, 'utf8') : '(game was in front, nothing saved)'); }
  else if (a === 'back') console.log(giveFocusBack());
  else console.log(focusGame() ? 'in front' : 'not in front');
}
