// Machine load beside a profile run (Rodney, 2026-10-17: rule out external factors such as other Claude sessions, IntelliJ
// or thermal throttling). One PowerShell loop per run, a JSON line every 30 s into <pre>-machine.jsonl:
//   other = CPU used by every process except TS3W and Idle, in % of the whole machine (all logical cores)
//   ts3w  = TS3W's own CPU in % of the whole machine; top = the 3 busiest other processes (% of machine)
//   perf  = '% Processor Performance' (CPU clock vs base; > 100 = turbo; a drop during the run = throttling or power limit)
//   og/oo = busy % of the game CPUs (gc, pin.ts, default 4-15) minus TS3W's share / busy % of the other CPUs (per-core raw
//           counters over the same 30 s; the split is exact only while TS3W is pinned: pin = its affinity == the game set)
//   mhz   = clock in MHz (base 'Processor Frequency' x perf); g3d / g3dOther / gdec = GPU 3D engine % (all, not TS3W) and
//           video decode % ('\GPU Engine(*)\Utilization Percentage', 1 s snapshot, null when the counters are missing)
// summarize() gives the run-log line and a verdict: a run with other >= 10% on average, or perf below 80% of its first
// sample, is flagged as not comparable; plus noise: low|medium|high (NOISE below). Windows only, like the rest of the harness.
// The sampler runs on the non-game CPUs and ends on its own once the node process that started it is gone.
//   node tools/harness/machine-load.ts [seconds]   self-check: samples every 5 s for N s (default 15) and prints the summary
import { spawn, type ChildProcess } from 'node:child_process';
import { existsSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { availableParallelism } from 'node:os';
import { cpuMask, gameCpus, maskCpus } from './pin.ts';
import { GAME_NAME } from './game-exe.ts';

const OTHER_MAX = 10, PERF_DROP = 0.8;
// noise verdict (job means): high = other apps take >= 10% of the game CPUs (with other CPU >= OG_OTHER_MIN), the clock dropped or the machine was busy (the
// NOT comparable rule), or another app keeps the GPU 3D engine >= 30% busy; medium = >= 3% of the game CPUs, other CPUs >= 50%
// busy, other CPU >= 10% of the machine, GPU 3D (not TS3W) or video decode >= 10%; else low
const NOISE = { highOg: 10, highG3d: 30, medOg: 3, medOo: 50, medGpu: 10 };
// "others on game CPUs" (og) is busy time minus TS3W's share, so it also holds kernel/driver time no process owns: vanilla IP
// read og >= 10% twice with other CPU ~2% (2026-10-24). og alone makes a run high/invalid only when processes also used >= 5%
export const OG_OTHER_MIN = 5;

const script = (file: string, everyS: number, cpus: string | null) => {
  const game = cpus ? cpuMask(cpus) : 0n, rest = ((1n << BigInt(availableParallelism())) - 1n) & ~game;
  return `
$ErrorActionPreference = 'SilentlyContinue'
$n = [Environment]::ProcessorCount
${cpus && rest ? `[Diagnostics.Process]::GetCurrentProcess().ProcessorAffinity = [IntPtr][long]${rest}` : ''}
$game = @(${cpus ? maskCpus(game).join(',') : ''}); $gm = [long]${game}
$raw = { $h = @{}; foreach ($c in Get-CimInstance Win32_PerfRawData_PerfOS_Processor) { $h[$c.Name] = @([double]$c.PercentProcessorTime, [double]$c.Timestamp_Sys100NS) }; $h }
$pc = & $raw
$prev = @{}; foreach ($p in Get-Process) { $prev[$p.Id] = $p.TotalProcessorTime.TotalSeconds }
$t = Get-Date
while (Get-Process -Id ${process.pid}) {
  Start-Sleep -Seconds ${everyS}
  $now = Get-Date; $dt = ($now - $t).TotalSeconds; $t = $now
  $cur = @{}; $rows = @()
  foreach ($p in Get-Process) {
    $s = $p.TotalProcessorTime.TotalSeconds; $cur[$p.Id] = $s
    if ($prev.ContainsKey($p.Id) -and $p.Id -ne 0) { $rows += [pscustomobject]@{ n = $p.ProcessName; c = ($s - $prev[$p.Id]) / $dt / $n * 100 } }
  }
  $prev = $cur
  $ts3w = ($rows | Where-Object n -eq '${GAME_NAME}' | Measure-Object c -Sum).Sum
  $others = $rows | Where-Object { $_.n -ne '${GAME_NAME}' -and $_.n -ne 'Idle' }
  $other = ($others | Measure-Object c -Sum).Sum
  $top = $others | Group-Object n | ForEach-Object { [pscustomobject]@{ n = $_.Name; c = ($_.Group | Measure-Object c -Sum).Sum } } |
    Sort-Object c -Descending | Select-Object -First 3 | ForEach-Object { '{0} {1:N1}' -f $_.n, $_.c }
  $cs = (Get-Counter '\\Processor Information(_Total)\\% Processor Performance', '\\Processor Information(_Total)\\Processor Frequency').CounterSamples
  $perf = ($cs | Where-Object Path -like '*% processor performance').CookedValue
  $freq = ($cs | Where-Object Path -like '*processor frequency').CookedValue
  $cc = & $raw; $bg = 0; $bo = 0; $ng = 0; $no = 0
  foreach ($k in $cc.Keys) {
    if ($k -notmatch '^\\d+$' -or -not $pc.ContainsKey($k)) { continue }
    $d = $cc[$k][1] - $pc[$k][1]; if ($d -le 0) { continue }
    $busy = [math]::Max(0, 100 * (1 - ($cc[$k][0] - $pc[$k][0]) / $d))
    if ($game -contains [int]$k) { $bg += $busy; $ng++ } else { $bo += $busy; $no++ }
  }
  $pc = $cc
  $ts = Get-Process -Name ${GAME_NAME} | Select-Object -First 1
  $pin = if ($ts -and $gm) { $ts.ProcessorAffinity.ToInt64() -eq $gm } else { $null }
  $og = if ($ng) { [math]::Round([math]::Max(0, $bg - [double]$ts3w * $n) / $ng, 1) } else { $null }
  $oo = if ($no) { [math]::Round($bo / $no, 1) } else { $null }
  $g3d = $null; $g3o = $null; $gdec = $null
  $gs = (Get-Counter '\\GPU Engine(*engtype_3D)\\Utilization Percentage', '\\GPU Engine(*engtype_VideoDecode)\\Utilization Percentage').CounterSamples
  if ($gs) {
    $g3 = @($gs | Where-Object InstanceName -like '*engtype_3d')
    $tsp = if ($ts) { "pid_$($ts.Id)_*" } else { 'none' }
    $g3d = [math]::Min(100, [math]::Round([double]($g3 | Measure-Object CookedValue -Sum).Sum, 1))
    $g3o = [math]::Min(100, [math]::Round([double]($g3 | Where-Object InstanceName -notlike $tsp | Measure-Object CookedValue -Sum).Sum, 1))
    $gdec = [math]::Min(100, [math]::Round([double]($gs | Where-Object InstanceName -like '*engtype_videodecode' | Measure-Object CookedValue -Sum).Sum, 1))
  }
  $o = [ordered]@{ t = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds(); other = [math]::Round($other, 1); ts3w = [math]::Round([double]$ts3w, 1); top = @($top); perf = if ($perf) { [math]::Round($perf) } else { $null };
    mhz = if ($perf -and $freq) { [math]::Round($freq * $perf / 100) } else { $null }; gc = '${cpus ?? ''}'; og = $og; oo = $oo; pin = $pin; g3d = $g3d; g3dOther = $g3o; gdec = $gdec }
  Add-Content -Path '${file.replace(/'/g, "''")}' -Value ($o | ConvertTo-Json -Compress)
}`;
};

// cpus: the game CPU set for the og/oo split (pin.ts: TS3_GAME_CPUS, default 4-<n-1>); the sampler itself runs on the rest
export function startMachineLoad(file: string, everyS = 30, cpus: string | null = gameCpus()): () => void {
  const p: ChildProcess = spawn('powershell', ['-NoProfile', '-NonInteractive', '-Command', script(file, everyS, cpus)], { stdio: 'ignore', windowsHide: true });
  return () => { try { p.kill(); } catch { /* gone */ } };
}

// rows of a machine file; a line the sampler is still writing is skipped (queue.ts reads the file mid-run)
const rowsOf = (file: string): any[] => (existsSync(file) ? readFileSync(file, 'utf8').trim().split(/\r?\n/).filter(Boolean)
  .flatMap(l => { try { return [JSON.parse(l)]; } catch { return []; } }) : []);
export const summarize = (file: string) => summarizeRows(rowsOf(file));
// early verdict (queue.ts, timing jobs): the NOT comparable rule below over the samples from `from` + 60 s (profiler start-up
// skipped); null until `min` samples exist (ask again), '' when fine, else why the run is void
export function earlyNoise(file: string, from: number, min = 3): string | null {
  const rows = rowsOf(file).filter(r => r.t >= from + 60000);
  if (rows.length < min) return null;
  const s = summarizeRows(rows);
  return s.ok ? '' : `noisy machine early: ${s.line}`;
}
export function summarizeRows(rows: any[]): { line: string; ok: boolean; noise: string; short: string } {
  if (!rows.length) return { line: 'machine load: no samples', ok: false, noise: 'unknown', short: 'noise: unknown (no samples)' };
  const mean = (k: string) => rows.reduce((s, r) => s + (r[k] ?? 0), 0) / rows.length;
  const perfs = rows.map(r => r.perf).filter((x: number | null) => x != null);
  const perfMin = perfs.length ? Math.min(...perfs) : null, perf0 = perfs[0] ?? null;
  const busiest = rows.reduce((a, r) => (r.other > a.other ? r : a));
  const why = [mean('other') >= OTHER_MAX && `other CPU ${mean('other').toFixed(1)}% >= ${OTHER_MAX}%`,
    perf0 && perfMin !== null && perfMin < perf0 * PERF_DROP && `clock fell to ${perfMin}% from ${perf0}%`].filter(Boolean);
  const line = `machine load: other CPU mean ${mean('other').toFixed(1)}% max ${busiest.other}% (${busiest.top.join(', ')}), TS3W ${mean('ts3w').toFixed(1)}%, ` +
    `clock ${perf0 ?? '-'}% -> min ${perfMin ?? '-'}% (${rows.length} samples): ${why.length ? `NOT comparable: ${why.join('; ')}` : 'ok'}`;
  const n = noise(rows, why.length > 0);
  return { line: `${line}; ${n.text}`, ok: !why.length, noise: n.level, short: n.short };
}

// mean of a field over the rows that have it (older files lack the new fields)
const avg = (rows: any[], k: string) => { const v = rows.map(r => r[k]).filter((x: unknown) => typeof x === 'number'); return v.length ? v.reduce((a: number, b: number) => a + b, 0) / v.length : null; };
export function noise(rows: any[], notComparable: boolean): { level: string; text: string; short: string } {
  const og = avg(rows, 'og'), oo = avg(rows, 'oo'), other = avg(rows, 'other') ?? 0, g3o = avg(rows, 'g3dOther'), g3 = avg(rows, 'g3d'), dec = avg(rows, 'gdec');
  const mhz = rows.map(r => r.mhz).filter((x: unknown) => typeof x === 'number') as number[], gc = rows.find(r => r.gc)?.gc ?? '?';
  const pinned = rows.filter(r => r.pin === true).length, withGame = rows.filter(r => typeof r.pin === 'boolean').length;
  const f = (x: number | null) => (x === null ? '-' : x.toFixed(1));
  const high = [og !== null && og >= NOISE.highOg && other >= OG_OTHER_MIN && `other apps ${f(og)}% of game CPUs`, notComparable && 'NOT comparable (load or clock)',
    g3o !== null && g3o >= NOISE.highG3d && `GPU 3D others ${f(g3o)}%`].filter(Boolean);
  const med = [og !== null && og >= NOISE.medOg && `other apps ${f(og)}% of game CPUs`, oo !== null && oo >= NOISE.medOo && `other CPUs ${f(oo)}% busy`,
    other >= OTHER_MAX && `other CPU ${other.toFixed(1)}%`, g3o !== null && g3o >= NOISE.medGpu && `GPU 3D others ${f(g3o)}%`,
    dec !== null && dec >= NOISE.medGpu && `video decode ${f(dec)}%`].filter(Boolean);
  const level = high.length ? 'high' : med.length ? 'medium' : 'low', why = (high.length ? high : med).join(', ');
  const clock = mhz.length ? `${Math.round(mhz.reduce((a, b) => a + b, 0) / mhz.length)}/${Math.min(...mhz)} MHz` : '- MHz';
  const text = `game CPUs ${gc}: others ${f(og)}%, rest CPUs ${f(oo)}% busy, TS3W pinned ${pinned}/${withGame} samples; clock avg/min ${clock}; ` +
    `GPU 3D ${f(g3)}% (others ${f(g3o)}%), video decode ${f(dec)}%; noise: ${level}${why ? ` (${why})` : ''}`;
  return { level, text, short: `noise: ${level}${why ? ` (${why})` : ''}; others on game CPUs ${f(og)}%; clock avg/min ${clock}` };
}

if (import.meta.main) {
  const lv = (rs: object[], bad = false) => noise(rs, bad).level;
  const check = (ok: boolean, what: string) => { if (!ok) { console.log(`self-check FAIL: ${what}`); process.exit(1); } };
  check(lv([{ other: 2, og: 0.5, oo: 10, g3dOther: 1, gdec: 0 }]) === 'low', 'noise low');
  check(lv([{ other: 2, og: 4, oo: 10 }]) === 'medium' && lv([{ other: 2, og: 0, gdec: 15 }]) === 'medium', 'noise medium');
  check(lv([{ other: 6, og: 12 }]) === 'high' && lv([{ other: 2, og: 0 }], true) === 'high', 'noise high');
  check(lv([{ other: 2, og: 12 }]) === 'medium', 'og alone (kernel/driver time) is not high');
  { // early verdict: null before 3 samples past the first minute, '' when fine, the reason when not comparable
    const f = `${process.env.TEMP ?? '.'}/machine-load-early-${process.pid}.jsonl`, w = (rs: object[]) => writeFileSync(f, rs.map(r => JSON.stringify(r)).join('\n') + '\n{"t":');
    const r = (t: number, o: object = {}) => ({ t, other: 2, perf: 100, top: [], ...o });
    w([r(0, { other: 50 }), r(70000), r(100000)]); check(earlyNoise(f, 0) === null, 'early: too few samples (first minute skipped)');
    w([r(70000), r(100000), r(130000)]); check(earlyNoise(f, 0) === '', 'early: fine');
    w([r(70000, { other: 14 }), r(100000, { other: 12 }), r(130000)]); check(earlyNoise(f, 0) === '', 'early: mean 9.3% < 10%');
    w([r(70000, { other: 20 }), r(100000, { other: 12 }), r(130000)]); check(/^noisy machine early: .*NOT comparable: other CPU 11\.3% >= 10%/.test(earlyNoise(f, 0) ?? ''), 'early: noisy');
    w([r(70000), r(100000), r(130000, { perf: 70 })]); check(/clock fell to 70% from 100%/.test(earlyNoise(f, 0) ?? ''), 'early: clock drop');
    rmSync(f);
  }
  check(lv([{ other: 2, perf: 100 }]) === 'low', 'old rows without the new fields');
  if (process.argv.includes('--verdict-only')) { console.log('self-check PASS (verdict only)'); process.exit(0); }
  const secs = Number(process.argv[2] ?? 15), file = `${process.env.TEMP ?? '.'}/machine-load-selfcheck-${Date.now()}.jsonl`;
  const stop = startMachineLoad(file, 5);
  await new Promise(r => setTimeout(r, (secs + 3) * 1000));
  stop();
  const s = summarize(file);
  console.log(s.line);
  if (!existsSync(file) || !readFileSync(file, 'utf8').trim()) { console.log('self-check FAIL: no samples written'); process.exit(1); }
  const r = JSON.parse(readFileSync(file, 'utf8').trim().split(/\r?\n/)[0]);
  check(typeof r.other === 'number' && Array.isArray(r.top) && 'og' in r && 'mhz' in r && 'g3d' in r, `row shape ${JSON.stringify(r)}`);
  console.log('self-check PASS');
}
