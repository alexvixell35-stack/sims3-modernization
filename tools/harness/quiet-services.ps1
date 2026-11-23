<#
Optional, per PC: keep noisy vendor services off during test runs, on only when a flag file asks for them
(vendor control centers, updaters). Windows-only by nature (Windows services, scheduled tasks), so PowerShell.
The harness never installs this; see docs/TESTING.md "Quiet services".

pin.ts moves user apps off the game CPUs, but services running as SYSTEM are access denied there. This stops them.
First use (2026-11-23, Alienware m15 R6): AWCC.UCSubAgent was the busiest other process in ~100 noisy queue jobs
(avg 1.7% of the machine, peaks 8-17%, one "noisy machine early" stop); it runs under the DellTechHub service.

Config (which services; differs per PC): probe-out/quiet-services.json if present, else quiet-services.example.json
next to this script. Fields: services (service names), processes (regex of leftovers killed after the stop),
apps (process names closed only by a manual `quiet -Apps`). install copies it, so edit, then install again.

Flag probe-out/quiet-services.flag (gitignored, no admin needed):
  first word "updates"      -> services on, except while TS3W.exe or a queue.ts runs
  anything else / no file   -> services off
  Set-Content probe-out\quiet-services.flag updates    /    Remove-Item probe-out\quiet-services.flag

Actions:
  status    : services, processes, flag, task, last log lines (no admin needed)
  install   : one UAC prompt; task ts3-quiet-services (SYSTEM, starts at boot) runs watch from an admin-only copy
  uninstall : removes the task and the installed copy, leaves the services as they are
  watch     : sync every 60 s forever (what the task runs; one idle process, not a new pwsh per minute)
  sync      : apply the flag once
  quiet / restore : force off / on once, ignoring the flag (the next sync applies the flag again)
Elevated actions relaunch themselves elevated. Log: C:\ProgramData\ts3-quiet-services\sync.log.
#>
param(
  [ValidateSet('status', 'sync', 'watch', 'quiet', 'restore', 'install', 'uninstall')][string]$Action = 'status',
  [switch]$Apps,
  [string]$Flag = (Join-Path (Split-Path (Split-Path $PSScriptRoot)) 'probe-out\quiet-services.flag'),
  [string]$Config
)

$repo = Split-Path (Split-Path $PSScriptRoot)
$dir = Join-Path $env:ProgramData 'ts3-quiet-services'
$installed = Join-Path $dir 'quiet-services.ps1'
if (-not $Config) {
  # the installed copy reads the admin-only config next to it; the repo copy the local or example config
  $Config = @((Join-Path $dir 'config.json'), (Join-Path $repo 'probe-out\quiet-services.json'), (Join-Path $PSScriptRoot 'quiet-services.example.json')) |
    Where-Object { ($PSCommandPath -eq $installed -or $_ -notlike "$dir*") -and (Test-Path $_) } | Select-Object -First 1
}
$cfg = Get-Content $Config -Raw | ConvertFrom-Json
$names = @($cfg.services)
$leftovers = if ($cfg.processes) { $cfg.processes } else { '^$' }
$state = Join-Path $dir 'state.json'
$log = Join-Path $dir 'sync.log'
$task = 'ts3-quiet-services'

function Get-Saved {
  $s = @{}
  if (Test-Path $state) { (Get-Content $state -Raw | ConvertFrom-Json).PSObject.Properties | ForEach-Object { $s[$_.Name] = $_.Value } }
  $s
}
function Write-Log($msg) { Add-Content $log "$(Get-Date -Format s) $msg" }

function Invoke-Quiet {
  $saved = Get-Saved
  $svc = Get-Service -Name $names -ErrorAction SilentlyContinue
  $procs = Get-Process | Where-Object Name -Match $leftovers
  if (-not ($svc | Where-Object { $_.Status -ne 'Stopped' -or $_.StartType -ne 'Manual' }) -and -not $procs) { return }
  foreach ($s in $svc) {
    # keep the first saved start type, so quiet twice does not record Manual as the original
    if (-not $saved.ContainsKey($s.Name)) { $saved[$s.Name] = "$($s.StartType)" }
    Set-Service -Name $s.Name -StartupType Manual
    Stop-Service -Name $s.Name -Force -ErrorAction Continue
  }
  $saved | ConvertTo-Json | Set-Content $state
  # child processes that outlive their host service
  Get-Process | Where-Object Name -Match $leftovers | Stop-Process -Force -ErrorAction SilentlyContinue
  Write-Log "quiet: $($names -join ', ')"
}

