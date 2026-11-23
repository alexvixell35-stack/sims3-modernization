# Sandbox test of TS3Kit.ps1 (Windows PowerShell 5.1 or PowerShell 7; the kit runs under the same host as this test):
# fake game and user folders, fake shortcuts and a fake ts3native.log under %TEMP%, never the real install, never the
# real game (the "game" is a stub exe under another process name). Windows only, like the kit itself.
#   powershell -NoProfile -ExecutionPolicy Bypass -File test-kit.ps1 -Kit <assembled kit folder with the payload files>
# The kit is copied into the sandbox; a missing payload\ts3fixes.package gets a stand-in there, and the diagnostic
# build (payload\diag) is a stand-in made by the test.
param([Parameter(Mandatory = $true)][string]$Kit, [switch]$Keep)
$ErrorActionPreference = 'Stop'
$sb = Join-Path ([System.IO.Path]::GetTempPath()) "ts3kit-sandbox-$PID"
$hostExe = (Get-Process -Id $PID).Path
$fails = 0
function Check([bool]$c, [string]$what) { if ($c) { Write-Host "  ok   $what" } else { Write-Host "  FAIL $what" -ForegroundColor Red; $script:fails++ } }
function Run([string[]]$a) { $o = & $hostExe -NoProfile -ExecutionPolicy Bypass -File $ps1 @a -ProcNames TS3KitStub 2>&1 | Out-String; $script:code = $LASTEXITCODE; return $o }
function Tree([string]$root, [string]$skip = '^$') { (Get-ChildItem -LiteralPath $root -Recurse -File -Force | Where-Object { $_.FullName.Substring($root.Length) -notmatch $skip } | Sort-Object FullName | ForEach-Object { "$($_.FullName.Substring($root.Length))|$($_.Length)|$($_.LastWriteTimeUtc.Ticks)|$((Get-FileHash -LiteralPath $_.FullName).Hash)" }) -join "`n" }
function FileSha([string]$p) { (Get-FileHash -LiteralPath $p).Hash }
function FakeExe([string]$path, [uint32]$ts, [uint32]$soi) {
  $b = New-Object byte[] 4096; $b[0] = 0x4D; $b[1] = 0x5A; $b[0x3C] = 0x80; $b[0x80] = 0x50; $b[0x81] = 0x45
  [BitConverter]::GetBytes($ts).CopyTo($b, 0x88); [BitConverter]::GetBytes([uint16]0x0122).CopyTo($b, 0x96); [BitConverter]::GetBytes($soi).CopyTo($b, 0x80 + 24 + 56)
  [System.IO.File]::WriteAllBytes($path, $b)
}
function Unzip([string]$zip) { $d = "$zip-x"; Add-Type -AssemblyName System.IO.Compression.FileSystem; [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $d); return $d }
function NewGame([string]$root, [uint32]$ts, [uint32]$soi, [string]$exeName = 'TS3W.exe') {
  $bin = Join-Path $root 'Game\Bin'; [void](New-Item -ItemType Directory -Force -Path $bin)
  FakeExe (Join-Path $bin $exeName) $ts $soi
  Set-Content -LiteralPath (Join-Path $bin 'dinput8.dll') -Value 'loader'
  Set-Content -LiteralPath (Join-Path $bin 'Sims3SettingsSetter.asi') -Value 's3ss'
  Set-Content -LiteralPath (Join-Path $bin 'ts3native.ini') -Value "[Old]`r`nMine=1"
  return $bin
}
function Lnk([string]$path, [string]$target, [string]$arguments = '', [string]$cwd = '') {
  [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $path))
  if (-not (Test-Path -LiteralPath $target)) { [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target)); Set-Content -LiteralPath $target -Value 'x' }
  $l = (New-Object -ComObject WScript.Shell).CreateShortcut($path); $l.TargetPath = $target; $l.Arguments = $arguments; if ($cwd) { $l.WorkingDirectory = $cwd }; $l.Save()
}
try {
  $kc = Join-Path $sb 'kit'; [void](New-Item -ItemType Directory -Force -Path $kc); Copy-Item -Path (Join-Path $Kit '*') -Destination $kc -Recurse -Force
  $ps1 = Join-Path $kc 'tools\TS3Kit.ps1'
  # the kit's target edition, exe and asi name (kit 1120.k1 is retail 1.67, e1-e6 were EA app 1.69)
  $src = Get-Content -LiteralPath $ps1 -Raw
  if ($src -notmatch "\`$KitEdition = '([^']+)'; \`$KitExe = '([^']+)'; \`$KitAsi = '([^']+)'") { throw 'kit edition line not found in TS3Kit.ps1' }
  $kEd = $Matches[1]; $kExe = $Matches[2]; $kAsi = $Matches[3]
  $kPe = @{ 'retail 1.67' = @(0x52D872DA, 0xE5B000); 'Steam 1.67' = @(0x52DEC247, 0xE5A000); 'EA app 1.69' = @(0x6707155C, 0xEB5000) }[$kEd]
  if (Test-Path -LiteralPath (Join-Path $kc 'payload\diag')) { Remove-Item -LiteralPath (Join-Path $kc 'payload\diag') -Recurse -Force }
  $pkg = Join-Path $kc 'payload\ts3fixes.package'; if (-not (Test-Path -LiteralPath $pkg)) { Set-Content -LiteralPath $pkg -Value 'stand-in ts3fixes package' }
  $game = Join-Path $sb 'Game Copy\The Sims 3'; $bin = NewGame $game $kPe[0] $kPe[1] $kExe   # the kit's edition (kit 1120.e1-e6: EA app 1.69, k1: retail 1.67)
  $user = Join-Path $sb 'Docs\Electronic Arts\The Sims 3'
  [void](New-Item -ItemType Directory -Force -Path (Join-Path $user 'S3SS'), (Join-Path $user 'Saves\Town.sims3'), (Join-Path $user 'Mods\Packages'))
  $opt = Join-Path $user 'Options.ini'
  Set-Content -LiteralPath $opt -Value "[options]`r`nfullscreen = 1`r`nshadowquality = 3`r`ntexturequality = 3`r`ntreequality = 4`r`nlastdevice = 0;10de;1080;65535"
  Set-Content -LiteralPath (Join-Path $user 'DeviceConfig.log') -Value "User:            $env:USERNAME`r`nComputer:        $env:COMPUTERNAME`r`n=== Graphics device info ===`r`nNumber:          0`r`nName (driver):   Intel(R) UHD Graphics 630`r`nName (database): Intel(R) UHD Graphics 630 [Found: 1, Matched: 0]`r`nChipset:         Vendor: 8086, Device: 3e9b, Board: 00000000, Chipset: 0000`r`nDriver version:  101`r`nTexture memory:  512MB"
  $toml0 = "[patches.Mimalloc]`r`nenabled = false`r`n`r`n[qol.ui]`r`ndisable_overlay = true`r`n"
  [System.IO.File]::WriteAllText((Join-Path $user 'S3SS\S3SS.toml'), $toml0)
  Set-Content -LiteralPath (Join-Path $user 'Saves\Town.sims3\Town.nhd') -Value 'save data'
  Set-Content -LiteralPath (Join-Path $user 'Mods\Packages\a.package') -Value 'pkg'
  Set-Content -LiteralPath (Join-Path $user 'Mods\Packages\ts3fixes.package') -Value 'old fixes'   # no Resource.cfg: the installer adds one
  Set-Content -LiteralPath (Join-Path $user "ScriptError_$($env:COMPUTERNAME)__11-18-2026.xml") -Value "<x>$env:USERPROFILE\Documents</x>"
  Set-Content -LiteralPath (Join-Path $bin 'ts3native.log') -Value (@('ts3native 0.36.293 2026-11-19 10:00:00', "host exe $env:USERPROFILE\Games\TS3W.exe", 'compat: Apex Radiance not loaded at start',
    'SKIP alloc cache: EA allocator entry bytes differ', 'route async: mode 1 started 375 refused 8 collected 374', 'SKIP alloc cache: EA allocator entry bytes differ',
    'ts3fixes: ts3fixes 0.36.290 build ab12cd34, 3 settings on/valued, 9 off', 'AutShadow: stands down (foreign module)', 'monojit: method X unsupported opcode', 'gate: transform refused for M') -join "`r`n")
  Set-Content -LiteralPath (Join-Path $bin 'fps-summary.txt') -Value "Bottleneck: BRIDGE / GPU side`r`nuser folder $env:USERPROFILE\OneDrive\Documents"
  Set-Content -LiteralPath (Join-Path $bin 'framediag.csv') -Value 'time,in_world,fps'
  Set-Content -LiteralPath (Join-Path $user 'ApexRadiance_LOG.txt') -Value "Apex 4.0.4 on $env:COMPUTERNAME"
  $out = Join-Path $sb 'out'; [void](New-Item -ItemType Directory -Force -Path $out)
  $t0 = Tree $sb '^\\kit\\'

  Write-Host "Diag (given folders only; host $hostExe)"
  $o = Run @('-Mode', 'Diag', '-NoSystem', '-GameDir', $game, '-UserDir', $user, '-OutDir', $out)
  $zip = @(Get-ChildItem -LiteralPath $out -Filter 'TS3Diag-*.zip')
  Check ($zip.Count -eq 1 -and $code -eq 0) 'one zip written'
  $x = Unzip $zip[0].FullName
  $j = Get-Content -LiteralPath (Join-Path $x 'report.json') -Raw | ConvertFrom-Json
  Check ($j.games[0].edition -eq $kEd -and @($j.games[0].exes | Where-Object { $_.name -eq $kExe })[0].largeAddressAware) "exe read as $kEd ($kExe), LAA"
  Check ($j.findings[0].id -eq 'lastdevice-mismatch' -and $j.verdict -match 'lastdevice|graphics device') 'verdict: lastdevice mismatch'
  Check ($j.userFolders[0].saves[0].name -eq 'Town.sims3' -and -not (Get-ChildItem -LiteralPath $x -Recurse -Filter '*.nhd')) 'saves listed, not copied'
  Check ((Test-Path -LiteralPath (Join-Path $x 'files\user1\Options.ini')) -and (Test-Path -LiteralPath (Join-Path $x 'files\user1\S3SS\S3SS.toml')) -and (Test-Path -LiteralPath (Join-Path $x 'files\game1-bin\ts3native.ini'))) 'settings files copied'
  Check ((Test-Path -LiteralPath (Join-Path $x 'files\game1-bin\fps-summary.txt')) -and (Test-Path -LiteralPath (Join-Path $x 'files\game1-bin\framediag.csv')) -and (Test-Path -LiteralPath (Join-Path $x 'files\user1\apex\ApexRadiance_LOG.txt'))) 'diag build outputs and the Apex log copied'
  Check ((Get-Content -LiteralPath (Join-Path $x 'files\game1-bin\fps-summary.txt') -Raw) -match '%USERPROFILE%\\OneDrive') 'fps-summary.txt masked'
  $n = @($j.games[0].ts3nativeLogNotes)
  Check ($n.Count -eq 7 -and @($n -match '^SKIP alloc cache').Count -eq 1 -and -not ($n -match 'started 375') -and ($n -match 'stands down') -and ($n -match 'unsupported') -and ($n -match 'transform refused') -and ($n -match '^compat:') -and ($n -match '^ts3native 0\.36\.293')) "log notes: stand-downs / refusals / loads, deduplicated, stat counters left out ($($n.Count))"
  Check ($j.games[0].ts3fixesVersion -eq '0.36.290' -and "$(@($j.userFolders[0].mods.ts3fixes)[0].sha12)".Length -eq 12) 'ts3fixes version from the log, package sha12'
  Check ((Get-Content -LiteralPath (Join-Path $x 'report.txt') -Raw) -match 'stand-downs / refusals / loads: 7') 'log notes section in report.txt'
  $leak = @(Get-ChildItem -LiteralPath $x -Recurse -File | Where-Object { $_.Name -match [regex]::Escape($env:COMPUTERNAME) -or (Select-String -LiteralPath $_.FullName -Pattern ([regex]::Escape($env:USERNAME)), ([regex]::Escape($env:COMPUTERNAME)), ([regex]::Escape($env:USERPROFILE)) -Quiet) })
  Check ($leak.Count -eq 0) "no user name / home path / computer name in the zip ($($leak.Name -join ', '))"
  Check (($o -split "`n" | Where-Object { $_ -notmatch '^Report written' }) -join '' -notmatch [regex]::Escape($env:USERNAME)) 'no user name on the console (the zip path line aside)'
  Remove-Item -LiteralPath $x -Recurse -Force; Remove-Item -LiteralPath $zip[0].FullName -Force
  Check ((Tree $sb '^\\kit\\') -eq $t0) 'diag changed nothing in the sandbox'

  Write-Host 'Install'
  $exe0 = FileSha (Join-Path $bin $kExe); $opt0 = FileSha $opt
  $o = Run @('-Mode', 'Install', '-NoSystem', '-Yes', '-S3SS', '-GameDir', $game, '-UserDir', $user)
  Check ($code -eq 0 -and $o -match 'Installed \(test build\)' -and $o -notmatch 'diagnostic') 'install ok, no build question without payload\diag'; if ($code) { Write-Host $o }
  Check ((FileSha (Join-Path $bin 'ts3native.asi')) -eq (FileSha (Join-Path $kc "payload\$kAsi"))) 'asi equals the payload'
  Check ((FileSha (Join-Path $bin 'ts3native.ini')) -eq (FileSha (Join-Path $kc 'payload\ts3native.ini'))) 'ini equals the payload'
  Check ((FileSha (Join-Path $user 'Mods\Packages\ts3fixes.package')) -eq (FileSha $pkg)) 'ts3fixes.package equals the payload'
  Check ((Get-Content -LiteralPath (Join-Path $user 'Mods\Resource.cfg') -Raw) -match 'PackedFile Packages/\*\.package' -and $o -match 'standard Resource\.cfg') 'missing Resource.cfg written, and said so'
  $toml = [System.IO.File]::ReadAllText((Join-Path $user 'S3SS\S3SS.toml'))
  Check ($toml -match '(?s)\[patches\.Mimalloc\]\r\nenabled = true.*\[qol\.ui\]\r\ndisable_overlay = true.*frameRateLimit = 60') 'S3SS.toml merged, user key kept'
  Check ((FileSha (Join-Path $bin $kExe)) -eq $exe0 -and (FileSha $opt) -eq $opt0) 'TS3.exe and Options.ini untouched'
  $bk = @(Get-ChildItem -LiteralPath (Join-Path $user 'TS3Kit-Backup') -Directory)
  Check ($bk.Count -eq 1 -and (Get-Content -LiteralPath (Join-Path $bk[0].FullName '2-ts3native.ini') -Raw) -match 'Mine=1' -and (Test-Path -LiteralPath (Join-Path $bk[0].FullName 'Options.ini.safety-copy'))) 'backup holds the old ini and an Options.ini copy'
  Check ((Get-Content -LiteralPath (Join-Path $bk[0].FullName '3-ts3fixes.package') -Raw) -match 'old fixes' -and -not (Get-ChildItem -LiteralPath $bk[0].FullName -Filter '*-Resource.cfg')) 'backup holds the old ts3fixes.package (no Resource.cfg: there was none)'
  $o = Run @('-Mode', 'Install', '-NoSystem', '-Yes', '-S3SS', '-GameDir', $game, '-UserDir', $user)
  Check ($o -match 'Nothing to do' -and @(Get-ChildItem -LiteralPath (Join-Path $user 'TS3Kit-Backup') -Directory).Count -eq 1) 'second install: nothing to do, no new backup'

  Write-Host 'Uninstall'
  # benchmark leftovers (docs/315 §5): the uninstall hook runs TS3Bench.ps1 -Cleanup
  $bl = @((Join-Path $user 'Saves\TS3Kit-Bench.sims3'), (Join-Path $user 'Saves\TS3Kit-Bench.sims3.backup'), (Join-Path $user 'TS3Kit-Bench'))
  foreach ($d in $bl) { [void](New-Item -ItemType Directory -Force -Path $d) }
  Set-Content -LiteralPath (Join-Path $bl[0] 'x.nhd') -Value 'copy'; Set-Content -LiteralPath (Join-Path $bl[2] 'bench-state.json') -Value '{}'
  Set-Content -LiteralPath (Join-Path $user 'Mods\Packages\TS3KitBench-ts3agent.package') -Value 'agent'
  $o = Run @('-Mode', 'Uninstall', '-NoSystem', '-Yes', '-GameDir', $game, '-UserDir', $user)
  Check (-not (Test-Path -LiteralPath $bl[0]) -and -not (Test-Path -LiteralPath $bl[1]) -and -not (Test-Path -LiteralPath (Join-Path $bl[2] 'bench-state.json')) -and -not (Test-Path -LiteralPath (Join-Path $user 'Mods\Packages\TS3KitBench-ts3agent.package'))) 'uninstall: benchmark leftovers removed (TS3Bench.ps1 -Cleanup)'
  if (Test-Path -LiteralPath $bl[2]) { Remove-Item -LiteralPath $bl[2] -Recurse -Force }   # the state folder itself is not ours to compare
  Check ($code -eq 0 -and -not (Test-Path -LiteralPath (Join-Path $bin 'ts3native.asi'))) 'asi removed'
  Check ((Get-Content -LiteralPath (Join-Path $bin 'ts3native.ini') -Raw) -match 'Mine=1' -and [System.IO.File]::ReadAllText((Join-Path $user 'S3SS\S3SS.toml')) -eq $toml0) 'old ini and S3SS.toml back'
  Check ((Get-Content -LiteralPath (Join-Path $user 'Mods\Packages\ts3fixes.package') -Raw) -match 'old fixes' -and -not (Test-Path -LiteralPath (Join-Path $user 'Mods\Resource.cfg'))) 'old ts3fixes.package back, the kit''s Resource.cfg removed'
  Check ((Tree $sb '^\\kit\\|TS3Kit-Backup|\\ts3native\.ini$|S3SS\.toml$').Replace("`r", '') -eq (($t0 -split "`n" | Where-Object { $_ -notmatch '\\ts3native\.ini\||S3SS\.toml\|' }) -join "`n")) 'everything else as before the install'
  $o = Run @('-Mode', 'Uninstall', '-NoSystem', '-Yes', '-GameDir', $game, '-UserDir', $user)
  Check ($o -match 'Nothing to undo') 'second uninstall: nothing to undo'

  Write-Host 'Install the diagnostic build (payload\diag, answers typed in)'
  $dg = Join-Path $kc 'payload\diag'; [void](New-Item -ItemType Directory -Force -Path $dg)
  $hasAd = Test-Path -LiteralPath (Join-Path $kc 'payload\adaptdiag')   # kit 1121.a1+: the adaptive diag kit rides on the diagnostic build
  # the benchmark lane's flag (docs/317) as it would leave it; report and uninstall reset it
  function SetBench([string]$b) { $p = Join-Path $b 'ts3native.adaptdiag-state.ini'; [System.IO.File]::WriteAllText($p, ([System.IO.File]::ReadAllText($p) -replace 'Benchmark = 0', 'Benchmark = 1')) }
  Set-Content -LiteralPath (Join-Path $dg 'ts3fixes.package') -Value 'diag package'; Set-Content -LiteralPath (Join-Path $dg 'ts3native.ini') -Value "[UserDiag]`r`nVerbose=1"
  Set-Content -LiteralPath (Join-Path $dg $kAsi) -Value 'ts3native 0.36.999-diagtest'   # the diagnostic's own asi
  [System.IO.File]::WriteAllText((Join-Path $user 'Mods\Resource.cfg'), 'Priority 500 mine')
  $o = Run @('-Mode', 'Install', '-NoSystem', '-Answers', 'D,y', '-GameDir', $game, '-UserDir', $user)
  Check ($code -eq 0 -and $o -match 'normal test build \(N\) or the diagnostic build' -and $o -match 'Installed \(diagnostic build\)') 'asked normal or diagnostic, installed the diagnostic build'; if ($code) { Write-Host $o }
  Check ((FileSha (Join-Path $user 'Mods\Packages\ts3fixes.package')) -eq (FileSha (Join-Path $dg 'ts3fixes.package')) -and $(if ($hasAd) { $bi = Get-Content -LiteralPath (Join-Path $bin 'ts3native.ini') -Raw; $bi -match '(?m)^Verbose=1' -and $bi -match '(?m)^\[AdaptDiag\]\r?\nEnabled = 1' -and $bi -match '(?m)^HitchMs = 1000' } else { (FileSha (Join-Path $bin 'ts3native.ini')) -eq (FileSha (Join-Path $dg 'ts3native.ini')) }) -and (FileSha (Join-Path $bin 'ts3native.asi')) -eq (FileSha (Join-Path $dg $kAsi)) -and $o -match 'OK +ts3native\.asi version 0\.36\.999-diagtest \(kit 0\.36\.999-diagtest\)') 'diagnostic package, ini and its own asi in place, version read back'
  Check ([System.IO.File]::ReadAllText((Join-Path $user 'Mods\Resource.cfg')) -eq 'Priority 500 mine') 'existing Resource.cfg left alone'
  if ($hasAd) {
    Check ((Test-Path -LiteralPath (Join-Path $bin 'ts3native.adaptdiag-known.tsv')) -and (Test-Path -LiteralPath (Join-Path $bin 'ts3native.adaptdiag-expect.txt')) -and (Get-Content -LiteralPath (Join-Path $bin 'ts3native.adaptdiag-state.ini') -Raw) -match 'Benchmark = 0') 'adaptive diag kit: known-issue library, expected list and state file (Benchmark = 0) in Bin'
    Write-Host 'Make report (-Mode Report): size first, then the zip with the adaptdiag files, masked'
    Set-Content -LiteralPath (Join-Path $bin 'adaptdiag-report.txt') -Value "## session`r`nuser folder $env:USERPROFILE\Documents"
    Set-Content -LiteralPath (Join-Path $bin 'adaptdiag.csv') -Value 'minute,activity,noise'
    [void](New-Item -ItemType Directory -Force -Path (Join-Path $bin 'adaptdiag-bench')); Set-Content -LiteralPath (Join-Path $bin 'adaptdiag-bench\run1.txt') -Value 'bench'
    SetBench $bin
    $ro = Join-Path $sb 'report-out'; [void](New-Item -ItemType Directory -Force -Path $ro)
    $o = Run @('-Mode', 'Report', '-NoSystem', '-Yes', '-GameDir', $game, '-UserDir', $user, '-OutDir', $ro)
    $rz = @(Get-ChildItem -LiteralPath $ro -Filter 'TS3Diag-*.zip')
    Check ($code -eq 0 -and $o -match 'Make report: \d+ log / diag files' -and $o -match 'adaptive diag report: found' -and $o -match 'Zip size: \d+ KB' -and $rz.Count -eq 1) 'report: size line first, then one zip'; if ($code) { Write-Host $o }
    if ($rz.Count) { $rx = Unzip $rz[0].FullName; $rt = Join-Path $rx 'files\game1-bin\adaptdiag-report.txt'
      Check ((Test-Path -LiteralPath $rt) -and (Test-Path -LiteralPath (Join-Path $rx 'files\game1-bin\adaptdiag.csv')) -and (Test-Path -LiteralPath (Join-Path $rx 'files\game1-bin\adaptdiag-bench\run1.txt')) -and (Get-Content -LiteralPath $rt -Raw) -notmatch [regex]::Escape($env:USERNAME)) 'report zip: adaptdiag report, csv and bench folder, user name masked' }
    Check ((Get-Content -LiteralPath (Join-Path $bin 'ts3native.adaptdiag-state.ini') -Raw) -match 'Benchmark = 0') 'report resets the benchmark flag'
  }
  $o = Run @('-Mode', 'Uninstall', '-NoSystem', '-Yes', '-GameDir', $game, '-UserDir', $user)
  if ($hasAd) { Check (-not (Test-Path -LiteralPath (Join-Path $bin 'ts3native.adaptdiag-known.tsv')) -and -not (Test-Path -LiteralPath (Join-Path $bin 'ts3native.adaptdiag-expect.txt'))) 'diagnostic uninstall: adaptive diag kit files removed' }
  Check ($code -eq 0 -and (Get-Content -LiteralPath (Join-Path $user 'Mods\Packages\ts3fixes.package') -Raw) -match 'old fixes' -and (Get-Content -LiteralPath (Join-Path $bin 'ts3native.ini') -Raw) -match 'Mine=1' -and (Test-Path -LiteralPath (Join-Path $user 'Mods\Resource.cfg'))) 'diagnostic uninstall: old package and ini back, user Resource.cfg kept'
  Remove-Item -LiteralPath $dg -Recurse -Force; Remove-Item -LiteralPath (Join-Path $user 'Mods\Resource.cfg') -Force

  Write-Host 'Install finds the user folder in a fake Documents (The Sims 3 + a newer "Sims 3 Backup"), moves an old ts3fixes and a second asi out'
  $docs = Join-Path $sb 'FakeDocs'; $real = Join-Path $docs 'Electronic Arts\The Sims 3'; $bak = Join-Path $docs 'Electronic Arts\Sims 3 Backup'
  foreach ($d in $real, $bak) { [void](New-Item -ItemType Directory -Force -Path (Join-Path $d 'Mods\Packages\Old'), (Join-Path $d 'Saves')); Set-Content -LiteralPath (Join-Path $d 'Options.ini') -Value '[options]' }
  Get-ChildItem -LiteralPath $real -Recurse -Force | ForEach-Object { $_.LastWriteTime = (Get-Date).AddDays(-3) }
  $oldPkg = Join-Path $real 'Mods\Packages\Old\ts3fixes_v0211b.package'; Set-Content -LiteralPath $oldPkg -Value 'old 0211b'
  $oldAsi = Join-Path $bin 'ts3native-retail167.asi'; Set-Content -LiteralPath $oldAsi -Value 'old asi'
  $bak0 = Tree $bak
  $o = Run @('-Mode', 'Install', '-NoSystem', '-Yes', '-DocsDir', $docs, '-GameDir', $game)
  Check ($code -eq 0 -and $o -match 'skipped \(named like a copy or backup' -and $o -match 'Sims 3 user folder: .*\\The Sims 3\r?\n') 'backup skipped, "The Sims 3" picked'; if ($code) { Write-Host $o }
  Check ((FileSha (Join-Path $real 'Mods\Packages\ts3fixes.package')) -eq (FileSha $pkg) -and (Tree $bak) -eq $bak0) 'package in The Sims 3\Mods\Packages, backup folder untouched'
  Check (-not (Test-Path -LiteralPath $oldPkg) -and -not (Test-Path -LiteralPath $oldAsi) -and @(Get-ChildItem -LiteralPath (Join-Path $real 'TS3Kit-Backup') -Recurse -Filter '*-moved-*').Count -eq 2) 'old ts3fixes package and second asi moved into the backup (not deleted)'
  Check ($o -match 'OK +ts3native asi files the game loads: 1 \(ts3native\.asi\)' -and $o -match 'OK +ts3native\.asi version 0\.\d+\.\d+' -and $o -match 'OK +ts3fixes packages under .*: 1' -and $o -match 'Install check: OK') 'check after the install: OK lines'
  $o = Run @('-Mode', 'Uninstall', '-NoSystem', '-Yes', '-DocsDir', $docs, '-GameDir', $game)
  Check ($code -eq 0 -and (Get-Content -LiteralPath $oldPkg -Raw) -match 'old 0211b' -and (Get-Content -LiteralPath $oldAsi -Raw) -match 'old asi' -and -not (Test-Path -LiteralPath (Join-Path $real 'Mods\Packages\ts3fixes.package'))) 'uninstall puts the moved files back'
  $o = Run @('-Mode', 'Install', '-NoSystem', '-Answers', 'n,y', '-DocsDir', $docs, '-GameDir', $game, '-UserDir', $real)
  Check ($code -eq 2 -and $o -match 'MISMATCH ts3native asi files the game loads: 2' -and $o -match 'MISMATCH ts3fixes packages under .*: 2' -and $o -match 'also loaded: .*ts3fixes_v0211b' -and $o -match 'Install check: MISMATCH') 'moving declined: MISMATCH lines, exit 2'
  $o = Run @('-Mode', 'Uninstall', '-NoSystem', '-Yes', '-GameDir', $game, '-UserDir', $real)
  $die = Join-Path $docs 'Electronic Arts\Die Sims 3'; [void](New-Item -ItemType Directory -Force -Path $die); Set-Content -LiteralPath (Join-Path $die 'Options.ini') -Value '[options]'
  $t1 = Tree $docs
  $o = Run @('-Mode', 'Install', '-NoSystem', '-Yes', '-DocsDir', $docs, '-GameDir', $game)
  Check ($code -ne 0 -and $o -match 'Several Sims 3 user folders' -and (Tree $docs) -eq $t1) 'two real-looking folders and -Yes: refused, nothing changed'
  $o = Run @('-Mode', 'Install', '-NoSystem', '-Answers', '2,y,y', '-DocsDir', $docs, '-GameDir', $game)
  Check ($code -eq 0 -and $o -match '(?s)1\. .*Die Sims 3.*2\. .*The Sims 3 .*number of the folder the game uses' -and (Test-Path -LiteralPath (Join-Path $real 'Mods\Packages\ts3fixes.package')) -and -not (Test-Path -LiteralPath (Join-Path $die 'Mods'))) 'two real-looking folders: listed newest first, the typed number is used'; if ($code) { Write-Host $o }
  $o = Run @('-Mode', 'Uninstall', '-NoSystem', '-Yes', '-GameDir', $game, '-UserDir', $real)
  Remove-Item -LiteralPath $docs -Recurse -Force; Remove-Item -LiteralPath $oldAsi -Force

  Write-Host 'Install moves TS3 Turbo Engine (fake Turbo.asi + Turbo.txt) into the backup, kit.log line, uninstall puts them back by sha'
  $tAsi = Join-Path $bin 'Turbo.asi'; $tTxt = Join-Path $bin 'Turbo.txt'
  Set-Content -LiteralPath $tAsi -Value 'fake turbo engine'; Set-Content -LiteralPath $tTxt -Value 'Turbo = 2018'
  $tAsiSha = FileSha $tAsi; $tTxtSha = FileSha $tTxt
  $o = Run @('-Mode', 'Install', '-NoSystem', '-Yes', '-Build', 'Normal', '-GameDir', $game, '-UserDir', $user)
  $tl = @(Get-ChildItem -LiteralPath (Join-Path $user 'TS3Kit-Backup') -Recurse -Filter 'kit.log' | Sort-Object LastWriteTime -Descending)
  Check ($code -eq 0 -and -not (Test-Path -LiteralPath $tAsi) -and -not (Test-Path -LiteralPath $tTxt) -and @(Get-ChildItem -LiteralPath (Join-Path $user 'TS3Kit-Backup') -Recurse -Filter '*-moved-Turbo.*').Count -eq 2) 'Turbo.asi and Turbo.txt moved into the backup (not deleted)'; if ($code) { Write-Host $o }
  Check ($o -match 'Found TS3 Turbo Engine' -and $o -match 'not the copy we know' -and $tl.Count -ge 1 -and (Get-Content -LiteralPath $tl[0].FullName -Raw) -match 'moved Turbo\.asi to .*-moved-Turbo\.asi, restore with TS3Kit\.bat -Mode Uninstall \(sha [0-9a-f]{12}, not the known') 'Turbo: notice, unknown-sha note, kit.log line'
  $o = Run @('-Mode', 'Uninstall', '-NoSystem', '-Yes', '-GameDir', $game, '-UserDir', $user)
  Check ($code -eq 0 -and (Test-Path -LiteralPath $tAsi) -and (FileSha $tAsi) -eq $tAsiSha -and (FileSha $tTxt) -eq $tTxtSha -and $o -notmatch 'sha differs') 'uninstall puts Turbo.asi and Turbo.txt back, same sha'
  Remove-Item -LiteralPath $tAsi, $tTxt -Force
  if (Test-Path -LiteralPath (Join-Path $user 'Mods\Resource.cfg')) { Remove-Item -LiteralPath (Join-Path $user 'Mods\Resource.cfg') -Force }

  Write-Host 'Install refused on a Steam exe'
  $steam = Join-Path $sb 'Steam\The Sims 3'; $sbin = NewGame $steam 0x52DEC247 0xE5A000; $ts = Tree $steam
  $o = Run @('-Mode', 'Install', '-NoSystem', '-Yes', '-GameDir', $steam, '-UserDir', $user)
  Check ($code -ne 0 -and $o -match "No Game\\Bin\\$([regex]::Escape($kExe)) in|This $([regex]::Escape($kExe)) is 'Steam 1\.67'" -and (Tree $steam) -eq $ts) 'refused, nothing changed'

  Write-Host 'Launch routes (nothing is started: every run answers N / 0 / Enter)'
  $o = Run @('-Mode', 'Capture', '-NoSystem', '-Answers', ',', '-GameDir', $steam, '-UserDir', $user, '-OutDir', $out)
  Check ($code -eq 0 -and $o -match '(?s)1\. I start it myself.*2\. TS3W\.exe directly.*Warning: a direct start can make the game see another graphics device.*Cancelled\. Nothing was started' -and $o -notmatch 'usual way\r?\n') 'no route found: Enter cancels, never a silent direct start'
  $solo = Join-Path $sb 'lib\steamapps\common\The Sims 3'; $sobin = NewGame $solo 0x52DEC247 0xE5A000
  $o = Run @('-Mode', 'Capture', '-NoSystem', '-Answers', 'n,0', '-GameDir', $solo, '-UserDir', $user, '-OutDir', $out)
  Check ($code -eq 0 -and $o -match '(?s)Start the game the way you normally do: Steam \(steam://rungameid/47890\)\? \(Y/N\) n.*1\. Steam .*<- looks like your usual way.*3\. TS3W\.exe directly.*Warning.*Cancelled') 'one route (Steam under steamapps): Y/N first, then the list, Direct last'
  Check (@(Get-ChildItem -LiteralPath $out -Filter 'TS3Diag-*.zip').Count -eq 0) 'cancelled: no zip'

  Write-Host 'Launch and capture through the player''s shortcut (stub game under another process name)'
  $stub = Join-Path $bin 'TS3KitStub.exe'
  # PowerShell 7 cannot write an exe: Windows PowerShell 5.1 (on every Windows) compiles the stub
  $src = Join-Path $sb 'stub.cs'
  [System.IO.File]::WriteAllText($src, @'
using System; using System.IO; using System.Threading;
public static class Stub { public static void Main() {
  string u = Environment.GetEnvironmentVariable("TS3KIT_STUB_USER");
  File.WriteAllText("ts3native.log", "release: stub\r\nhost build: stub\r\n");
  Thread.Sleep(6000);
  File.WriteAllText(Path.Combine(u, "Options.ini"), "[options]\r\nfullscreen = 0\r\nshadowquality = 1\r\ntexturequality = 1\r\ntreequality = 1\r\nlastdevice = 0;8086;3e9b;101\r\n");
  Thread.Sleep(2000);
} }
'@)
  & powershell.exe -NoProfile -Command "Add-Type -OutputAssembly '$stub' -OutputType ConsoleApplication -TypeDefinition ([System.IO.File]::ReadAllText('$src'))"
  Set-Content -LiteralPath (Join-Path $bin 'Sims3Launcher.exe') -Value 'launcher'
  $links = Join-Path $sb 'Links'
  Lnk (Join-Path $links 'Desktop\The Sims 3 Play.lnk') $stub '' $bin
  Lnk (Join-Path $links 'Desktop\Notes.lnk') (Join-Path $sb 'Other\tool.exe')
  Lnk (Join-Path $links 'Start Menu\EA\Uninstall The Sims 3.lnk') (Join-Path $game 'unins000.exe')
  Lnk (Join-Path $links 'Start Menu\Steam\The Sims 3.lnk') (Join-Path $sb 'SteamApp\steam.exe') '-applaunch 47890'
  $env:TS3KIT_STUB_USER = $user
  $o = Run @('-Mode', 'Capture', '-NoSystem', '-LinkDir', $links, '-Answers', '1', '-InfoAfterSec', '2', '-CaptureMaxSec', '90', '-GameDir', $game, '-UserDir', $user, '-OutDir', $out)
  Check ($o -match "(?s)1\. shortcut 'The Sims 3 Play' .*<- looks like your usual way.*2\. shortcut 'The Sims 3' .*steam\.exe -applaunch 47890\).*3\. Sims3Launcher\.exe.*4\. I start it myself.*5\. TS3W\.exe directly.*Warning" -and $o -notmatch 'Notes|Uninstall') 'routes: game shortcut (marked), Steam shortcut, launcher, self, direct last; other shortcuts left out'
  $zip = @(Get-ChildItem -LiteralPath $out -Filter 'TS3Diag-*.zip')
  Check ($zip.Count -eq 1 -and $code -eq 0) 'capture zip written'
  if ($zip.Count -eq 1) {
    $x = Unzip $zip[0].FullName
    $j = Get-Content -LiteralPath (Join-Path $x 'report.json') -Raw | ConvertFrom-Json
    Check ($j.session.launch -eq 'Shortcut' -and $j.session.route -match "'The Sims 3 Play'" -and (Get-Content -LiteralPath (Join-Path $x 'report.txt') -Raw) -match "launch route: shortcut 'The Sims 3 Play'") 'chosen route recorded in report.txt / report.json'
    $oc = @($j.session.fileChanges | Where-Object { $_.file -like '*Options.ini' })
    Check ($oc.Count -eq 1 -and $oc[0].state -eq 'changed' -and $oc[0].keyChangeCount -eq 5) 'Options.ini seen as changed, 5 settings'
    Check ($j.findings[0].id -eq 'session-reset-observed') 'verdict: reset seen during the session'
    Check (-not $j.session.collectedWhileRunning -and @($j.session.process.modules).Count -gt 3 -and $j.session.process.token -match 'elevated=') 'waited for exit; modules and token read'
    Check ((Test-Path -LiteralPath (Join-Path $x 'session\after\game-bin\ts3native.log')) -and (Test-Path -LiteralPath (Join-Path $x 'session\before\user1\Options.ini')) -and (Test-Path -LiteralPath (Join-Path $x 'session\after\user1\Options.ini'))) 'session folder: before / after copies and the new log'
    $leak = @(Get-ChildItem -LiteralPath $x -Recurse -File | Where-Object { Select-String -LiteralPath $_.FullName -Pattern ([regex]::Escape($env:USERNAME)), ([regex]::Escape($env:COMPUTERNAME)), ([regex]::Escape($env:USERPROFILE)) -Quiet })
    Check ($leak.Count -eq 0) "no user name / home path / computer name in the capture zip ($($leak.Name -join ', '))"
  } else { Write-Host $o }

  # A/B mode (docs/314): only when the kit has payload\ab\arms.json. The arms get stand-in packages (text); the stub game
  # writes the ts3fixes kit lines for the arm whose package it finds, and no ts3native.log when ts3native.asi is absent.
  $abCfgFile = Join-Path $kc 'payload\ab\arms.json'
  if (Test-Path -LiteralPath $abCfgFile) {
    Write-Host 'A/B mode: install, sessions A / B / forced B, uninstall'
    $real = Get-Content -LiteralPath $abCfgFile -Raw | ConvertFrom-Json
    Check ($real.arms.A.package -and (FileSha (Join-Path $kc "payload\$($real.arms.A.package)")) -eq "$($real.arms.A.packageSha)".ToUpper() -and ($real.arms.B.off -or (FileSha (Join-Path $kc "payload\$($real.arms.B.package)")) -eq "$($real.arms.B.packageSha)".ToUpper())) 'the kit''s arms.json names its own packages with their sha256'
    Check ((FileSha (Join-Path $kc "payload\$($real.arms.A.package)")) -eq (FileSha (Join-Path $kc 'payload\ts3fixes.package'))) 'arm A package = the package the install writes (a session started outside the kit runs arm A)'
    $o = Run @('-Mode', 'Play', '-NoSystem', '-Launch', 'Direct', '-GameDir', $game, '-UserDir', $user, '-OutDir', $out)
    Check ($code -ne 0 -and $o -match 'not installed in this game folder') 'Play before the install: refused'
    $o = Run @('-Mode', 'Install', '-NoSystem', '-Yes', '-Build', 'Normal', '-GameDir', $game, '-UserDir', $user)
    Check ($code -eq 0 -and $o -match 'Install check: OK') 'install for the A/B sessions'; if ($code) { Write-Host $o }
    $abd = Join-Path $kc 'payload\ab'
    Set-Content -LiteralPath (Join-Path $abd 'A-test.package') -Value 'stand-in arm A package'
    $arms = [ordered]@{ A = [ordered]@{ label = "$($real.arms.A.label)"; package = 'ab\A-test.package'; packageSha = (FileSha (Join-Path $abd 'A-test.package')).ToLower() } }
    if ($real.arms.B.off) { $arms['B'] = [ordered]@{ label = "$($real.arms.B.label)"; off = $true } }
    else { Set-Content -LiteralPath (Join-Path $abd 'B-test.package') -Value 'stand-in arm B package'; $arms['B'] = [ordered]@{ label = "$($real.arms.B.label)"; package = 'ab\B-test.package'; packageSha = (FileSha (Join-Path $abd 'B-test.package')).ToLower() } }
    [System.IO.File]::WriteAllText($abCfgFile, ([ordered]@{ kit = $real.kit; arms = $arms } | ConvertTo-Json -Depth 4))
    $abStub = Join-Path $bin 'ab\TS3KitStub.exe'; [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $abStub))
    $src2 = Join-Path $sb 'stub-ab.cs'
    [System.IO.File]::WriteAllText($src2, @'
using System; using System.IO; using System.Threading; using System.Text.RegularExpressions;
public static class Stub { public static void Main() {
  string u = Environment.GetEnvironmentVariable("TS3KIT_STUB_USER");
  string pkg = Path.Combine(u, "Mods\\Packages\\ts3fixes.package");
  if (File.Exists("ts3native.asi") && File.Exists(pkg)) {
    Match m = Regex.Match(File.ReadAllText(pkg), "arm ([AB])");
    string a = m.Success ? m.Groups[1].Value : "?";
    File.WriteAllText("ts3native.log", "ts3native 0.36.298-stub\r\nts3fixes: kit: arm " + a + ": TS3 test kit: arm " + a + " - stub label | read-back commodmapfast=" + (a == "A" ? "on" : "off") + "\r\n"
      + "ts3fixes: kit: stats arm " + a + " (minute): in world 1.0 min, worlds 1, sim updates/s median 30 min 2 (60 s), hitches >= 250 ms 4, >= 1 s 1, longest 1200 ms, save pauses 0 (longest 0 ms), read-back commodmapfast=" + (a == "A" ? "on" : "off") + "\r\n"
      + "ts3fixes: kit: stats arm " + a + " (world quit): in world 2.5 min, worlds 1, sim updates/s median 31 min 1 (150 s), hitches >= 250 ms 5, >= 1 s 2, longest 2400 ms, save pauses 1 (longest 8000 ms), read-back commodmapfast=" + (a == "A" ? "on" : "off") + "\r\n");
  }
  File.WriteAllText(Path.Combine(u, "ScriptError_stub_" + DateTime.Now.Ticks + ".xml"), "<x/>");
  Thread.Sleep(4000);
} }
'@)
    & powershell.exe -NoProfile -Command "Add-Type -OutputAssembly '$abStub' -OutputType ConsoleApplication -TypeDefinition ([System.IO.File]::ReadAllText('$src2'))"
    $pkgU = Join-Path $user 'Mods\Packages\ts3fixes.package'; $asiB = Join-Path $bin 'ts3native.asi'; $asiSha = FileSha $asiB
    $state = Join-Path $kc 'ab\state.json'
    $play = @('-Mode', 'Play', '-NoSystem', '-Launch', 'Direct', '-DirectExe', 'ab\TS3KitStub.exe', '-InfoAfterSec', '1', '-CaptureMaxSec', '60', '-GameDir', $game, '-UserDir', $user, '-OutDir', $out)
    Get-ChildItem -LiteralPath $out -Filter 'TS3Diag-*.zip' | Remove-Item -Force
    Remove-Item -LiteralPath (Join-Path $bin 'ts3native.log') -Force -ErrorAction SilentlyContinue
    $o = Run $play
    $res = @(Get-ChildItem -LiteralPath (Join-Path $kc 'ab\results') -Filter '*-armA.txt' -ErrorAction SilentlyContinue)
    Check ($code -eq 0 -and $o -match [regex]::Escape("TS3 test kit: arm A - $($real.arms.A.label)") -and (Get-Content -LiteralPath $pkgU -Raw) -match 'arm A') 'session 1 = arm A: notice on the console, arm A package in Mods'; if ($code) { Write-Host $o }
    $rt = $(if ($res.Count) { Get-Content -LiteralPath $res[0].FullName -Raw } else { '' })
    Check ($res.Count -eq 1 -and $rt -match 'in-game notice \(ts3native\.log\): TS3 test kit: arm A - stub label' -and $rt -match 'hitches \(one sim update took\): >= 250 ms 5, >= 1 s 2; longest pause 2400 ms; save pauses \(counted apart\) 1, longest 8000 ms' -and $rt -match 'sim updates per second: median 31, lowest 1' -and $rt -match 'switch read-back at the last stats line: commodmapfast=on' -and $rt -match 'script error files written during this session: 1') 'arm A summary: the last stats line, read-back, script errors'
    Check ((Get-Content -LiteralPath $state -Raw | ConvertFrom-Json).next -eq 'B' -and (Get-Content -LiteralPath (Join-Path $kc 'ab\kit.log') -Raw) -match 'session start: TS3 test kit: arm A') 'state: next arm B; kit.log session line'
    $zip = @(Get-ChildItem -LiteralPath $out -Filter 'TS3Diag-*.zip')
    if ($zip.Count -eq 1) { $x = Unzip $zip[0].FullName; Check ((Test-Path -LiteralPath (Join-Path $x "ab\results\$($res[0].Name)")) -and (Get-Content -LiteralPath (Join-Path $x 'report.txt') -Raw) -match '== A/B session') 'session zip: the summary file and the report section'; Remove-Item -LiteralPath $x -Recurse -Force } else { Check $false 'session zip written' }
    Get-ChildItem -LiteralPath $out -Filter 'TS3Diag-*.zip' | Remove-Item -Force
    $o = Run $play
    $resB = @(Get-ChildItem -LiteralPath (Join-Path $kc 'ab\results') -Filter '*-armB.txt' -ErrorAction SilentlyContinue)
    $rb = $(if ($resB.Count) { Get-Content -LiteralPath $resB[0].FullName -Raw } else { '' })
    if ($real.arms.B.off) {
      Check ($code -eq 0 -and -not (Test-Path -LiteralPath $asiB) -and -not (Test-Path -LiteralPath $pkgU) -and (FileSha (Join-Path $kc 'ab\off\ts3native.asi')) -eq $asiSha -and $o -match 'no kit message shows in the game') 'session 2 = arm B (off): asi and package moved into ab\off, console notice'; if ($code) { Write-Host $o }
      Check ($rb -match 'not loaded in this arm' -and $rb -match 'ts3native\.log not written during this session \(our mod was not loaded\): OK' -and $rb -match 'script error files written during this session: 1') 'arm B summary: mod not loaded, checked by the log'
    } else {
      Check ($code -eq 0 -and (Get-Content -LiteralPath $pkgU -Raw) -match 'arm B' -and (FileSha $asiB) -eq $asiSha) 'session 2 = arm B: arm B package, same asi'; if ($code) { Write-Host $o }
      Check ($rb -match 'in-game notice \(ts3native\.log\): TS3 test kit: arm B' -and $rb -match 'commodmapfast=off') 'arm B summary: notice and read-back off'
    }
    Check ((Get-Content -LiteralPath $state -Raw | ConvertFrom-Json).next -eq 'A') 'state: next arm A'
    $o = Run ($play + @('-Arm', 'B'))
    Check ($code -eq 0 -and @(Get-ChildItem -LiteralPath (Join-Path $kc 'ab\results') -Filter '*-armB.txt').Count -eq 2 -and (Get-Content -LiteralPath $state -Raw | ConvertFrom-Json).next -eq 'A') '-Arm B forces arm B; next stays A'; if ($code) { Write-Host $o }
    $o = Run $play
    Check ($code -eq 0 -and (FileSha $asiB) -eq $asiSha -and (Get-Content -LiteralPath $pkgU -Raw) -match 'arm A' -and -not (Test-Path -LiteralPath (Join-Path $kc 'ab\off\ts3native.asi'))) 'back to arm A: asi back byte for byte, arm A package'; if ($code) { Write-Host $o }
    $st = Get-Content -LiteralPath $state -Raw | ConvertFrom-Json
    Check (@($st.sessions).Count -eq 4 -and (@($st.sessions | ForEach-Object { $_.arm }) -join '') -eq 'ABBA' -and $st.route -eq 'Direct') 'state: 4 sessions A B B A, the launch route kept'
    # benchmark arms (docs/315): TS3Bench sets each arm through -Mode ArmApply, no game start, no session counted
    $o = Run @('-Mode', 'ArmApply', '-NoSystem', '-Yes', '-Arm', 'B', '-GameDir', $game, '-UserDir', $user); $c1 = $code
    $bOk = if ($real.arms.B.off) { -not (Test-Path -LiteralPath $asiB) } else { (Get-Content -LiteralPath $pkgU -Raw) -match 'arm B' }
    $o = Run @('-Mode', 'ArmApply', '-NoSystem', '-Yes', '-Arm', 'A', '-GameDir', $game, '-UserDir', $user)
    Check ($c1 -eq 0 -and $code -eq 0 -and $bOk -and (FileSha $asiB) -eq $asiSha -and (Get-Content -LiteralPath $pkgU -Raw) -match 'arm A' -and @((Get-Content -LiteralPath $state -Raw | ConvertFrom-Json).sessions).Count -eq 4) '-Mode ArmApply: arm B then A set the files, no session counted'; if ($code) { Write-Host $o }
    $o = Run @('-Mode', 'Diag', '-NoSystem', '-GameDir', $game, '-UserDir', $user, '-OutDir', $out)
    $zip = @(Get-ChildItem -LiteralPath $out -Filter 'TS3Diag-*.zip' | Sort-Object LastWriteTime -Descending)
    if ($zip.Count) { $x = Unzip $zip[0].FullName; $rt = Get-Content -LiteralPath (Join-Path $x 'report.txt') -Raw; Check (@(Get-ChildItem -LiteralPath (Join-Path $x 'ab\results') -Filter '*.txt').Count -eq 4 -and (Test-Path -LiteralPath (Join-Path $x 'ab\state.json')) -and $rt -match 'A/B mode: 4 session summaries .*arm A: 2, arm B: 2.*next arm B') 'diagnose zip: all 4 summaries, state and the A/B line'
      # the installed payload ini is the kit's own text (its comments name the maintainer), not the player's
      $leak = @(Get-ChildItem -LiteralPath $x -Recurse -File | Where-Object { (FileSha $_.FullName) -ne (FileSha (Join-Path $kc 'payload\ts3native.ini')) } | Where-Object { Select-String -LiteralPath $_.FullName -Pattern ([regex]::Escape($env:USERNAME)), ([regex]::Escape($env:COMPUTERNAME)), ([regex]::Escape($env:USERPROFILE)) -Quiet })
      Check ($leak.Count -eq 0) "no user name / home path / computer name in the A/B zip ($($leak.Name -join ', '))"; Remove-Item -LiteralPath $x -Recurse -Force } else { Check $false 'diagnose zip written' }
    if ($real.arms.B.off) { $o = Run ($play + @('-Arm', 'B')); Check ($code -eq 0 -and -not (Test-Path -LiteralPath $asiB)) 'arm B again before the uninstall (asi in ab\off)' }
    $o = Run @('-Mode', 'Uninstall', '-NoSystem', '-Yes', '-GameDir', $game, '-UserDir', $user)
    $why = @(); if ($code) { $why += "exit $code" }; if (Test-Path -LiteralPath $asiB) { $why += 'asi still there' }
    if ((Get-Content -LiteralPath $pkgU -Raw) -notmatch 'old fixes') { $why += 'package: ' + (Get-Content -LiteralPath $pkgU -Raw) }
    if ((Get-Content -LiteralPath (Join-Path $bin 'ts3native.ini') -Raw) -notmatch 'Mine=1') { $why += 'ini not the old one' }
    if (Test-Path -LiteralPath (Join-Path $kc 'ab\off')) { $why += 'ab\off: ' + (@(Get-ChildItem -LiteralPath (Join-Path $kc 'ab\off') -Force).Name -join ',') }
    if ((Get-Content -LiteralPath $state -Raw | ConvertFrom-Json).next -ne 'A') { $why += 'next ' + (Get-Content -LiteralPath $state -Raw | ConvertFrom-Json).next }
    if ($why.Count) { Write-Host ('  uninstall: ' + ($why -join '; ')); Write-Host $o }
    Check ($code -eq 0 -and -not (Test-Path -LiteralPath $asiB) -and (Get-Content -LiteralPath $pkgU -Raw) -match 'old fixes' -and (Get-Content -LiteralPath (Join-Path $bin 'ts3native.ini') -Raw) -match 'Mine=1' -and -not (Test-Path -LiteralPath (Join-Path $kc 'ab\off')) -and (Get-Content -LiteralPath $state -Raw | ConvertFrom-Json).next -eq 'A') 'uninstall after A/B sessions: old package and ini back, kit asi gone, ab\off cleared, next arm A'; if ($code) { Write-Host $o }
  }
} finally {
  if (-not $Keep -and $sb -like '*ts3kit-sandbox-*' -and (Test-Path -LiteralPath $sb)) { Remove-Item -LiteralPath $sb -Recurse -Force }
}
if ($fails) { Write-Host "test-kit: $fails FAIL" -ForegroundColor Red; exit 1 } else { Write-Host 'test-kit: 0 FAIL' }
