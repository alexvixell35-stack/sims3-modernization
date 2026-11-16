// Test isolation (Rodney 2026-11-15, docs/270 §20): an outbound Windows Firewall block for TS3W.exe while a test chain runs, so
// EA online code cannot reach the internet and urlmon's cross-process zone map (UrlZonesSM) is not touched by network calls.
// Changing a firewall rule needs admin; this session is not elevated. So Rodney sets it up once (elevated PowerShell):
//   node tools/harness/net-block.ts setup     prints the one-time commands: a DISABLED block rule plus two scheduled tasks
//                                             that run elevated and only enable / disable that rule
// and chains then use, without elevation (a user may run his own tasks, schtasks /run):
//   node tools/harness/net-block.ts on        enable the rule (chain start); exit 1 if the tasks are missing
//   node tools/harness/net-block.ts off       disable it (chain end; also from `trap ... EXIT`)
//   node tools/harness/net-block.ts status    rule enabled / disabled / missing
//   node tools/harness/net-block.ts --self-check
// Chain use:  node tools/harness/net-block.ts on && trap 'node tools/harness/net-block.ts off' EXIT
// The rule matches TS3W.exe of the Steam install, so it also blocks Rodney's own play while it is on: `off` at every exit.
import { execFileSync } from 'node:child_process';
import { join } from 'node:path';
import { BIN } from './game-exe.ts';

export const RULE = 'TS3 test offline';
export const TASK_ON = 'TS3 test offline on', TASK_OFF = 'TS3 test offline off';
const EXE = join(BIN, 'TS3W.exe');

export function setupCommands(exe = EXE): string[] {
  const act = (verb: string) => `(New-ScheduledTaskAction -Execute powershell.exe -Argument "-NoProfile -Command ${verb}-NetFirewallRule -DisplayName '${RULE}'")`;
  return [
    `New-NetFirewallRule -DisplayName '${RULE}' -Direction Outbound -Action Block -Program '${exe}' -Enabled False`,
    `Register-ScheduledTask -TaskName '${TASK_ON}' -Action ${act('Enable')} -RunLevel Highest -User $env:USERNAME`,
    `Register-ScheduledTask -TaskName '${TASK_OFF}' -Action ${act('Disable')} -RunLevel Highest -User $env:USERNAME`,
  ];
}
const ps = (cmd: string) => execFileSync('powershell.exe', ['-NoProfile', '-Command', cmd], { encoding: 'utf8', timeout: 30000 }).trim();
export function status(): 'enabled' | 'disabled' | 'missing' {
  const s = ps(`$r = Get-NetFirewallRule -DisplayName '${RULE}' -ErrorAction SilentlyContinue; if (!$r) { 'missing' } elseif ($r.Enabled -eq 'True') { 'enabled' } else { 'disabled' }`);
  return s === 'enabled' || s === 'disabled' ? s : 'missing';
}
function run(task: string, want: 'enabled' | 'disabled') {
  try { execFileSync('schtasks.exe', ['/run', '/tn', task], { stdio: 'ignore', timeout: 30000 }); }
  catch { console.error(`net-block: task "${task}" missing or not runnable: run \`node tools/harness/net-block.ts setup\` once (elevated)`); process.exit(1); }
  for (let i = 0; i < 20; i++) {  // the task runs asynchronously: up to ~10 s
    if (status() === want) { console.log(`net-block: rule "${RULE}" ${want}`); return; }
    execFileSync('powershell.exe', ['-NoProfile', '-Command', 'Start-Sleep -Milliseconds 500'], { timeout: 10000 });
  }
  console.error(`net-block: rule "${RULE}" is not ${want} after the task ran (status ${status()})`);
  process.exit(1);
}

const cmd = process.argv[2];
if (cmd === '--self-check') {
  const c = setupCommands('X:\\Bin\\TS3W.exe');
  if (c.length !== 3 || !c[0].includes("-Program 'X:\\Bin\\TS3W.exe'") || !c[0].includes('-Enabled False') || !c[1].includes(`Enable-NetFirewallRule -DisplayName '${RULE}'`) ||
      !c[2].includes('Disable-NetFirewallRule') || !c.slice(1).every(x => x.includes('-RunLevel Highest'))) throw new Error('setup commands');
  console.log('self-check ok');
} else if (cmd === 'setup') {
  console.log('# once, in an elevated PowerShell (the rule starts disabled; only these two tasks change it):');
  for (const l of setupCommands()) console.log(l);
} else if (cmd === 'on') run(TASK_ON, 'enabled');
else if (cmd === 'off') run(TASK_OFF, 'disabled');
else if (cmd === 'status') console.log(status());
else { console.error('usage: node tools/harness/net-block.ts setup | on | off | status | --self-check'); process.exit(2); }
