# TS3 hang and crash diagnostics for testers. Windows PowerShell 5.1+. Windows-only (Toolhelp32, dbghelp, event log, registry); no admin needed
# except the opt-in crash-dump step. Docs: docs/256 sections 7 (hang) and 9 (crash).
# Run while TS3W.exe is hung:  ts3-hang-diag.bat        (collect only)
#   -Launch [-Bin <Game\Bin>]  hang mode: start TS3W.exe directly, wait 90 s for a window, collect if none
#   -KillAfter                 stop the collected processes afterwards (never done otherwise)
#   -Crash                     crash mode (ts3-crash-diag.bat): wait for the game to start (EA app / Steam / -Launch), watch it,
#                              record exit code and time, dump if it hangs (-HangSeconds), then collect everything
#   -EnableCrashDumps          turn on Windows crash dumps (WER LocalDumps, HKLM: asks for admin) for TS3W/TS3/NvRemixBridge;
#                              with -Crash they are turned off again at the end; alone they stay until -DisableCrashDumps
#   -SelfCheck                 test the tool on fixtures and harmless child processes (never starts the game)
param([switch]$Launch, [string]$Bin, [switch]$KillAfter, [switch]$SelfCheck, [switch]$Crash, [int]$WaitSeconds = 300,
  [int]$HangSeconds = 120, [int]$WatchSeconds = 240, [switch]$EnableCrashDumps, [switch]$DisableCrashDumps, [string]$LocalDumpsStep)
$ErrorActionPreference = 'Continue'
$names = 'TS3W', 'TS3', 'NvRemixBridge', 'Sims3Launcher*'
$GameNames = 'TS3W', 'TS3'
$CrashExes = 'TS3W.exe', 'TS3.exe', 'NvRemixBridge.exe'
$LatestNative = '0.36.168e'; $LatestRelease = 'v0.2.10'   # update on each ts3native release (CHANGELOG "The log shows")
$LdKey = 'HKLM:\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps'
$script:Findings = New-Object System.Collections.Generic.List[string]
$script:DocsDirs = $null   # SelfCheck points this at a fixture

# Regexes (each has cases in -SelfCheck)
$ReExe = '(?i)^(TS3W|TS3|NvRemixBridge|Sims3Launcher)\.exe$'
$ReWerDir = '(?i)^App(Crash|Hang)_(TS3W|TS3|NvRemixBridge|Sims3Launcher)\.exe_'
$ReLayerName = '(?i)\\(TS3W|TS3|Sims3Launcher|NvRemixBridge|EADesktop|EALauncher)\.exe$'
$ReCompatMode = '(?i)\b(WIN(XP|7|8|95|98|2000)\w*|VISTA\w*|NT4SP5)\b'
$ReNativeVer = '(?m)^ts3native (\d+\.\d+\.\d+\S*) \d{4}-\d\d-\d\d'
$ReOs = '(?m)\bos: Windows (\d+)\.(\d+)\.(\d+)'
$ReCrashTitle = '(?m)^title: (\[crash\][^\r\n]*)'
$ReEmail = '[A-Za-z0-9._%+-]+@[A-Za-z0-9-]+(\.[A-Za-z0-9-]+)*\.[A-Za-z]{2,}'
$ReLoaderName = '(?i)^(wininet|dinput8|dinput|dsound|version|winmm|xlive|vorbisfile|d3d8|ddraw|winhttp|binkw32)\.dll$'
$ReD3d9Hooker = '(?i)D3DShaderReplacer|WinterRoadsFix|ReShade|SweetFX|ENB|d3d9_|GShade|dxvk'
$ReDumpFile = '(?i)^(TS3W|TS3|NvRemixBridge)\.exe\.\d+\.dmp$'

Add-Type -TypeDefinition @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class HangNative {
  [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
  struct MODULEENTRY32 { public uint dwSize, th32ModuleID, th32ProcessID, GlblcntUsage, ProccntUsage; public IntPtr modBaseAddr; public uint modBaseSize; public IntPtr hModule;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szModule; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExePath; }
  [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint f, uint pid);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Module32FirstW(IntPtr s, ref MODULEENTRY32 e);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool Module32NextW(IntPtr s, ref MODULEENTRY32 e);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  [DllImport("dbghelp.dll", SetLastError = true)] static extern bool MiniDumpWriteDump(IntPtr proc, uint pid, IntPtr file, uint type, IntPtr a, IntPtr b, IntPtr c);
  // Returns "base|size|name|path" lines; WOW64 modules included (flags 0x8|0x10). Retries on ERROR_BAD_LENGTH.
  public static List<string> Modules(uint pid) {
    var r = new List<string>(); IntPtr s = IntPtr.Zero;
    for (int i = 0; i < 10; i++) { s = CreateToolhelp32Snapshot(0x18, pid); if (s != new IntPtr(-1)) break; if (Marshal.GetLastWin32Error() != 24) break; }
    if (s == new IntPtr(-1)) { r.Add("ERROR snapshot failed, win32=" + Marshal.GetLastWin32Error()); return r; }
    var e = new MODULEENTRY32(); e.dwSize = (uint)Marshal.SizeOf(typeof(MODULEENTRY32));
    for (bool ok = Module32FirstW(s, ref e); ok; ok = Module32NextW(s, ref e))
      r.Add(e.modBaseAddr.ToInt64().ToString("X8") + "|" + e.modBaseSize + "|" + e.szModule + "|" + e.szExePath);
    CloseHandle(s); return r;
  }
  // ThreadInfo 0x1000 | UnloadedModules 0x20 | HandleData 0x4 | FullMemoryInfo 0x800 | ProcessThreadData 0x100 (TEBs) | PrivateReadWriteMemory 0x200.
  // 0x200: a 64-bit dumper writes only the 64-bit stacks of a 32-bit (WOW64) game; the 32-bit stacks and WOW64 contexts live in private memory. Not full memory.
  public static string Dump(IntPtr proc, uint pid, string path) {
    using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create)) {
      bool ok = MiniDumpWriteDump(proc, pid, fs.SafeFileHandle.DangerousGetHandle(), 0x1000 | 0x20 | 0x4 | 0x800 | 0x100 | 0x200, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
      return ok ? "ok" : "failed, win32=" + Marshal.GetLastWin32Error();
    }
  }
}
'@

function Say($m) { Write-Host $m }
function F($m) { $script:Findings.Add($m) }

