// Noise mitigation for game test runs while the laptop is in use (Rodney, 2026-10-20: videos, other work beside a queue).
// While a job's game is up, one PowerShell loop (every 5 s) keeps:
//   TS3W.exe on the game CPUs (default logical CPUs 4-15 = physical cores 2-7 with SMT on the i7-11800H) at priority High;
//   heavy user apps of the same user (browsers, video players, IDE; name list below) on the remaining CPUs (default 0-3).
// The first time an app is moved, its original affinity goes to a state file (tmpdir); restorePins() puts it back after the
// job, on exit/Ctrl-C, and at the next queue start (a killed run). Only processes in this user's session; never kills
// anything; elevated or other-user processes fail the set and are skipped. No-op off Windows.
// Off: TS3_PIN=off (everything), TS3_PIN_OTHERS=off (only the app moves), TS3_GAME_PRIORITY=off (affinity only);
// TS3_GAME_CPUS="4-15" (the rest go to the apps), TS3_PIN_NAMES="chrome,vlc,..." (replaces the default list),
// TS3_MACHINE=ea-laptop (per-machine game CPU set, MACHINES below; unset = the main machine's default; TS3_GAME_CPUS wins).
// Queue job (or queue-level { "pin": ... } beside "jobs"): "pin": false | { cpus?: "4-15", priority?: "High" | "AboveNormal" | "Normal" | "off", others?: boolean | [names] }.
//   node tools/harness/pin.ts --self-check     mask math only, touches nothing
import { execFileSync, spawn } from 'node:child_process';
import { existsSync } from 'node:fs';
import { availableParallelism, tmpdir } from 'node:os';
import { join } from 'node:path';
import { GAME_EXE, GAME_NAME, MACHINE } from './game-exe.ts';

export type PinOpts = { cpus?: string; priority?: string; others?: boolean | string[] };
export type PinCfg = { cpus: string; gameMask: bigint; otherMask: bigint; fullMask: bigint; priority: string | null; others: string[] };
export const DEFAULT_APPS = ['chrome', 'msedge', 'firefox', 'brave', 'opera', 'vlc', 'mpc-hc64', 'mpc-hc', 'mpc-be64', 'PotPlayerMini64', 'mpv',
  'Teams', 'ms-teams', 'Spotify', 'Discord', 'Code', 'idea64', 'java'];
const PRIORITIES = ['Normal', 'AboveNormal', 'High']; // never RealTime: it can starve input and the disk
const STATE = join(tmpdir(), 'ts3-harness-pin-state.jsonl');
const N = availableParallelism();

// "4-15" or "0-3,8,10-11" -> bit mask; throws on syntax, empty set or a CPU >= n
export function cpuMask(spec: string, n = N): bigint {
  let m = 0n;
  for (const part of spec.split(',').map(s => s.trim())) {
    const r = /^(\d+)(?:-(\d+))?$/.exec(part);
    if (!r) throw new Error(`cpu set "${spec}": expected e.g. 4-15 or 0-3,8`);
    const a = Number(r[1]), b = Number(r[2] ?? r[1]);
    if (a > b || b >= n) throw new Error(`cpu set "${spec}": ${part} outside 0-${n - 1}`);
    for (let i = a; i <= b; i++) m |= 1n << BigInt(i);
  }
  if (!m) throw new Error(`cpu set "${spec}" is empty`);
  return m;
}
export const maskCpus = (m: bigint) => [...m.toString(2)].reverse().flatMap((b, i) => (b === '1' ? [i] : []));
// default: everything from CPU 4 up; null on machines too small to split
export const defaultCpus = (n = N) => (n > 8 ? `4-${n - 1}` : null);
// ea-laptop: Core Ultra 7 155U, 14 logical CPUs (GetLogicalProcessorInformationEx, docs/230): P-cores (EfficiencyClass 1, SMT) =
// logical 0-1 and 10-11, E-cores 2-9, LP E-cores 12-13. Game on both P-cores + 4 E-cores, apps on the remaining 6.
export const MACHINES: Record<string, string> = { 'ea-laptop': '0-5,10-11' };
export const gameCpus = (env = process.env, n = N) => {
  if (env.TS3_GAME_CPUS) return env.TS3_GAME_CPUS;
  const m = env === process.env ? MACHINE : env.TS3_MACHINE ?? 'main'; // an injected env (self-check) is read as is
  if (m === 'main') return defaultCpus(n);
  if (!(m in MACHINES)) throw new Error(`TS3_MACHINE "${m}": use ${Object.keys(MACHINES).join(', ')} or unset`);
  return MACHINES[m];
};

