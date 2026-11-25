# TS3 test kit: benchmark mode (docs/315). Opt-in, unattended, repeatable scripted run on the tester's own PC for A/B
# measurement. Windows PowerShell 5.1 or PowerShell 7, no node / python. One entry point: this script (Benchmark.cmd starts it).
#   Benchmark.cmd                                        asks for the save, shows the plan, asks YES, runs
#   TS3Bench.ps1 -Save <name> [-Game <dir>] [-User <dir>] [-ArmsFile <json>] [-Rounds 1] [-Yes]
# Results (docs/317 §3): Game\Bin\adaptdiag-bench.txt (newest summary) + Game\Bin\adaptdiag-bench\bench-<stamp>-*.{txt,jsonl};
# the kit's Make report step zips them. [Session] Benchmark/BenchmarkName/BenchmarkRun/Arm of ts3native.adaptdiag-state.ini
# are set through the kit's own SetAdSession (TS3Kit.ps1, loaded by AST) around every arm and reset to 0 after.
#   TS3Bench.ps1 -Save <name> -DryRun                    plan and checks only, nothing changed
#   TS3Bench.ps1 -Cleanup [-User <dir>]                  removes a leftover save copy / agent package (uninstall hook)
#   TS3Bench.ps1 -SelfCheck [-FakeTarget <exe>]          offline checks; with a fake game exe also the mailbox round trip
# Safety: the tester's save is never loaded: a fresh copy "TS3Kit-Bench" is made per arm and saved only under that name; the
# original folder is hashed before and after (mismatch = FAIL). The ts3agent package is copied in only for the run and removed
# after, and its channel is locked to a random per-run token. Only the game PID this script started is ever stopped. The
# game window is never focused. Arms come from the kit A/B lane through -ArmsFile (see docs/315 "Hook points"); without it
# two no-change arms A and B run (noise check: A must equal B).
param(
  [string]$Save = '', [string]$Game = '', [string]$User = '', [string]$KitScript = '', [string]$ArmsFile = '',
  [string]$AgentPackage = '', [int]$Rounds = 1, [int]$LiveS = 180, [int]$BuyLoops = 5, [int]$MinFreeGB = 5, [long]$Seed = 1121,
  [switch]$Yes, [switch]$DryRun, [switch]$Cleanup, [switch]$SelfCheck, [string]$FakeTarget = ''
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$CopyName = 'TS3Kit-Bench'
$AgentDest = 'TS3KitBench-ts3agent.package'
$Rx = @{
  SaveName    = '^[^\\/:*?"<>|]{1,80}$'             # a Saves\<name>.sims3 folder name
  OwnName     = '^TS3Kit-Bench'                      # never benchmark our own copy
  ScriptError = '^ScriptError_.*\.xml$'
  ModScripts  = 'mod scripts'
}
# Frame counter = GameClock +0x4C, u32, one per main-loop iteration (docs/315 §3; docs/51 §570). Steam / retail 1.67 [verified];
# EA app 1.69 by the .data delta [inferred]: every sample is plausibility-checked, else fps is n/a.
$FrameAddrs = @{ 'TS3W.exe' = 0x11CDD94; 'TS3.exe' = 0x1227E14 }   # per exe; $script:FrameAddr = this run's (one name for both overwrote the table: run 2 fps n/a, run 3 threw)

function Log([string]$s) { $l = "$((Get-Date).ToString('HH:mm:ss')) $s"; Write-Host $l; if ($script:LogFile) { Add-Content -LiteralPath $script:LogFile -Value $l } }

# ---------------------------------------------------------------- pure helpers (self-checked)
function Get-ArmOrder([string[]]$names, [int]$rounds) {   # ABBA: forward then reverse, per round
  $o = @(); for ($r = 0; $r -lt $rounds; $r++) { $o += $names; $o += $names[($names.Count - 1)..0] }; return ,$o
}
function Get-Median([double[]]$v) {
  if (-not $v -or $v.Count -eq 0) { return $null }
  $s = @($v | Sort-Object); $n = $s.Count
  if ($n % 2) { return $s[[int](($n - 1) / 2)] } else { return ($s[$n / 2 - 1] + $s[$n / 2]) / 2 }
}
# fps samples: frames per wall second; null/negative = unreadable; > 2000 = not a frame counter
function Get-FpsStats($samples) {
  $ok = @($samples | Where-Object { $_ -ne $null -and $_ -ge 0 -and $_ -le 2000 } | ForEach-Object { [double]$_ })
  $bad = @($samples).Count - $ok.Count
  if ($ok.Count -lt 5 -or $bad -gt @($samples).Count / 4) { return [ordered]@{ median = $null; min = $null; samples = $ok.Count; note = 'n/a (frame counter not readable on this build)' } }
  return [ordered]@{ median = [math]::Round((Get-Median $ok), 1); min = [math]::Round(($ok | Measure-Object -Minimum).Minimum, 1); samples = $ok.Count; note = '' }
}
function Get-NoiseGrade([double]$otherCpuPct) {   # other processes' CPU while an arm ran (memory external-load-check: >= 10% = rerun)
  if ($otherCpuPct -lt 5) { 'quiet' } elseif ($otherCpuPct -lt 10) { 'ok' } else { 'noisy (rerun)' }
}
function Test-SaveName([string]$n) { ($n -match $Rx.SaveName) -and ($n -notmatch $Rx.OwnName) -and ($n -notmatch '\.\.') }
function Get-DirHash([string]$dir) {   # one sha256 over every file's relative path + content hash, sorted
  if (-not (Test-Path -LiteralPath $dir)) { return 'missing' }
  $root = (Resolve-Path -LiteralPath $dir).Path.TrimEnd('\')
  $rows = Get-ChildItem -LiteralPath $root -Recurse -File -Force | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($root.Length))|$((Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash)" }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  return ([System.BitConverter]::ToString($sha.ComputeHash([System.Text.Encoding]::UTF8.GetBytes(($rows -join "`n")))) -replace '-', '').ToLower()
}
function New-Token { $b = New-Object byte[] 24; [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($b); return ([System.BitConverter]::ToString($b) -replace '-', '').ToLower() }
function New-Request([long]$id, [string]$cmd, $cmdArgs, [string]$token) {
  $r = [ordered]@{ id = $id; cmd = $cmd }; if ($token) { $r.token = $token }; if ($cmdArgs) { $r.args = $cmdArgs }
  return ($r | ConvertTo-Json -Compress -Depth 6)
}
function Get-PhaseSum($phases, [string]$key, [string[]]$tags) { $s = 0; foreach ($p in $phases) { if ($tags -contains $p.tag -and $p.ticks -and $p.ticks.$key -ne $null) { $s += [double]$p.ticks.$key } }; return $s }
function Get-PhaseMax($phases, [string]$key, [string[]]$tags) { $m = 0; foreach ($p in $phases) { if ($tags -contains $p.tag -and $p.ticks -and $p.ticks.$key -ne $null -and [double]$p.ticks.$key -gt $m) { $m = [double]$p.ticks.$key } }; return $m }
function Format-ArmLine($a) {
  $f = if ($a.fps.median -ne $null) { "fps median $($a.fps.median) min $($a.fps.min)" } else { 'fps n/a' }
  "$($a.label): $($a.status); $f; hitches >=250ms $($a.hitch250) >=1s $($a.hitch1000); longest pause $($a.longestMs) ms; script errors $($a.scriptErrors); noise $($a.noise)"
}

# ---------------------------------------------------------------- kit session state (docs/317 §1) and antivirus
# The kit's SetAdSession (+ MergeToml, ParseToml, $Rx, $AdState) from TS3Kit.ps1, by AST, into a private module: one writer
# for ts3native.adaptdiag-state.ini, and TS3Kit.ps1's menu never runs. $null when the kit predates the adaptive diag kit.
function Import-KitSession([string]$kit) {
  if (-not $kit -or -not (Test-Path -LiteralPath $kit)) { return $null }
  $ast = [System.Management.Automation.Language.Parser]::ParseFile($kit, [ref]$null, [ref]$null)
  $fns = @($ast.FindAll({ param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and @('SetAdSession', 'MergeToml', 'ParseToml') -contains $n.Name }, $false))
  if (-not @($fns | Where-Object { $_.Name -eq 'SetAdSession' }).Count) { return $null }
  $vars = @($ast.EndBlock.Statements | Where-Object { $_ -is [System.Management.Automation.Language.AssignmentStatementAst] -and @('$Rx', '$AdState') -contains $_.Left.Extent.Text })
  $text = (@($vars) + @($fns) | ForEach-Object { $_.Extent.Text }) -join "`n"
  return (New-Module -Name 'TS3BenchKitSession' -ScriptBlock ([scriptblock]::Create($text + "`nExport-ModuleMember -Function SetAdSession")) -AsCustomObject)
}
function Set-Session([string]$key, [string]$value) {   # one [Session] key; ascii only, length caps of docs/317 §1
  if (-not $script:KitSession) { return }
  $v = ($value -replace '[^\x20-\x7E]', '_'); $cap = @{ BenchmarkName = 40; BenchmarkRun = 40; Arm = 16 }[$key]; if ($cap -and $v.Length -gt $cap) { $v = $v.Substring(0, $cap) }
  $script:KitSession.SetAdSession((Join-Path $Game 'Game\Bin'), $key, $v)
}
# Antivirus (docs/315 §8): the runner opens the game with VM_READ/VM_WRITE and calls Read/WriteProcessMemory, which behaviour
# blockers (Kaspersky on the dev PC) may flag. Recorded per benchmark: products, OpenProcess / scan result, AV log lines.
function Get-AvProducts { @(Try1 { Get-CimInstance -Namespace 'root/SecurityCenter2' -ClassName AntiVirusProduct -ErrorAction Stop | ForEach-Object { $_.displayName } } @()) }
$AvRelevant = '(?i)(TS3Bench|TS3W\.exe|TS3\.exe|powershell|pwsh)'   # an AV log line about this run
function Get-AvEvents([datetime]$since) {   # Kaspersky's own event log channel when it exists (no admin needed to read it on most installs)
  $logs = @(Try1 { Get-WinEvent -ListLog '*Kaspersky*' -ErrorAction Stop | Where-Object { $_.RecordCount } } @())
  $out = @(); foreach ($l in $logs) { $out += @(Try1 { Get-WinEvent -FilterHashtable @{ LogName = $l.LogName; StartTime = $since } -MaxEvents 200 -ErrorAction Stop | Where-Object { $_.Message -match $AvRelevant } | ForEach-Object { "$($_.TimeCreated.ToString('HH:mm:ss')) $($l.LogName) id $($_.Id): $(($_.Message -split "`n")[0])" } } @()) }
  return ,$out
}

# ---------------------------------------------------------------- game side
function Load-Native { if (-not ('Ts3Bench.Mbox' -as [type])) { Add-Type -TypeDefinition ([System.IO.File]::ReadAllText((Join-Path $here 'TS3Bench.cs'))) } }

$script:Seq = 0
function Agent([string]$cmd, $cmdArgs = $null, [int]$timeoutS = 30) {
  $mb = $script:Mb; $t0 = [Environment]::TickCount
  while ($mb.Pending()) { if ([Environment]::TickCount - $t0 -gt $timeoutS * 1000) { return @{ ok = $false; error = 'previous request still pending' } }; Start-Sleep -Milliseconds 100 }
  $seq = $mb.Send((New-Request (++$script:Seq) $cmd $cmdArgs $script:Token))
  while ([Environment]::TickCount - $t0 -lt $timeoutS * 1000) {
    $r = $mb.Reply($seq); if ($r) { return ($r | ConvertFrom-Json) }
    Sample; Start-Sleep -Milliseconds 100
  }
  return @{ ok = $false; error = "no reply to $cmd in $timeoutS s" }
}
function Must([string]$cmd, $cmdArgs = $null, [int]$timeoutS = 30) { $r = Agent $cmd $cmdArgs $timeoutS; if (-not $r.ok) { throw "$cmd failed: $($r.error)" }; return $r.result }

# per-second fps sample + fail-fast progress check every 30 s (2 misses in a row = abort)
function Sample {
  $now = [Environment]::TickCount
  if ($script:Proc.HasExited) { throw "the game closed (exit code $($script:Proc.ExitCode))" }
  if ($now - $script:LastSample -ge 1000 -and $script:FrameAddr) {
    $f = $script:Mb.U32At($script:FrameAddr)
    if ($script:LastFrames -ge 0 -and $f -ge 0) { [void]$script:Fps.Add([double](($f - $script:LastFrames) * 1000.0 / ($now - $script:LastSample))) } elseif ($script:LastFrames -ge 0) { [void]$script:Fps.Add($null) }
    if (-not [Ts3Bench.Win]::Foreground($script:Proc.Id)) { $script:Unfocused++ }
    $script:LastFrames = $f; $script:LastSample = $now
  }
  if ($now - $script:CheckAt -ge 30000) {
    $script:CheckAt = $now
    $script:Proc.Refresh()
    $hb = Try1 { $script:Mb.Heartbeat() } -1; $fr = if ($script:FrameAddr) { $script:Mb.U32At($script:FrameAddr) } else { -1 }; $cpu = $script:Proc.TotalProcessorTime.TotalSeconds
    $moved = ($hb -ne $script:Prog.hb) -or ($fr -ne $script:Prog.fr) -or ($cpu - $script:Prog.cpu -gt 0.5)
    $script:Prog = @{ hb = $hb; fr = $fr; cpu = $cpu }
    if ($moved) { $script:Misses = 0 } else { $script:Misses++; Log "  progress check: no progress ($($script:Misses) of 2)" }
    if ($script:Misses -ge 2) { throw 'game stalled: no heartbeat, frame or CPU progress in two 30 s checks' }
  }
}
function Try1([scriptblock]$b, $default = $null) { try { & $b } catch { $default } }
function Wait-Until([string]$what, [int]$maxS, [scriptblock]$cond, [int]$pollMs = 1000) {
  $t0 = [Environment]::TickCount
  while ([Environment]::TickCount - $t0 -lt $maxS * 1000) { Sample; if (& $cond) { return [math]::Round(([Environment]::TickCount - $t0) / 1000.0, 1) }; Start-Sleep -Milliseconds $pollMs }
  throw "timeout: $what (${maxS} s)"
}
function Dwell([int]$s) { $t0 = [Environment]::TickCount; while ([Environment]::TickCount - $t0 -lt $s * 1000) { Sample; Start-Sleep -Milliseconds 250 } }
function SubState { $g = Agent 'game_state' $null 20; if ($g.ok) { $g.result.sub_state } else { $null } }

# one measured phase: tick_stats window + fps samples of that phase only
function Phase([string]$tag, [scriptblock]$body) {
  $script:Fps = New-Object System.Collections.ArrayList; $script:Unfocused = 0
  [void](Agent 'tick_stats' @{ reset = $true })
  $t0 = [Environment]::TickCount
  & $body
  $ts = Agent 'tick_stats' @{ reset = $true }
  $p = [ordered]@{ tag = $tag; seconds = [math]::Round(([Environment]::TickCount - $t0) / 1000.0, 1); ticks = $(if ($ts.ok) { $ts.result } else { $null }); fps = @($script:Fps); unfocusedS = $script:Unfocused }
  [void]$script:Phases.Add($p)
  Log "  phase $tag $($p.seconds) s: max tick $(if ($p.ticks) { $p.ticks.max_ms }) ms, >=250ms $(if ($p.ticks) { $p.ticks.over_250 })"
}

function OtherCpuSeconds([int]$gamePid) {
  $s = 0.0; foreach ($p in Get-Process) { if ($p.Id -eq $gamePid -or $p.Id -eq $PID -or $p.Id -eq 0) { continue }; $t = Try1 { $p.TotalProcessorTime.TotalSeconds } 0; $s += $t }; return $s
}

function Run-Arm($arm, [int]$idx, [int]$total, [int]$minutesLeft) {
  $label = "$idx/$total $($arm.name)"
  $res = [ordered]@{ label = $label; arm = $arm.name; status = 'FAIL'; error = ''; phases = @(); fps = $null; hitch250 = 0; hitch1000 = 0; longestMs = 0; saveS = $null; scriptErrors = 0; noise = ''; otherCpuPct = $null; unfocusedS = 0 }
  $script:Phases = New-Object System.Collections.ArrayList
  $started = Get-Date; $w0 = Get-Date; $cpu0 = $null
  try {
    if ($arm.apply) { Log "  arm $($arm.name): $(if ($arm.apply -is [scriptblock]) { 'kit A/B arm' } else { $arm.apply })"; & $arm.apply -Arm $arm.name -Game $Game -User $User }
    # fresh copy per arm: every arm starts from the same bytes
    foreach ($d in @($script:CopyDir, "$($script:CopyDir).backup")) { if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force } }
    Copy-Item -LiteralPath $script:OrigDir -Destination $script:CopyDir -Recurse
    # docs/317 §1: label the session before the start, so ts3native runs every arm with identical diagnostics
    Set-Session 'Benchmark' '1'; Set-Session 'BenchmarkName' 'ts3bench'; Set-Session 'BenchmarkRun' "$($script:Stamp)-$idx"; Set-Session 'Arm' $arm.name
    $exe =Join-Path $Game "Game\Bin\$($script:ExeName)"
    $script:Proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru
    $script:State.pid = $script:Proc.Id; $script:State.pidStart = $script:Proc.StartTime.ToString('o'); Save-State
    Log "  started $($script:ExeName) pid $($script:Proc.Id)"
    $cpu0 = OtherCpuSeconds $script:Proc.Id; $w0 = Get-Date
    $script:Mb = New-Object Ts3Bench.Mbox($script:Proc.Id)
    $script:FrameAddr = $FrameAddrs[$script:ExeName]; $script:LastFrames = -1; $script:LastSample = [Environment]::TickCount
    $script:CheckAt = [Environment]::TickCount; $script:Misses = 0; $script:Prog = @{ hb = -2; fr = -2; cpu = -1 }; $script:Fps = New-Object System.Collections.ArrayList
    # agent mailbox (scan every 5 s; the native mod-version warning is answered on the way)
    $script:Token = ''
    try { [void](Wait-Until 'agent mailbox' 300 { $d = [Ts3Bench.Win]::AnswerModVersionDialog($script:Proc.Id); if ($d) { Log "  start-up warning: Continue ($d)" }; $script:Mb.Scan() } 5000) }
    catch { throw "$_ (no mailbox: the agent package did not load, or an antivirus blocks reading game memory; see docs/315 §8)" }
    Log "  mailbox found ($($script:Mb.LastScanMs) ms scan)"
    $tok = New-Token
    $lk = Agent 'session_lock' @{ token = $tok } 60
    if (-not $lk.ok -or -not $lk.result.locked) { throw "session_lock: $($lk.error)" }
    $script:Token = $tok
    # main menu: clear "Mod Scripts Found", then wait for 5 s of quiet (a load sent before the menu is lost, docs cycle.ts)
    $seen = $false; $quiet = [Environment]::TickCount; $t0 = [Environment]::TickCount
    while ([Environment]::TickCount - $t0 -lt 300000 -and -not ($seen -and [Environment]::TickCount - $quiet -ge 5000)) {
      Sample; $d = Agent 'auto_dialog' $null 20
      if ($d.ok -and $d.result.open) { Log "  menu dialog '$($d.result.title)' -> $($d.result.clicked)"; if ("$($d.result.title)" -match $Rx.ModScripts) { $seen = $true }; $quiet = [Environment]::TickCount }
      Start-Sleep -Seconds 2
    }
    [void](Must 'autopilot' @{ enabled = $true; speed = 'none'; dialogs = $true })
    [void](Must 'load_save' @{ file = "$CopyName.sims3" } 120)
    [void](Agent 'dialog_expect' @{ seconds = 60 })
    $loadS = Wait-Until 'world loaded' 900 { $script:Mb.WorldLoaded() } 1000
    [void](Agent 'dialog_expect' @{ seconds = 0 })
    Log "  world loaded in $loadS s"
    Dwell 10
    [void](Agent 'quiet_ui' @{ memories_off = $true }); [void](Agent 'decline_requests')
    [void](Wait-Until 'world state' 60 { "$(SubState)" -match 'State$' })
    if ((SubState) -ne 'LiveModeState') { [void](Agent 'enter_live_mode'); [void](Wait-Until 'live mode' 120 { (SubState) -eq 'LiveModeState' }) }
    [void](Agent 'seed_rng' @{ seed = $Seed })
    [void](Must 'notify' @{ text = "TS3 test kit: automated benchmark running, please don't touch (about $minutesLeft min). Run $idx of $total." })
    [void](Must 'tick_stats' @{ start = $true })
    $res.loadS = $loadS
    Phase 'live' { [void](Must 'autopilot' @{ speed = 'ultra' }); Dwell $LiveS; [void](Must 'autopilot' @{ speed = 'none' }) }
    Phase 'buy' {
      for ($i = 0; $i -lt $BuyLoops; $i++) {
        [void](Must 'buy_open')
        [void](Wait-Until 'buy catalog' 120 { $c = Agent 'cas_state' $null 20; $c.ok -and $c.result.buy_catalog })
        Dwell 5
        [void](Must 'buy_close')
        [void](Wait-Until 'back to live' 120 { (SubState) -eq 'LiveModeState' })
        Dwell 3
      }
    }
    Phase 'cas' {
      [void](Must 'cas_open')
      [void](Wait-Until 'CAS ready' 180 { $c = Agent 'cas_state' $null 20; $c.ok -and $c.result.cas_ready -eq $true })
      Dwell 10
      [void](Must 'cas_close')
      [void](Wait-Until 'back to live' 180 { (SubState) -eq 'LiveModeState' })
    }
    Phase 'lots' {   # camera to 5 far lots, 8 s each: lot streaming, no Sim is changed
      [void](Must 'camera_jumps' @{ order = 'far'; lots = 5; dwellS = 8; loops = 1 })
      [void](Wait-Until 'camera jumps' 150 { $c = Agent 'camera_jumps' @{ status = $true } 20; $c.ok -and $c.result.jumps -ge 5 })
      [void](Agent 'camera_jumps' @{ stop = $true })
    }
    Phase 'save' {
      [void](Must 'autopilot' @{ dialogs = $true; speed = 'none' })
      [void](Must 'save_game' @{ name = $CopyName } 60)   # name forces a save under the copy's name, never the original's
      $script:SaveS = Wait-Until 'save done' 600 { $g = Agent 'game_state' $null 30; $g.ok -and -not $g.result.save_in_progress -and -not $g.result.modal } 2000
    }
    $res.saveS = $script:SaveS
    [void](Agent 'notify' @{ text = "TS3 test kit: benchmark run $idx of $total done, closing the game." })
    Dwell 3
    $mm = Agent 'main_menu' $null 30
    if ($mm.ok) { Try1 { [void](Wait-Until 'main menu' 300 { -not $script:Mb.WorldLoaded() }) } }
    if (-not $script:Proc.HasExited) { [void](Try1 { Agent 'quit_game' $null 30 }) }
    $script:Proc.WaitForExit(120000) | Out-Null
    $res.status = 'ok'
  } catch {
    $res.error = "$_"; Log "  FAIL: $_"
  } finally {
    if ($script:Mb) { $script:Mb.Dispose(); $script:Mb = $null }
    if ($script:Proc -and -not $script:Proc.HasExited) { Log "  stopping own game pid $($script:Proc.Id)"; Stop-Process -Id $script:Proc.Id -Force; $script:Proc.WaitForExit(60000) | Out-Null }
    if ($arm.restore) { Try1 { & $arm.restore -Arm $arm.name -Game $Game -User $User } }
    Try1 { Set-Session 'Benchmark' '0'; Set-Session 'Arm' '' }
    $res.av = [ordered]@{ events = (Get-AvEvents $started) }
  }
  $wall = ((Get-Date) - $w0).TotalSeconds
  if ($cpu0 -ne $null -and $wall -gt 0) { $res.otherCpuPct = [math]::Round(((OtherCpuSeconds -1) - $cpu0) * 100.0 / ($wall * [Environment]::ProcessorCount), 1); $res.noise = Get-NoiseGrade $res.otherCpuPct }
  $measured = @('live', 'buy', 'cas', 'lots')
  $res.phases = @($script:Phases)
  $res.fps = Get-FpsStats @($script:Phases | Where-Object { $measured -contains $_.tag } | ForEach-Object { $_.fps })
  $res.hitch250 = Get-PhaseSum $script:Phases 'over_250' $measured
  $res.hitch1000 = Get-PhaseSum $script:Phases 'over_1000' $measured
  $res.longestMs = Get-PhaseMax $script:Phases 'max_ms' $measured
  $res.unfocusedS = 0; foreach ($p in $script:Phases) { $res.unfocusedS += $p.unfocusedS }   # ordered dicts: no Measure-Object -Property in 5.1
  $res.scriptErrors = @(Get-ChildItem -LiteralPath $User -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $Rx.ScriptError -and $_.LastWriteTime -ge $started }).Count
  return $res
}

# ---------------------------------------------------------------- state, install, restore
function Save-State { $script:State | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $script:StateFile -Encoding UTF8 }
function Do-Cleanup([string]$user) {
  $sf = Join-Path $user 'TS3Kit-Bench\bench-state.json'
  $done = @()
  foreach ($p in @((Join-Path $user "Mods\Packages\$AgentDest"), (Join-Path $user "Saves\$CopyName.sims3"), (Join-Path $user "Saves\$CopyName.sims3.backup"))) {
    if (Test-Path -LiteralPath $p) { Remove-Item -LiteralPath $p -Recurse -Force; $done += $p }
  }
  if (Test-Path -LiteralPath $sf) { Remove-Item -LiteralPath $sf -Force }
  if ($script:KitSession -and $Game -and (Test-Path -LiteralPath (Join-Path $Game 'Game\Bin\ts3native.adaptdiag-state.ini'))) { Try1 { Set-Session 'Benchmark' '0'; Set-Session 'Arm' '' } }   # a crashed run never labels later play
  return ,$done
}

function Resolve-Paths {
  if (-not $script:User) { $script:User = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Electronic Arts\The Sims 3' }
  if (-not $script:Game) {
    foreach ($k in 'HKLM:\SOFTWARE\WOW6432Node\Sims\The Sims 3', 'HKLM:\SOFTWARE\Sims\The Sims 3') { $v = Try1 { (Get-ItemProperty -LiteralPath $k -ErrorAction Stop).'Install Dir' }; if ($v) { $script:Game = $v.TrimEnd('\'); break } }
    $steam = Join-Path ${env:ProgramFiles(x86)} 'Steam\steamapps\common\The Sims 3'   # stale registry: Steam default library
    if ((-not $script:Game -or -not (Test-Path -LiteralPath (Join-Path $script:Game 'Game\Bin'))) -and (Test-Path -LiteralPath (Join-Path $steam 'Game\Bin'))) { $script:Game = $steam }
  }
  if (-not $script:KitScript) { $script:KitScript = Join-Path $here 'TS3Kit.ps1' }
  $script:KitSession = Import-KitSession $script:KitScript
  if (-not $script:AgentPackage) { $script:AgentPackage = Join-Path (Split-Path -Parent $here) 'payload\bench\ts3agent.package' }
}

function Invoke-Bench {
  Resolve-Paths
  if (-not $Game -or -not (Test-Path -LiteralPath (Join-Path $Game 'Game\Bin'))) { throw "game folder not found ($Game); pass -Game" }
  $script:ExeName = if (Test-Path -LiteralPath (Join-Path $Game 'Game\Bin\TS3W.exe')) { 'TS3W.exe' } else { 'TS3.exe' }
  $saves = Join-Path $User 'Saves'
  if (-not $Save) {
    $list = @(Get-ChildItem -LiteralPath $saves -Directory -Filter '*.sims3' | Where-Object { $_.Name -notmatch $Rx.OwnName } | Sort-Object LastWriteTime -Descending)
    if (-not $list.Count) { throw "no saves in $saves" }
    for ($i = 0; $i -lt [math]::Min(15, $list.Count); $i++) { Write-Host "  $($i + 1). $($list[$i].BaseName)" }
    $n = [int](Read-Host 'Benchmark which save (number)? It is copied; the original is never loaded or changed')
    $script:Save = $list[$n - 1].BaseName
  }
  if (-not (Test-SaveName $Save)) { throw "save name '$Save' refused (bad characters or our own copy)" }
  $script:OrigDir = Join-Path $saves "$Save.sims3"; $script:CopyDir = Join-Path $saves "$CopyName.sims3"
  if (-not (Test-Path -LiteralPath $OrigDir)) { throw "no save folder $OrigDir" }
  if (-not (Test-Path -LiteralPath $AgentPackage)) { throw "agent package missing: $AgentPackage" }
  $saveMB = [math]::Ceiling((Get-ChildItem -LiteralPath $OrigDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB)
  $freeGB = [math]::Round((Get-PSDrive -Name ($User.Substring(0, 1))).Free / 1GB, 1)
  $needGB = [math]::Max($MinFreeGB, [math]::Ceiling($saveMB * 3 / 1024) + 2)
  $abFile = Join-Path (Split-Path -Parent $here) 'payload\ab\arms.json'   # the kit's A/B arms (docs/314): TS3Kit.ps1 -Mode ArmApply sets each
  $kit = $script:KitScript
  $abApply = { param($Arm, $Game, $User) $global:LASTEXITCODE = 0; & $kit -Mode ArmApply -Arm $Arm -GameDir $Game -UserDir $User -Yes; if ($LASTEXITCODE) { throw "arm $Arm could not be set (TS3Kit.ps1 -Mode ArmApply exit $LASTEXITCODE)" } }.GetNewClosure()
  $arms = if ($ArmsFile) { @((Get-Content -LiteralPath $ArmsFile -Raw | ConvertFrom-Json).arms) }
    elseif (Test-Path -LiteralPath $abFile) { @('A', 'B' | ForEach-Object { [pscustomobject]@{ name = $_; apply = $abApply } }) }
    else { @([pscustomobject]@{ name = 'A' }, [pscustomobject]@{ name = 'B' }) }
  $stub = -not $ArmsFile -and -not (Test-Path -LiteralPath $abFile)
  $order = Get-ArmOrder @($arms | ForEach-Object { $_.name }) $Rounds
  $perArm = [math]::Ceiling(5 + ($LiveS + $BuyLoops * 25 + 60 + 50 + 60) / 60.0)
  $mins = $perArm * $order.Count
  $running = @(Get-Process -Name 'TS3W', 'TS3' -ErrorAction SilentlyContinue)
  $other = @(Get-ChildItem -LiteralPath (Join-Path $User 'Mods') -Recurse -File -Filter 'ts3agent*.package' -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne $AgentDest })
  Write-Host "Benchmark plan: save '$Save' ($saveMB MB) copied to '$CopyName' per run; $($order.Count) runs, order $($order -join ' '); about $mins min."
  Write-Host "  game $Game ($script:ExeName), user folder $User, free $freeGB GB (need $needGB GB)$(if ($stub) { '; arms: no-change stub A/B (noise check)' } elseif (-not $ArmsFile) { '; arms: the kit''s A/B arms (payload\ab\arms.json)' })"
  $refuse = @()
  if ($freeGB -lt $needGB) { $refuse += "free disk $freeGB GB < $needGB GB" }
  if ($running.Count) { $refuse += 'The Sims 3 is running; close it first' }
  if ($other.Count) { $refuse += "another ts3agent package is installed: $($other[0].FullName)" }
  $probe = Join-Path $Game "Game\Bin\ts3bench-write-$PID.tmp"   # results go to Game\Bin (docs/317 §3)
  if (-not (Try1 { [System.IO.File]::WriteAllText($probe, 'x'); Remove-Item -LiteralPath $probe -Force; $true } $false)) { $refuse += 'Game\Bin is not writable (run Benchmark.cmd as administrator)' }
  if ($refuse.Count) { throw "refused: $($refuse -join '; ')" }
  if ($DryRun) { Write-Host 'Dry run: nothing changed.'; return }
  if (-not $Yes) { if ((Read-Host "The game will start and run by itself for about $mins min. Don't touch the PC meanwhile. Type YES to start") -ne 'YES') { Write-Host 'Cancelled, nothing changed.'; return } }

  Load-Native
  $stamp = (Get-Date).ToString('yyyyMMdd-HHmmss'); $script:Stamp = $stamp
  $bin = Join-Path $Game 'Game\Bin'
  $run = Join-Path $bin 'adaptdiag-bench'; [void](New-Item -ItemType Directory -Force -Path $run)   # docs/317 §3: zipped by Make report
  $script:LogFile = Join-Path $run "bench-$stamp-log.txt"
  $av0 = Get-AvProducts
  Log "antivirus: $(if ($av0.Count) { $av0 -join ', ' } else { 'none reported' }); kit session state: $(if ($script:KitSession) { 'SetAdSession from TS3Kit.ps1' } else { 'not available (kit without adaptive diag)' })"
  $sd = Join-Path $User 'TS3Kit-Bench'; [void](New-Item -ItemType Directory -Force -Path $sd)
  $script:StateFile = Join-Path $sd 'bench-state.json'
  if (Test-Path -LiteralPath $StateFile) { Log "leftover from an earlier run removed: $((Do-Cleanup $User) -join ', ')"; [void](New-Item -ItemType Directory -Force -Path $sd) }
  $h0 = Get-DirHash $OrigDir; $hb0 = Get-DirHash "$OrigDir.backup"
  $script:State = [ordered]@{ started = (Get-Date).ToString('o'); save = $Save; copy = $CopyDir; agent = (Join-Path $User "Mods\Packages\$AgentDest"); pid = 0; pidStart = '' }
  Save-State
  $results = @()
  try {
    Copy-Item -LiteralPath $AgentPackage -Destination $State.agent
    Log "agent package in for the run: $($State.agent)"
    $i = 0
    foreach ($name in $order) {
      $i++
      $arm = $arms | Where-Object { $_.name -eq $name } | Select-Object -First 1
      Log "run $i of $($order.Count): arm $name"
      $r = Run-Arm $arm $i $order.Count ($perArm * ($order.Count - $i + 1))
      $results += $r
      Log "  $(Format-ArmLine $r)"
      if ($r.status -ne 'ok') { Log 'aborting the benchmark (fail-fast)'; break }
    }
  } finally {
    $gone = Do-Cleanup $User
    Log "restored: removed $($gone -join ', ')"
  }
  $h1 = Get-DirHash $OrigDir; $hb1 = Get-DirHash "$OrigDir.backup"
  $safe = ($h0 -eq $h1) -and ($hb0 -eq $hb1)
  $report = [ordered]@{
    kind = 'ts3kit-benchmark'; version = 1; started = $State.started; save = $Save; saveMB = $saveMB; exe = $script:ExeName; seed = $Seed
    order = $order; originalSaveUntouched = $safe; originalHash = $h0; antivirus = $av0; runs = $results
    perArm = @($arms | ForEach-Object { $n = $_.name; $rs = @($results | Where-Object { $_.arm -eq $n -and $_.status -eq 'ok' })
      [ordered]@{ arm = $n; runs = $rs.Count; fpsMedian = (Get-Median @($rs | Where-Object { $_.fps.median -ne $null } | ForEach-Object { [double]$_.fps.median })); hitch250 = (Get-Median @($rs | ForEach-Object { [double]$_.hitch250 })); hitch1000 = (Get-Median @($rs | ForEach-Object { [double]$_.hitch1000 })); longestMs = (Get-Median @($rs | ForEach-Object { [double]$_.longestMs })) } })
  }
  $report | ConvertTo-Json -Depth 8 -Compress | Set-Content -LiteralPath (Join-Path $run "bench-$stamp-report.jsonl") -Encoding ASCII
  $avLines = @($results | ForEach-Object { $_.av.events } | Where-Object { $_ })
  $sum = @("TS3 kit benchmark $stamp, save '$Save', order $($order -join ' ')", "original save untouched: $(if ($safe) { 'yes (hash match)' } else { 'NO: HASH CHANGED' })",
    "antivirus: $(if ($av0.Count) { $av0 -join ', ' } else { 'none reported' }); AV log lines about this run: $($avLines.Count)") + @($avLines | ForEach-Object { "  av: $_" }) + @($results | ForEach-Object { Format-ArmLine $_ })
  $sum | Set-Content -LiteralPath (Join-Path $run "bench-$stamp-summary.txt") -Encoding ASCII
  $sum | Set-Content -LiteralPath (Join-Path $bin 'adaptdiag-bench.txt') -Encoding ASCII   # newest summary, docs/317 §3
  $sum | ForEach-Object { Write-Host $_ }
  Write-Host 'Make the report for us with TS3Kit.bat option 8 (Make report): it zips these results with the diag files.'
  if (-not $safe) { throw 'original save hash changed' }
}

# ---------------------------------------------------------------- self-check
function Invoke-SelfCheck {
  function Check([bool]$c, [string]$what) { if ($c) { Write-Host "  ok   $what" } else { Write-Host "  FAIL $what"; $script:scFails++ } }
  $script:scFails = 0
  Check ((Get-ArmOrder @('A', 'B') 1) -join '' -eq 'ABBA') 'ABBA order, 1 round'
  Check ((Get-ArmOrder @('A', 'B', 'C') 2) -join '' -eq 'ABCCBAABCCBA') 'ABCCBA, 2 rounds'
  Check ((Get-Median @(3, 1, 2)) -eq 2 -and (Get-Median @(4, 1, 2, 3)) -eq 2.5 -and $null -eq (Get-Median @())) 'median odd / even / empty'
  $fs = Get-FpsStats @(60, 58, 61, 30, 59, 60); Check ($fs.median -eq 59.5 -and $fs.min -eq 30 -and $fs.samples -eq 6) 'fps median / min'
  $fs = Get-FpsStats @(60, $null, 5000, $null, 60, $null); Check ($null -eq $fs.median -and $fs.note -match 'n/a') 'fps n/a when the counter is unreadable or implausible'
  Check ((Get-NoiseGrade 2) -eq 'quiet' -and (Get-NoiseGrade 7) -eq 'ok' -and (Get-NoiseGrade 10) -eq 'noisy (rerun)') 'noise grade thresholds'
  Check ((Test-SaveName 'My Family') -and (Test-SaveName 'Baseline-BP') -and -not (Test-SaveName 'TS3Kit-Bench') -and -not (Test-SaveName 'TS3Kit-Bench2') -and -not (Test-SaveName '..\x') -and -not (Test-SaveName 'a/b') -and -not (Test-SaveName '')) 'save name regex: real names pass, own copy / paths refused'
  Check (('ScriptError_7f3a_2026.xml' -match $Rx.ScriptError) -and -not ('xScriptError_1.xml' -match $Rx.ScriptError) -and -not ('ScriptError_1.xml.bak' -match $Rx.ScriptError)) 'script error file regex'
  Check (("Mod Scripts Found" -match $Rx.ModScripts) -and -not ("Save your town" -match $Rx.ModScripts)) 'mod scripts dialog regex'
  $rq = (New-Request 7 'ping' @{ echo = 'x' } 'tok') | ConvertFrom-Json; Check ($rq.id -eq 7 -and $rq.cmd -eq 'ping' -and $rq.token -eq 'tok' -and $rq.args.echo -eq 'x') 'request shape with token'
  $rq = (New-Request 8 'ping' $null '') | ConvertFrom-Json; Check ($null -eq $rq.token -and $null -eq $rq.args) 'request without token / args'
  $t = New-Token; Check ($t.Length -eq 48 -and $t -ne (New-Token)) 'token random, 48 hex'
  $ph = @([ordered]@{ tag = 'live'; ticks = @{ over_250 = 2; over_1000 = 1; max_ms = 1200 } }, [ordered]@{ tag = 'save'; ticks = @{ over_250 = 9; max_ms = 9000 } }, [ordered]@{ tag = 'buy'; ticks = @{ over_250 = 1; max_ms = 400 } })
  Check ((Get-PhaseSum $ph 'over_250' @('live', 'buy')) -eq 3 -and (Get-PhaseMax $ph 'max_ms' @('live', 'buy')) -eq 1200) 'phase sums leave the save out'
  $line = Format-ArmLine ([ordered]@{ label = '1/4 A'; status = 'ok'; fps = @{ median = 59.5; min = 30 }; hitch250 = 3; hitch1000 = 1; longestMs = 1200; scriptErrors = 0; noise = 'quiet' })
  Check ($line -eq '1/4 A: ok; fps median 59.5 min 30; hitches >=250ms 3 >=1s 1; longest pause 1200 ms; script errors 0; noise quiet') 'summary line'
  $tmp = Join-Path ([IO.Path]::GetTempPath()) "ts3bench-sc-$PID"; [void](New-Item -ItemType Directory -Force -Path "$tmp\a\sub")
  Set-Content -LiteralPath "$tmp\a\x.nhd" -Value 'one'; Set-Content -LiteralPath "$tmp\a\sub\y" -Value 'two'
  $h1 = Get-DirHash "$tmp\a"; Copy-Item -LiteralPath "$tmp\a" -Destination "$tmp\b" -Recurse; $h2 = Get-DirHash "$tmp\b"
  Set-Content -LiteralPath "$tmp\b\sub\y" -Value 'tw0'; $h3 = Get-DirHash "$tmp\b"
  Check ($h1 -eq $h2 -and $h1 -ne $h3 -and (Get-DirHash "$tmp\none") -eq 'missing') 'save hash: copy equal, change detected, missing'
  $u = "$tmp\user"; [void](New-Item -ItemType Directory -Force -Path "$u\Mods\Packages", "$u\Saves\$CopyName.sims3", "$u\Saves\Keep.sims3", "$u\TS3Kit-Bench")
  Set-Content -LiteralPath "$u\Mods\Packages\$AgentDest" -Value 'p'; Set-Content -LiteralPath "$u\TS3Kit-Bench\bench-state.json" -Value '{}'
  $gone = Do-Cleanup $u
  Check ($gone.Count -eq 2 -and -not (Test-Path -LiteralPath "$u\Saves\$CopyName.sims3") -and (Test-Path -LiteralPath "$u\Saves\Keep.sims3") -and -not (Test-Path -LiteralPath "$u\TS3Kit-Bench\bench-state.json")) 'cleanup removes only the copy, the agent package and the state file'
  Check (('12:01:02 Kaspersky Event Log id 302: Detected: PDM:Trojan.Win32.Generic C:\kit\tools\TS3Bench.ps1' -match $AvRelevant) -and ('process TS3W.exe blocked' -match $AvRelevant) -and -not ('Database update completed' -match $AvRelevant)) 'AV log line regex: about this run / not'
  $noKit = "$tmp\old-kit.ps1"; Set-Content -LiteralPath $noKit -Value "function Say { 'x' }`n`$Rx = @{ Eol = '\r?\n' }"
  Check ($null -eq (Import-KitSession $noKit) -and $null -eq (Import-KitSession "$tmp\missing.ps1")) 'kit without SetAdSession: no session state, no error'
  $kitPs1 = if ($KitScript) { $KitScript } else { Join-Path $here 'TS3Kit.ps1' }
  $script:KitSession = Import-KitSession $kitPs1
  if ($script:KitSession) {
    $gm = "$tmp\game"; [void](New-Item -ItemType Directory -Force -Path "$gm\Game\Bin"); $script:Game = $gm
    Set-Content -LiteralPath "$gm\Game\Bin\ts3native.adaptdiag-state.ini" -Value "[Live]`r`nClean=1`r`n" -Encoding ASCII
    Set-Session 'Benchmark' '1'; Set-Session 'Arm' 'k4-on-but-too-long-name'; Set-Session 'BenchmarkName' "ts3b$([char]0xE9)nch"
    $ini = [System.IO.File]::ReadAllText("$gm\Game\Bin\ts3native.adaptdiag-state.ini")
    Check ($ini -match '(?m)^\s*Benchmark\s*=\s*1' -and $ini -match '(?m)^\s*Arm\s*=\s*k4-on-but-too-lo\s*$' -and $ini -match '(?m)^\s*BenchmarkName\s*=\s*ts3b_nch' -and $ini -match '(?m)^\s*Clean\s*=\s*1') "kit SetAdSession: keys set, ascii, capped, [Live] kept"
    [void](Do-Cleanup "$tmp\user")
    $ini = [System.IO.File]::ReadAllText("$gm\Game\Bin\ts3native.adaptdiag-state.ini")
    Check ($ini -match '(?m)^\s*Benchmark\s*=\s*0') 'cleanup resets [Session] Benchmark to 0'
    $script:KitSession = $null; $script:Game = ''
  } else { Write-Host "  skip kit session state: $kitPs1 has no SetAdSession (pass -KitScript <adaptive diag kit TS3Kit.ps1>)" }
  Remove-Item -LiteralPath $tmp -Recurse -Force
  Load-Native
  Check ([Ts3Bench.Win]::IsModVersionDialog('#32770', $true, $true, $true) -and -not [Ts3Bench.Win]::IsModVersionDialog('#32770', $true, $false, $true) -and -not [Ts3Bench.Win]::IsModVersionDialog('Button', $true, $true, $true)) 'mod-version dialog match'
  if ($FakeTarget) {
    # end to end against a fake game (tools/selftest/kitbench: the real Mailbox.cs + Dispatcher.cs in an x86 exe)
    $script:Proc = Start-Process -FilePath $FakeTarget -PassThru -WindowStyle Hidden
    try {
      $script:Mb = New-Object Ts3Bench.Mbox($script:Proc.Id); $script:FrameAddr = $null; $script:CheckAt = [Environment]::TickCount; $script:Misses = 0
      $found = $false; for ($i = 0; $i -lt 50 -and -not $found; $i++) { Start-Sleep -Milliseconds 200; $found = $script:Mb.Scan() }
      Check $found "fake game: mailbox found by scan ($($script:Mb.LastScanMs) ms)"
      $script:Token = ''
      $p = Agent 'ping' @{ echo = 'hi' } 10; Check ($p.ok -and $p.result.echo -eq 'hi') 'fake game: ping round trip'
      $tok = New-Token; $l = Agent 'session_lock' @{ token = $tok } 10; Check ($l.ok -and $l.result.locked) 'session_lock claims the channel'
      $p = Agent 'ping' $null 10; Check (-not $p.ok -and $p.error -match 'locked') 'request without the token refused'
      $script:Token = 'wrong-token-0123456789'; $p = Agent 'ping' $null 10; Check (-not $p.ok) 'request with another token refused'
      $l = Agent 'session_lock' @{ token = 'another-token-0123456789' } 10; Check (-not $l.ok) 'second session_lock refused'
      $script:Token = $tok; $p = Agent 'ping' $null 10; Check ($p.ok) 'request with the token served'
      $l = Agent 'session_lock' @{ token = 'short' } 10; Check (-not $l.ok) 'short token refused'
      $l = Agent 'session_lock' @{ token = $tok; release = $true } 10; $script:Token = ''; $p = Agent 'ping' $null 10; Check ($l.ok -and -not $l.result.locked -and $p.ok) 'release frees the channel'
      Check ($script:Mb.U32At(0x10) -eq -1) 'unmapped address reads as -1'
    } finally { if (-not $script:Proc.HasExited) { Stop-Process -Id $script:Proc.Id -Force }; if ($script:Mb) { $script:Mb.Dispose() } }
  }
  Write-Host "TS3Bench self-check: $(if ($script:scFails) { "$($script:scFails) FAIL" } else { 'all ok' })"
  if ($script:scFails) { exit 1 }
}

if ($SelfCheck) { Invoke-SelfCheck; exit 0 }
if ($Cleanup) { Resolve-Paths; $g = Do-Cleanup $User; Write-Host "benchmark leftovers removed: $(if ($g.Count) { $g -join ', ' } else { 'none' })"; exit 0 }
try { Invoke-Bench } catch { Write-Host "Benchmark stopped: $_" -ForegroundColor Red; exit 1 }