# ---------- pure helpers (tested in -SelfCheck) ----------
function Describe-Code($code) {
  $h = ('{0:X8}' -f ([int64]$code -band [int64]4294967295))
  $d = @{ 'C0000005' = 'access violation'; 'C0000409' = 'stack buffer overrun / fail-fast'; 'C0000374' = 'heap corruption'; 'C0000135' = 'a DLL was not found';
    'C000007B' = 'bad image format (a 64-bit DLL loaded into the 32-bit game?)'; 'C06D007E' = 'delay-load DLL not found'; 'C0000142' = 'DLL init failed';
    'C00000FD' = 'stack overflow'; 'E0434352' = '.NET exception'; '80000003' = 'breakpoint'; 'C0000017' = 'out of memory' }[$h]
  if ($d) { "0x$h ($d)" } else { "0x$h" }
}
function Get-PeInfo($path) {
  $b = New-Object byte[] 4096; $n = 0
  try { $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite'); $n = $fs.Read($b, 0, 4096); $fs.Close() } catch { return [pscustomobject]@{ Machine = '?'; LAA = $null } }
  if ($n -lt 64 -or $b[0] -ne 0x4D -or $b[1] -ne 0x5A) { return [pscustomobject]@{ Machine = 'not PE'; LAA = $null } }
  $pe = [BitConverter]::ToInt32($b, 0x3C); if ($pe -lt 0 -or $pe + 24 -gt $n) { return [pscustomobject]@{ Machine = '?'; LAA = $null } }
  $m = [BitConverter]::ToUInt16($b, $pe + 4); $ch = [BitConverter]::ToUInt16($b, $pe + 22)
  $mn = switch ($m) { 0x14C { 'x86' } 0x8664 { 'x64' } 0xAA64 { 'arm64' } default { '0x{0:X}' -f $m } }
  [pscustomobject]@{ Machine = $mn; LAA = [bool]($ch -band 0x20) }
}
function Get-Strings($path) {   # ASCII + UTF-16 text of a binary (NULs dropped) for name searches
  try { $fi = Get-Item -LiteralPath $path; if ($fi.Length -gt 64MB) { return '' }; [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes($path)).Replace("`0", '') } catch { '' }
}
function Get-TomlEnabled($text, $section) {   # $true / $false / $null (section absent)
  $m = [regex]::Match([string]$text, '(?ms)^\[' + [regex]::Escape($section) + '\]\s*$(.*?)(?=^\[|\z)')
  if (-not $m.Success) { return $null }
  return [bool]([regex]::IsMatch($m.Groups[1].Value, '(?m)^\s*enabled\s*=\s*true\b'))
}
function Compare-NativeVersion($v, $latest) {   # older / same / newer
  $k = { param($x) $m = [regex]::Match([string]$x, '^(\d+)\.(\d+)\.(\d+)([a-z]?)'); if (-not $m.Success) { return $null }
    '{0:D4}.{1:D4}.{2:D6}{3}' -f [int]$m.Groups[1].Value, [int]$m.Groups[2].Value, [int]$m.Groups[3].Value, $(if ($m.Groups[4].Value) { $m.Groups[4].Value } else { ' ' }) }
  $a = & $k $v; $b = & $k $latest
  if (-not $a) { return '?' }; $c = [string]::CompareOrdinal($a, $b)
  if ($c -lt 0) { 'older' } elseif ($c -eq 0) { 'same' } else { 'newer' }
}
function Mask-Text([string]$s) {
  $s = [regex]::Replace($s, '(?i)(\\Users\\)[^\\/\r\n"<>|]+', '$1<user>')
  if ($env:USERNAME -and $env:USERNAME.Length -ge 3) { $s = [regex]::Replace($s, '(?i)\b' + [regex]::Escape($env:USERNAME) + '\b', '<user>') }
  if ($env:COMPUTERNAME) { $s = [regex]::Replace($s, '(?i)' + [regex]::Escape($env:COMPUTERNAME), '<pc>') }
  [regex]::Replace($s, $ReEmail, '<email>')
}
function Parse-Wer($lines) {   # Report.wer lines -> one line: event, app, module, offset, code
  $sig = @{}; $v = @{}; $top = @{}
  foreach ($l in $lines) {
    if ($l -match '^Sig\[(\d+)\]\.Name=(.*)$') { $sig[$Matches[1]] = $Matches[2] } elseif ($l -match '^Sig\[(\d+)\]\.Value=(.*)$') { $v[$Matches[1]] = $Matches[2] }
    elseif ($l -match '^(EventType|AppPath)=(.*)$') { $top[$Matches[1]] = $Matches[2] }
  }
  $by = @{}; foreach ($i in $sig.Keys) { $by[$sig[$i]] = $v[$i] }
  $code = $by['Exception Code']; if ($code) { $code = Describe-Code ([Convert]::ToInt64($code, 16)) }
  '{0} {1}: module {2} {3}, offset 0x{4}, exception {5}' -f $top['EventType'], $by['Application Name'], $by['Fault Module Name'], $by['Fault Module Version'], $by['Exception Offset'], $code
}
function Format-Event($id, $p) {   # Application log 1000/1002 (props 0 app .. 7 offset) and WER 1001 (2 event, 5 P1 app, 8 P4 module, 11 P7, 12 P8)
  $p = @($p | ForEach-Object { [string]$_ })
  if ($id -eq 1001) { if ($p.Count -gt 12 -and $p[5] -match $ReExe) { return 'WER {0} {1}: module {2}, exception {3}, offset 0x{4}' -f $p[2], $p[5], $p[8], $p[11], $p[12] } ; return $null }
  if ($p.Count -lt 1 -or $p[0] -notmatch $ReExe) { return $null }
  if ($id -eq 1002) { return 'hang (Application Hang 1002): {0} {1}' -f $p[0], $p[1] }
  $code = $p[6]; try { $code = Describe-Code ([Convert]::ToInt64($p[6], 16)) } catch {}
  'crash (Application Error 1000): {0} {1}, faulting module {2} {3}, exception {4}, offset 0x{5}' -f $p[0], $p[1], $p[3], $p[4], $code, $p[7]
}

# ---------- install, environment, inventory ----------
function Get-Installs {
  $r = @()
  foreach ($k in 'HKLM:\SOFTWARE\WOW6432Node\Sims(Steam)\The Sims 3', 'HKLM:\SOFTWARE\WOW6432Node\Sims\The Sims 3', 'HKLM:\SOFTWARE\Sims\The Sims 3',
      'HKLM:\SOFTWARE\WOW6432Node\EA Games\The Sims 3', 'HKLM:\SOFTWARE\WOW6432Node\Electronic Arts\The Sims 3') {
    try { $d = (Get-ItemProperty -LiteralPath $k -ErrorAction Stop).'Install Dir'; if ($d) { $r += [pscustomobject]@{ Src = 'registry ' + ($k -replace '^HKLM:\\SOFTWARE\\', ''); Dir = $d.TrimEnd('\') } } } catch {}
  }
  foreach ($d in "$env:ProgramFiles\EA Games\The Sims 3", "${env:ProgramFiles(x86)}\EA Games\The Sims 3", "${env:ProgramFiles(x86)}\Origin Games\The Sims 3",
      "${env:ProgramFiles(x86)}\Steam\steamapps\common\The Sims 3") { $r += [pscustomobject]@{ Src = 'default path'; Dir = $d } }
  @($r | Where-Object { Test-Path -LiteralPath (Join-Path $_.Dir 'Game\Bin') } | Group-Object Dir | ForEach-Object {
      [pscustomobject]@{ Dir = $_.Name; Bin = (Join-Path $_.Name 'Game\Bin'); Src = (($_.Group | ForEach-Object { $_.Src }) -join ', ') } })
}
function Find-Bin {
  if ($Bin) { return $Bin }
  $g = Get-Process -Name $GameNames -ErrorAction SilentlyContinue | Select-Object -First 1; if ($g) { try { return (Split-Path $g.Path) } catch {} }
  foreach ($i in Get-Installs) { if ((Test-Path (Join-Path $i.Bin 'TS3W.exe')) -or (Test-Path (Join-Path $i.Bin 'TS3.exe'))) { return $i.Bin } }
  if (Test-Path (Join-Path $PSScriptRoot 'TS3W.exe')) { return $PSScriptRoot }
  return $null
}
function Get-Edition($binDir) {
  $v = @{}; foreach ($e in 'TS3W.exe', 'TS3.exe') { $p = Join-Path $binDir $e; if (Test-Path -LiteralPath $p) { $v[$e] = [string](Get-Item -LiteralPath $p).VersionInfo.FileVersion } }
  if ($v['TS3.exe'] -like '1.69*' -or $v['TS3W.exe'] -like '1.69*') { 'EA' } elseif ($v['TS3W.exe'] -like '1.67*') { 'Steam' } else { '?' }
}
function Get-DocsDirs {
  if ($script:DocsDirs) { return $script:DocsDirs }
  $ea = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Electronic Arts'
  @(Get-ChildItem -LiteralPath $ea -Directory -ErrorAction SilentlyContinue | Where-Object { (Test-Path (Join-Path $_.FullName 'Options.ini')) -or $_.Name -match 'Sims 3' } | ForEach-Object { $_.FullName })
}
function Get-Targets { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $n = $_.ProcessName; $names | Where-Object { $n -like $_ } }) }
function Copy-Capped($src, $dst, $note, $cap = 5MB) {   # newest first, each <= $cap (tail of larger files)
  foreach ($f in @($src | Where-Object { $_ } | Sort-Object LastWriteTime -Descending)) {
    $t = Join-Path $dst ($note + '__' + $f.Name)
    try {
      if ($f.Length -le $cap) { Copy-Item -LiteralPath $f.FullName $t } else {
        $fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite'); [void]$fs.Seek(-$cap, 'End'); $o = [IO.File]::Create($t); $fs.CopyTo($o); $o.Close(); $fs.Close() }
      $st = $(if ($f.Length -gt $cap) { 'last {0:N0} MB only' -f ($cap / 1MB) } else { 'copied' })
    } catch { $st = 'not copied: ' + $_.Exception.Message }
    "{0,-60} {1,12:N0} bytes  last written {2:yyyy-MM-dd HH:mm:ss}  ({3})" -f $f.FullName, $f.Length, $f.LastWriteTime, $st
  }
}
function Read-Head($path, $n = 65536) { try { $fs = [IO.File]::Open($path, 'Open', 'Read', 'ReadWrite'); $b = New-Object byte[] $n; $k = $fs.Read($b, 0, $n); $fs.Close(); [Text.Encoding]::UTF8.GetString($b, 0, $k) } catch { '' } }

function Check-Bin($binDir, $o, $ld) {
  $o.Add('=== Game\Bin inventory (name, size, date, sha256 prefix, PE machine, file version)')
  $rows = @(Get-ChildItem -LiteralPath $binDir -Force -ErrorAction SilentlyContinue | Where-Object { -not $_.PSIsContainer -and $_.Name -match '\.(exe|dll|asi|ini|toml|conf|log)$' })
  $trex = Join-Path $binDir '.trex'
  if (Test-Path -LiteralPath $trex) { $rows += @(Get-ChildItem -LiteralPath $trex -Recurse -Force -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match '\.(exe|dll|conf|log)$' } | Select-Object -First 200) }
  $info = @{}
  foreach ($f in $rows) {
    $rel = $f.FullName.Substring($binDir.Length).TrimStart('\'); $pe = $null; $h = ''
    if ($f.Name -match '\.(exe|dll|asi)$') { $pe = Get-PeInfo $f.FullName }
    if ($f.Length -le 200MB) { try { $h = (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.Substring(0, 12) } catch {} }
    $o.Add(("{0,-48} {1,12:N0} {2:yyyy-MM-dd HH:mm}  {3,-12} {4,-6} {5}" -f $rel, $f.Length, $f.LastWriteTime, $h, $(if ($pe) { $pe.Machine } else { '' }), $f.VersionInfo.FileVersion))
    $info[$rel.ToLower()] = [pscustomobject]@{ F = $f; Rel = $rel; Pe = $pe }
  }
  foreach ($d in Get-ChildItem -LiteralPath $binDir -Directory -Force -ErrorAction SilentlyContinue) { $o.Add("<dir> $($d.Name)\") }
  $root = @($info.Values | Where-Object { $_.Rel -notmatch '\\' })
  # 64-bit modules in Bin (the bridge server under .trex is 64-bit on purpose)
  foreach ($r in $root | Where-Object { $_.Pe -and $_.Pe.Machine -eq 'x64' -and $_.Rel -match '\.(dll|asi)$' }) { F "64-bit DLL in Game\Bin: $($r.Rel) (the game is 32-bit: a 64-bit DLL/ASI there fails to load or crashes the start)" }
  $str = @{}; foreach ($r in $root | Where-Object { $_.Rel -match '\.(dll|asi)$' }) { $str[$r.Rel.ToLower()] = Get-Strings $r.F.FullName }
  $d3 = [string]$str['d3d9.dll']
  $remix = ($d3 -match '(?i)NvRemix|RemixBridge|bridge32') -or (Test-Path -LiteralPath $trex)
  if ($d3) { $o.Add('d3d9.dll in Bin: ' + $(if ($remix) { 'RTX Remix bridge' } elseif ($d3 -match '(?i)dxvk') { 'DXVK' } elseif ($d3 -match '(?i)ReShade') { 'ReShade' } else { 'unknown proxy' })) }
  if ($remix) {
    if (-not $info.ContainsKey('.trex\nvremixbridge.exe')) { F 'RTX Remix: bridge server .trex\NvRemixBridge.exe is missing (bridge d3d9.dll present): reinstall the Remix/Apex package' }
    if (-not $d3) { F 'RTX Remix: .trex folder present but no bridge d3d9.dll in Game\Bin' }
    foreach ($r in $root | Where-Object { $_.Rel -match '\.(dll|asi)$' -and $_.Rel -notmatch '(?i)^(d3d9\.dll|ApexRadiance\.asi|ts3native\.asi|d3dcompiler_\d+\.dll)$' }) {
      $s = [string]$str[$r.Rel.ToLower()]
      if ($r.Rel -match $ReD3d9Hooker -or $s -match '(?i)Direct3DCreate9|d3d9\.dll') { F "d3d9 hook next to the Remix bridge: $($r.Rel) $(if ($r.Rel -match $ReD3d9Hooker) { '(known d3d9 mod)' } else { '(references d3d9)' }): try one start without it" }
    }
    foreach ($n in 'bridge.conf', 'dxvk.conf') { $o.Add("$n present: $($info.ContainsKey($n))") }
  }
  foreach ($r in $root | Where-Object { $_.Rel -ne 'd3d9.dll' -and [string]$str[$_.Rel.ToLower()] -match 'ReShade' -and $_.Rel -match '\.dll$' }) { F "ReShade found: $($r.Rel)$(if ($remix) { ' (ReShade and the Remix bridge both hook D3D)' })" }
  $loaders = @($root | Where-Object { $_.Rel -match $ReLoaderName -and [string]$str[$_.Rel.ToLower()] -match '(?i)ASI.?Loader' } | ForEach-Object { $_.Rel })
  $asis = @($root | Where-Object { $_.Rel -match '\.asi$' } | ForEach-Object { $_.Rel })
  $o.Add("ASI loaders: $($loaders -join ', ')   ASI files: $($asis -join ', ')")
  if ($loaders.Count -gt 1) { F "more than one ASI loader in Game\Bin: $($loaders -join ', ') (ASIs can load twice)" }
  if ($asis.Count -and -not $loaders.Count) { F 'ASI files in Game\Bin but no ASI loader found (wininet.dll/dinput8.dll ...): they will not load' }
  # S3SS / Apex / ts3native
  foreach ($dd in @(Get-DocsDirs) + $binDir) {
    foreach ($t in @(Get-ChildItem -LiteralPath $dd -Filter 'S3SS.toml' -Recurse -Depth 1 -ErrorAction SilentlyContinue)) {
      if (Get-TomlEnabled (Get-Content -LiteralPath $t.FullName -Raw) 'patches.ExpandedCrashLogs') { F "S3SS ExpandedCrashLogs is on ($($t.FullName)): known direct-start hang (docs/256 section 8)" }
    }
  }
  $nl = Get-ChildItem -LiteralPath $binDir -Filter 'ts3native*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($nl) {
    $head = Read-Head $nl.FullName; $m = [regex]::Match($head, $ReNativeVer)
    if ($m.Success) {
      $v = $m.Groups[1].Value; $c = Compare-NativeVersion $v $LatestNative
      $o.Add("ts3native log: $v ($c than latest release $LatestRelease = $LatestNative), written $($nl.LastWriteTime)")
      if ($c -eq 'older') { F "ts3native $v is older than the latest release $LatestRelease ($LatestNative): update" }
    }
    $os = [regex]::Match($head, $ReOs)
    if ($os.Success -and [int]$os.Groups[1].Value -lt 10 -and [Environment]::OSVersion.Version.Major -ge 10) { F "game ran in Windows compatibility mode: ts3native log says 'os: Windows $($os.Groups[1].Value).$($os.Groups[2].Value).$($os.Groups[3].Value)' on Windows 10/11 (exe Properties > Compatibility: untick)" }
  } elseif ($info.ContainsKey('ts3native.asi')) { F 'ts3native.asi present but no ts3native.log: it never ran' }
  return $remix
}

function Check-Env($o, $binDir, $remix) {
  $o.Add('=== Environment')
  $os = Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue
  if ($os) {
    $o.Add("Windows: $($os.Caption) $($os.Version) build $($os.BuildNumber)  RAM free $([int]($os.FreePhysicalMemory / 1KB)) / $([int]($os.TotalVisibleMemorySize / 1KB)) MB")
    if ($os.FreePhysicalMemory / 1KB -lt 2048) { F "low free RAM: $([int]($os.FreePhysicalMemory / 1KB)) MB" }
  }
  $pf = @(Get-CimInstance Win32_PageFileUsage -ErrorAction SilentlyContinue); $auto = (Get-CimInstance Win32_ComputerSystem -ErrorAction SilentlyContinue).AutomaticManagedPagefile
  $o.Add("page file: $(($pf | ForEach-Object { '{0} {1} MB' -f $_.Name, $_.AllocatedBaseSize }) -join ', ')  auto-managed: $auto")
  if (-not $pf.Count -and -not $auto) { F 'no page file: the game and the bridge can fail to allocate memory' }
  foreach ($g in Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue) { $o.Add("GPU: $($g.Name)  driver $($g.DriverVersion) ($($g.DriverDate))") }
  foreach ($d in 'System32', 'SysWOW64') {
    $p = Join-Path $env:windir "$d\vulkan-1.dll"
    if (Test-Path $p) { $o.Add("Vulkan runtime $d`: $((Get-Item $p).VersionInfo.FileVersion)") } else {
      $o.Add("Vulkan runtime $d`: missing"); if ($remix -or $d -eq 'SysWOW64') { F "Vulkan runtime missing in $d (vulkan-1.dll): $(if ($d -eq 'System32') { 'the Remix bridge server needs it' } else { 'DXVK x32 needs it' }); update the GPU driver" } }
  }
  foreach ($k in @(@('x86', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\VisualStudio\14.0\VC\Runtimes\x86'), @('x64', 'HKLM:\SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\x64'))) {
    $v = $null; try { $v = (Get-ItemProperty -LiteralPath $k[1] -ErrorAction Stop).Version } catch {}
    $o.Add("VC++ 2015-2022 $($k[0]): $(if ($v) { $v } else { 'missing' })"); if (-not $v) { F "VC++ 2015-2022 redistributable $($k[0]) missing (mods and the bridge need it)" }
  }
  if ($binDir) {
    foreach ($e in 'TS3W.exe', 'TS3.exe', 'Sims3Launcher.exe') {
      $p = Join-Path $binDir $e; if (-not (Test-Path -LiteralPath $p)) { continue }
      $pe = Get-PeInfo $p; $h = ''; try { $h = (Get-FileHash -LiteralPath $p -Algorithm SHA256).Hash.Substring(0, 16) } catch {}
      $o.Add("$e  version $((Get-Item -LiteralPath $p).VersionInfo.FileVersion)  sha256 $h  $($pe.Machine)  large address aware: $($pe.LAA)")
      if ($e -ne 'Sims3Launcher.exe' -and $pe.LAA -eq $false -and -not ($e -eq 'TS3.exe' -and (Test-Path (Join-Path $binDir 'TS3W.exe')))) { F "$e is not Large Address Aware (2 GB limit instead of 4 GB)" }
    }
  }
  # Compatibility layers (HKCU + HKLM, both views)
  foreach ($k in 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers', 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers',
      'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers') {
    $it = Get-Item -LiteralPath $k -ErrorAction SilentlyContinue; if (-not $it) { continue }
    foreach ($n in $it.GetValueNames()) {
      if ($n -notmatch $ReLayerName -and -not ($binDir -and $n -like "$binDir\*")) { continue }
      $d = [string]$it.GetValue($n); $mm = [regex]::Match($d, $ReCompatMode)
      $s = "compatibility settings on {0} ({1}): '{2}'" -f $n, ($k -replace '\\Software.*|\\SOFTWARE.*', ''), $d.Trim()
      if ($mm.Success) { F "$s = Windows compatibility mode $($mm.Value) set: remove it (exe Properties > Compatibility)" } else { $o.Add($s) }   # HIGHDPIAWARE etc. are harmless
    }
  }
  # Defender detections touching the game or mods (no admin path first)
  $re = '(?i)Sims 3|TS3|\.asi|NvRemix|\.trex'
  try { foreach ($t in @(Get-MpThreatDetection -ErrorAction Stop | Where-Object { ($_.Resources -join ' ') -match $re })) { F "Windows Defender detection $($t.InitialDetectionTime): $(($t.Resources | Select-Object -First 3) -join '; ')" } } catch { $o.Add("Get-MpThreatDetection: not available ($($_.Exception.Message))") }
  try { Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Windows Defender/Operational'; Id = 1116, 1117; StartTime = (Get-Date).AddDays(-14) } -ErrorAction Stop | Where-Object { $_.Message -match $re } | Select-Object -First 5 |
      ForEach-Object { F "Defender event $($_.Id) $($_.TimeCreated): $((($_.Message -split "`n") | Where-Object { $_ -match 'Path|Name:' } | Select-Object -First 2) -join ' ')" } } catch {}
  # LocalDumps state
  foreach ($e in $CrashExes) { $p = Get-ItemProperty -LiteralPath "$LdKey\$e" -ErrorAction SilentlyContinue; if ($p) { $o.Add("WER LocalDumps for $e`: on (folder $($p.DumpFolder), ours: $([bool]$p.ts3diag))") } }
}

function Check-Crashes($o, $ld) {
  $o.Add('=== Application event log, last 24 h (TS3W / TS3 / NvRemixBridge / Sims3Launcher)')
  try {
    Get-WinEvent -FilterHashtable @{ LogName = 'Application'; Id = 1000, 1001, 1002; StartTime = (Get-Date).AddHours(-24) } -ErrorAction Stop |
      Where-Object { $_.ProviderName -match 'Application Error|Windows Error Reporting|Application Hang' } | ForEach-Object {
        $s = Format-Event $_.Id @($_.Properties | ForEach-Object { $_.Value }); if ($s) { $o.Add(('[{0:s}] {1}' -f $_.TimeCreated, $s)); if ($_.Id -ne 1001) { F ('{0:yyyy-MM-dd HH:mm:ss} {1}' -f $_.TimeCreated, $s) } } }
  } catch { $o.Add("none or unreadable: $($_.Exception.Message)") }
  $o.Add('=== WER reports (Report.wer, newest 5)')
  $wd = Join-Path $ld 'wer'; New-Item -ItemType Directory -Force $wd | Out-Null; $n = 0
  $dirs = foreach ($r in "$env:ProgramData\Microsoft\Windows\WER", "$env:LOCALAPPDATA\Microsoft\Windows\WER") { foreach ($s in 'ReportArchive', 'ReportQueue') { Get-ChildItem -LiteralPath "$r\$s" -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $ReWerDir } } }
  foreach ($d in @($dirs | Sort-Object LastWriteTime -Descending | Select-Object -First 5)) {
    $w = Join-Path $d.FullName 'Report.wer'; if (-not (Test-Path -LiteralPath $w)) { continue }
    try { $lines = Get-Content -LiteralPath $w -ErrorAction Stop; $n++; Copy-Item -LiteralPath $w (Join-Path $wd "Report-$n.wer")
      $s = Parse-Wer $lines; $o.Add(('{0:yyyy-MM-dd HH:mm:ss} {1}  ({2})' -f $d.LastWriteTime, $s, $d.Name)); if ($d.LastWriteTime -gt (Get-Date).AddDays(-2)) { F ('{0:yyyy-MM-dd HH:mm:ss} WER {1}' -f $d.LastWriteTime, $s) } } catch { $o.Add("unreadable: $($d.Name)") }
  }
  # Windows crash dumps (LocalDumps default folder) from the last 24 h
  $dumps = @(Get-ChildItem -LiteralPath "$env:LOCALAPPDATA\CrashDumps" -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $ReDumpFile -and $_.LastWriteTime -gt (Get-Date).AddHours(-24) } | Sort-Object LastWriteTime -Descending | Select-Object -First 2)
  foreach ($d in $dumps) { if ($d.Length -le 300MB) { Copy-Item -LiteralPath $d.FullName (Join-Path $ld $d.Name); F "Windows crash dump captured: $($d.Name) ($('{0:N0}' -f ($d.Length / 1MB)) MB, in the zip)" } else { $o.Add("crash dump too big to zip: $($d.FullName)") } }
}

function Collect-Logs($o, $binDir, $ld) {
  $o.Add('=== Logs (stale LastWriteTime = that mod never ran in the last start)')
  $src = @()
  if ($binDir) {
    $src += @(Get-ChildItem (Join-Path $binDir '*.log') -ErrorAction SilentlyContinue)
    $src += @(Get-ChildItem (Join-Path $binDir 'rtx-remix\logs\*.log') -ErrorAction SilentlyContinue)
    $src += @(Get-ChildItem -LiteralPath (Join-Path $binDir '.trex') -Filter '*.log' -Recurse -Depth 2 -ErrorAction SilentlyContinue)
    $src += @(Get-ChildItem (Join-Path $binDir '*.toml'), (Join-Path $binDir '*.conf'), (Join-Path $binDir 'ts3native.ini'), (Join-Path $binDir 'ApexRadiance_Crash.txt') -ErrorAction SilentlyContinue)
    $o.AddRange([string[]]@(Copy-Capped $src $ld 'bin'))
    $o.AddRange([string[]]@(Copy-Capped @(Get-ChildItem (Join-Path $binDir 'ts3native-crashes\crash-*.txt') -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3) $ld 'bin-crash'))
  }
  foreach ($dd in Get-DocsDirs) {
    $o.AddRange([string[]]@(Copy-Capped (@(Get-ChildItem (Join-Path $dd 'S3SS\*') -Include 'S3SS_LOG.txt', 'S3SS.toml' -ErrorAction SilentlyContinue) + @(Get-ChildItem (Join-Path $dd 'S3SS_LOG.txt') -ErrorAction SilentlyContinue)) $ld 'S3SS'))
    $o.AddRange([string[]]@(Copy-Capped @(Get-ChildItem (Join-Path $dd 'Apex Radiance\*') -Include 'ApexRadiance_LOG.txt', 'ApexRadiance_Crash.txt', 'ApexRadiance.toml' -ErrorAction SilentlyContinue) $ld 'Apex'))
    $o.AddRange([string[]]@(Copy-Capped @(Get-ChildItem (Join-Path $dd '*') -Include 'Options.ini', 'DeviceConfig.log', 'Sims3LauncherLogFile.log' -ErrorAction SilentlyContinue) $ld 'docs'))
    $o.AddRange([string[]]@(Copy-Capped @(Get-ChildItem (Join-Path $dd 'xcpt*') -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3) $ld 'docs'))
    $o.AddRange([string[]]@(Copy-Capped @(Get-ChildItem (Join-Path $dd 'ScriptError*.xml') -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3) $ld 'docs'))
    $cr = @(Get-ChildItem (Join-Path $dd 'ts3native-crashes\crash-*.txt') -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 3)
    $o.AddRange([string[]]@(Copy-Capped $cr $ld 'ts3native-crash'))
    foreach ($c in $cr | Where-Object { $_.LastWriteTime -gt (Get-Date).AddDays(-2) }) { $m = [regex]::Match((Read-Head $c.FullName 4096), $ReCrashTitle); F ('ts3native crash report {0:yyyy-MM-dd HH:mm:ss}: {1}' -f $c.LastWriteTime, $(if ($m.Success) { $m.Groups[1].Value } else { $c.Name })) }
    foreach ($c in @(Get-ChildItem (Join-Path $dd 'Apex Radiance\ApexRadiance_Crash.txt') -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -gt (Get-Date).AddDays(-2) })) { F ('Apex Radiance crash report {0:yyyy-MM-dd HH:mm:ss} (logs\Apex__ApexRadiance_Crash.txt)' -f $c.LastWriteTime) }
  }
  # EA app logs: only lines about the game, never lines that look like auth data
  $ea = @(Get-ChildItem "$env:ProgramData\EA Desktop\Logs\EADesktop.log", "$env:LOCALAPPDATA\Electronic Arts\EA Desktop\Logs\EADesktop.log" -ErrorAction SilentlyContinue)
  $keep = foreach ($f in $ea) { try { $fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite'); if ($fs.Length -gt 2MB) { [void]$fs.Seek(-2MB, 'End') }; $rd = New-Object IO.StreamReader($fs); $t = $rd.ReadToEnd(); $rd.Close()
      $t -split "`r?`n" | Where-Object { $_ -match '(?i)sims ?3|TS3W?\.exe|Sims3Launcher' -and $_ -notmatch '(?i)token|auth|bearer|cookie|password|session|secret|@' } | Select-Object -Last 100 } catch {} }
  if ($keep) { $keep | Out-File (Join-Path $ld 'ea-app-filtered.txt') -Encoding utf8; $o.Add("EA app log: $(@($keep).Count) game lines kept (no auth lines)") }
}

function Mask-Folder($dir) {   # user name, PC name and e-mails out of every text file and file name
  foreach ($f in Get-ChildItem -LiteralPath $dir -Recurse -File | Where-Object { $_.Extension -match '(?i)^\.(txt|log|ini|toml|conf|wer|xml|json)$' }) {
    $b = [IO.File]::ReadAllBytes($f.FullName); $enc = if ($b.Length -ge 2 -and $b[0] -eq 0xFF -and $b[1] -eq 0xFE) { [Text.Encoding]::Unicode } else { New-Object Text.UTF8Encoding($false) }
    $t = $enc.GetString($b); $m = Mask-Text $t; if ($m -ne $t) { [IO.File]::WriteAllText($f.FullName, $m, $enc) }
  }
  foreach ($f in Get-ChildItem -LiteralPath $dir -Recurse -File) { $n = Mask-Text $f.Name; if ($n -ne $f.Name) { Rename-Item -LiteralPath $f.FullName ($n -replace '[<>]', '_') } }
}

function Collect($targets, $out, $binDir, $watchLines) {
  New-Item -ItemType Directory -Force $out | Out-Null
  $ld = Join-Path $out 'logs'; New-Item -ItemType Directory -Force $ld | Out-Null
  $cim = @{}; Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | ForEach-Object { $cim[[int]$_.ProcessId] = $_ }
  $o = New-Object System.Collections.Generic.List[string]
  $o.Add("ts3-diag  $(Get-Date -Format s)  PS $($PSVersionTable.PSVersion)  OS $([Environment]::OSVersion.Version)  steam.exe running: $([bool](Get-Process steam -ErrorAction SilentlyContinue))  EA app running: $([bool](Get-Process EADesktop -ErrorAction SilentlyContinue))")
  $o.Add("Bin folder: $binDir   edition: $(if ($binDir) { Get-Edition $binDir })")
  foreach ($i in Get-Installs) { $o.Add("install found: $($i.Dir)  ($($i.Src))") }
  if (-not $binDir) { F 'The Sims 3 install was not found: rerun with -Bin "<...>\The Sims 3\Game\Bin"' }
  if ($watchLines) { $o.Add(''); $o.Add('=== Watch'); $o.AddRange([string[]]$watchLines) }
  $o.Add('')
  if (-not $targets) { $o.Add('No TS3W/TS3/NvRemixBridge/Sims3Launcher process is running.') }
  foreach ($p in $targets) {
    $id = [int]$p.Id; $c = $cim[$id]; $tag = "$($p.ProcessName)-$id"
    $mods = [HangNative]::Modules([uint32]$id)
    $mods | Out-File (Join-Path $out "modules-$tag.txt") -Encoding utf8
    $o.Add("=== $($p.ProcessName) PID $id  parent PID $(if($c){$c.ParentProcessId})  started $($p.StartTime)  main window: $([bool]($p.MainWindowHandle -ne [IntPtr]::Zero)) '$($p.MainWindowTitle)'  responding: $($p.Responding)")
    $o.Add("    CPU time $($p.TotalProcessorTime)  threads $($p.Threads.Count)  modules $($mods.Count)  cmd: $(if($c){$c.CommandLine})")
    $parsed = @($mods | ForEach-Object { $x = $_ -split '\|', 4; if ($x.Count -eq 4) { [pscustomobject]@{ B = [Convert]::ToInt64($x[0], 16); S = [int64]$x[1]; N = $x[2] } } })
    $o.Add('    interesting modules: ' + (($parsed | Where-Object { $_.N -match 'd3d9|dxvk|wininet|steam|\.asi$|ts3native|apex|s3ss|remix|bridge|mono' } | ForEach-Object { $_.N }) -join ', '))
    foreach ($t in $p.Threads) {
      $sa = [int64]0; try { $sa = [int64]$t.StartAddress } catch {}
      $m = $parsed | Where-Object { $sa -ge $_.B -and $sa -lt ($_.B + $_.S) } | Select-Object -First 1
      $w = ''; try { if ($t.ThreadState -eq 'Wait') { $w = $t.WaitReason } } catch {}
      $o.Add(("      tid {0,6}  {1,-10} {2,-16} start 0x{3:X} {4}" -f $t.Id, $t.ThreadState, $w, $sa, $(if ($m) { $m.N } else { '(module unknown)' })))
    }
    if (-not $script:NoDump -and ($script:DumpAll -or $p.ProcessName -match '^(TS3W|TS3|NvRemixBridge)$')) {
      $dp = Join-Path $out "$tag.dmp"; $r = 'error'
      try { $r = [HangNative]::Dump($p.Handle, [uint32]$id, $dp) } catch { $r = $_.Exception.Message }
      $o.Add("    minidump: $r  $(if(Test-Path $dp){'{0:N1} MB' -f ((Get-Item $dp).Length/1MB)})")
    }
    $o.Add('')
  }
  $remix = $false
  if ($binDir -and (Test-Path -LiteralPath $binDir)) { $remix = Check-Bin $binDir $o $ld; $o.Add('') }
  $null = Check-Env $o $binDir $remix; $o.Add('')
  $null = Collect-Logs $o $binDir $ld; $o.Add('')
  $null = Check-Crashes $o $ld
  $sum = @("ts3-diag summary  $(Get-Date -Format s)", '', "FINDINGS ($($script:Findings.Count))") + @($script:Findings | ForEach-Object { "- $_" })
  if (-not $script:Findings.Count) { $sum += '- nothing flagged' }
  ($sum + @('', '----- details -----') + $o) | Out-File (Join-Path $out 'summary.txt') -Encoding utf8
  $null = Mask-Folder $out
  $zip = "$out.zip"; if (Test-Path $zip) { Remove-Item $zip }
  Compress-Archive -Path "$out\*" -DestinationPath $zip
  return $zip
}

# ---------- crash mode: watch, LocalDumps ----------
function Watch-Game($gameNames, [int]$waitStart, [int]$hangSec, [int]$maxSec) {
  $seen = @{}; $t0 = Get-Date; $first = $null; $hung = $null; $gone = $null; $why = ''
  while ($true) {
    foreach ($p in @(Get-Process -Name $gameNames -ErrorAction SilentlyContinue)) {
      if ($seen.ContainsKey($p.Id)) { continue }
      try { [void]$p.Handle } catch {}   # keep a handle so ExitCode is readable after exit
      $seen[$p.Id] = [pscustomobject]@{ P = $p; Name = $p.ProcessName; Id = $p.Id; Start = (Get-Date); Exit = $null; Code = $null; Window = $false; NotResp = $null }
      if (-not $first) { $first = Get-Date }; Say "  seen $($p.ProcessName).exe PID $($p.Id)"
    }
    $alive = 0
    foreach ($s in $seen.Values) {
      if ($s.Exit) { continue }
      $s.P.Refresh()
      if ($s.P.HasExited) { $s.Exit = Get-Date; try { $s.Code = $s.P.ExitCode } catch {}; Say ("  {0}.exe PID {1} exited after {2:N1} s, code {3}" -f $s.Name, $s.Id, ($s.Exit - $s.Start).TotalSeconds, $(if ($null -ne $s.Code) { Describe-Code $s.Code } else { '?' })); continue }
      $alive++
      if ($s.P.MainWindowHandle -ne [IntPtr]::Zero) { $s.Window = $true }
      $resp = $true; try { $resp = $s.P.Responding } catch {}
      if ($resp) { $s.NotResp = $null } elseif (-not $s.NotResp) { $s.NotResp = Get-Date }
      if ((-not $s.Window -and ((Get-Date) - $s.Start).TotalSeconds -ge $hangSec) -or ($s.NotResp -and ((Get-Date) - $s.NotResp).TotalSeconds -ge $hangSec)) { $hung = $s }
    }
    if ($hung) { $why = "hang: $($hung.Name).exe $(if ($hung.Window) { 'not responding' } else { 'no window' }) for $hangSec s"; break }
    if ($first -and $alive -eq 0) { if (-not $gone) { $gone = Get-Date } elseif (((Get-Date) - $gone).TotalSeconds -ge 10) { $why = 'all game processes exited'; break } } else { $gone = $null }
    if (-not $first -and ((Get-Date) - $t0).TotalSeconds -ge $waitStart) { $why = "the game did not start within $waitStart s"; break }
    if ($first -and ((Get-Date) - $first).TotalSeconds -ge $maxSec) { $why = "still running after $maxSec s: no crash seen (the game keeps running)"; break }
    Start-Sleep -Milliseconds 1000
  }
  $lines = @("watch ended: $why") + @($seen.Values | Sort-Object Start | ForEach-Object {
      '{0}.exe PID {1}: started {2:HH:mm:ss}, {3}, window seen: {4}' -f $_.Name, $_.Id, $_.Start, $(if ($_.Exit) { 'exited after {0:N1} s with code {1}' -f ($_.Exit - $_.Start).TotalSeconds, $(if ($null -ne $_.Code) { Describe-Code $_.Code } else { '?' }) } else { 'still running' }), $_.Window })
  [pscustomobject]@{ Seen = @($seen.Values); Hung = $hung; Why = $why; Lines = $lines }
}
function Set-LocalDumps([bool]$on) {
  foreach ($e in $CrashExes) {
    $k = "$LdKey\$e"; $p = Get-ItemProperty -LiteralPath $k -ErrorAction SilentlyContinue
    if ($on) {
      if ($p -and -not $p.ts3diag) { Say "kept the existing $k (not set by this tool)"; continue }
      New-Item -Path $k -Force | Out-Null
      New-ItemProperty -LiteralPath $k -Name DumpFolder -Value '%LOCALAPPDATA%\CrashDumps' -PropertyType ExpandString -Force | Out-Null
      foreach ($v in @(@('DumpType', 0), @('CustomDumpFlags', 0x1B24), @('DumpCount', 3), @('ts3diag', 1))) { New-ItemProperty -LiteralPath $k -Name $v[0] -Value $v[1] -PropertyType DWord -Force | Out-Null }
      Say "crash dumps on for $e"
    } elseif ($p -and $p.ts3diag) { Remove-Item -LiteralPath $k -Force; Say "crash dumps off for $e" }
  }
}
function Invoke-LocalDumps([bool]$on) {
  $admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  if ($admin) { Set-LocalDumps $on; return }
  Say "Windows will ask for admin rights to turn crash dumps $(if ($on) { 'ON' } else { 'OFF' }) for TS3W.exe, TS3.exe, NvRemixBridge.exe (registry WER LocalDumps is machine-wide). Nothing else runs as admin."
  try { $p = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", '-LocalDumpsStep', $(if ($on) { 'on' } else { 'off' }); Say "admin step exit code $($p.ExitCode)" }
  catch { Say "admin step not done: $($_.Exception.Message)" }
}

function Out-Dir($name) {   # next to the tool when writable, else the Desktop
  try { $t = Join-Path $PSScriptRoot '.w'; [IO.File]::WriteAllText($t, ''); Remove-Item $t; return (Join-Path $PSScriptRoot $name) } catch { return (Join-Path ([Environment]::GetFolderPath('Desktop')) $name) }
}

# ---------- self-check ----------
function Run-SelfCheck {
  $fail = New-Object System.Collections.Generic.List[string]
  function T($n, $c) { if (-not $c) { $fail.Add($n) } }
  # regexCases: real lines (or the exact format string) that must match and must not
  $cases = @(
    @($ReExe, 'TS3W.exe', 'dwm.exe'), @($ReExe, 'NvRemixBridge.exe', 'TS3W.exe.orig'),
    @($ReWerDir, 'AppCrash_TS3W.exe_28bcf9febb24505dd38263d17c2b14779a25531_82bdd582_6a4e43fd-f802-4015-b0c6-bc3ca2439a83', 'AppCrash_cl.exe_865590bf4384617ba272f295bef86fb176b482b_30487379_e220bf89'),
    @($ReLayerName, 'C:\Program Files\EA Games\The Sims 3\Game\Bin\TS3.exe', 'C:\Games\NotTS3.exe.bak'),
    @($ReCompatMode, '~ WIN7RTM RUNASADMIN', '~ HIGHDPIAWARE'),
    @($ReNativeVer, 'ts3native 0.36.239 2026-11-10 19:47:21', 'prior log: ts3native 0.36.239 2026-11-10 19:14:13'),
    @($ReOs, 'diag: os: Windows 10.0.26300', 'host TS3W.exe base 0x00400000 pe 52DEC247 size 0xE5A000'),
    @($ReCrashTitle, 'title: [crash] TS3W.exe+0x1234 (ts3native 0.36.168c)', 'signature id: crashsig-0000abcd'),
    @($ReEmail, 'user a.b+x@example.co.uk wrote', 'ApexRadiance.asi+0x7DD90 @ 16:21:45'),
    @($ReLoaderName, 'wininet.dll', 'wininet.dll.bak'), @($ReD3d9Hooker, 'D3DShaderReplacer.asi', 'Sims3SettingsSetter.asi'),
    @($ReDumpFile, 'TS3W.exe.17908.dmp', 'selftest.exe.17908.dmp'))
  foreach ($c in $cases) { T "regex $($c[0]) matches '$($c[1])'" ($c[1] -match $c[0]); T "regex $($c[0]) rejects '$($c[2])'" ($c[2] -notmatch $c[0]) }
  T 'os regex groups' ([regex]::Match('os: Windows 6.1.7600', $ReOs).Groups[1].Value -eq '6')
  $wer = 'EventType=APPCRASH', 'Sig[0].Name=Application Name', 'Sig[0].Value=cl.exe', 'Sig[3].Name=Fault Module Name', 'Sig[3].Value=c1xx.dll', 'Sig[6].Name=Exception Code', 'Sig[6].Value=c0000005', 'Sig[7].Name=Exception Offset', 'Sig[7].Value=0029c25d'
  $w = Parse-Wer $wer; T "Parse-Wer: $w" ($w -match 'APPCRASH cl\.exe: module c1xx\.dll .*0x0029c25d.*access violation')
  $dwm = 'dwm.exe', '10.0.26100.9818', 'fb0c1258', 'dwmcore.dll', '10.0.26100.9818', 'b342355f', '88980006', '000000000012b2e9', '2436'
  T 'Format-Event skips dwm.exe' ($null -eq (Format-Event 1000 $dwm))
  $ts = 'TS3W.exe', '1.67.2.24002', '52dec247', 'd3d9.dll', '0.0.0.0', '00000000', 'c0000005', '0002d484', '1a2b'
  $e = Format-Event 1000 $ts; T "Format-Event TS3W: $e" ($e -match 'faulting module d3d9\.dll .*access violation.*0x0002d484')
  T 'Describe-Code negative int' ((Describe-Code -1073741819) -eq '0xC0000005 (access violation)')
  foreach ($c in @(@('0.36.168c', 'older'), @('0.36.168e', 'same'), @('0.36.239', 'newer'), @('0.36.189-rftest1', 'newer'), @('0.35.999', 'older'), @('junk', '?'))) { T "Compare-NativeVersion $($c[0])" ((Compare-NativeVersion $c[0] $LatestNative) -eq $c[1]) }
  $toml = "[patches.StartupWarningDialogFix]`nenabled = true`n`n[patches.ExpandedCrashLogs]`nenabled = true`n`n[patches.Mimalloc]`nenabled = false`n"
  T 'toml on' ((Get-TomlEnabled $toml 'patches.ExpandedCrashLogs') -eq $true); T 'toml off' ((Get-TomlEnabled $toml 'patches.Mimalloc') -eq $false); T 'toml absent' ($null -eq (Get-TomlEnabled $toml 'patches.Nope'))
  $mk = Mask-Text "C:\Users\$env:USERNAME\Documents on $env:COMPUTERNAME mail a.b@example.com"; T "Mask-Text: $mk" ($mk -notmatch [regex]::Escape($env:USERNAME) -and $mk -notmatch [regex]::Escape($env:COMPUTERNAME) -and $mk -match '<email>')

  # Fixture install: fake Bin + Documents, with the known bad combinations
  $fx = Join-Path $env:TEMP "ts3-diag-fixture-$stamp"; $fb = Join-Path $fx 'Game\Bin'; $fd = Join-Path $fx 'Docs\The Sims 3'
  New-Item -ItemType Directory -Force $fb, "$fb\.trex", "$fd\S3SS", "$fd\Apex Radiance", "$fd\ts3native-crashes" | Out-Null
  Copy-Item "$env:windir\SysWOW64\d3d9.dll" "$fb\d3d9.dll"; Copy-Item "$env:windir\SysWOW64\d3d9.dll" "$fb\WinterRoadsFix.asi"
  Copy-Item "$env:windir\System32\version.dll" "$fb\x64thing.dll"; Copy-Item "$env:windir\SysWOW64\version.dll" "$fb\wininet.dll"
  Set-Content "$fb\ts3native.log" "ts3native 0.36.168c 2026-11-10 10:00:00`r`nhost TS3W.exe base 0x00400000`r`ndiag: os: Windows 6.1.7600`r`nC:\Users\$env:USERNAME\x"
  Set-Content "$fd\S3SS\S3SS.toml" $toml; Set-Content "$fd\Apex Radiance\ApexRadiance_Crash.txt" 'Apex crash'
  for ($i = 1; $i -le 4; $i++) { Set-Content "$fd\ts3native-crashes\crash-2026111$i-000000-1.txt" "title: [crash] d3d9.dll+0x2D484 (ts3native 0.36.168c)"; (Get-Item "$fd\ts3native-crashes\crash-2026111$i-000000-1.txt").LastWriteTime = (Get-Date).AddMinutes($i - 10) }
  Set-Content "$fd\ScriptError_$($env:COMPUTERNAME)__1.xml" "<x>$env:USERPROFILE a@b.com</x>"
  $script:DocsDirs = @($fd); $script:DumpAll = $true
  $child = Start-Process powershell -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 120' -WindowStyle Hidden -PassThru
  Start-Sleep 2
  $out = Join-Path $env:TEMP "ts3-diag-selfcheck-$stamp"
  try { $zip = Collect @($child) $out $fb @('watch fixture line') } finally { Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue }
  $sum = Get-Content (Join-Path $out 'summary.txt') -Raw
  foreach ($x in '64-bit DLL in Game\\Bin: x64thing\.dll', 'NvRemixBridge\.exe is missing', 'd3d9 hook next to the Remix bridge: WinterRoadsFix\.asi', 'ExpandedCrashLogs is on',
      'ts3native 0\.36\.168c is older', "os: Windows 6\.1\.7600", 'ts3native crash report .*d3d9\.dll\+0x2D484', 'Apex Radiance crash report', 'watch fixture line') { T "summary has /$x/" ($sum -match $x) }
  T 'three ts3native crash files' (@(Get-ChildItem "$out\logs" -Filter 'ts3native-crash__*').Count -eq 3)
  $mod = @(Get-Content (Join-Path $out "modules-powershell-$($child.Id).txt")); $dmp = Get-Item (Join-Path $out "powershell-$($child.Id).dmp") -ErrorAction SilentlyContinue
  T 'module list has ntdll' ($mod -match 'ntdll'); T 'dump written' ($dmp -and $dmp.Length -gt 10KB); T 'zip written' (Test-Path $zip)
  $leak = @(Get-ChildItem $out -Recurse -File | Where-Object { $_.Extension -ne '.dmp' -and ($_.Name -match [regex]::Escape($env:COMPUTERNAME) -or (Get-Content $_.FullName -Raw) -match ([regex]::Escape($env:USERNAME) + '|' + [regex]::Escape($env:COMPUTERNAME))) } | ForEach-Object { $_.Name })
  T "no user/PC name in text files: $($leak -join ', ')" (-not $leak.Count)
  Say "summary head:`n$((Get-Content (Join-Path $out 'summary.txt') | Select-Object -First 14) -join "`n")"

  # Watch: a copied cmd.exe under a fake name exits with code 3, then hangs without a window
  $fake = Join-Path $fx 'ts3diagfake.exe'; Copy-Item "$env:windir\System32\cmd.exe" $fake
  $p1 = Start-Process $fake -ArgumentList '/c', 'ping -n 3 127.0.0.1 >nul & exit 3' -WindowStyle Hidden -PassThru
  $r = Watch-Game @('ts3diagfake') 10 60 60; T "watch exit code: $($r.Lines -join ' / ')" (($r.Lines -join ' ') -match 'exited after .* code 0x00000003' -and $r.Why -match 'exited')
  $p2 = Start-Process $fake -ArgumentList '/c', 'ping -n 30 127.0.0.1 >nul' -WindowStyle Hidden -PassThru
  $r = Watch-Game @('ts3diagfake') 10 3 60; T "watch hang: $($r.Why)" ($r.Hung -and $r.Why -match 'no window')
  Stop-Process -Id $p2.Id -Force -ErrorAction SilentlyContinue
  Start-Sleep 1
  Say "SelfCheck: $($cases.Count * 2) regex cases, dump=$(if($dmp){$dmp.Length}else{'none'}) bytes"
  Remove-Item $out, $zip, $fx -Recurse -Force -ErrorAction SilentlyContinue
  if ($fail.Count) { Say ('SelfCheck FAIL: ' + ($fail -join "`n  ")); return 1 }
  Say 'SelfCheck PASS'; return 0
}

# ---------- main ----------
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ($LocalDumpsStep) { Set-LocalDumps ($LocalDumpsStep -eq 'on'); exit 0 }
if ($SelfCheck) { exit (Run-SelfCheck) }
if ($DisableCrashDumps) { Invoke-LocalDumps $false; if (-not $Crash) { exit 0 } }
if ($EnableCrashDumps -and -not $Crash) { Invoke-LocalDumps $true; Say 'Crash dumps stay ON until you run: ts3-crash-diag.bat -DisableCrashDumps'; exit 0 }

$binDir = Find-Bin
if ($Crash) {
  $ed = $(if ($binDir) { Get-Edition $binDir } else { '?' })
  Say "Game\Bin: $binDir  (edition: $ed)"
  if ($EnableCrashDumps) { Invoke-LocalDumps $true }
  if ($Launch -and $ed -eq 'Steam') { Say 'Starting The Sims 3 through Steam ...'; Start-Process 'steam://rungameid/47890' }
  else {
    # EA app: a direct TS3.exe start bounces to the EA app (docs/230), so the player starts it there
    Say "Start The Sims 3 now the way you normally do ($(if ($ed -eq 'EA') { 'EA app: Play' } else { 'EA app or Steam' })). Waiting up to $WaitSeconds s; leave this window open."
  }
  $r = Watch-Game $GameNames $WaitSeconds $HangSeconds $WatchSeconds
  Say $r.Why
  foreach ($s in $r.Seen | Where-Object { $_.Exit -and $null -ne $_.Code -and $_.Code -ne 0 }) { F ('{0}.exe exited after {1:N1} s with code {2}' -f $s.Name, ($s.Exit - $s.Start).TotalSeconds, (Describe-Code $s.Code)) }
  if ($r.Why -match 'did not start|hang') { F "watch: $($r.Why)" }
  if (-not $r.Hung) { $script:NoDump = $true } else { Say 'Hang: writing dumps ...' }
  if ($r.Seen.Count) { Start-Sleep 5 }   # let Windows Error Reporting write its event and report
  $targets = $(if ($r.Hung) { Get-Targets } else { @() })
  $zip = Collect $targets (Out-Dir "ts3-crash-diag-$stamp") $binDir $r.Lines
  if ($EnableCrashDumps) { Invoke-LocalDumps $false }
} else {
  if ($Launch) {
    if (-not $binDir -or -not (Test-Path (Join-Path $binDir 'TS3W.exe'))) { Say 'Cannot find Game\Bin\TS3W.exe. Pass -Bin "<...>\The Sims 3\Game\Bin".'; exit 2 }
    Say "Starting $binDir\TS3W.exe directly, waiting up to 90 s for a window ..."
    $g = Start-Process (Join-Path $binDir 'TS3W.exe') -WorkingDirectory $binDir -PassThru
    $t0 = Get-Date
    while (((Get-Date) - $t0).TotalSeconds -lt 90) { Start-Sleep 2; $g.Refresh(); if ($g.HasExited) { Say "TS3W.exe exited early (code $($g.ExitCode))."; break }; if ($g.MainWindowHandle -ne [IntPtr]::Zero) { Say 'Window appeared: no hang. Nothing collected.'; exit 0 } }
  }
  $targets = Get-Targets
  $zip = Collect $targets (Out-Dir "ts3-hang-diag-$stamp") $binDir $null
  if ($KillAfter) { $targets | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }; Say 'Collected processes stopped (-KillAfter).' }
}
Say ''; Say "Done. Send this file: $zip"
Say 'Text files in it have your Windows user name and PC name masked; dump files (.dmp) can still contain them.'