export function pinConfig(job?: false | PinOpts, env: NodeJS.ProcessEnv = process.env, n = N): PinCfg | null {
  if (job === false || env.TS3_PIN === 'off') return null;
  // priority checked before the small-machine exit: a bad queue fails on every machine (CI runners have <= 8 CPUs)
  const priority = job?.priority ?? env.TS3_GAME_PRIORITY ?? 'High';
  if (priority !== 'off' && !PRIORITIES.includes(priority)) throw new Error(`pin priority "${priority}": use ${PRIORITIES.join(', ')} or off`);
  const cpus = job?.cpus ?? gameCpus(env, n);
  if (!cpus) return null;
  const fullMask = (1n << BigInt(n)) - 1n, gameMask = cpuMask(cpus, n), otherMask = fullMask & ~gameMask;
  const others = !otherMask || job?.others === false || env.TS3_PIN_OTHERS === 'off' ? []
    : Array.isArray(job?.others) ? job.others : env.TS3_PIN_NAMES ? env.TS3_PIN_NAMES.split(',').map(s => s.trim()).filter(Boolean) : DEFAULT_APPS;
  if (others.some(x => !/^[\w.-]+$/.test(x))) throw new Error(`pin others: process names only (no .exe, no paths): ${others.join(',')}`);
  return { cpus, gameMask, otherMask, fullMask, priority: priority === 'off' ? null : priority, others };
}
export const describePin = (c: PinCfg | null) => (!c ? 'pin off'
  : `${GAME_EXE} -> CPUs ${c.cpus}${c.priority ? ` priority ${c.priority}` : ''}; ${c.others.length ? `${c.others.length} app names -> CPUs ${maskCpus(c.otherMask).join(',')}, restored after the job` : 'other apps untouched'}`);

const q = (s: string) => `'${s.replace(/'/g, "''")}'`;
const names = (c: PinCfg) => c.others.map(q).join(',');

// background loop for one job; returns stop(). Exits on its own when this node process is gone.
export function startPinner(c: PinCfg | null): () => void {
  if (!c || process.platform !== 'win32') return () => {};
  const p = spawn('powershell', ['-NoProfile', '-NonInteractive', '-Command', pinnerScript(c)], { stdio: 'ignore', windowsHide: true });
  return () => { try { p.kill(); } catch { /* gone */ } };
}
export const pinnerScript = (c: PinCfg) => `
$ErrorActionPreference = 'SilentlyContinue'
$me = (Get-Process -Id $PID).SessionId
${c.otherMask ? `[Diagnostics.Process]::GetCurrentProcess().ProcessorAffinity = [IntPtr][long]${c.otherMask}` : ''}
$seen = @{}
while (Get-Process -Id ${process.pid}) {
  foreach ($p in Get-Process -Name ${GAME_NAME}) {
    if ($p.SessionId -ne $me) { continue }
    try { if ($p.ProcessorAffinity.ToInt64() -ne ${c.gameMask}) { $p.ProcessorAffinity = [IntPtr][long]${c.gameMask} } } catch {}
    ${c.priority ? `try { if ("$($p.PriorityClass)" -ne '${c.priority}') { $p.PriorityClass = '${c.priority}' } } catch {}` : ''}
  }
  ${c.others.length ? `foreach ($p in Get-Process -Name @(${names(c)})) {
    if ($p.SessionId -ne $me) { continue }
    try {
      $a = $p.ProcessorAffinity.ToInt64()
      if ($a -eq ${c.otherMask}) { continue }
      $k = "$($p.Id)/$($p.StartTime.Ticks)"
      if (-not $seen.ContainsKey($k)) { Add-Content -Path ${q(STATE)} -Value (@{ pid = $p.Id; ticks = $p.StartTime.Ticks; name = $p.ProcessName; mask = $a } | ConvertTo-Json -Compress); $seen[$k] = 1 }
      $p.ProcessorAffinity = [IntPtr][long]${c.otherMask}
    } catch {}
  }` : ''}
  Start-Sleep -Seconds 5
}`;

// Put moved apps back (state file: same pid and start time only). Apps started during the run inherited the narrowed mask
// from their parent: those still on exactly otherMask get all CPUs.
// shortcut: an app whose own original mask was exactly otherMask also ends up on all CPUs; record those too if that matters.
export function restorePins(c: PinCfg | null = pinConfig({})): string {
  if (process.platform !== 'win32' || (!existsSync(STATE) && !c?.others.length)) return '';
  try { return execFileSync('powershell', ['-NoProfile', '-NonInteractive', '-Command', restoreScript(c)], { timeout: 60000, windowsHide: true }).toString().trim(); }
  catch (e) { return `restore failed: ${String(e).split('\n')[0]} (state ${STATE})`; }
}
export const restoreScript = (c: PinCfg | null) => `
$ErrorActionPreference = 'SilentlyContinue'
$me = (Get-Process -Id $PID).SessionId; $n = 0; $m = 0
if (Test-Path ${q(STATE)}) {
  foreach ($l in Get-Content ${q(STATE)}) {
    $r = $l | ConvertFrom-Json; $p = Get-Process -Id $r.pid
    if ($p -and $p.StartTime.Ticks -eq [long]$r.ticks) { try { $p.ProcessorAffinity = [IntPtr][long]$r.mask; $n++ } catch {} }
  }
  Remove-Item ${q(STATE)}
}
${c?.others.length ? `foreach ($p in Get-Process -Name @(${names(c)})) {
  if ($p.SessionId -ne $me) { continue }
  try { if ($p.ProcessorAffinity.ToInt64() -eq ${c.otherMask}) { $p.ProcessorAffinity = [IntPtr][long]${c.fullMask}; $m++ } } catch {}
}` : ''}
"$n restored, $m children widened"`;
export const pinStatePending = () => existsSync(STATE);