function Invoke-Restore {
  $saved = Get-Saved
  $svc = Get-Service -Name $names -ErrorAction SilentlyContinue
  # shortcut: no saved start type (never quieted, state lost) means Automatic; true for every service in the example
  $want = { param($n) if ($saved.ContainsKey($n)) { $saved[$n] } else { 'Automatic' } }
  if (-not ($svc | Where-Object { $_.StartType -ne (& $want $_.Name) -or ($_.Status -ne 'Running' -and (& $want $_.Name) -ne 'Disabled') })) { return }
  foreach ($s in $svc) {
    $w = & $want $s.Name
    Set-Service -Name $s.Name -StartupType $w
    if ($w -ne 'Disabled') { Start-Service -Name $s.Name -ErrorAction Continue }
  }
  Remove-Item $state -ErrorAction SilentlyContinue
  Write-Log "restore: $($names -join ', ')"
}

function Invoke-Sync {
  $on = (Test-Path $Flag) -and ((Get-Content $Flag -Raw) -match '^\s*updates\b')
  # never start vendor services under a running game or queue (the game closes between queue jobs)
  $busy = (Get-Process TS3W -ErrorAction SilentlyContinue) -or (Get-CimInstance Win32_Process -Filter "Name='node.exe'" | Where-Object CommandLine -Match 'queue\.ts')
  if ($on -and -not $busy) { Invoke-Restore } elseif (-not $on) { Invoke-Quiet }
}

if ($Action -eq 'status') {
  "config $Config"
  Get-Service -Name $names -ErrorAction SilentlyContinue | Format-Table Name, Status, StartType -AutoSize
  Get-Process | Where-Object Name -Match $leftovers | Sort-Object CPU -Descending | Format-Table Name, Id, CPU, @{ n = 'WS_MB'; e = { [int]($_.WS / 1MB) } } -AutoSize
  "flag $Flag : $(if (Test-Path $Flag) { (Get-Content $Flag -Raw).Trim() } else { '(none) -> off' })"
  "task $task : $(if (Get-Process pwsh -ErrorAction SilentlyContinue | Where-Object SessionId -EQ 0) { 'a SYSTEM pwsh is running' } else { 'no SYSTEM pwsh running (not installed, or stopped)' })"
  if (Test-Path $log) { Get-Content $log -Tail 5 }
  return
}

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
  $a = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"", $Action, '-Flag', "`"$Flag`"", '-Config', "`"$Config`"") + @(if ($Apps) { '-Apps' })
  Start-Process pwsh -Verb RunAs -ArgumentList $a -Wait
  & $PSCommandPath status -Flag $Flag -Config $Config
  return
}
New-Item -ItemType Directory -Force $dir | Out-Null

switch ($Action) {
  'sync' { Invoke-Sync }
  'watch' { while ($true) { try { Invoke-Sync } catch { Write-Log "sync error: $_" }; Start-Sleep -Seconds 60 } }
  'quiet' {
    Invoke-Quiet
    if ($Apps -and $cfg.apps) { Get-Process -Name @($cfg.apps) -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue }
  }
  'restore' { Invoke-Restore }
  'install' {
    # the task runs as SYSTEM: it runs a copy of the script and config only admins can change, never the repo files
    # the user account can edit (that would hand admin to anything running as the user). The flag is one word of data.
    Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
    Copy-Item $PSCommandPath $installed -Force
    Copy-Item $Config (Join-Path $dir 'config.json') -Force
    icacls $dir /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' | Out-Null
    # earlier name of this tool (tools/vendor-services.ps1): its task goes, its saved start types carry over
    Stop-ScheduledTask -TaskName 'ts3-vendor-sync' -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName 'ts3-vendor-sync', 'ts3-vendor-quiet', 'ts3-vendor-restore' -Confirm:$false -ErrorAction SilentlyContinue
    $old = Join-Path $env:ProgramData 'ts3-vendor'
    if ((Test-Path "$old\state.json") -and -not (Test-Path $state)) { Copy-Item "$old\state.json" $state }
    Remove-Item $old -Recurse -Force -ErrorAction SilentlyContinue
    # SYSTEM cannot open the Microsoft Store pwsh (WindowsApps alias): task result 0x80070780
    $pwsh = (Get-Command pwsh -All).Source | Where-Object { $_ -notmatch '\\WindowsApps\\' } | Select-Object -First 1
    if (-not $pwsh) { throw 'install needs a non-Store pwsh (e.g. winget install Microsoft.PowerShell)' }
    $arg = "-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$installed`" watch -Flag `"$Flag`""
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew `
      -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
    Register-ScheduledTask -TaskName $task -Action (New-ScheduledTaskAction -Execute $pwsh -Argument $arg) `
      -Trigger (New-ScheduledTaskTrigger -AtStartup) -Principal (New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest) `
      -Settings $settings -Force | Out-Null
    Start-ScheduledTask -TaskName $task
    Write-Host "installed $task (SYSTEM, starts at boot, checks $Flag every minute), config $Config"
  }
  'uninstall' {
    Stop-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $task -Confirm:$false -ErrorAction SilentlyContinue
    Remove-Item $installed, (Join-Path $dir 'config.json') -ErrorAction SilentlyContinue
  }
}
