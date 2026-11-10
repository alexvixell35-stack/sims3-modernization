# TS3W hang diagnostics for testers. Windows PowerShell 5.1+. Windows-only (Toolhelp32, dbghelp, event log); no admin needed.
# Run while TS3W.exe is hung:  ts3-hang-diag.bat        (collect only)
#   -Launch [-Bin <Game\Bin>]  start TS3W.exe directly, wait 90 s for a window, collect if none
#   -KillAfter                 stop the collected processes afterwards (never done otherwise)
#   -SelfCheck                 test the tool on a harmless child process (not the game)
param([switch]$Launch, [string]$Bin, [switch]$KillAfter, [switch]$SelfCheck)
$ErrorActionPreference = 'Continue'
$names = 'TS3W', 'TS3', 'NvRemixBridge', 'Sims3Launcher*'

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
  // ThreadInfo 0x1000 | UnloadedModules 0x20 | HandleData 0x4 | FullMemoryInfo 0x800. Not full memory.
  public static string Dump(IntPtr proc, uint pid, string path) {
    using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Create)) {
      bool ok = MiniDumpWriteDump(proc, pid, fs.SafeFileHandle.DangerousGetHandle(), 0x1000 | 0x20 | 0x4 | 0x800, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
      return ok ? "ok" : "failed, win32=" + Marshal.GetLastWin32Error();
    }
  }
}
'@

