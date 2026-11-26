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
// Queue job (or queue-level { "pin": ... } beside "jobs"): "pin": false | { cpus?: "4-15", priority?: "High" | "AboveNormal" | "Normal" | "off", others?: boolean | [names],
//   sweep?: boolean (default: on for timing jobs; true also for noiseCheck-false timing jobs), keep?: [names] (never swept; replaces TS3_PIN_KEEP="a,b") }. TS3_PIN_SWEEP=off: no sweep at all.
// Timing jobs also run the sweep below (every other process off the game CPUs for the job; startSweep / restoreSweep).
//   node tools/harness/pin.ts --self-check     mask math only, touches nothing
//   node tools/harness/pin.ts --sweep-dry      lists what a timing sweep would move now (reads affinities only)
//   node tools/harness/pin.ts --restore-affinity  puts back what a dead run left moved (pin + sweep state files)
import { execFileSync, spawn } from 'node:child_process';
import { existsSync, mkdtempSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { cpus, tmpdir } from 'node:os';
import { join } from 'node:path';
import { GAME_EXE, GAME_NAME, MACHINE } from './game-exe.ts';

export type PinOpts = { cpus?: string; priority?: string; others?: boolean | string[]; sweep?: boolean; keep?: string[] };
export type PinCfg = { cpus: string; gameMask: bigint; otherMask: bigint; fullMask: bigint; priority: string | null; others: string[]; sweep: boolean; keep: string[] };
export const DEFAULT_APPS = ['chrome', 'msedge', 'firefox', 'brave', 'opera', 'vlc', 'mpc-hc64', 'mpc-hc', 'mpc-be64', 'PotPlayerMini64', 'mpv',
  'Teams', 'ms-teams', 'Spotify', 'Discord', 'Code', 'idea64', 'java'];
const PRIORITIES = ['Normal', 'AboveNormal', 'High']; // never RealTime: it can starve input and the disk
const STATE = join(tmpdir(), 'ts3-harness-pin-state.jsonl');
const N = cpus().length; // all logical CPUs: availableParallelism() follows this process's affinity, and the timing sweep puts the harness on 0-3

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

// timing: the job's timing counts (queue.ts isTimingJob: profile, noiseCheck not false) -> sweep on unless TS3_PIN_SWEEP=off or
// pin.sweep false; pin.sweep true forces it (timing queues that set noiseCheck false, e.g. z1-m1-timing-1116)
export function pinConfig(job?: false | PinOpts, env: NodeJS.ProcessEnv = process.env, n = N, timing = false): PinCfg | null {
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
  if (job?.sweep !== undefined && typeof job.sweep !== 'boolean') throw new Error('sweep must be true or false');
  const keep = job?.keep ?? (env.TS3_PIN_KEEP ? env.TS3_PIN_KEEP.split(',').map(s => s.trim()).filter(Boolean) : []);
  if (keep.some(x => !/^[\w.-]+$/.test(x))) throw new Error(`pin keep: process names only (no .exe, no paths): ${keep.join(',')}`);
  const sweep = !!otherMask && env.TS3_PIN_SWEEP !== 'off' && (job?.sweep === true || (timing && job?.sweep !== false));
  return { cpus, gameMask, otherMask, fullMask, priority: priority === 'off' ? null : priority, others, sweep, keep };
}
export const describePin = (c: PinCfg | null) => (!c ? 'pin off'
  : `${GAME_EXE} -> CPUs ${c.cpus}${c.priority ? ` priority ${c.priority}` : ''}; ${c.others.length ? `${c.others.length} app names -> CPUs ${maskCpus(c.otherMask).join(',')}, restored after the job` : 'other apps untouched'}` +
    (c.sweep ? `; timing sweep: every other process -> CPUs ${maskCpus(c.otherMask).join(',')}, ${BRIDGE_NAME} -> ${c.cpus}, restored after the job` : ''));

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

// ---- timing sweep (Rodney 2026-11-18): during a TIMING job every other process we may change leaves the game CPUs.
// Once TS3W.exe is up and the job's machine sampler has its first row (max 40 s: an og "before" sample), every 60 s:
// each process whose affinity touches the game CPUs -> otherMask (old mask to SWEEP_STATE first, so a dead harness is
// undone at the next queue start / pin.ts --restore); a process started since the sweep began that already sits on
// otherMask inherited it from a moved parent (new agents of a moved claude.exe) -> recorded with the full mask.
// Every 5 s NvRemixBridge -> game CPUs (renders for the game; would inherit 0-3 when the game restarts under a moved
// driver). Never touched: the game, PROTECTED, pin.keep / TS3_PIN_KEEP; access denied (services, elevated) = skipped.
// At job end stopSweep() kills the loop, restoreSweep() puts back every recorded pid (same start time) and widens
// processes started during the sweep that still sit on exactly otherMask (except children of this harness).
export const BRIDGE_NAME = 'NvRemixBridge'; // = machine-load.ts BRIDGE_NAME (that file imports this one)
export const PROTECTED = ['System', 'Idle', 'Registry', 'smss', 'csrss', 'wininit', 'winlogon', 'services', 'lsass', 'LsaIso',
  'audiodg', 'dwm', 'Secure System', 'Memory Compression', 'fontdrvhost', 'MsMpEng', 'NisSrv', 'MpDefenderCoreService', 'SgrmBroker', 'vmmem', 'vmmemWSL'];
const SWEEP_STATE = join(tmpdir(), 'ts3-harness-sweep-state.jsonl');
export const keepNames = (c: PinCfg) => [...new Set([GAME_NAME, BRIDGE_NAME, ...PROTECTED, ...c.keep])];
// status: per-job JSON written after each sweep { first (unix ms of the first sweep), moved, inherited, denied, failed }
export const sweepScript = (c: PinCfg, o: { status?: string; machine?: string; dry?: boolean; owner?: number } = {}) => `
$ErrorActionPreference = 'SilentlyContinue'
$gm = [long]${c.gameMask}; $om = [long]${c.otherMask}; $fm = [long]${c.fullMask}
$keep = @{}; foreach ($k in @(${keepNames(c).map(q).join(',')})) { $keep[$k] = 1 }
$seen = @{}; $moved = 0; $inh = 0; $denied = 0; $failed = 0; $first = $null; $would = @{}
${o.dry ? '' : `[Diagnostics.Process]::GetCurrentProcess().ProcessorAffinity = [IntPtr]$om
$start = (Get-Date).Ticks
Add-Content -Path ${q(SWEEP_STATE)} -Value (@{ start = $start } | ConvertTo-Json -Compress)`}
$gameAt = $null; $i = 0
while (${o.dry ? '$i -eq 0' : `Get-Process -Id ${o.owner ?? process.pid}`}) {
  ${o.dry ? '' : `foreach ($b in Get-Process -Name ${q(BRIDGE_NAME)}) { try { if ($b.ProcessorAffinity.ToInt64() -ne $gm) { $b.ProcessorAffinity = [IntPtr]$gm } } catch {} }
  if (-not $gameAt -and (Get-Process -Name ${q(GAME_NAME)})) { $gameAt = Get-Date }
  $ready = $gameAt -and ((Test-Path ${q(o.machine ?? '')}) -or ((Get-Date) - $gameAt).TotalSeconds -ge 40)
  if (-not $ready) { Start-Sleep -Seconds 5; continue }`}
  if ($i % 12 -eq 0) {
    foreach ($p in Get-Process) {
      if ($p.Id -le 4 -or $p.Id -eq $PID -or $keep.ContainsKey($p.ProcessName)) { continue }
      $k = "$($p.Id)/$($p.ProcessName)"
      if ($seen.ContainsKey($k)) { continue }
      $seen[$k] = 1
      try { $a = $p.ProcessorAffinity.ToInt64(); $ticks = $p.StartTime.Ticks } catch { $denied++; continue }
      if (($a -band $gm) -ne 0) {
        $moved++
        ${o.dry ? `$would[$p.ProcessName] = 1 + [int]$would[$p.ProcessName]` : `Add-Content -Path ${q(SWEEP_STATE)} -Value (@{ pid = $p.Id; ticks = $ticks; name = $p.ProcessName; mask = $a } | ConvertTo-Json -Compress)
        try { $p.ProcessorAffinity = [IntPtr]$om } catch { $failed++ }`}
      }${o.dry ? '' : ` elseif ($a -eq $om -and $ticks -gt $start) {
        $inh++; Add-Content -Path ${q(SWEEP_STATE)} -Value (@{ pid = $p.Id; ticks = $ticks; name = $p.ProcessName; mask = $fm } | ConvertTo-Json -Compress)
      }`}
    }
    if (-not $first) { $first = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds() }
    ${o.dry ? `"would move $moved processes off the game CPUs; unreadable (access denied, skipped) $denied; kept by name: $((Get-Process | Where-Object { $keep.ContainsKey($_.ProcessName) } | Select-Object -ExpandProperty ProcessName -Unique) -join ',')"
    ($would.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { "$($_.Key) x$($_.Value)" }) -join ', '`
    : `Set-Content -Path ${q(o.status ?? '')} -Value (@{ first = $first; moved = $moved; inherited = $inh; denied = $denied; failed = $failed } | ConvertTo-Json -Compress)`}
  }
  $i++
  ${o.dry ? '' : 'Start-Sleep -Seconds 5'}
}`;
export function startSweep(c: PinCfg | null, status: string, machine: string): () => void {
  if (!c?.sweep || process.platform !== 'win32') return () => {};
  const p = spawn('powershell', ['-NoProfile', '-NonInteractive', '-Command', sweepScript(c, { status, machine })], { stdio: 'ignore', windowsHide: true });
  return () => { try { p.kill(); } catch { /* gone */ } };
}
export const restoreSweepScript = (owner: number, om: bigint, fm: bigint, state = SWEEP_STATE) => `
$ErrorActionPreference = 'SilentlyContinue'
$n = 0; $g = 0; $w = 0; $start = $null
if (Test-Path ${q(state)}) {
  foreach ($l in Get-Content ${q(state)}) {
    $r = $l | ConvertFrom-Json
    if ($r.start) { if (-not $start) { $start = [long]$r.start }; continue }
    $p = Get-Process -Id $r.pid
    if ($p -and $p.StartTime.Ticks -eq [long]$r.ticks) { try { $p.ProcessorAffinity = [IntPtr][long]$r.mask; $n++ } catch {} } else { $g++ }
  }
  Remove-Item ${q(state)}
}
if ($start) {
  $par = @{}
  foreach ($c in Get-CimInstance Win32_Process) { $par[[int]$c.ProcessId] = [int]$c.ParentProcessId }
  foreach ($p in Get-Process) {
    if ($par[$p.Id] -eq ${owner} -or $p.Id -eq $PID) { continue }
    try { if ($p.StartTime.Ticks -gt $start -and $p.ProcessorAffinity.ToInt64() -eq ${om}) { $p.ProcessorAffinity = [IntPtr][long]${fm}; $w++ } } catch {}
  }
}
"$n restored, $g gone, $w started during the sweep widened"`;
// otherMask for the widen pass: the default config's (the sweep always uses the job's cpus; a job with its own cpus
// leaves its late children narrowed: shortcut, record otherMask in the state file if jobs ever sweep with custom cpus)
export function restoreSweep(owner = process.pid): string {
  if (process.platform !== 'win32' || !existsSync(SWEEP_STATE)) return '';
  const c = pinConfig(undefined, process.env, N, true);
  try { return execFileSync('powershell', ['-NoProfile', '-NonInteractive', '-Command', restoreSweepScript(owner, c?.otherMask ?? 0n, c?.fullMask ?? 0n)], { timeout: 60000, windowsHide: true }).toString().trim(); }
  catch (e) { return `sweep restore failed: ${String(e).split('\n')[0]} (state ${SWEEP_STATE})`; }
}
export const sweepStatePending = () => existsSync(SWEEP_STATE);
// status file + machine rows -> "sweep: N moved (...), others on game CPUs before X% / after Y%"
export function sweepNote(status: string, rows: { t: number; og?: number | null }[]): string {
  let s: any; try { s = JSON.parse(readFileSync(status, 'utf8').replace(/^﻿/, '')); } catch { return 'sweep: no status (never reached the game)'; }
  const mean = (rs: typeof rows) => { const v = rs.map(r => r.og).filter((x): x is number => typeof x === 'number'); return v.length ? `${(v.reduce((a, b) => a + b, 0) / v.length).toFixed(1)}%` : '-'; };
  // a row covers the 30 s before its t: rows ending after the first sweep + 30 s are fully after it
  return `sweep: ${s.moved} moved off the game CPUs (+${s.inherited} inherited, ${s.denied} denied, ${s.failed} set failed); others on game CPUs before ${mean(rows.filter(r => r.t <= s.first))} / after ${mean(rows.filter(r => r.t >= s.first + 30000))}`;
}

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
  // timing sweep: on only for timing jobs with rest CPUs; keep names validated (real names from this machine's process list)
  eq(c.sweep, false, 'sweep off for a correctness job');
  const t = pinConfig(undefined, {}, 16, true)!;
  eq(t.sweep, true, 'sweep on for a timing job'); eq(t.otherMask, 0xFn, 'sweep target = complement of the game set');
  eq(pinConfig(undefined, { TS3_PIN_SWEEP: 'off' }, 16, true)!.sweep, false, 'TS3_PIN_SWEEP=off');
  eq(pinConfig({ sweep: false }, {}, 16, true)!.sweep, false, 'job pin.sweep false');
  eq(pinConfig({ sweep: true }, {}, 16, false)!.sweep, true, 'pin.sweep true on a noiseCheck-false job');
  eq(pinConfig({ sweep: true }, { TS3_PIN_SWEEP: 'off' }, 16, false)!.sweep, false, 'TS3_PIN_SWEEP=off beats pin.sweep true');
  eq(pinConfig({ cpus: '0-15' }, {}, 16, true)!.sweep, false, 'no rest CPUs -> no sweep');
  eq(pinConfig({ cpus: '8-15' }, {}, 16, true)!.otherMask, 0xFFn, 'sweep follows job cpus');
  eq(pinConfig(undefined, { TS3_PIN_KEEP: 'logioptionsplus_agent, msedgewebview2' }, 16, true)!.keep.join(','), 'logioptionsplus_agent,msedgewebview2', 'TS3_PIN_KEEP');
  for (const bad of ['C:\\x\\a.exe', 'two words', 'a/b']) { let th = false; try { pinConfig({ keep: [bad] }, {}, 16, true); } catch { th = true; } eq(th, true, `keep rejects "${bad}"`); }
  const kn = keepNames(pinConfig({ keep: ['Killer'] }, {}, 16, true)!);
  for (const n of [GAME_NAME, BRIDGE_NAME, 'csrss', 'dwm', 'audiodg', 'lsass', 'Killer']) eq(kn.includes(n), true, `keeps ${n}`);
  eq(kn.includes('claude') || kn.includes('node'), false, 'claude/node get moved');
  for (const [s0, want] of [[sweepScript(t, { status: 's', machine: 'm' }), 'Add-Content'], [sweepScript(t, { dry: true }), 'would move']] as const) eq(s0.includes(want), true, `sweep script has ${want}`);
  eq(/ProcessorAffinity = \[IntPtr\]\$om/.test(sweepScript(t, { dry: true })), false, 'dry sweep sets nothing');
  { // sweepNote: before = rows ending at/before the first sweep, after = rows from first + 30 s
    const d = mkdtempSync(join(tmpdir(), 'pin-sc-')), f = join(d, 's.json');
    writeFileSync(f, '﻿{"first":100000,"moved":42,"inherited":3,"denied":7,"failed":1}');
    eq(sweepNote(f, [{ t: 70000, og: 22 }, { t: 100000, og: 24 }, { t: 120000, og: 10 }, { t: 130000, og: 4 }, { t: 160000, og: 6 }]),
      'sweep: 42 moved off the game CPUs (+3 inherited, 7 denied, 1 set failed); others on game CPUs before 23.0% / after 5.0%', 'sweepNote');
    eq(sweepNote(join(d, 'none.json'), []), 'sweep: no status (never reached the game)', 'sweepNote no status');
    rmSync(d, { recursive: true, force: true });
  }
  if (process.platform === 'win32') for (const [what, s] of [['pinner', pinnerScript(c)], ['restore', restoreScript(c)], ['restore (state only)', restoreScript(null)],
    ['sweep', sweepScript(t, { status: 's', machine: 'm' })], ['sweep dry', sweepScript(t, { dry: true })], ['sweep restore', restoreSweepScript(1234, t.otherMask, t.fullMask)]]) {
    // parse only, nothing runs: PowerShell's own parser on the generated script
    const errs = execFileSync('powershell', ['-NoProfile', '-NonInteractive', '-Command',
      '$e = $null; [void][System.Management.Automation.Language.Parser]::ParseInput([Console]::In.ReadToEnd(), [ref]$null, [ref]$e); $e | ForEach-Object { $_.Message }'],
      { input: s, timeout: 30000, windowsHide: true }).toString().trim();
    eq(errs, '', `${what} script parses`);
  }
  if (process.platform === 'win32') { // restore from a state file: own dummy process on CPU 0, a temp state says 0x3; om 0 = no widen pass
    const d = mkdtempSync(join(tmpdir(), 'pin-rs-')), st = join(d, 'state.jsonl');
    const dummy = spawn(process.execPath, ['-e', 'setTimeout(() => {}, 30000)'], { stdio: 'ignore', windowsHide: true });
    try {
      const ps = (cmd: string) => execFileSync('powershell', ['-NoProfile', '-NonInteractive', '-Command', cmd], { timeout: 30000, windowsHide: true }).toString().trim();
      const ticks = ps(`$p = Get-Process -Id ${dummy.pid}; $p.ProcessorAffinity = [IntPtr]1; $p.StartTime.Ticks`);
      writeFileSync(st, `{"start":1}
{"pid":${dummy.pid},"ticks":${ticks},"name":"node","mask":3}
{"pid":${dummy.pid},"ticks":1,"name":"node","mask":15}
`);
      eq(ps(restoreSweepScript(process.pid, 0n, 0xFn, st)), '1 restored, 1 gone, 0 started during the sweep widened', 'restore from state file');
      eq(ps(`(Get-Process -Id ${dummy.pid}).ProcessorAffinity.ToInt64()`), '3', 'restored mask (pid + start time match only)');
      eq(existsSync(st), false, 'state file removed after restore');
    } finally { dummy.kill(); rmSync(d, { recursive: true, force: true }); }
  }
  console.log(`self-check PASS (this machine: ${N} CPUs, ${describePin(pinConfig())})`);
}
if (import.meta.main && process.argv.includes('--sweep-dry')) {
  const c = pinConfig(undefined, process.env, N, true);
  if (!c?.sweep || process.platform !== 'win32') console.log('sweep: off here (not Windows, TS3_PIN_SWEEP=off or no rest CPUs)');
  else console.log(`game CPUs ${c.cpus}, sweep target CPUs ${maskCpus(c.otherMask).join(',')}\n` +
    execFileSync('powershell', ['-NoProfile', '-NonInteractive', '-Command', sweepScript(c, { dry: true })], { timeout: 60000, windowsHide: true }).toString().trim());
}
if (import.meta.main && process.argv.includes('--restore-affinity')) {
  console.log(`pin: ${pinStatePending() ? restorePins() : 'nothing pending'}; sweep: ${sweepStatePending() ? restoreSweep() : 'nothing pending'}`);
}