if (import.meta.main && process.argv.includes('--self-check')) {
  const eq = (a: unknown, b: unknown, what: string) => { if (String(a) !== String(b)) { console.log(`self-check FAIL: ${what}: ${a} != ${b}`); process.exit(1); } };
  eq(cpuMask('4-15', 16), 0xFFF0n, '4-15');
  eq(cpuMask('0-3', 16), 0xFn, '0-3');
  eq(cpuMask('0-3,8,10-11', 16), 0b110100001111n, 'list');
  eq(maskCpus(0xFFF0n).join(','), '4,5,6,7,8,9,10,11,12,13,14,15', 'maskCpus');
  for (const bad of ['', '4-16', '5-4', 'x', '4-']) { let threw = false; try { cpuMask(bad, 16); } catch { threw = true; } eq(threw, true, `rejects "${bad}"`); }
  const c = pinConfig(undefined, {}, 16)!;
  eq(c.gameMask, 0xFFF0n, 'default game'); eq(c.otherMask, 0xFn, 'default others'); eq(c.fullMask, 0xFFFFn, 'full'); eq(c.priority, 'High', 'priority');
  eq(c.others.includes('chrome') && !c.others.includes('node'), true, 'default names (never node: the harness itself)');
  eq(pinConfig(undefined, { TS3_PIN: 'off' }, 16), null, 'TS3_PIN=off');
  eq(pinConfig(false, {}, 16), null, 'job pin false');
  eq(pinConfig(undefined, { TS3_PIN_OTHERS: 'off' }, 16)!.others.length, 0, 'TS3_PIN_OTHERS=off');
  eq(pinConfig(undefined, { TS3_GAME_PRIORITY: 'off' }, 16)!.priority, null, 'priority off');
  eq(pinConfig({ cpus: '0-15' }, {}, 16)!.others.length, 0, 'no rest CPUs -> no app moves');
  eq(pinConfig(undefined, {}, 8), null, 'small machine: off by default');
  const l = pinConfig(undefined, { TS3_MACHINE: 'ea-laptop' }, 14)!;
  eq(l.gameMask, 0xC3Fn, 'ea-laptop game = P-cores 0-1,10-11 + E-cores 2-5'); eq(l.otherMask, 0x33C0n, 'ea-laptop apps = 6-9,12-13');
  eq(pinConfig(undefined, { TS3_MACHINE: 'ea-laptop', TS3_GAME_CPUS: '0-3' }, 14)!.gameMask, 0xFn, 'TS3_GAME_CPUS beats TS3_MACHINE');
  { let t = false; try { pinConfig(undefined, { TS3_MACHINE: 'nope' }, 14); } catch { t = true; } eq(t, true, 'rejects unknown TS3_MACHINE'); }
  eq(pinConfig({ cpus: '8-15', others: ['vlc'] }, {}, 16)!.otherMask, 0xFFn, 'job cpus');
  let threw = false; try { pinConfig({ priority: 'RealTime' }, {}, 16); } catch { threw = true; } eq(threw, true, 'rejects RealTime');
  threw = false; try { pinConfig({ priority: 'RealTime' }, {}, 8); } catch { threw = true; } eq(threw, true, 'rejects RealTime on a small machine');
  if (process.platform === 'win32') for (const [what, s] of [['pinner', pinnerScript(c)], ['restore', restoreScript(c)], ['restore (state only)', restoreScript(null)]]) {
    // parse only, nothing runs: PowerShell's own parser on the generated script
    const errs = execFileSync('powershell', ['-NoProfile', '-NonInteractive', '-Command',
      '$e = $null; [void][System.Management.Automation.Language.Parser]::ParseInput([Console]::In.ReadToEnd(), [ref]$null, [ref]$e); $e | ForEach-Object { $_.Message }'],
      { input: s, timeout: 30000, windowsHide: true }).toString().trim();
    eq(errs, '', `${what} script parses`);
  }
  console.log(`self-check PASS (this machine: ${N} CPUs, ${describePin(pinConfig())})`);
}