function Say($m) { Write-Host $m }
function Find-Bin {
  if ($Bin) { return $Bin }
  try { $d = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\WOW6432Node\Sims(Steam)\The Sims 3' -ErrorAction Stop).'Install Dir'; if ($d) { return (Join-Path $d 'Game\Bin') } } catch {}
  if (Test-Path (Join-Path $PSScriptRoot 'TS3W.exe')) { return $PSScriptRoot }
  return $null
}
function Get-Targets { @(Get-Process -ErrorAction SilentlyContinue | Where-Object { $n = $_.ProcessName; $names | Where-Object { $n -like $_ } }) }
function Copy-Capped($src, $dst, $note) {   # newest first, each <= 20 MB (tail of larger files)
  foreach ($f in @($src | Where-Object { $_ } | Sort-Object LastWriteTime -Descending)) {
    $t = Join-Path $dst ($note + '__' + $f.Name)
    if ($f.Length -le 20MB) { Copy-Item -LiteralPath $f.FullName $t } else {
      $fs = [IO.File]::Open($f.FullName, 'Open', 'Read', 'ReadWrite'); [void]$fs.Seek(-20MB, 'End'); $o = [IO.File]::Create($t); $fs.CopyTo($o); $o.Close(); $fs.Close() }
    "{0,-60} {1,12:N0} bytes  last written {2:yyyy-MM-dd HH:mm:ss}  ({3})" -f $f.FullName, $f.Length, $f.LastWriteTime, $(if ($f.Length -gt 20MB) { 'last 20 MB only' } else { 'copied' })
  }
}

function Collect($targets, $out, $binDir) {
  New-Item -ItemType Directory -Force $out | Out-Null
  $cim = @{}; Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | ForEach-Object { $cim[[int]$_.ProcessId] = $_ }
  $o = New-Object System.Collections.Generic.List[string]
  $o.Add("ts3-hang-diag  $(Get-Date -Format s)  PS $($PSVersionTable.PSVersion)  OS $([Environment]::OSVersion.Version)  steam.exe running: $([bool](Get-Process steam -ErrorAction SilentlyContinue))")
  $o.Add("Bin folder: $binDir"); $o.Add('')
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
    if ($script:DumpAll -or $p.ProcessName -match '^(TS3W|NvRemixBridge)$') {
      $dp = Join-Path $out "$tag.dmp"; $r = 'error'
      try { $r = [HangNative]::Dump($p.Handle, [uint32]$id, $dp) } catch { $r = $_.Exception.Message }
      $o.Add("    minidump: $r  $(if(Test-Path $dp){'{0:N1} MB' -f ((Get-Item $dp).Length/1MB)})")
    }
    $o.Add('')
  }
  if ($binDir -and (Test-Path -LiteralPath $binDir)) {
    $ea = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Electronic Arts'
    $o.Add('=== Bin listing')
    Get-ChildItem -LiteralPath $binDir -Force | Where-Object { $_.PSIsContainer -or $_.Name -match '\.(exe|dll|asi|log|conf|toml|ini)$' } | ForEach-Object {
      $o.Add(("{0,-40} {1,12} {2:yyyy-MM-dd HH:mm:ss}  {3}" -f $(if($_.PSIsContainer){$_.Name+'\'}else{$_.Name}), $(if($_.PSIsContainer){'<dir>'}else{$_.Length}), $_.LastWriteTime, $(if(-not $_.PSIsContainer){$_.VersionInfo.FileVersion}))) }
    foreach ($d in '.trex', 'rtx-remix') { $dd = Join-Path $binDir $d; if (Test-Path $dd) { $o.Add("--- $d\"); Get-ChildItem -LiteralPath $dd -Recurse -Force -ErrorAction SilentlyContinue | Select-Object -First 80 | ForEach-Object { $o.Add(("  {0}  {1:N0}  {2:yyyy-MM-dd HH:mm:ss}" -f $_.FullName.Substring($binDir.Length), $_.Length, $_.LastWriteTime)) } } }
    $o.Add(''); $o.Add('=== Logs (stale LastWriteTime = that mod never ran in the hung start)')
    $ld = Join-Path $out 'logs'; New-Item -ItemType Directory -Force $ld | Out-Null
    $o.AddRange([string[]]@(Copy-Capped (@(Get-ChildItem (Join-Path $binDir 'rtx-remix\logs\*.log') -ErrorAction SilentlyContinue) + @(Get-ChildItem (Join-Path $binDir 'ts3native*.log') -ErrorAction SilentlyContinue)) $ld 'bin'))
    $o.AddRange([string[]]@(Copy-Capped (@(Get-ChildItem "$ea\*\S3SS\S3SS_LOG.txt" -ErrorAction SilentlyContinue) + @(Get-ChildItem "$ea\*\S3SS_LOG.txt" -ErrorAction SilentlyContinue)) $ld 'S3SS'))
    $o.AddRange([string[]]@(Copy-Capped @(Get-ChildItem "$ea\*\Apex Radiance\ApexRadiance_LOG.txt" -ErrorAction SilentlyContinue) $ld 'Apex'))
    $o.Add('')
  }
  $o.Add('=== Application event log, last 2 h (TS3W / NvRemixBridge)')
  try { Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = (Get-Date).AddHours(-2) } -ErrorAction Stop | Where-Object { $_.Message -match 'TS3W|NvRemixBridge' } |
      ForEach-Object { $o.Add(("[{0:s}] {1} id {2}: {3}" -f $_.TimeCreated, $_.ProviderName, $_.Id, (($_.Message -split "`n" | Select-Object -First 4) -join ' | '))) } } catch { $o.Add("none or unreadable: $($_.Exception.Message)") }
  $o | Out-File (Join-Path $out 'report.txt') -Encoding utf8
  $zip = "$out.zip"; if (Test-Path $zip) { Remove-Item $zip }
  Compress-Archive -Path "$out\*" -DestinationPath $zip
  return $zip
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
if ($SelfCheck) {
  $script:DumpAll = $true   # the child is powershell.exe, not TS3W: dump it anyway
  $child = Start-Process powershell -ArgumentList '-NoProfile', '-Command', 'Start-Sleep 120' -WindowStyle Hidden -PassThru
  Start-Sleep 2
  $out = Join-Path $env:TEMP "ts3-hang-diag-selfcheck-$stamp"
  try { $zip = Collect @($child) $out $null } finally { Stop-Process -Id $child.Id -Force -ErrorAction SilentlyContinue }
  $mod = @(Get-Content (Join-Path $out "modules-powershell-$($child.Id).txt")); $dmp = Get-Item (Join-Path $out "powershell-$($child.Id).dmp") -ErrorAction SilentlyContinue
  $fail = @()
  if (-not ($mod -match 'ntdll')) { $fail += 'module list has no ntdll' }
  if (-not $dmp -or $dmp.Length -lt 10KB) { $fail += 'no dump' }
  if (-not (Test-Path $zip)) { $fail += 'no zip' }
  Say "SelfCheck: modules=$($mod.Count) dump=$(if($dmp){$dmp.Length}else{'none'}) bytes zip=$zip"
  Say ((Get-Content (Join-Path $out 'report.txt') | Select-Object -First 6) -join "`n")
  $rc = 0; if ($fail) { Say ('SelfCheck FAIL: ' + ($fail -join '; ')); $rc = 1 } else { Say 'SelfCheck PASS' }
  Remove-Item $out, $zip -Recurse -Force -ErrorAction SilentlyContinue; exit $rc
}

$binDir = Find-Bin
if ($Launch) {
  if (-not $binDir -or -not (Test-Path (Join-Path $binDir 'TS3W.exe'))) { Say 'Cannot find Game\Bin\TS3W.exe. Pass -Bin "<...>\The Sims 3\Game\Bin".'; exit 2 }
  Say "Starting $binDir\TS3W.exe directly, waiting up to 90 s for a window ..."
  $g = Start-Process (Join-Path $binDir 'TS3W.exe') -WorkingDirectory $binDir -PassThru
  $t0 = Get-Date
  while (((Get-Date) - $t0).TotalSeconds -lt 90) { Start-Sleep 2; $g.Refresh(); if ($g.HasExited) { Say "TS3W.exe exited early (code $($g.ExitCode))."; break }; if ($g.MainWindowHandle -ne [IntPtr]::Zero) { Say 'Window appeared: no hang. Nothing collected.'; exit 0 } }
}
$targets = Get-Targets
if (-not $binDir) { $ts = $targets | Where-Object { $_.ProcessName -eq 'TS3W' } | Select-Object -First 1; if ($ts) { try { $binDir = Split-Path $ts.Path } catch {} } }
$zip = Collect $targets (Join-Path ([Environment]::GetFolderPath('Desktop')) "ts3-hang-diag-$stamp") $binDir
if ($KillAfter) { $targets | ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }; Say 'Collected processes stopped (-KillAfter).' }
Say ''; Say "Done. Send this file: $zip"
Say 'The dump and logs may contain your Windows user name and folder paths; check before sharing.'
