# TS3 kit for testers: a read-only diagnostic (writes one zip report) and an optional installer / uninstaller for the
# ts3native retail 1.67 test build that sits in ..\payload.
# Why PowerShell and not Node like the rest of this repo: a player has no Node, and Windows PowerShell 5.1 ships with
# every Windows 10/11. Windows only by nature (registry, The Sims 3 PC folders). No network, no upload, no AI.
#   TS3Kit.bat                                   menu (1 diagnose, 2 launch and capture, 3 install, 4 uninstall, 5 quit)
#   TS3Kit.bat -Mode Diag                        diagnose only, no questions; zip lands on the Desktop
#   TS3Kit.bat -Mode Diag -OutDir D:\x           zip lands in D:\x
#   TS3Kit.bat -Mode Capture -GameDir "..."     start the game the way the player does (asks: his shortcuts, launcher,
#                                                Steam, EA app; TS3W.exe directly only as a last, warned choice), wait until
#                                                it exits, then diagnose with a before/after comparison of every settings
#                                                file (-Launch Direct|Steam|Launcher|Self skips the question)
#   TS3Kit.bat -Mode Install -GameDir "D:\Games\The Sims 3 copy"     (-Yes skips the question, -S3SS adds the S3SS tunings,
#                                                -Build Normal|Diag picks the build when payload\diag exists)
#   TS3Kit.bat -Mode Uninstall -GameDir "..."
#   -UserDir "<Documents>\Electronic Arts\The Sims 3"   use this user folder
#   -NoSystem      look only at -GameDir / -UserDir (no registry, no system, no event log); used by the tests
#   -DocsDir <dir> tests only: a fake Documents folder whose Electronic Arts\* folders are searched like the real one
#   -Mode SelfTest checks of the merge / mask / exe-header code on temp data, then exit
# The diagnostic never writes outside its zip (and a temp staging folder it removes). The installer writes only
# Game\Bin\ts3native.asi, Game\Bin\ts3native.ini, Mods\Packages\ts3fixes.package (and Mods\Resource.cfg when there is
# none) and (if asked) S3SS\S3SS.toml, after a backup. TS3W.exe and Options.ini are never changed.
param(
  [ValidateSet('Menu', 'Diag', 'Capture', 'Install', 'Uninstall', 'SelfTest')][string]$Mode = 'Menu',
  [ValidateSet('', 'Direct', 'Steam', 'Launcher', 'Self')][string]$Launch = '', [int]$CaptureMaxSec = 0, [int]$InfoAfterSec = 20,
  [string[]]$ProcNames = @('TS3W', 'TS3'), [string]$DirectExe = '',   # tests only: a stub under another name, so no fake TS3W process shows up
  [string]$LinkDir = '', [string]$Answers = '',   # tests only: a folder of .lnk files instead of Desktop / Start Menu; comma-separated answers to the questions
  [ValidateSet('', 'Normal', 'Diag')][string]$Build = '',
  [string]$GameDir = '', [string]$UserDir = '', [string]$OutDir = '', [switch]$Yes, [switch]$S3SS, [switch]$NoSystem,
  [string]$DocsDir = ''   # tests only: stands in for the Documents known folder (with -NoSystem)
)
$ErrorActionPreference = 'Stop'
$KitVersion = '1119.2'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$payload = Join-Path (Split-Path -Parent $here) 'payload'
$CapBytes = 5MB          # copied text files above this are listed, not copied
$HashCapBytes = 256MB    # Bin files above this are listed without a hash
$Days = 14
# The retail test build of kit 1119: retail167-test-1118 basis (b666b151a) + [EaCrashFixes] KeepOptionsOnGpuChange
# (0.36.276-retail1-ko1); ini = that tree's defaults with personal names scrubbed; ts3fixes.package = v0.2.13-exp.20261118.
$RcAsiSha = 'a2ab9cf226f318a6de7a7767382150acf6536b9adde44661fe9cf9c6b8fea199'
$RcIniSha = '86b7d521eea54ecea76673ec7395df56208b267b3860bbe8faf11ee957fc1f01'
$RcPkgSha = '5deb4c3d920d29c08c64bd47b9879e714c8ddf02c36436255924e5f367421842'
$RcTomlSha = '706ad37b61382a28935740087cb3e07e844eadce485880b1b9c65ea5cd4569aa'   # payload\S3SS-tunings.toml, LF line ends
$RunNames = @($ProcNames) + @('Sims3Launcher', 'Sims3LauncherW')
$Editions = @{ '52D872DA/E5B000' = 'retail 1.67'; '52DEC247/E5A000' = 'Steam 1.67'; '6707155C/EB5000' = 'EA app 1.69' }
# Every regex the kit uses, by name; SelfTest runs each against a real matching and a real non-matching line (AGENTS.md).
$Rx = [ordered]@{
  MaskOtherUser = '(?i)\b([A-Z]:(?:\\\\|\\|/)Users(?:\\\\|\\|/))(?!Public\b|Default\b|All Users\b|<)[^\\/"<>|\r\n]+(?=\\|/)'
  AnswerSep = ','
  FileNameBad = '[<>%]'
  IniSection = '^\s*\[([^\]]+)\]\s*$'
  IniKeyValue = '^\s*([A-Za-z0-9_\-]+)\s*=\s*(.+?)\s*$'
  IniKey = '^\s*([A-Za-z0-9_\-]+)\s*='
  SimsWord = 'Sims'
  Sims3Name = 'Sims 3'
  UninstallSims3 = 'Sims.{0,3}3'
  VdfPath = '"path"\s+"([^"]+)"'
  DocsDir = '^(Documents|OneDrive.*)$'
  UserSims3Dir = 'Sims\D{0,2}3'
  LastPathPart = '\\[^\\]+$'
  Ts3OrSims = 'TS3|Sims'
  VcRedist = 'Visual C\+\+.*(Redist|Runtime)'
  GameEvent = 'TS3W|TS3\.exe|Sims3'
  Ws = '\s+'
  Ts3wExe = 'TS3W?\.exe'
  RunAsAdmin = 'RUNASADMIN'
  ProgramFiles = '(?i)\\Program Files'
  LoaderDll = '^(dinput8|dinput|wininet|winmm|version|d3d9|dsound|msacm32|msvfw32|winhttp|xlive|binkw32)\.dll$'
  ApexAsi = '^ApexRadiance.*\.asi$'
  S3ssFile = 'Sims3SettingsSetter|^S3SS'
  S3ppFile = 'Sims3Performance'
  FixesVersion = 'ts3fixes (\d+\.\d+\.\d+[^\s,]*)'
  LogNote = '^(SKIP |compat:|ts3native 0\.)|ts3fixes \d|unsupported|stands? down|refused(?!\s*\d)'
  VsExt = '^\.(ini|log|txt|toml|conf|cfg)$'
  BinExt = '^\.(ini|toml|conf|cfg|log|jsonl)$'
  ApexTxt = '^(Turbo|Apex Radiance.*)\.txt$'
  RemixExt = '^\.(log|conf|txt)$'
  OptKeyValue = '^\s*([A-Za-z0-9_]+)\s*=\s*(.*)$'
  DcNumber = '(?m)^Number:\s*(\d+)'
  DcNameDriver = '(?m)^Name \(driver\):\s*(.+?)\s*$'
  DcNameDb = '(?m)^Name \(database\):\s*(.+?)\s*$'
  DcVendorDevice = 'Vendor:\s*([0-9a-fA-F]{4}),\s*Device:\s*([0-9a-fA-F]{4})'
  DcDriverVersion = '(?m)^Driver version:\s*(\d+)'
  DcTextureMemory = '(?m)^Texture memory:\s*(.+?)\s*$'
  DcMatched = 'Matched:\s*1'
  LastDeviceSep = ';'
  UserCfgExt = '^\.(toml|ini|txt|log|json|jsonl|bak)$'
  CrashFile = '^xcpt|\.mdmp$|\.dmp$'
  NotPlayUser = 'Medieval|Create a (Pattern|World) Tool|Backup'
  RunExt = '(?i)\.(exe|bat|cmd)$'
  NotGameExe = '(?i)unins|setup|Create.?a.?(World|Pattern)|\bCAW\b|\bCAP\b'
  ClientExe = '(?i)^(steam|Origin|EADesktop|EALauncher|EALaunchHelper)\.exe$'
  ClientArgs = '(?i)47890|Sims\W?3(?!\d)'
  SteamApps = '(?i)\\steamapps\\'
  OriginName = 'Origin'
  Yes = '^[Yy]'
  Number = '^\d+$'
  TomlKeyValue = '^\s*([A-Za-z0-9_\-]+)\s*=\s*(.*?)\s*$'
  WindowsDir = '(?i)\\Windows\\'
  CfgFile = '\.(ini|toml|conf|cfg)$'
  IniTomlFile = '\.(ini|toml)$'
  CollectKey = '^[cC]$'
  SessionFile = '\.(ini|toml|conf|cfg|log|jsonl|txt|xml)$'
  LastDeviceChange = '(^|.)lastdevice '
  CopyName = '(?i)backup|\bbak\b|\bcop(y|ia|ie)\b|kopie|\bold\b|\(\d+\)'
  Diag = '^[Dd]'
  LoaderDllInstall = '^(dinput8|dinput|wininet|winmm|version|d3d9|dsound|winhttp)\.dll$'
  FixesPkg = '(?i)ts3fixes'
  PkgVersion = '\b0\.\d{1,3}\.\d{1,4}(-[A-Za-z0-9]+)*'
  GameBinDir = '(?i)\\Game\\Bin$'
  S3ppOff = '(?m)(^\[patches\.(?:AdaptiveWait|OversizedThreadStackFix)\]\r?\nenabled = )true'
  Eol = "`r?`n"
  SnapBinFile = '\.(ini|toml|conf|cfg|log|jsonl|txt|xml|dmp)$'
  SnapRemixFile = '\.(log|conf|txt)$'
  Any = '.'
}

# ---------- masking ----------
$maskExact = New-Object System.Collections.ArrayList   # [regex, replacement], longest first
function AddMaskPath([string]$p, [string]$rep) {
  if (-not $p -or $p.Length -lt 4) { return }
  $p = $p.TrimEnd('\')
  foreach ($v in @($p, $p.Replace('\', '\\'), $p.Replace('\', '/'))) { [void]$maskExact.Add(@([regex]::Escape($v), $rep)) }
}
function AddMaskWord([string]$w, [string]$rep) {
  if (-not $w -or $w.Length -lt 3) { return }   # a 1-2 letter name would eat normal text; its paths are still masked
  [void]$maskExact.Add(@(('(?<![A-Za-z0-9])' + [regex]::Escape($w) + '(?![A-Za-z0-9])'), $rep))
}
function InitMask([string]$profile, [string]$user, [string]$pc, [string]$sid) {
  $maskExact.Clear()
  AddMaskPath $profile '%USERPROFILE%'
  AddMaskWord $sid '<sid>'
  AddMaskWord $user '<user>'
  AddMaskWord $pc '<pc>'
}
function Mask([string]$s) {
  if (-not $s) { return $s }
  foreach ($m in $maskExact) { $s = [regex]::Replace($s, $m[0], $m[1], 'IgnoreCase') }
  # other profiles: X:\Users\<name>\...  (the name ends at the next separator)
  $s = [regex]::Replace($s, $Rx.MaskOtherUser, '${1}<other-user>')
  return $s
}

# ---------- small helpers ----------
$T = New-Object System.Collections.ArrayList        # report.txt lines
$R = [ordered]@{}                                   # report.json
$Findings = New-Object System.Collections.ArrayList
$Copied = New-Object System.Collections.ArrayList
$Skipped = New-Object System.Collections.ArrayList
function Say([string]$s, [string]$color = '') { $m = Mask $s; if ($color) { Write-Host $m -ForegroundColor $color } else { Write-Host $m } }
function Line([string]$s) { [void]$T.Add($s); Say $s }
function Finding([int]$rank, [string]$id, [string]$text) { [void]$Findings.Add([ordered]@{ rank = $rank; id = $id; text = $text }) }
function Sec([string]$name, [scriptblock]$body) {
  Line ''; Line "== $name"
  try { & $body } catch { Line "  (section failed: $($_.Exception.Message))" }
}
function Try1([scriptblock]$b, $default = $null) { try { & $b } catch { $default } }
$AnswerQ = New-Object System.Collections.Queue; if ($Answers) { foreach ($a in ($Answers -split $Rx.AnswerSep)) { $AnswerQ.Enqueue($a) } }
function Ask([string]$q) { if ($AnswerQ.Count) { $a = "$($AnswerQ.Dequeue())"; Say "$q $a"; return $a }; return (Read-Host $q) }
function Sha([string]$p) { Try1 { (Get-FileHash -Algorithm SHA256 -LiteralPath $p).Hash.ToLower() } '?' }
function TextSha([string]$p) { Try1 { $t = [System.IO.File]::ReadAllText($p) -replace $Rx.Eol, "`n"; ([System.BitConverter]::ToString([System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.UTF8Encoding]::new($false).GetBytes($t))) -replace '-', '').ToLower() } '?' }
function Sha12([string]$p) { $h = Sha $p; if ($h.Length -ge 12) { $h.Substring(0, 12) } else { $h } }
function Stamp($d) { if ($d) { ([datetime]$d).ToString('yyyy-MM-dd HH:mm:ss') } else { '' } }
function FileRow([string]$p, [bool]$hash = $true) {
  $f = Get-Item -LiteralPath $p -Force
  $h = ''
  if ($hash) { if ($f.Length -le $HashCapBytes) { $h = Sha12 $p } else { $h = 'too-big' } }
  [ordered]@{ name = $f.Name; size = $f.Length; modified = (Stamp $f.LastWriteTime); sha12 = $h; readOnly = [bool]$f.IsReadOnly }
}
function DirSize([string]$p) {
  $sum = 0; $n = 0
  Get-ChildItem -LiteralPath $p -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object { $sum += $_.Length; $n++ }
  [ordered]@{ bytes = $sum; files = $n }
}
function PeInfo([string]$p) {
  $fs = New-Object System.IO.FileStream($p, 'Open', 'Read', 'ReadWrite')
  try { $b = New-Object byte[] 4096; $n = $fs.Read($b, 0, 4096) } finally { $fs.Close() }
  if ($n -lt 0x100 -or $b[0] -ne 0x4D -or $b[1] -ne 0x5A) { throw 'not an exe' }
  $e = [BitConverter]::ToInt32($b, 0x3C)
  if ($e -lt 0 -or $e + 84 -gt $n -or $b[$e] -ne 0x50 -or $b[$e + 1] -ne 0x45) { throw 'no PE header' }
  $ts = [BitConverter]::ToUInt32($b, $e + 8); $ch = [BitConverter]::ToUInt16($b, $e + 22); $soi = [BitConverter]::ToUInt32($b, $e + 24 + 56)
  $key = ('{0:X8}/{1:X}' -f $ts, $soi)
  $ed = $Editions[$key]; if (-not $ed) { $ed = 'unknown build' }
  [ordered]@{ peTimestamp = ('{0:X8}' -f $ts); sizeOfImage = ('0x{0:X}' -f $soi); largeAddressAware = [bool]($ch -band 0x20); edition = $ed }
}
function CanOpenWrite([string]$p) {   # opens an existing file for writing and closes it again: nothing is changed
  try { $fs = [System.IO.File]::Open($p, 'Open', 'ReadWrite', 'ReadWrite'); $fs.Close(); return 'yes' } catch [System.UnauthorizedAccessException] { return 'no (access denied)' } catch { return "unknown ($($_.Exception.GetType().Name))" }
}
function IsAdmin { Try1 { ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator) } $false }
function RegProps([string]$key) { Try1 { $o = Get-ItemProperty -LiteralPath $key -ErrorAction Stop; $h = [ordered]@{}; $o.PSObject.Properties | Where-Object { $_.Name -notlike 'PS*' } | ForEach-Object { $h[$_.Name] = "$($_.Value)" }; $h } $null }

# Copies a text file into the staging folder, masked. Too big or unreadable: listed instead.
function CopyText([string]$src, [string]$rel) {
  try {
    $f = Get-Item -LiteralPath $src -Force
    if ($f.Length -gt $CapBytes) { [void]$Skipped.Add([ordered]@{ file = $src; size = $f.Length; why = "over $([int]($CapBytes / 1MB)) MB" }); return }
    $fs = New-Object System.IO.FileStream($src, 'Open', 'Read', 'ReadWrite')
    try { $sr = New-Object System.IO.StreamReader($fs, [System.Text.Encoding]::UTF8, $true); $txt = $sr.ReadToEnd() } finally { $fs.Close() }
    $rel = (Mask $rel) -replace $Rx.FileNameBad, '_'   # the masks' own <> are not allowed in file names
    $dst = Join-Path $script:stage $rel
    [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dst))
    [System.IO.File]::WriteAllText($dst, (Mask $txt), (New-Object System.Text.UTF8Encoding($false)))
    [void]$Copied.Add([ordered]@{ zipPath = $rel.Replace('\', '/'); from = $src; size = $f.Length; modified = (Stamp $f.LastWriteTime) })
  } catch { [void]$Skipped.Add([ordered]@{ file = $src; size = -1; why = $_.Exception.Message }) }
}
function CopyNewest([string]$dir, [string]$filter, [int]$max, [string]$relDir) {
  if (-not (Test-Path -LiteralPath $dir)) { return }
  Get-ChildItem -LiteralPath $dir -Filter $filter -File -Force -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First $max | ForEach-Object { CopyText $_.FullName (Join-Path $relDir $_.Name) }
}

# ---------- S3SS.toml merge (line based: sets our keys, keeps every other line) ----------
function ParseToml([string]$text) {
  $d = [ordered]@{}; $sec = ''
  foreach ($l in ($text -split $Rx.Eol)) {
    if ($l -match $Rx.IniSection) { $sec = $Matches[1].Trim(); if (-not $d.Contains($sec)) { $d[$sec] = [ordered]@{} } }
    elseif ($sec -and $l -match $Rx.IniKeyValue) { $d[$sec][$Matches[1]] = $Matches[2] }
  }
  return $d
}
# With the Sims 3 Performance Patch, S3SS AdaptiveWait and OversizedThreadStackFix must be off (S3PP's ShortWaitSpin and its
# thread stack change do the same job, docs/07 §4 and §5). Written as enabled = false, so a value the player had on goes off too.
function S3ppTune([string]$tune) { [regex]::Replace($tune, $Rx.S3ppOff, '${1}false') }
function MergeToml([string]$existing, [string]$tuning) {
  $want = ParseToml $tuning
  $out = New-Object System.Collections.ArrayList
  $sec = ''; $done = @{}
  $flush = {
    if ($sec -and $want.Contains($sec)) {
      $ins = $out.Count; while ($ins -gt 0 -and "$($out[$ins - 1])".Trim() -eq '') { $ins-- }   # before the blank lines that end the section
      foreach ($k in $want[$sec].Keys) { if (-not $done["$sec|$k"]) { $out.Insert($ins, "$k = $($want[$sec][$k])"); $ins++; $done["$sec|$k"] = $true } }
    }
  }
  $lines = @(); if ($existing) { $lines = $existing -split $Rx.Eol }
  foreach ($l in $lines) {
    if ($l -match $Rx.IniSection) { & $flush; $sec = $Matches[1].Trim(); $done["sec|$sec"] = $true; [void]$out.Add($l); continue }
    if ($sec -and $want.Contains($sec) -and $l -match $Rx.IniKey) {
      $k = $Matches[1]
      if ($want[$sec].Contains($k)) { [void]$out.Add("$k = $($want[$sec][$k])"); $done["$sec|$k"] = $true; continue }
    }
    [void]$out.Add($l)
  }
  & $flush
  foreach ($s in $want.Keys) {
    if ($done["sec|$s"]) { continue }
    if ($out.Count -gt 0 -and "$($out[$out.Count - 1])".Trim() -ne '') { [void]$out.Add('') }
    [void]$out.Add("[$s]"); foreach ($k in $want[$s].Keys) { [void]$out.Add("$k = $($want[$s][$k])") }
  }
  return (($out -join "`r`n").TrimEnd() + "`r`n")
}

# ---------- find game installs and user folders ----------
function HasExe([string]$root) { $root -and ((Test-Path -LiteralPath (Join-Path $root 'Game\Bin\TS3W.exe')) -or (Test-Path -LiteralPath (Join-Path $root 'Game\Bin\TS3.exe'))) }
function FindGames {
  $found = [ordered]@{}; $reg = New-Object System.Collections.ArrayList
  $add = { param($p, $src) if (-not $p) { return }; $p = "$p".Trim('"').TrimEnd('\'); if (-not $p) { return }
    $k = $p.ToLower(); if ($found.Contains($k)) { $found[$k].sources += $src } else { $found[$k] = [ordered]@{ path = $p; sources = @($src); exists = (HasExe $p) } } }
  if ($GameDir) { & $add $GameDir 'given on the command line' }
  $up = $here; for ($i = 0; $i -lt 5 -and $up; $i++) { if (HasExe $up) { & $add $up 'kit folder is inside it' }; $up = Split-Path -Parent $up }
  if (-not $NoSystem) {
    foreach ($base in 'HKLM:\SOFTWARE\WOW6432Node\Sims', 'HKLM:\SOFTWARE\Sims', 'HKLM:\SOFTWARE\WOW6432Node\Electronic Arts\Sims', 'HKLM:\SOFTWARE\Electronic Arts\Sims', 'HKLM:\SOFTWARE\WOW6432Node\Origin Games', 'HKLM:\SOFTWARE\WOW6432Node\EA Games') {
      foreach ($sk in @(Try1 { Get-ChildItem -LiteralPath $base -ErrorAction Stop } @())) {
        $p = RegProps $sk.PSPath; if (-not $p) { continue }
        $dir = $null; foreach ($n in 'Install Dir', 'InstallDir', 'Install Location', 'InstallLocation') { if ($p[$n]) { $dir = $p[$n]; break } }
        if (-not $dir -or ($sk.PSChildName -notmatch $Rx.SimsWord) -and ($dir -notmatch $Rx.Sims3Name)) { continue }
        [void]$reg.Add([ordered]@{ key = "$base\$($sk.PSChildName)".Replace('HKLM:\', 'HKLM\'); installDir = $dir; onDisk = (Test-Path -LiteralPath $dir); values = $p })
        if ($sk.PSChildName -eq 'The Sims 3' -or (HasExe $dir)) { & $add $dir "registry $($sk.PSChildName)" }
      }
    }
    foreach ($u in 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall', 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall') {
      foreach ($sk in @(Try1 { Get-ChildItem -LiteralPath $u -ErrorAction Stop } @())) {
        $p = Try1 { Get-ItemProperty -LiteralPath $sk.PSPath -ErrorAction Stop } $null
        if ($p -and "$($p.DisplayName)" -match $Rx.UninstallSims3) {
          [void]$reg.Add([ordered]@{ key = "Uninstall\$($sk.PSChildName)"; displayName = "$($p.DisplayName)"; version = "$($p.DisplayVersion)"; installDir = "$($p.InstallLocation)" })
          if (HasExe "$($p.InstallLocation)") { & $add "$($p.InstallLocation)" 'uninstall entry' }
        }
      }
    }
    $steam = @(); foreach ($k in 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam', 'HKCU:\Software\Valve\Steam') { $p = RegProps $k; if ($p) { $steam += $p['InstallPath']; $steam += $p['SteamPath'] } }
    $libs = @()
    foreach ($s in ($steam | Where-Object { $_ } | ForEach-Object { $_.Replace('/', '\') } | Select-Object -Unique)) {
      $libs += $s; $vdf = Join-Path $s 'steamapps\libraryfolders.vdf'
      if (Test-Path -LiteralPath $vdf) { foreach ($l in (Get-Content -LiteralPath $vdf)) { if ($l -match $Rx.VdfPath) { $libs += $Matches[1].Replace('\\', '\') } } }
    }
    foreach ($l in ($libs | Select-Object -Unique)) { $g = Join-Path $l 'steamapps\common\The Sims 3'; if (HasExe $g) { & $add $g 'Steam library' } }
    foreach ($d in @(Try1 { [System.IO.DriveInfo]::GetDrives() | Where-Object { $_.DriveType -eq 'Fixed' -and $_.IsReady } } @())) {
      foreach ($rel in 'Program Files (x86)\Electronic Arts\The Sims 3', 'Program Files\Electronic Arts\The Sims 3', 'Program Files (x86)\Origin Games\The Sims 3', 'Program Files\EA Games\The Sims 3', 'Program Files (x86)\EA Games\The Sims 3', 'Games\The Sims 3', 'The Sims 3') {
        $g = Join-Path $d.Name $rel; if (HasExe $g) { & $add $g 'common path' }
      }
    }
  }
  $R['registryEntries'] = @($reg)
  return @($found.Values)
}
function FindUserDirs {
  $docs = [ordered]@{}; $info = [ordered]@{}
  $addDoc = { param($p, $src) if ($p -and (Test-Path -LiteralPath $p)) { $k = "$p".TrimEnd('\').ToLower(); if (-not $docs.Contains($k)) { $docs[$k] = [ordered]@{ path = "$p".TrimEnd('\'); source = $src } } } }
  $out = New-Object System.Collections.ArrayList; $known = ''
  if ($UserDir) { [void]$out.Add([ordered]@{ path = $UserDir.TrimEnd('\'); docs = 'given on the command line'; expected = $true }) }
  if (-not $NoSystem) {
    $known = [Environment]::GetFolderPath('MyDocuments')
    $usf = RegProps 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders'
    $regDocs = ''; if ($usf -and $usf['Personal']) { $regDocs = [Environment]::ExpandEnvironmentVariables($usf['Personal']) }
    $info['documentsKnownFolder'] = $known
    $info['documentsRegistry'] = $regDocs
    $info['desktopKnownFolder'] = [Environment]::GetFolderPath('Desktop')
    $info['documentsRedirected'] = [bool]($known -and $env:USERPROFILE -and ($known.TrimEnd('\') -ne (Join-Path $env:USERPROFILE 'Documents')))
    $one = @(); foreach ($v in 'OneDrive', 'OneDriveConsumer', 'OneDriveCommercial') { $x = [Environment]::GetEnvironmentVariable($v); if ($x) { $one += $x } }
    $one = @($one | Select-Object -Unique)
    $info['oneDriveRoots'] = $one
    $info['documentsInOneDrive'] = [bool]($one | Where-Object { $known -and $known.ToLower().StartsWith($_.ToLower()) })
    $kfm = RegProps 'HKCU:\Software\Microsoft\OneDrive\Accounts\Personal'
    if ($kfm) { $info['oneDriveKfmState'] = "$($kfm['KfmFoldersProtectedNow'])" }
    & $addDoc $known 'Documents known folder (the one the game uses)'
    & $addDoc $regDocs 'Documents in the registry'
    if ($env:USERPROFILE) { & $addDoc (Join-Path $env:USERPROFILE 'Documents') 'profile Documents' }
    foreach ($o in $one) { foreach ($c in @(Try1 { Get-ChildItem -LiteralPath $o -Directory -Force -ErrorAction Stop } @())) { if (Test-Path -LiteralPath (Join-Path $c.FullName 'Electronic Arts')) { & $addDoc $c.FullName 'OneDrive folder' } } }
    $usersRoot = Split-Path -Parent "$env:USERPROFILE"
    foreach ($u in @(Try1 { Get-ChildItem -LiteralPath $usersRoot -Directory -Force -ErrorAction Stop } @())) {
      if ($u.FullName -eq $env:USERPROFILE) { continue }
      foreach ($c in @(Try1 { Get-ChildItem -LiteralPath $u.FullName -Directory -Force -ErrorAction Stop | Where-Object { $_.Name -match $Rx.DocsDir } } @())) {
        if (Test-Path -LiteralPath (Join-Path $c.FullName 'Electronic Arts')) { & $addDoc $c.FullName 'another Windows user' }
        foreach ($c2 in @(Try1 { Get-ChildItem -LiteralPath $c.FullName -Directory -Force -ErrorAction Stop } @())) { if (Test-Path -LiteralPath (Join-Path $c2.FullName 'Electronic Arts')) { & $addDoc $c2.FullName 'another Windows user (OneDrive)' } }
      }
    }
  }
  if ($DocsDir) { $known = $DocsDir; & $addDoc $DocsDir 'Documents given by -DocsDir (tests)' }
  if ($docs.Count) {
    foreach ($d in $docs.Values) {
      $ea = Join-Path $d.path 'Electronic Arts'
      foreach ($c in @(Try1 { Get-ChildItem -LiteralPath $ea -Directory -Force -ErrorAction Stop } @())) {
        # localized folder names: The Sims 3, Die Sims 3, Les Sims 3, Los Sims 3, De Sims 3; names in other scripts are
        # caught by the game's own files (The Sims 4 has neither)
        $looks = ($c.Name -match $Rx.UserSims3Dir) -or (Test-Path -LiteralPath (Join-Path $c.FullName 'DeviceConfig.log')) -or (Test-Path -LiteralPath (Join-Path $c.FullName 'DCCache'))
        if ($looks -and -not ($out | Where-Object { $_.path -eq $c.FullName })) { [void]$out.Add([ordered]@{ path = $c.FullName; docs = $d.source; expected = ($d.path -eq "$known".TrimEnd('\')) }) }
      }
    }
  }
  $R['documents'] = $info
  return @($out)
}

# ---------- diagnostic sections ----------
function DiagSystem {
  $s = [ordered]@{}
  Sec 'System' {
    $os = Get-CimInstance Win32_OperatingSystem
    $cv = RegProps 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $s['os'] = "$($os.Caption) $($os.OSArchitecture)"; $s['build'] = "$($os.BuildNumber).$($cv['UBR'])"; $s['displayVersion'] = "$($cv['DisplayVersion'])"; $s['locale'] = (Get-Culture).Name; $s['uiLanguage'] = (Get-UICulture).Name
    $s['elevated'] = (IsAdmin)
    $cpu = @(Get-CimInstance Win32_Processor)[0]
    $s['cpu'] = "$($cpu.Name)".Trim(); $s['cores'] = $cpu.NumberOfCores; $s['threads'] = $cpu.NumberOfLogicalProcessors
    $s['ramGB'] = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1); $s['ramFreeGB'] = [math]::Round($os.FreePhysicalMemory / 1MB, 1)
    $s['pageFiles'] = @(Try1 { Get-CimInstance Win32_PageFileUsage | ForEach-Object { [ordered]@{ file = $_.Name; allocatedMB = $_.AllocatedBaseSize; usedMB = $_.CurrentUsage; peakMB = $_.PeakUsage } } } @())
    $s['pageFileAuto'] = Try1 { [bool](Get-CimInstance Win32_ComputerSystem).AutomaticManagedPagefile } $null
    $s['gpus'] = @(Get-CimInstance Win32_VideoController | ForEach-Object { [ordered]@{ name = $_.Name; driverVersion = $_.DriverVersion; driverDate = (Stamp $_.DriverDate); vramMB_wmi = [int]($_.AdapterRAM / 1MB); pnp = "$($_.PNPDeviceID)" -replace $Rx.LastPathPart, ''; resolution = "$($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution)@$($_.CurrentRefreshRate)"; status = $_.Status } })
    $s['gpuCount'] = $s['gpus'].Count
    $s['monitors'] = @(Try1 { Get-CimInstance Win32_DesktopMonitor | ForEach-Object { "$($_.Name) $($_.ScreenWidth)x$($_.ScreenHeight)" } } @())
    $dpi = RegProps 'HKCU:\Control Panel\Desktop\WindowMetrics'; if ($dpi) { $s['appliedDpi'] = $dpi['AppliedDPI'] }
    $s['battery'] = [bool](Try1 { @(Get-CimInstance Win32_Battery).Count } 0)
    $s['powerPlan'] = Try1 { (& powercfg.exe /getactivescheme 2>$null | Out-String).Trim() } ''
    $gp = RegProps 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
    $s['gpuPreferences'] = @(); if ($gp) { $s['gpuPreferences'] = @($gp.Keys | Where-Object { $_ -match $Rx.Ts3OrSims } | ForEach-Object { "$_ = $($gp[$_])" }) }
    Line "  $($s.os) build $($s.build) $($s.displayVersion); elevated: $($s.elevated); locale $($s.locale)"
    Line "  CPU $($s.cpu) ($($s.cores)c/$($s.threads)t); RAM $($s.ramGB) GB ($($s.ramFreeGB) free); page file auto: $($s.pageFileAuto)"
    foreach ($g in $s.gpus) { Line "  GPU $($g.name), driver $($g.driverVersion) ($($g.driverDate)), $($g.resolution)" }
    Line "  DPI $($s.appliedDpi); battery: $($s.battery); power plan: $($s.powerPlan)"
    foreach ($g in $s.gpuPreferences) { Line "  Windows GPU preference: $g" }
    if ($s.gpuCount -gt 1 -and $s.gpuPreferences.Count -eq 0) { Line '  more than one GPU and no per-app GPU preference set for the game' }
  }
  Sec 'Runtimes' {
    $rt = [ordered]@{}
    $sys32 = Join-Path $env:WINDIR 'System32'; $wow = Join-Path $env:WINDIR 'SysWOW64'
    $ver = { param($p) if (Test-Path -LiteralPath $p) { (Get-Item -LiteralPath $p).VersionInfo.FileVersion } else { 'missing' } }
    $rt['d3d9_32bit'] = & $ver (Join-Path $wow 'd3d9.dll')
    $rt['d3dx9_32bit'] = @(Try1 { Get-ChildItem -LiteralPath $wow -Filter 'd3dx9_*.dll' -ErrorAction Stop | ForEach-Object { $_.Name } } @())
    $rt['vulkan_64bit'] = & $ver (Join-Path $sys32 'vulkan-1.dll'); $rt['vulkan_32bit'] = & $ver (Join-Path $wow 'vulkan-1.dll')
    $vc = @(); $net = [ordered]@{}
    foreach ($u in 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall') {
      foreach ($sk in @(Try1 { Get-ChildItem -LiteralPath $u -ErrorAction Stop } @())) { $p = Try1 { Get-ItemProperty -LiteralPath $sk.PSPath -ErrorAction Stop } $null; if ($p -and "$($p.DisplayName)" -match $Rx.VcRedist) { $vc += "$($p.DisplayName) $($p.DisplayVersion)" } }
    }
    $rt['vcRedists'] = @($vc | Sort-Object -Unique)
    $n4 = RegProps 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full'; if ($n4) { $net['v4'] = "$($n4['Version']) (release $($n4['Release']))" }
    $n35 = RegProps 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5'; if ($n35) { $net['v3.5'] = "$($n35['Version']) install=$($n35['Install'])" } else { $net['v3.5'] = 'not installed' }
    $rt['dotnet'] = $net
    $s['runtimes'] = $rt
    Line "  d3d9 (32-bit) $($rt.d3d9_32bit); d3dx9 dlls: $($rt.d3dx9_32bit.Count); Vulkan 64-bit $($rt.vulkan_64bit), 32-bit $($rt.vulkan_32bit)"
    Line "  VC++ redists: $($rt.vcRedists.Count); .NET v4 $($net['v4']); v3.5 $($net['v3.5'])"
  }
  $R['system'] = $s
}

# Application errors / hangs of the game, graphics driver resets and Defender blocks since a time.
function GameEvents([datetime]$since) {
  $ev = New-Object System.Collections.ArrayList
  foreach ($q in @(@('Application', 'Application Error'), @('Application', 'Application Hang'), @('Application', 'Windows Error Reporting'), @('System', 'nvlddmkm'), @('System', 'Display'), @('System', 'amdkmdag'), @('System', 'igfx'), @('Microsoft-Windows-Windows Defender/Operational', 'Microsoft-Windows-Windows Defender'))) {
    $got = @(Try1 { Get-WinEvent -FilterHashtable @{ LogName = $q[0]; ProviderName = $q[1]; StartTime = $since } -MaxEvents 400 -ErrorAction Stop } @())
    if ($q[0] -eq 'Application') { $got = @($got | Where-Object { $_.Message -match $Rx.GameEvent -or ("$($_.Properties | ForEach-Object { $_.Value })" -match $Rx.GameEvent) }) }
    if ($q[0] -like '*Defender*') { $got = @($got | Where-Object { $_.Id -in 1116, 1117, 1121, 1123, 1127 }) }   # detections and blocks only
    foreach ($e in ($got | Select-Object -First 40)) { $m = "$($e.Message)" -replace $Rx.Ws, ' '; if ($m.Length -gt 700) { $m = $m.Substring(0, 700) }; [void]$ev.Add([ordered]@{ time = (Stamp $e.TimeCreated); log = $q[0]; source = $q[1]; id = $e.Id; text = $m }) }
    Line "  $($q[1]): $($got.Count)"
  }
  return @($ev | Sort-Object { $_.time } -Descending)
}

function DiagSecurity([string[]]$exePaths) {
  $d = [ordered]@{}
  Sec 'Security software and compatibility flags' {
    $mp = Try1 { Get-MpPreference -ErrorAction Stop } $null
    if ($mp) {
      $cfa = @{ 0 = 'off'; 1 = 'ON (blocks)'; 2 = 'audit'; 3 = 'block disk changes only'; 4 = 'audit disk changes only' }["$($mp.EnableControlledFolderAccess)" -as [int]]
      $d['controlledFolderAccess'] = "$cfa"; $d['controlledFolderAccessRaw'] = "$($mp.EnableControlledFolderAccess)"
      $d['cfaAllowedApps'] = @($mp.ControlledFolderAccessAllowedApplications | Where-Object { $_ })
      $d['cfaProtectedFolders'] = @($mp.ControlledFolderAccessProtectedFolders | Where-Object { $_ })
      $d['defenderExclusionPaths'] = @($mp.ExclusionPath | Where-Object { $_ })
      $d['defenderExclusionProcesses'] = @($mp.ExclusionProcess | Where-Object { $_ })
      $d['defenderRealtimeOff'] = [bool]$mp.DisableRealtimeMonitoring
    } else { $d['controlledFolderAccess'] = 'unknown (Defender settings not readable)' }
    $d['antivirusProducts'] = @(Try1 { Get-CimInstance -Namespace 'root\SecurityCenter2' -ClassName AntiVirusProduct -ErrorAction Stop | ForEach-Object { $_.displayName } | Sort-Object -Unique } @())
    $since = (Get-Date).AddDays(-$Days)
    $blocks = @(Try1 { Get-WinEvent -FilterHashtable @{ LogName = 'Microsoft-Windows-Windows Defender/Operational'; Id = 1123, 1127; StartTime = $since } -MaxEvents 300 -ErrorAction Stop } @())
    $d['cfaBlockEvents'] = $blocks.Count
    $ours = @($blocks | Where-Object { $_.Message -match $Rx.Ts3OrSims })
    $d['cfaBlockEventsGame'] = @($ours | Select-Object -First 10 | ForEach-Object { [ordered]@{ time = (Stamp $_.TimeCreated); text = ("$($_.Message)" -replace $Rx.Ws, ' ') } })
    $layers = @()
    foreach ($k in 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers', 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Layers') {
      $p = RegProps $k; if ($p) { foreach ($n in $p.Keys) { if ($n -match $Rx.Ts3OrSims) { $layers += [ordered]@{ hive = $k.Substring(0, 4); exe = $n; flags = $p[$n] } } } }
    }
    $d['compatFlags'] = @($layers)
    Line "  Controlled Folder Access: $($d.controlledFolderAccess); block events in $Days days: $($d.cfaBlockEvents) (game: $($ours.Count))"
    Line "  Antivirus: $($d.antivirusProducts -join ', '); Defender exclusions readable: $([bool]$mp) ($(@($d.defenderExclusionPaths).Count) paths)"
    foreach ($l in $layers) { Line "  compatibility flags $($l.hive) $($l.exe): $($l.flags)" }
    if ($layers.Count -eq 0) { Line '  no compatibility / run-as-admin flags on the game exes' }
    if ("$($d.controlledFolderAccessRaw)" -eq '1') {
      $allowed = [bool]($d.cfaAllowedApps | Where-Object { $_ -match $Rx.Ts3wExe })
      if ($ours.Count -gt 0) { Finding 1 'cfa-blocked' "Windows 'Controlled folder access' is ON and blocked the game $($ours.Count) time(s) in the last $Days days: the game cannot save Options.ini (or saves) into Documents, so settings come back as defaults. Fix: Windows Security > Virus & threat protection > Ransomware protection > Allow an app through Controlled folder access > add TS3W.exe (and TS3.exe / Sims3Launcher.exe)." }
      elseif (-not $allowed) { Finding 2 'cfa-on' "Windows 'Controlled folder access' is ON and TS3W.exe is not in its allowed list: the game may be unable to write Options.ini into Documents. Add TS3W.exe under Ransomware protection > Allow an app." }
    }
    $ra = @($layers | Where-Object { $_.flags -match $Rx.RunAsAdmin } | ForEach-Object { $_.exe } | Select-Object -Unique)   # one finding, not one per exe and hive
    if ($ra.Count -gt 0) { Finding 7 'runasadmin' "The game exe has 'Run as administrator' set ($($ra -join ', ')). If the admin account is another Windows user, the game uses that user's Documents folder and its own settings." }
  }
  Sec "Windows event log (last $Days days)" {
    $d['events'] = @(GameEvents ((Get-Date).AddDays(-$Days)))
  }
  $R['security'] = $d
}

function DiagGame($g, [int]$n) {
  $o = [ordered]@{ id = "game$n"; path = $g.path; sources = @($g.sources); found = [bool]$g.exists }
  Sec "Game install $n" {
    Line "  $($g.path)   [$($g.sources -join '; ')]"
    if (-not $g.exists) { Line '  no Game\Bin\TS3W.exe here (registry points at a folder that is gone or moved)'; Finding 8 'registry-stale' "The registry says a Sims 3 install is in $($g.path), but there is no game there. Tools that trust the registry (launchers, pack installers) look in the wrong place."; return }
    $bin = Join-Path $g.path 'Game\Bin'; $o['bin'] = $bin
    $exes = @()
    foreach ($x in 'TS3W.exe', 'TS3.exe', 'Sims3Launcher.exe', 'Sims3LauncherW.exe') {
      $p = Join-Path $bin $x; if (-not (Test-Path -LiteralPath $p)) { continue }
      $f = Get-Item -LiteralPath $p; $pe = Try1 { PeInfo $p } ([ordered]@{ edition = 'unreadable' })
      $e = [ordered]@{ name = $x; size = $f.Length; modified = (Stamp $f.LastWriteTime); fileVersion = "$($f.VersionInfo.FileVersion)"; productVersion = "$($f.VersionInfo.ProductVersion)"; sha256 = (Sha $p) }
      foreach ($k in $pe.Keys) { $e[$k] = $pe[$k] }
      $exes += $e
      Line "  $x  $($e.fileVersion)  PE $($e.peTimestamp)/$($e.sizeOfImage)  LAA $($e.largeAddressAware)  $($e.edition)  sha $($e.sha256.Substring(0, [math]::Min(12, $e.sha256.Length)))"
    }
    $o['exes'] = @($exes)
    $main = $exes | Where-Object { $_.name -eq 'TS3W.exe' } | Select-Object -First 1
    $o['edition'] = if ($main) { $main.edition } else { 'no TS3W.exe' }
    $sku = Join-Path $bin 'skuversion.txt'; if (Test-Path -LiteralPath $sku) { $o['skuversion'] = (Get-Content -LiteralPath $sku -Raw).Trim(); CopyText $sku "files\game$n-bin\skuversion.txt" }
    $o['underProgramFiles'] = [bool]($bin -match $Rx.ProgramFiles)
    $o['packFolders'] = @(Try1 { Get-ChildItem -LiteralPath (Split-Path -Parent $g.path) -Directory -ErrorAction Stop | Where-Object { $_.Name -match $Rx.Sims3Name } | ForEach-Object { $_.Name } } @())
    $files = @(Get-ChildItem -LiteralPath $bin -File -Force | Sort-Object Name | ForEach-Object { FileRow $_.FullName })
    $o['binFiles'] = $files
    $o['binDirs'] = @(Get-ChildItem -LiteralPath $bin -Directory -Force | ForEach-Object { $z = DirSize $_.FullName; [ordered]@{ name = $_.Name; bytes = $z.bytes; files = $z.files } })
    $names = @($files | ForEach-Object { $_.name })
    $asis = @($files | Where-Object { $_.name -like '*.asi' })
    $loaders = @($names | Where-Object { $_ -match $Rx.LoaderDll })
    $o['asi'] = @($asis | ForEach-Object { $_.name }); $o['possibleAsiLoaders'] = $loaders
    $o['hasApex'] = [bool]($names -match $Rx.ApexAsi); $o['hasS3SS'] = [bool]($names -match $Rx.S3ssFile); $o['hasS3PP'] = [bool]($names -match $Rx.S3ppFile)
    $o['hasBridge'] = (Test-Path -LiteralPath (Join-Path $bin '.trex')); $o['hasD3d9Wrapper'] = [bool]($names -contains 'd3d9.dll'); $o['hasDxvkConf'] = [bool]($names -contains 'dxvk.conf')
    $d9 = Join-Path $bin 'd3d9.dll'; if (Test-Path -LiteralPath $d9) { $vi = (Get-Item -LiteralPath $d9).VersionInfo; $o['d3d9Wrapper'] = "$($vi.ProductName) $($vi.FileVersion) $($vi.FileDescription)".Trim() }
    Line "  Bin: $($files.Count) files; ASI: $($o.asi -join ', '); loader candidates: $($loaders -join ', ')"
    Line "  Apex $($o.hasApex), S3SS $($o.hasS3SS), S3PP $($o.hasS3PP), RTX Remix bridge $($o.hasBridge), d3d9.dll in Bin $($o.hasD3d9Wrapper) $($o.d3d9Wrapper)"
    foreach ($a in $asis) { Line "    $($a.name)  $($a.size)  $($a.sha12)  $($a.modified)" }
    $ours = $files | Where-Object { $_.name -eq 'ts3native.asi' } | Select-Object -First 1
    if ($ours) { $o['ts3nativeIsRc'] = ((Sha (Join-Path $bin 'ts3native.asi')) -eq $RcAsiSha); Line "  ts3native.asi is the retail test build: $($o.ts3nativeIsRc)" }
    $ini = Join-Path $bin 'ts3native.ini'
    if (Test-Path -LiteralPath $ini) { $o['ts3nativeIniIsRcDefault'] = ((Sha $ini) -eq $RcIniSha); $o['ts3nativeIniWritable'] = (CanOpenWrite $ini); Line "  ts3native.ini equals the test build's default ini: $($o.ts3nativeIniIsRcDefault); writable: $($o.ts3nativeIniWritable)" }
    if ($asis.Count -gt 0 -and $loaders.Count -eq 0) { Line '  ASI files but no ASI loader dll seen in Bin: the .asi mods will not load' }
    $log = Join-Path $bin 'ts3native.log'
    # "$_" below: Get-Content lines carry provider objects that ConvertTo-Json would walk for minutes
    if (Test-Path -LiteralPath $log) { $o['ts3nativeLogModified'] = (Stamp (Get-Item -LiteralPath $log).LastWriteTime); $o['ts3nativeLogHead'] = @(Get-Content -LiteralPath $log -TotalCount 12 -ErrorAction SilentlyContinue | ForEach-Object { "$_" }) } else { $o['ts3nativeLogModified'] = '' ; if ($ours) { Line '  ts3native.asi is there but no ts3native.log: the asi never loaded, or Bin is not writable' } }
    # which features stood down, were refused or loaded. The log sits next to the asi (native Run: <asi path>.log), so
    # Bin or the ASI loader's scripts / plugins folder. "refused <n>" counters of the periodic stat lines are left out.
    $notes = New-Object System.Collections.ArrayList; $fx = ''; $more = 0
    foreach ($d in @($bin, (Join-Path $bin 'scripts'), (Join-Path $bin 'plugins'))) {
      $lp = Join-Path $d 'ts3native.log'; if (-not (Test-Path -LiteralPath $lp)) { continue }
      foreach ($l in @(Get-Content -LiteralPath $lp -ErrorAction SilentlyContinue)) {
        $l = "$l".Trim()
        if (-not $fx -and $l -match $Rx.FixesVersion) { $fx = $Matches[1] }
        if ($l -match $Rx.LogNote -and -not $notes.Contains($l)) { if ($notes.Count -lt 80) { [void]$notes.Add($l) } else { $more++ } }
      }
    }
    $o['ts3nativeLogNotes'] = @($notes); $o['ts3nativeLogNotesMore'] = $more; $o['ts3fixesVersion'] = $fx
    Line "  ts3native.log stand-downs / refusals / loads: $($notes.Count)$(if ($more) { " (+$more not listed)" }); ts3fixes version in the log: $(if ($fx) { $fx } else { '-' })"
    foreach ($l in $notes) { Line "    $l" }
    $o['elevated'] = (IsAdmin)
    if ($o.underProgramFiles -and -not (IsAdmin)) { Line '  Bin is under Program Files: files there need administrator rights to change' }
    # VirtualStore: writes of a non-elevated old program to Program Files land here instead
    if ($env:LOCALAPPDATA) {
      $vs = Join-Path $env:LOCALAPPDATA ('VirtualStore' + $g.path.Substring(2))
      if (Test-Path -LiteralPath $vs) {
        $vf = @(Get-ChildItem -LiteralPath $vs -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object { [ordered]@{ file = $_.FullName.Substring($vs.Length); size = $_.Length; modified = (Stamp $_.LastWriteTime) } })
        $o['virtualStore'] = $vf; Line "  VirtualStore copy exists with $($vf.Count) file(s): Windows redirected writes away from the game folder"
        if ($vf.Count -gt 0) { Finding 3 'virtualstore' "Windows redirected $($vf.Count) file(s) of this install to VirtualStore ($vs). Files written there (ini, log) hide or replace the ones in the game folder, so an edited ini can look 'reset'. Move the game out of Program Files or run the kit as administrator." }
        Get-ChildItem -LiteralPath $vs -Recurse -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Extension -match $Rx.VsExt } | Select-Object -First 20 | ForEach-Object { CopyText $_.FullName ("files\game$n-virtualstore" + $_.FullName.Substring($vs.Length)) }
      }
    }
    # settings and logs in Bin (text only; EA's own files are hashed above, not copied)
    Get-ChildItem -LiteralPath $bin -File -Force | Where-Object { $_.Extension -match $Rx.BinExt -or $_.Name -match $Rx.ApexTxt } | ForEach-Object { CopyText $_.FullName "files\game$n-bin\$($_.Name)" }
    foreach ($sub in 'rtx-remix\logs', '.trex', 'rtx-remix') { $p = Join-Path $bin $sub; if (Test-Path -LiteralPath $p) { Get-ChildItem -LiteralPath $p -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Extension -match $Rx.RemixExt } | Sort-Object LastWriteTime -Descending | Select-Object -First 8 | ForEach-Object { CopyText $_.FullName "files\game$n-bin\$sub\$($_.Name)" } } }
    foreach ($sgr in 'GraphicsCards.sgr', 'GraphicsRules.sgr') { $p = Join-Path $bin $sgr; if (Test-Path -LiteralPath $p) { $rules = FileRow $p; $o[$sgr] = $rules; Line "  $sgr  $($rules.size)  $($rules.sha12)  $($rules.modified)" } }
  }
  return $o
}

function DiagUser($u, [int]$n, $games) {
  $o = [ordered]@{ id = "user$n"; path = $u.path; documents = $u.docs; isWhereTheGameLooks = [bool]$u.expected }
  Sec "User folder $n" {
    $p = $u.path
    Line "  $p   [$($u.docs)]"
    if (-not (Test-Path -LiteralPath $p)) { Line '  folder does not exist'; $o['exists'] = $false; return }
    $o['exists'] = $true
    $di = Get-Item -LiteralPath $p -Force
    $o['attributes'] = "$($di.Attributes)"; $o['isLink'] = [bool]($di.Attributes -band [IO.FileAttributes]::ReparsePoint)
    $o['topLevel'] = @(Get-ChildItem -LiteralPath $p -Force | Sort-Object Name | ForEach-Object { if ($_.PSIsContainer) { [ordered]@{ name = $_.Name + '\'; modified = (Stamp $_.LastWriteTime) } } else { [ordered]@{ name = $_.Name; size = $_.Length; modified = (Stamp $_.LastWriteTime); readOnly = [bool]$_.IsReadOnly; cloudOnly = [bool]($_.Attributes -band 0x400000) } } })
    $opt = Join-Path $p 'Options.ini'
    if (Test-Path -LiteralPath $opt) {
      $f = Get-Item -LiteralPath $opt -Force
      $kv = [ordered]@{}; foreach ($l in (Get-Content -LiteralPath $opt)) { if ($l -match $Rx.OptKeyValue) { $kv[$Matches[1]] = $Matches[2].Trim() } }
      $o['options'] = [ordered]@{ size = $f.Length; modified = (Stamp $f.LastWriteTime); created = (Stamp $f.CreationTime); readOnly = [bool]$f.IsReadOnly; cloudOnly = [bool]($f.Attributes -band 0x400000); writable = (CanOpenWrite $opt); lastdevice = "$($kv['lastdevice'])"; keys = $kv.Count; values = $kv }
      Line "  Options.ini  $($f.Length) bytes, written $(Stamp $f.LastWriteTime), created $(Stamp $f.CreationTime), read-only $($f.IsReadOnly), writable $($o.options.writable), lastdevice $($kv['lastdevice'])"
      CopyText $opt "files\user$n\Options.ini"
      if ($f.IsReadOnly) { Finding 2 'options-readonly' "Options.ini in $p is read-only: the game cannot save its settings, so they are the same (or default) at every start. Clear the read-only box in the file's Properties." }
      elseif ($o.options.writable -like 'no*') { Finding 2 'options-denied' "Options.ini in $p cannot be opened for writing (access denied): the game cannot save its settings. Check Controlled folder access, antivirus and the folder's permissions." }
    } else { Line '  no Options.ini here'; $o['options'] = $null }
    Get-ChildItem -LiteralPath $p -File -Force -Filter 'Options*' -ErrorAction SilentlyContinue | Where-Object { $_.Name -ne 'Options.ini' } | Select-Object -First 5 | ForEach-Object { CopyText $_.FullName "files\user$n\$($_.Name)" }
    $dc = Join-Path $p 'DeviceConfig.log'
    if (Test-Path -LiteralPath $dc) {
      $txt = Get-Content -LiteralPath $dc -Raw; $f = Get-Item -LiteralPath $dc
      $g = [ordered]@{ modified = (Stamp $f.LastWriteTime) }
      if ($txt -match $Rx.DcNumber) { $g['number'] = $Matches[1] }
      if ($txt -match $Rx.DcNameDriver) { $g['nameDriver'] = $Matches[1] }
      if ($txt -match $Rx.DcNameDb) { $g['nameDatabase'] = $Matches[1] }
      if ($txt -match $Rx.DcVendorDevice) { $g['vendor'] = $Matches[1].ToLower(); $g['device'] = $Matches[2].ToLower() }
      if ($txt -match $Rx.DcDriverVersion) { $g['driverVersion'] = $Matches[1] }
      if ($txt -match $Rx.DcTextureMemory) { $g['textureMemory'] = $Matches[1] }
      $g['cardRecognized'] = [bool]("$($g['nameDatabase'])" -match $Rx.DcMatched)
      $o['deviceConfig'] = $g
      Line "  DeviceConfig.log ($($g.modified)): $($g.nameDriver) [$($g.vendor):$($g.device)] driver $($g.driverVersion); database: $($g.nameDatabase); texture memory $($g.textureMemory)"
      CopyText $dc "files\user$n\DeviceConfig.log"
      if ($o['options'] -and $o.options.lastdevice) {
        $ld = $o.options.lastdevice -split $Rx.LastDeviceSep
        $now = "$($g.number);$($g.vendor);$($g.device);$($g.driverVersion)"
        $o['lastdeviceMatchesDeviceConfig'] = ($o.options.lastdevice.ToLower() -eq $now)
        Line "  Options.ini lastdevice $($o.options.lastdevice) vs last start $now : match $($o.lastdeviceMatchesDeviceConfig)"
        if (-not $o.lastdeviceMatchesDeviceConfig) { Finding 4 'lastdevice-mismatch' "Options.ini was saved for graphics device '$($o.options.lastdevice)' but at its last start the game saw '$now' ($($g.nameDriver)). When that value changes the game treats it as new hardware and resets its graphics options. Typical triggers: a laptop switching between two GPUs, a d3d9.dll wrapper (DXVK / RTX Remix bridge) added or removed, a driver update." }
      }
      if (-not $g.cardRecognized) { Line '  the graphics card is not in the game''s GraphicsCards.sgr list (the game then picks low defaults)' }
    } else { Line '  no DeviceConfig.log here (the game never started with this folder)' }
    foreach ($x in 'Version.tag', 'Sims3Logs.xml', 'Sims3LauncherLogFile.log') { $q = Join-Path $p $x; if (Test-Path -LiteralPath $q) { CopyText $q "files\user$n\$x" } }
    # Mods
    $mods = Join-Path $p 'Mods'; $m = [ordered]@{ exists = (Test-Path -LiteralPath $mods) }
    if ($m.exists) {
      $m['resourceCfg'] = (Test-Path -LiteralPath (Join-Path $mods 'Resource.cfg'))
      if ($m.resourceCfg) { CopyText (Join-Path $mods 'Resource.cfg') "files\user$n\Mods\Resource.cfg" }
      $m['files'] = @(Get-ChildItem -LiteralPath $mods -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object { [ordered]@{ file = $_.FullName.Substring($mods.Length + 1); size = $_.Length; modified = (Stamp $_.LastWriteTime) } })
      $m['packages'] = @($m.files | Where-Object { $_.file -like '*.package' }).Count
      $m['overrides'] = @($m.files | Where-Object { $_.file -like 'Overrides\*' }).Count
      $m['totalBytes'] = ($m.files | ForEach-Object { $_.size } | Measure-Object -Sum).Sum
      $m['ts3fixes'] = @($m.files | Where-Object { (Split-Path -Leaf $_.file) -like 'ts3fixes*.package' } | ForEach-Object { [ordered]@{ file = $_.file; size = $_.size; modified = $_.modified; sha12 = (Sha12 (Join-Path $mods $_.file)) } })
      foreach ($x in $m.ts3fixes) { Line "  ts3fixes package: Mods\$($x.file)  $($x.size) bytes  sha $($x.sha12)  $($x.modified)" }
      Line "  Mods: Resource.cfg $($m.resourceCfg), $($m.packages) packages ($([math]::Round($m.totalBytes / 1MB)) MB), $($m.overrides) in Overrides"
    } else { Line '  no Mods folder' }
    $o['mods'] = $m
    # caches
    $caches = @()
    foreach ($c in 'CASPartCache.package', 'compositorCache.package', 'scriptCache.package', 'simCompositorCache.package', 'socialCache.package') { $q = Join-Path $p $c; if (Test-Path -LiteralPath $q) { $f = Get-Item -LiteralPath $q; $caches += [ordered]@{ name = $c; bytes = $f.Length; modified = (Stamp $f.LastWriteTime) } } }
    foreach ($c in 'DCCache', 'DCBackup', 'Thumbnails', 'WorldCaches', 'FeaturedItems', 'IGACache', 'SigsCache', 'Downloads', 'InstalledWorlds', 'Library') { $q = Join-Path $p $c; if (Test-Path -LiteralPath $q) { $z = DirSize $q; $caches += [ordered]@{ name = "$c\"; bytes = $z.bytes; files = $z.files } } }
    $o['caches'] = @($caches)
    Line "  caches: $(($caches | ForEach-Object { "$($_.name) $([math]::Round($_.bytes / 1MB, 1)) MB" }) -join ', ')"
    # saves: names, sizes, dates only
    $sv = Join-Path $p 'Saves'
    $o['saves'] = @(); if (Test-Path -LiteralPath $sv) { $o['saves'] = @(Get-ChildItem -LiteralPath $sv -Force | Sort-Object LastWriteTime -Descending | ForEach-Object { if ($_.PSIsContainer) { $z = DirSize $_.FullName; [ordered]@{ name = $_.Name; bytes = $z.bytes; files = $z.files; modified = (Stamp $_.LastWriteTime) } } else { [ordered]@{ name = $_.Name; bytes = $_.Length; modified = (Stamp $_.LastWriteTime) } } }) }
    Line "  saves: $($o.saves.Count)"
    # other mods' settings and logs
    foreach ($sub in 'S3SS', 'Apex Radiance', 'ts3native') {
      $q = Join-Path $p $sub; if (-not (Test-Path -LiteralPath $q)) { continue }
      $o["folder_$sub"] = @(Get-ChildItem -LiteralPath $q -File -Force -ErrorAction SilentlyContinue | ForEach-Object { [ordered]@{ name = $_.Name; size = $_.Length; modified = (Stamp $_.LastWriteTime); readOnly = [bool]$_.IsReadOnly } })
      Line "  $sub\: $(($o["folder_$sub"] | ForEach-Object { "$($_.name) ($($_.modified))" }) -join ', ')"
      Get-ChildItem -LiteralPath $q -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Extension -match $Rx.UserCfgExt } | Sort-Object LastWriteTime -Descending | Select-Object -First 15 | ForEach-Object { CopyText $_.FullName "files\user$n\$sub\$($_.Name)" }
    }
    CopyNewest $p 'ScriptError_*.xml' 10 "files\user$n\crash"
    CopyNewest $p 'xcpt*.txt' 10 "files\user$n\crash"
    $o['scriptErrors'] = @(Get-ChildItem -LiteralPath $p -File -Filter 'ScriptError_*.xml' -ErrorAction SilentlyContinue).Count
    $o['crashFiles'] = @(Get-ChildItem -LiteralPath $p -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $Rx.CrashFile } | Sort-Object LastWriteTime -Descending | Select-Object -First 30 | ForEach-Object { [ordered]@{ name = $_.Name; size = $_.Length; modified = (Stamp $_.LastWriteTime) } })
    Line "  ScriptError files: $($o.scriptErrors); crash files: $($o.crashFiles.Count)"
  }
  return $o
}

function Verdict($games, $users, $sys) {
  # other EA products and Sims 3 tools keep their own Options.ini in the same Electronic Arts folder: never the game's settings
  $withOpt = @($users | Where-Object { $_['options'] -and ((Split-Path -Leaf $_.path) -notmatch $Rx.NotPlayUser) })
  if ($withOpt.Count -gt 1) {
    $list = ($withOpt | Sort-Object { $_.options.modified } -Descending | ForEach-Object { "$($_.path) (Options.ini written $($_.options.modified))" }) -join '; '
    Finding 3 'several-user-folders' "There are $($withOpt.Count) Sims 3 user folders with their own Options.ini: $list. The game uses only the one under the Documents known folder; a second one (OneDrive move, other language, other Windows user) holds the 'old' settings."
  }
  $exp = @($users | Where-Object { $_.isWhereTheGameLooks })
  if ($users.Count -gt 0 -and $withOpt.Count -eq 0) { Finding 5 'no-options' 'No Options.ini in any Sims 3 user folder: the game has not saved settings yet, or cannot write them.' }
  if ($withOpt.Count -gt 0 -and $exp.Count -gt 0 -and -not ($exp | Where-Object { $_['options'] })) { Finding 3 'options-elsewhere' 'The folder the game uses (under the Documents known folder) has no Options.ini, but another folder has one: the game reads and writes a different folder than the one holding your settings.' }
  $doc = $R['documents']
  if ($doc -and $doc['documentsInOneDrive']) { Finding 6 'onedrive' 'Documents is inside OneDrive. If OneDrive moved or restored the folder, or keeps Options.ini as an online-only file, the game can start with an empty or old user folder.' }
  $wrap = @($games | Where-Object { $_['hasD3d9Wrapper'] -or $_['hasBridge'] })
  $gpuN = 0; if ($sys -and $sys['gpuCount']) { $gpuN = [int]$sys['gpuCount'] }
  if ($gpuN -gt 1 -or $wrap.Count -gt 0) {
    $why = @(); if ($gpuN -gt 1) { $why += "$gpuN graphics adapters (the game can start on either one)" }; if ($wrap.Count -gt 0) { $why += 'a d3d9.dll wrapper / RTX Remix bridge in Game\Bin (it reports another device and driver version than plain Windows)' }
    Finding 5 'device-change-likely' "Likely cause of a graphics-settings reset: $($why -join ' and '). The game stores the device it last ran on in Options.ini ('lastdevice'); when the next start reports another one, it resets the graphics options once. ts3native itself does not write Options.ini."
  }
  foreach ($g in $games) { if ($g['ts3nativeIniIsRcDefault']) { Finding 8 'ini-default' "If 'settings' means ts3native.ini: the ts3native.ini in $($g.path) is the test build's default file. The test build ships its own default ini, so earlier ts3native.ini edits are replaced by design (this kit's installer keeps a backup)." } }
  $sorted = @($Findings | Sort-Object { $_.rank })
  $R['findings'] = $sorted
  if ($sorted.Count -eq 0) { $R['verdict'] = 'No cause for a settings reset found in the files. Please tell us which settings reset (game options, graphics only, S3SS, ts3native.ini) and when.' }
  else { $R['verdict'] = $sorted[0].text }
}

function StartStage {
  $rand = -join ((1..6) | ForEach-Object { '{0:x}' -f (Get-Random -Maximum 16) })
  $script:zipName = "TS3Diag-$(Get-Date -Format 'yyyyMMdd-HHmm')-$rand"
  $script:stage = Join-Path ([System.IO.Path]::GetTempPath()) "$($script:zipName)-stage"
  [void](New-Item -ItemType Directory -Force -Path $script:stage)
  $script:dest = $OutDir; if (-not $script:dest) { $script:dest = [Environment]::GetFolderPath('Desktop') }; if (-not $script:dest) { $script:dest = $here }
  $T.Clear(); $Findings.Clear(); $Copied.Clear(); $Skipped.Clear(); $R.Clear()
}
function RunDiag([string[]]$extraGames, [scriptblock]$session = $null) {
  StartStage
  $name = $script:zipName; $dest = $script:dest
  Line "TS3 kit diagnostic $KitVersion   $(Get-Date -Format 'yyyy-MM-dd HH:mm') (local time)"
  Line 'Read-only: nothing on this PC is changed. User name, home path and computer name are masked.'
  $R['kit'] = $KitVersion; $R['created'] = (Get-Date -Format 's'); $R['mode'] = $(if ($NoSystem) { 'given folders only' } else { 'full' })
  $R['gameRunning'] = @(Try1 { Get-Process -Name $RunNames -ErrorAction SilentlyContinue | ForEach-Object { $_.ProcessName } } @())
  if ($session) { & $session }
  if (-not $NoSystem) { DiagSystem }
  $games = @(FindGames); foreach ($x in $extraGames) { if ($x -and -not ($games | Where-Object { $_.path -eq $x.Trim('"').TrimEnd('\') })) { $games += [ordered]@{ path = $x.Trim('"').TrimEnd('\'); sources = @('typed in'); exists = (HasExe $x.Trim('"')) } } }
  $users = @(FindUserDirs)
  $go = @(); $i = 0; foreach ($g in $games) { $i++; $go += (DiagGame $g $i) }
  if ($games.Count -eq 0) { Line ''; Line 'No game install found.'; Finding 9 'no-game' 'No Sims 3 install was found through the registry, Steam or the usual folders. If you test on a copied game folder, start the kit again and type that folder in.' }
  $uo = @(); $i = 0; foreach ($u in $users) { $i++; $uo += (DiagUser $u $i $go) }
  if ($users.Count -eq 0) { Line ''; Line 'No Sims 3 user folder found under Documents.' }
  $R['games'] = $go; $R['userFolders'] = $uo
  if (-not $NoSystem) { DiagSecurity @() }
  Verdict $go $uo $R['system']
  Line ''; Line '== Verdict'
  Line "  $($R.verdict)"
  if (@($R.findings).Count -gt 1) { Line '  Other findings:'; $R.findings | Select-Object -Skip 1 | ForEach-Object { Line "  - $($_.text)" } }
  $R['copiedFiles'] = @($Copied); $R['skippedFiles'] = @($Skipped)
  Line ''; Line '== Files in the zip'
  Line '  report.txt, report.json'
  foreach ($c in $Copied) { Line "  $($c.zipPath)  ($($c.size) bytes, $($c.modified))" }
  if ($Skipped.Count) { Line '  Not copied:'; foreach ($s in $Skipped) { Line "  - $($s.file): $($s.why)" } }
  $utf8 = New-Object System.Text.UTF8Encoding($false)
  [System.IO.File]::WriteAllText((Join-Path $script:stage 'report.json'), (Mask ($R | ConvertTo-Json -Depth 8)), $utf8)
  [System.IO.File]::WriteAllText((Join-Path $script:stage 'report.txt'), (Mask ($T -join "`r`n")), $utf8)
  $zip = Join-Path $dest "$name.zip"
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  # entry names with forward slashes (CreateFromDirectory on old .NET writes backslashes, which other unzip tools mishandle)
  Add-Type -AssemblyName System.IO.Compression
  $za = [System.IO.Compression.ZipFile]::Open($zip, 'Create')
  try { foreach ($f in (Get-ChildItem -LiteralPath $script:stage -Recurse -File -Force)) { [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($za, $f.FullName, $f.FullName.Substring($script:stage.Length + 1).Replace('\', '/')) } } finally { $za.Dispose() }
  if ($script:stage -like '*TS3Diag-*-stage') { Remove-Item -LiteralPath $script:stage -Recurse -Force }   # only our own staging folder
  Write-Host ''
  Write-Host "Report written: $zip" -ForegroundColor Green
  Write-Host 'Nothing was uploaded. Look inside if you like, then send us that zip.'
  return $zip
}

# ---------- launch and capture ----------
$GameProcs = $ProcNames
# How the player starts the game. A launcher, an Apex Radiance / RTX Remix bridge setup or Steam can make the game see
# another graphics device than a plain TS3W.exe start; the game then resets ALL its options (Options.ini 'lastdevice').
# So the capture starts the game his usual way, and TS3W.exe directly only as a last, warned choice.
function LinkDirs {
  if ($LinkDir) { return @(@{ dir = $LinkDir; deep = $true; where = 'shortcut' }) }
  if ($NoSystem) { return @() }
  $d = @()
  foreach ($f in 'Desktop', 'CommonDesktopDirectory') { $p = [Environment]::GetFolderPath($f); if ($p) { $d += @{ dir = $p; deep = $false; where = 'Desktop shortcut' } } }
  foreach ($f in 'Programs', 'CommonPrograms') { $p = [Environment]::GetFolderPath($f); if ($p) { $d += @{ dir = $p; deep = $true; where = 'Start Menu shortcut' } } }
  return $d
}
function Route([string]$kind, [string]$label, [string]$file) { [ordered]@{ kind = $kind; label = $label; file = $file; usual = $false } }
function FindRoutes([string]$game) {
  $game = $game.TrimEnd('\'); $bin = Join-Path $game 'Game\Bin'
  $r = New-Object System.Collections.ArrayList; $seen = @{}
  $sh = Try1 { New-Object -ComObject WScript.Shell } $null
  foreach ($ld in @(LinkDirs)) {
    if (-not $sh -or -not (Test-Path -LiteralPath $ld.dir)) { continue }
    foreach ($f in @(Get-ChildItem -LiteralPath $ld.dir -Filter '*.lnk' -File -Force -Recurse:$ld.deep -ErrorAction SilentlyContinue)) {
      $l = Try1 { $sh.CreateShortcut($f.FullName) } $null; if (-not $l) { continue }
      $t = "$($l.TargetPath)"; $a = "$($l.Arguments)".Trim(); if (-not $t) { continue }
      $leaf = Split-Path -Leaf $t
      $inGame = $t.ToLower().StartsWith($game.ToLower() + '\') -and $leaf -match $Rx.RunExt -and "$leaf $($f.BaseName)" -notmatch $Rx.NotGameExe
      $client = $leaf -match $Rx.ClientExe -and "$a $($f.BaseName)" -match $Rx.ClientArgs
      if (-not ($inGame -or $client)) { continue }
      $k = "$t|$a".ToLower(); if ($seen[$k]) { continue }; $seen[$k] = $true
      [void]$r.Add((Route 'Shortcut' ("$($ld.where) '$($f.BaseName)' ($t $a".TrimEnd() + ')') $f.FullName))
    }
  }
  $la = Join-Path $bin 'Sims3Launcher.exe'; if (Test-Path -LiteralPath $la) { [void]$r.Add((Route 'Launcher' 'Sims3Launcher.exe in Game\Bin (you press Play)' $la)) }
  if ($game -match $Rx.SteamApps) { [void]$r.Add((Route 'Steam' 'Steam (steam://rungameid/47890)' 'steam://rungameid/47890')) }
  if (-not $NoSystem) {
    foreach ($c in @((Join-Path "$env:ProgramFiles" 'Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe'), (Join-Path "${env:ProgramFiles(x86)}" 'Origin\Origin.exe'))) {
      if (Test-Path -LiteralPath $c) { [void]$r.Add((Route 'Client' "$(if ($c -match $Rx.OriginName) { 'Origin' } else { 'EA app' }) (it opens; you press Play on The Sims 3)" $c)) }
    }
  }
  if ($r.Count) { $r[0].usual = $true }   # Desktop shortcuts first, then Start Menu, launcher, Steam, EA app
  [void]$r.Add((Route 'Self' 'I start it myself (the tool waits)' ''))
  $exe = Join-Path $bin 'TS3W.exe'; if ($DirectExe) { $exe = Join-Path $bin $DirectExe }
  [void]$r.Add((Route 'Direct' "TS3W.exe directly ($exe)" $exe))
  return @($r)
}
function PickRoute([string]$game) {
  $all = @(FindRoutes $game); $found = @($all | Where-Object { $_.kind -ne 'Self' -and $_.kind -ne 'Direct' })
  if ($found.Count -eq 1) { if ((Ask "Start the game the way you normally do: $($found[0].label)? (Y/N)") -match $Rx.Yes) { return $found[0] } }
  Say ''; Say 'How do you normally start the game?'
  $i = 0
  foreach ($x in $all) {
    $i++; Say "  $i. $($x.label)$(if ($x.usual) { '   <- looks like your usual way' })"
    if ($x.kind -eq 'Direct') { Say '     Warning: a direct start can make the game see another graphics device than your usual start and reset ALL game options.' 'Yellow' }
  }
  Say '  0. Cancel'
  $u = @($all | Where-Object { $_.usual })
  $a = Ask "Type a number$(if ($u.Count) { ', or Enter for the marked one' })"
  if ($a -eq '' -and $u.Count) { return $u[0] }
  if ($a -match $Rx.Number -and [int]$a -ge 1 -and [int]$a -le $all.Count) { return $all[[int]$a - 1] }
  return $null
}
# Every settings / log file the game or a mod may write: size, time, sha256 (small files).
function SnapRoots([string]$game, $users) {
  $roots = New-Object System.Collections.ArrayList
  $bin = Join-Path $game 'Game\Bin'
  [void]$roots.Add(@('game-bin', $bin, $Rx.SnapBinFile))
  foreach ($sub in 'rtx-remix\logs', '.trex') { [void]$roots.Add(@("game-bin\$sub", (Join-Path $bin $sub), $Rx.SnapRemixFile)) }
  if ($env:LOCALAPPDATA) { $vs = Join-Path $env:LOCALAPPDATA ('VirtualStore' + $game.Substring(2)); [void]$roots.Add(@('game-virtualstore', (Join-Path $vs 'Game\Bin'), $Rx.Any)) }
  $i = 0
  foreach ($u in $users) { $i++; [void]$roots.Add(@("user$i", $u.path, $Rx.Any)); foreach ($sub in 'S3SS', 'Apex Radiance', 'ts3native') { [void]$roots.Add(@("user$i\$sub", (Join-Path $u.path $sub), $Rx.Any)) } }
  return $roots
}
function Snap($roots) {
  $s = [ordered]@{}
  foreach ($r in $roots) {
    if (-not (Test-Path -LiteralPath $r[1])) { continue }
    foreach ($f in @(Get-ChildItem -LiteralPath $r[1] -File -Force -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $r[2] })) {
      $h = ''; if ($f.Length -le $CapBytes) { $h = Sha $f.FullName }
      $s[$f.FullName] = [ordered]@{ label = $r[0]; name = $f.Name; size = $f.Length; modified = (Stamp $f.LastWriteTime); ticks = $f.LastWriteTimeUtc.Ticks; sha = $h; readOnly = [bool]$f.IsReadOnly }
    }
  }
  return $s
}
function KeyValues([string]$path) {
  $kv = [ordered]@{}; $sec = ''
  foreach ($l in @(Try1 { Get-Content -LiteralPath $path -ErrorAction Stop } @())) {
    if ($l -match $Rx.IniSection) { $sec = $Matches[1] + '.' } elseif ($l -match $Rx.TomlKeyValue) { $kv[$sec + $Matches[1]] = $Matches[2] }
  }
  return $kv
}
function ProcInfo([int]$id, [string]$launchedCwd) {
  $o = [ordered]@{ pid = $id; capturedAt = (Stamp (Get-Date)) }
  $w = Try1 { Get-CimInstance Win32_Process -Filter "ProcessId=$id" -ErrorAction Stop } $null
  if ($w) { $o['exe'] = "$($w.ExecutablePath)"; $o['commandLine'] = "$($w.CommandLine)"; $o['parentPid'] = $w.ParentProcessId; $o['parent'] = "$(Try1 { (Get-Process -Id $w.ParentProcessId -ErrorAction Stop).ProcessName } '')"; $o['started'] = (Stamp $w.CreationDate); $o['workingSetMB'] = [int]($w.WorkingSetSize / 1MB); $o['virtualMB'] = [int]($w.VirtualSize / 1MB) }
  $o['workingDir'] = $(if ($launchedCwd) { "$launchedCwd (set by this tool)" } else { 'not readable from outside (the game was not started by this tool)' })
  if (-not ('Ts3Tok' -as [type])) {
    Try1 { Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class Ts3Tok {
  [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
  [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr h, uint access, out IntPtr tok);
  [DllImport("advapi32.dll", SetLastError = true)] static extern bool GetTokenInformation(IntPtr tok, int cls, out int val, int len, out int ret);
  [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
  // read-only token query: 20 = TokenElevation, 24 = TokenVirtualizationEnabled
  public static string Query(int pid) {
    IntPtr h = OpenProcess(0x1000, false, pid);
    if (h == IntPtr.Zero) return "not readable (error " + Marshal.GetLastWin32Error() + "; the game may run as administrator)";
    try {
      IntPtr t; if (!OpenProcessToken(h, 8, out t)) return "token not readable (error " + Marshal.GetLastWin32Error() + "; the game may run as administrator)";
      try { int v, r; string s = "";
        if (GetTokenInformation(t, 20, out v, 4, out r)) s += "elevated=" + (v != 0);
        if (GetTokenInformation(t, 24, out v, 4, out r)) s += " virtualized=" + (v != 0);
        return s; } finally { CloseHandle(t); }
    } finally { CloseHandle(h); }
  }
}
'@ } $null | Out-Null
  }
  $o['token'] = "$(Try1 { [Ts3Tok]::Query($id) } 'not readable')"
  # The game is 32-bit: a 64-bit PowerShell sees only its 64-bit system modules, so ask a 32-bit PowerShell.
  $mods = @()
  $ps32 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
  $cmd = "(Get-Process -Id $id).Modules | ForEach-Object { '{0}|{1}|{2}|{3}' -f `$_.ModuleName, `$_.FileVersionInfo.FileVersion, `$_.FileVersionInfo.CompanyName, `$_.FileName }"
  if (Test-Path -LiteralPath $ps32) { $mods = @(Try1 { $null | & $ps32 -NoProfile -NonInteractive -Command $cmd 2>$null } @()) }   # $null |: it must never wait on our stdin
  if ($mods.Count -lt 5) { $mods = @(Try1 { (Get-Process -Id $id -ErrorAction Stop).Modules | ForEach-Object { '{0}|{1}|{2}|{3}' -f $_.ModuleName, $_.FileVersionInfo.FileVersion, $_.FileVersionInfo.CompanyName, $_.FileName } } @()) }
  $o['modules'] = @($mods | Where-Object { $_ } | ForEach-Object { $p = "$_".Split('|'); [ordered]@{ name = $p[0]; version = $p[1]; company = $p[2]; path = $p[3] } })
  $o['modulesNotWindows'] = @($o.modules | Where-Object { $_.path -notmatch $Rx.WindowsDir } | ForEach-Object { $_.name })
  return $o
}
function RunCapture([string]$game, [string]$how) {
  if (-not (HasExe $game)) { throw "No Game\Bin\TS3W.exe (or TS3.exe) in $game" }
  if (@(Get-Process -Name $GameProcs -ErrorAction SilentlyContinue).Count) { throw 'The game is already running. Close it first, then start the capture.' }
  # -Launch given: that route (an explicit choice); else the player picks from his own routes
  if ($how) { $route = @(FindRoutes $game | Where-Object { $_.kind -eq $how })[0]; if (-not $route) { $route = Route $how $how '' } } else { $route = PickRoute $game }
  if (-not $route) { Say 'Cancelled. Nothing was started.'; return '' }
  $how = $route.kind
  $script:GameDir = $game
  $zip = RunDiag @() {
    $ses = [ordered]@{ game = $game; launch = $how; route = $route.label }
    Line "  launch route: $($route.label)"
    $bin = Join-Path $game 'Game\Bin'
    $users = @(FindUserDirs | Where-Object { Test-Path -LiteralPath $_.path })
    $roots = SnapRoots $game $users
    Line ''; Line '== Session capture'
    Line '  Verbose logging: ts3native.ini has no single log-level switch ([UserDiag] is already on), so no ini is changed.'
    $before = Snap $roots
    foreach ($p in $before.Keys) { if ($p -match $Rx.CfgFile) { CopyText $p "session\before\$($before[$p].label)\$($before[$p].name)" } }
    $kvBefore = @{}; foreach ($p in $before.Keys) { if ($p -match $Rx.IniTomlFile) { $kvBefore[$p] = KeyValues $p } }
    Line "  before: $($before.Count) settings / log files noted in $($roots.Count) folders"
    $start = Get-Date; $ses['started'] = (Stamp $start); $cwd = ''
    if ($how -eq 'Direct') { $exe = Join-Path $bin 'TS3W.exe'; if (-not (Test-Path -LiteralPath $exe)) { $exe = Join-Path $bin 'TS3.exe' }; if ($DirectExe) { $exe = Join-Path $bin $DirectExe }; $cwd = $bin; Start-Process -FilePath $exe -WorkingDirectory $bin; Line "  started $exe (working folder Game\Bin)" }
    elseif ($how -eq 'Shortcut') { Start-Process -FilePath $route.file; Line "  opened the shortcut $($route.file)" }
    elseif ($how -eq 'Client') { Start-Process -FilePath $route.file; Line "  started $($route.file); press Play on The Sims 3 there" }
    elseif ($how -eq 'Launcher') { $exe = Join-Path $bin 'Sims3Launcher.exe'; if (-not (Test-Path -LiteralPath $exe)) { throw 'No Sims3Launcher.exe in Game\Bin' }; Start-Process -FilePath $exe -WorkingDirectory $bin; Line "  started $exe; press Play in the launcher" }
    elseif ($how -eq 'Steam') { Start-Process 'steam://rungameid/47890'; Line '  asked Steam to start The Sims 3 (app 47890)' }
    else { Line '  waiting for you to start the game the way you normally do' }
    Say ''; Say 'Now: load a save, change one setting in the game options, save, and quit the game normally.' 'Cyan'
    Say 'This window waits until the game has closed. If the game hangs or crashed, press C here to collect now.' 'Cyan'
    $proc = $null; $info = $null; $collectNow = $false; $gone = 0; $lastInfo = [datetime]::MinValue
    $waitStart = $(if ($how -eq 'Direct') { 180 } else { 900 })
    while ($true) {
      Start-Sleep -Milliseconds 1000
      $el = ((Get-Date) - $start).TotalSeconds
      $ps = @(Get-Process -Name $GameProcs -ErrorAction SilentlyContinue | Sort-Object { if ($_.ProcessName -eq 'TS3W') { 0 } else { 1 } })
      if ($ps.Count) {
        $gone = 0
        if (-not $proc) { $proc = $ps[0]; $ses['pid'] = $proc.Id; $ses['processSeen'] = (Stamp (Get-Date)); Say "  game process found: $($proc.ProcessName).exe (pid $($proc.Id))" }
        # module list once the game is $InfoAfterSec s old, refreshed every 2 min: the last one before exit stays
        if ($el -ge $InfoAfterSec -and ((Get-Date) - $lastInfo).TotalSeconds -ge 120) { $i2 = Try1 { ProcInfo $ps[0].Id $cwd } $null; if ($i2 -and @($i2.modules).Count) { $info = $i2 }; $lastInfo = Get-Date }
      } elseif ($proc) { $gone++; if ($gone -ge 3) { break } }
      elseif ($el -gt $waitStart) { Line "  no game process appeared within $waitStart s"; $ses['neverStarted'] = $true; break }
      if ($CaptureMaxSec -gt 0 -and $el -gt $CaptureMaxSec) { Line "  stopped waiting after $CaptureMaxSec s"; $collectNow = $true; break }
      $key = Try1 { if ([Console]::KeyAvailable) { [Console]::ReadKey($true).KeyChar } } $null
      if ("$key" -match $Rx.CollectKey) { $collectNow = $true; break }
    }
    $ses['ended'] = (Stamp (Get-Date)); $ses['collectedWhileRunning'] = $collectNow
    if ($collectNow -and $proc -and -not $proc.HasExited) {
      $info = Try1 { ProcInfo $proc.Id $cwd } $info
      $a = 'N'; if (-not $Yes -and $CaptureMaxSec -eq 0) { $a = Read-Host 'Write a memory dump of the game for us? It can be up to 4 GB and holds whatever the game had in memory. (Y/N)' }
      if ($a -match $Rx.Yes) {
        $dmp = Join-Path $script:dest "$($script:zipName)-TS3W.dmp"
        # built-in Windows dump writer (the 32-bit one for the 32-bit game); no extra tool, nothing downloaded
        $rd = Join-Path $env:WINDIR 'SysWOW64\rundll32.exe'; $cs = Join-Path $env:WINDIR 'SysWOW64\comsvcs.dll'
        Try1 { Start-Process -FilePath $rd -ArgumentList "`"$cs`", MiniDump $($proc.Id) `"$dmp`" full" -Wait -WindowStyle Hidden } $null | Out-Null
        if (Test-Path -LiteralPath $dmp) { $ses['dump'] = [ordered]@{ file = $dmp; bytes = (Get-Item -LiteralPath $dmp).Length; inZip = $false; note = 'next to the zip; not put into the zip (size, and it is not masked). Send it only if we ask.' }; Line "  memory dump written: $dmp ($([int]((Get-Item -LiteralPath $dmp).Length / 1MB)) MB), kept outside the zip" }
        else { $ses['dump'] = 'failed (Windows or the antivirus refused)'; Line '  memory dump failed (Windows or the antivirus refused)' }
      }
      Say 'The game is still running: close it (or end TS3W.exe in Task Manager) when you are done.' 'Yellow'
    }
    $ses['process'] = $info
    if ($info) {
      Line "  process: $($info.exe); token $($info.token); parent $($info.parent); $(@($info.modules).Count) modules"
      Line "  modules not from the Windows folder: $($info.modulesNotWindows -join ', ')"
    } elseif ($proc) { Line "  process details not captured (the game closed within $InfoAfterSec s, or it is not readable)" }
    # ---- after ----
    $after = Snap $roots
    $changes = New-Object System.Collections.ArrayList
    foreach ($p in $after.Keys) {
      $a = $after[$p]; $b = $before[$p]
      $st = ''
      if (-not $b) { $st = 'created' } elseif ($a.ticks -ne $b.ticks -or $a.size -ne $b.size -or $a.sha -ne $b.sha) { $st = $(if ($a.sha -and $a.sha -eq $b.sha) { 'rewritten, same content' } else { 'changed' }) }
      if (-not $st) { continue }
      $row = [ordered]@{ file = $p; where = $a.label; state = $st; sizeBefore = $(if ($b) { $b.size } else { $null }); sizeAfter = $a.size; modifiedBefore = $(if ($b) { $b.modified } else { '' }); modifiedAfter = $a.modified }
      if ($p -match $Rx.IniTomlFile) {
        $k1 = $kvBefore[$p]; if (-not $k1) { $k1 = [ordered]@{} }; $k2 = KeyValues $p; $kd = @()
        foreach ($k in $k2.Keys) { if (-not $k1.Contains($k)) { $kd += "+ $k = $($k2[$k])" } elseif ("$($k1[$k])" -ne "$($k2[$k])") { $kd += "$k : $($k1[$k]) -> $($k2[$k])" } }
        foreach ($k in $k1.Keys) { if (-not $k2.Contains($k)) { $kd += "- $k (was $($k1[$k]))" } }
        $row['keyChanges'] = @($kd | Select-Object -First 150); $row['keyChangeCount'] = $kd.Count
      }
      [void]$changes.Add($row)
      if ($p -match $Rx.SessionFile) { CopyText $p "session\after\$($a.label)\$($a.name)" }
    }
    foreach ($p in $before.Keys) { if (-not $after.Contains($p)) { [void]$changes.Add([ordered]@{ file = $p; where = $before[$p].label; state = 'deleted'; sizeBefore = $before[$p].size; modifiedBefore = $before[$p].modified }) } }
    $ses['fileChanges'] = @($changes)
    Line "  after: $($changes.Count) file(s) created / changed / deleted during the session"
    foreach ($c in $changes) { Line "    $($c.state): [$($c.where)] $(Split-Path -Leaf $c.file)$(if ($c['keyChangeCount']) { "  ($($c.keyChangeCount) setting(s) differ)" })" }
    # where did Options.ini go?
    $optCh = @($changes | Where-Object { (Split-Path -Leaf $_.file) -eq 'Options.ini' -and $_.state -ne 'deleted' })
    $elsewhere = @()
    if (-not $NoSystem) {
      $search = @([Environment]::GetFolderPath('MyDocuments'), (Join-Path "$env:USERPROFILE" 'Documents'), $env:OneDrive, $(if ($env:LOCALAPPDATA) { Join-Path $env:LOCALAPPDATA 'VirtualStore' })) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -Unique
      foreach ($sr in $search) { $elsewhere += @(Try1 { Get-ChildItem -LiteralPath $sr -Recurse -Depth 5 -File -Filter 'Options.ini' -Force -ErrorAction SilentlyContinue | Where-Object { $_.LastWriteTime -ge $start -and -not $after.Contains($_.FullName) } | ForEach-Object { $_.FullName } } @()) }
    }
    $ses['optionsWrittenIn'] = @($optCh | ForEach-Object { $_.file }); $ses['optionsWrittenElsewhere'] = @($elsewhere | Select-Object -Unique)
    foreach ($c in $optCh) {
      Line "  Options.ini $($c.state) in $(Split-Path -Parent $c.file) at $($c.modifiedAfter)"
      $ld = @($c['keyChanges'] | Where-Object { $_ -match $Rx.LastDeviceChange })
      if ($ld.Count -and $c.keyChangeCount -ge 4) { Finding 1 'session-reset-observed' "Seen during the captured session: the game changed 'lastdevice' in Options.ini ($($ld[0])) together with $($c.keyChangeCount - 1) other settings. That is the game's own new-hardware reset: it saw another graphics device (or driver version) than at the start before. Every changed setting is in session.fileChanges of report.json." }
    }
    foreach ($e in $ses.optionsWrittenElsewhere) { Line "  an Options.ini was written OUTSIDE the known folders: $e"; Finding 1 'session-options-elsewhere' "During the captured session the game wrote an Options.ini in an unexpected place: $e. The game reads and writes a different folder than the one you edit or back up." }
    if ($proc -and $optCh.Count -eq 0 -and $ses.optionsWrittenElsewhere.Count -eq 0) { Finding 2 'session-no-options-write' 'During the captured session no Options.ini was written anywhere we looked. If you changed a setting in the game, the game could not save it (blocked or read-only), or it closed without saving.' }
    Line '  events during the session:'
    $ses['events'] = @(GameEvents $start.AddSeconds(-5))
    $R['session'] = $ses
  }
  return $zip
}

# ---------- installer ----------
function BackupRoots([string]$user) { $r = @(); if ($user) { $r += (Join-Path $user 'TS3Kit-Backup') }; $r += (Join-Path (Split-Path -Parent $here) 'backup'); return $r }
# The user folder names the game itself creates (localized); a folder named like a copy is never picked.
$GameUserNames = @('The Sims 3', 'Die Sims 3', 'Les Sims 3', 'Los Sims 3', 'De Sims 3')
function IsCopyName([string]$n) { $n -match $Rx.CopyName }
function LastActivity([string]$p) {   # newest write in Options.ini / Saves / DCCache / DeviceConfig.log (one level deep)
  $t = [datetime]::MinValue
  foreach ($n in 'Options.ini', 'Saves', 'DCCache', 'DeviceConfig.log') {
    $f = Join-Path $p $n; if (-not (Test-Path -LiteralPath $f)) { continue }
    $all = @(Get-Item -LiteralPath $f -Force) + @(Try1 { Get-ChildItem -LiteralPath $f -Force -ErrorAction Stop } @())
    foreach ($i in $all) { if ($i -and $i.LastWriteTime -gt $t) { $t = $i.LastWriteTime } }
  }
  return $t
}
# Narrows the user folder candidates to the ones the game can be using, newest activity first: no copy / backup names;
# then under the Documents folder the game uses; then the game's own folder names; then with an Options.ini.
function RankUsers($cands) {
  $c = @($cands | Where-Object { -not (IsCopyName (Split-Path -Leaf $_.path)) })
  foreach ($f in { $_.expected }, { $GameUserNames -contains (Split-Path -Leaf $_.path) }, { Test-Path -LiteralPath (Join-Path $_.path 'Options.ini') }) {
    $n = @($c | Where-Object $f); if ($n.Count) { $c = $n }
  }
  return @($c | Sort-Object { LastActivity $_.path } -Descending)
}
function PickUser {
  $u = @(FindUserDirs | Where-Object { Test-Path -LiteralPath $_.path })
  foreach ($x in $u) { if (IsCopyName (Split-Path -Leaf $x.path)) { Say "  skipped (named like a copy or backup, the game does not use it): $($x.path)" } }
  $r = @(RankUsers $u)
  if ($r.Count -eq 0) { Say 'No Sims 3 user folder found that the game uses (copies and backups are never picked).' 'Yellow'; return '' }
  if ($r.Count -eq 1) { Say "Sims 3 user folder: $($r[0].path)"; return $r[0].path }
  Say 'Several Sims 3 user folders look like the one the game uses:' 'Yellow'
  $i = 0; foreach ($x in $r) { $i++; Say "  $i. $($x.path)   (last activity $(Stamp (LastActivity $x.path)))" }
  if ($Yes) { throw 'Several Sims 3 user folders found (listed above). Pass -UserDir "<the one the game uses>". Nothing was changed.' }
  $a = Ask "Type the number of the folder the game uses (1-$($r.Count)), or Enter to cancel"
  if ($a -match $Rx.Number -and [int]$a -ge 1 -and [int]$a -le $r.Count) { return $r[[int]$a - 1].path }
  return ''
}
function CheckGameForInstall([string]$game) {
  $exe = Join-Path $game 'Game\Bin\TS3W.exe'
  if (-not (Test-Path -LiteralPath $exe)) { throw "No Game\Bin\TS3W.exe in $game" }
  $pe = PeInfo $exe
  if ($pe.edition -ne 'retail 1.67') { throw "This TS3W.exe is '$($pe.edition)' (PE $($pe.peTimestamp)/$($pe.sizeOfImage)). The test build is for the retail / disc 1.67 exe only (52D872DA/0xE5B000). Nothing was changed." }
  if (@(Get-Process -Name $RunNames -ErrorAction SilentlyContinue).Count) { throw 'The game or its launcher is running. Close it first. Nothing was changed.' }
}
# The game's usual Mods\Resource.cfg (Mod The Sims): loads .package files up to 5 folders deep under Mods\Packages.
$StdResourceCfg = "Priority 500`r`nPackedFile Packages/*.package`r`nPackedFile Packages/*/*.package`r`nPackedFile Packages/*/*/*.package`r`nPackedFile Packages/*/*/*/*.package`r`nPackedFile Packages/*/*/*/*/*.package`r`n`r`nPriority 501`r`nPackedFile DCCache/*.dbc`r`n"
function DoInstall([string]$game, [string]$user, [bool]$withS3ss) {
  CheckGameForInstall $game
  $bin = Join-Path $game 'Game\Bin'
  # payload\diag (optional): a diagnostic ts3fixes.package + ts3native.ini that log more, plus its own asi when the
  # diagnostic needs native code (payload\diag\ts3native-retail167.asi); same backup / restore
  $diag = Join-Path $payload 'diag'; $useDiag = $false
  if (Test-Path -LiteralPath $diag) {
    if ($Build) { $useDiag = ($Build -eq 'Diag') }
    elseif (-not $Yes) { $useDiag = ((Ask 'Install the normal test build (N) or the diagnostic build (D, logs more for us)? (N/D)') -match $Rx.Diag) }
  } elseif ($Build -eq 'Diag') { throw 'No payload\diag folder in this kit: there is no diagnostic build to install. Nothing was changed.' }
  $src = $payload; if ($useDiag) { $src = $diag }
  $srcAsi = Join-Path $payload 'ts3native-retail167.asi'; $diagAsi = $useDiag -and (Test-Path -LiteralPath (Join-Path $diag 'ts3native-retail167.asi'))
  if ($diagAsi) { $srcAsi = Join-Path $diag 'ts3native-retail167.asi' }
  $srcIni = Join-Path $src 'ts3native.ini'; $srcPkg = Join-Path $src 'ts3fixes.package'; $srcToml = Join-Path $payload 'S3SS-tunings.toml'
  foreach ($f in $srcAsi, $srcIni, $srcPkg, $srcToml) { if (-not (Test-Path -LiteralPath $f)) { throw "Kit file missing: $f. Extract the whole zip first." } }
  if ((-not $diagAsi -and (Sha $srcAsi) -ne $RcAsiSha) -or (-not $useDiag -and ((Sha $srcIni) -ne $RcIniSha -or (Sha $srcPkg) -ne $RcPkgSha)) -or (TextSha $srcToml) -ne $RcTomlSha) { throw 'The kit''s payload files do not match their expected sha256 (damaged download?). Nothing was changed.' }
  if (-not $user) { throw 'No Sims 3 user folder (Documents\Electronic Arts\The Sims 3) found for ts3fixes.package. Start the game once, or pass -UserDir. Nothing was changed.' }
  $utf8 = New-Object System.Text.UTF8Encoding($false)
  $kind = $(if ($useDiag) { 'diagnostic build' } else { 'test build' })
  $plan = @(
    [ordered]@{ target = (Join-Path $bin 'ts3native.asi'); bytes = [System.IO.File]::ReadAllBytes($srcAsi); what = "retail $kind" },
    [ordered]@{ target = (Join-Path $bin 'ts3native.ini'); bytes = [System.IO.File]::ReadAllBytes($srcIni); what = "$kind default settings" },
    [ordered]@{ target = (Join-Path $user 'Mods\Packages\ts3fixes.package'); bytes = [System.IO.File]::ReadAllBytes($srcPkg); what = "ts3fixes script mod ($kind)" })
  $cfg = Join-Path $user 'Mods\Resource.cfg'
  if (-not (Test-Path -LiteralPath $cfg)) { $plan += [ordered]@{ target = $cfg; bytes = $utf8.GetBytes($StdResourceCfg); what = 'the standard Resource.cfg (there was none, so the game would load no mods; uninstall removes it again)' } }
  if ($withS3ss) {
    if (-not $user) { throw 'No Sims 3 user folder found for the S3SS tunings. Start the game once, or pass -UserDir.' }
    $toml = Join-Path $user 'S3SS\S3SS.toml'; $old = ''; if (Test-Path -LiteralPath $toml) { $old = [System.IO.File]::ReadAllText($toml) }
    $tune = [System.IO.File]::ReadAllText($srcToml)
    if (@(Get-ChildItem -LiteralPath $bin -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $Rx.S3ppFile }).Count) {
      $tune = S3ppTune $tune; Say '  Sims 3 Performance Patch found: S3SS AdaptiveWait and OversizedThreadStackFix are set to off.'
    }
    $plan += [ordered]@{ target = $toml; bytes = $utf8.GetBytes((MergeToml $old $tune)); what = 'S3SS tunings merged into your file (your other S3SS settings stay)' }
    if (-not @(Get-ChildItem -LiteralPath $bin -File -ErrorAction SilentlyContinue | Where-Object { $_.Name -match $Rx.S3ssFile }).Count) { Say '  Note: Sims3SettingsSetter is not in Game\Bin; the tunings only take effect once it is installed.' 'Yellow' }
  }
  $sha = [System.Security.Cryptography.SHA256]::Create()
  $todo = @()
  foreach ($p in $plan) {
    $p['sha'] = (-join ($sha.ComputeHash($p.bytes) | ForEach-Object { $_.ToString('x2') }))
    $p['existed'] = (Test-Path -LiteralPath $p.target)
    if ($p.existed -and (Sha $p.target) -eq $p.sha) { Say "  already in place: $($p.target)" } else { $todo += $p }
  }
  # Other ts3native asis in Bin and other ts3fixes packages anywhere under Mods: the game loads them all, and mixed
  # versions break things. They are moved into the backup folder (never deleted); uninstall puts them back.
  $moves = @(Strays $bin $user | ForEach-Object { [ordered]@{ target = $_ } })
  if ($moves.Count) {
    Say ''; Say 'Found other copies the game would load as well (mixed versions break things):' 'Yellow'
    foreach ($m in $moves) { Say "  $($m.target)   sha $(Sha12 $m.target)" }
    if (-not $Yes -and (Ask 'Move them into the kit''s backup folder (not deleted; uninstall puts them back)? (Y/N)') -notmatch $Rx.Yes) { $moves = @(); Say '  Left in place: the check after the install will say MISMATCH.' 'Yellow' }
  }
  if ($todo.Count -eq 0 -and $moves.Count -eq 0) { Say 'Everything is already installed. Nothing to do.' 'Green'; return (VerifyInstall $bin $user $srcAsi $plan[2].sha) }
  $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
  $bdir = Join-Path (BackupRoots $user)[0] $stamp
  Say ''; Say 'The installer will:'
  foreach ($p in $todo) { Say "  $(if ($p.existed) { 'replace' } else { 'add    ' }) $($p.target)   ($($p.what))" }
  foreach ($m in $moves) { Say "  move    $($m.target)   (into the backup folder)" }
  Say "  first copy the current files (and Options.ini, which is not changed) to $bdir"
  Say '  TS3W.exe and Options.ini are not changed.'
  if (-not $Yes) { $a = Ask 'Go ahead? (Y/N)'; if ($a -notmatch $Rx.Yes) { Say 'Cancelled. Nothing was changed.'; return $false } }
  [void](New-Item -ItemType Directory -Force -Path $bdir)
  $entries = @(); $i = 0
  foreach ($p in $todo) { $i++; $bn = ''; if ($p.existed) { $bn = "$i-" + (Split-Path -Leaf $p.target); Copy-Item -LiteralPath $p.target -Destination (Join-Path $bdir $bn) }; $entries += [ordered]@{ target = $p.target; existed = [bool]$p.existed; backup = $bn; installedSha = $p.sha } }
  foreach ($m in $moves) { $i++; $bn = "$i-moved-" + (Split-Path -Leaf $m.target); Copy-Item -LiteralPath $m.target -Destination (Join-Path $bdir $bn); $entries += [ordered]@{ target = $m.target; existed = $true; backup = $bn; installedSha = '' } }
  if ($user -and (Test-Path -LiteralPath (Join-Path $user 'Options.ini'))) { Copy-Item -LiteralPath (Join-Path $user 'Options.ini') -Destination (Join-Path $bdir 'Options.ini.safety-copy') }
  $man = [ordered]@{ kit = $KitVersion; created = $stamp; game = $game; entries = $entries }
  [System.IO.File]::WriteAllText((Join-Path $bdir 'manifest.json'), ($man | ConvertTo-Json -Depth 5), $utf8)
  try {
    foreach ($p in $todo) {
      [void](New-Item -ItemType Directory -Force -Path (Split-Path -Parent $p.target))
      [System.IO.File]::WriteAllBytes($p.target, $p.bytes)
      if ((Sha $p.target) -ne $p.sha) { throw "sha256 check failed after writing $($p.target)" }
      Say "  written and verified: $($p.target)  sha $($p.sha.Substring(0, 12))"
    }
    foreach ($m in $moves) { Remove-Item -LiteralPath $m.target -Force; Say "  moved into the backup: $($m.target)" }
  } catch {
    Say "Install failed: $($_.Exception.Message)" 'Red'
    if ($_.Exception.GetBaseException() -is [System.UnauthorizedAccessException]) { Say 'Windows refused the write. If the game is under Program Files, right-click TS3Kit.bat and choose "Run as administrator".' 'Yellow' }
    Say 'Putting the earlier files back...'
    [void](RestoreBackup $bdir)
    return $false
  }
  if (-not @(Get-ChildItem -LiteralPath $bin -File | Where-Object { $_.Name -match $Rx.LoaderDllInstall }).Count) { Say 'Warning: no ASI loader dll seen in Game\Bin. ts3native.asi needs one (as any .asi mod does), or it will not load.' 'Yellow' }
  Say "Installed ($kind). Backup: $bdir" 'Green'
  Say 'After a game start the log is Game\Bin\ts3native.log; it should say "host build: retail 1.67".'
  return (VerifyInstall $bin $user $srcAsi $plan[2].sha)
}
# Every ts3native*.asi an ASI loader could load (Bin, Bin\scripts, Bin\plugins) and every ts3fixes*.package under Mods.
function AllAsis([string]$bin) { foreach ($d in '', 'scripts', 'plugins') { Try1 { Get-ChildItem -LiteralPath (Join-Path $bin $d) -File -Filter 'ts3native*.asi' -ErrorAction Stop | Where-Object { $_.Extension -eq '.asi' } | ForEach-Object { $_.FullName } } @() } }
function AllFixesPkgs([string]$user) { Try1 { Get-ChildItem -LiteralPath (Join-Path $user 'Mods') -Recurse -File -Filter '*.package' -ErrorAction Stop | Where-Object { $_.Extension -eq '.package' -and $_.Name -match $Rx.FixesPkg } | ForEach-Object { $_.FullName } } @() }
function Strays([string]$bin, [string]$user) {
  @(AllAsis $bin) | Where-Object { $_ -ne (Join-Path $bin 'ts3native.asi') }
  @(AllFixesPkgs $user) | Where-Object { $_ -ne (Join-Path $user 'Mods\Packages\ts3fixes.package') }
}
# The version the asi carries (ts3native has no version resource): its most frequent 0.N.N[-tag] string.
function AsiVersion([string]$p) {
  $m = @(Try1 { [regex]::Matches([System.Text.Encoding]::ASCII.GetString([System.IO.File]::ReadAllBytes($p)), $Rx.PkgVersion) } @())
  if (-not $m.Count) { return '?' }
  return ($m | Group-Object Value | Sort-Object Count -Descending | Select-Object -First 1).Name
}
function OkLine([bool]$r, [string]$text) { Say "  $(if ($r) { 'OK      ' } else { 'MISMATCH' }) $text" $(if ($r) { 'Green' } else { 'Red' }); return $r }
function VerifyInstall([string]$bin, [string]$user, [string]$srcAsi, [string]$pkgSha) {
  Say 'Check after the install:'
  $asi = Join-Path $bin 'ts3native.asi'; $asis = @(AllAsis $bin); $want = AsiVersion $srcAsi; $have = AsiVersion $asi
  $a = OkLine ($asis.Count -eq 1 -and $asis[0] -eq $asi) "ts3native asi files the game loads: $($asis.Count) ($((@($asis) | ForEach-Object { Split-Path -Leaf $_ }) -join ', ')); expected only ts3native.asi"
  $b = OkLine ($want -ne '?' -and $have -eq $want) "ts3native.asi version $have (kit $want)"
  $pk = @(AllFixesPkgs $user); $target = Join-Path $user 'Mods\Packages\ts3fixes.package'
  $c = OkLine ($pk.Count -eq 1 -and $pk[0] -eq $target -and (Sha $target) -eq $pkgSha) "ts3fixes packages under $user\Mods: $($pk.Count), sha $(Sha12 $target) (kit $($pkgSha.Substring(0, 12)))"
  foreach ($x in $pk) { if ($x -ne $target) { Say "           also loaded: $x   sha $(Sha12 $x)" 'Red' } }
  if ($a -and $b -and $c) { Say 'Install check: OK' 'Green'; return $true }
  Say 'Install check: MISMATCH (see above). Move the extra files out of Game\Bin / Mods, or run the install again and answer Y to moving them.' 'Red'
  return $false
}
function RestoreBackup([string]$bdir) {
  $man = Get-Content -LiteralPath (Join-Path $bdir 'manifest.json') -Raw | ConvertFrom-Json
  $ok = $true
  foreach ($e in @($man.entries)) {
    try {
      if ($e.existed) { Copy-Item -LiteralPath (Join-Path $bdir $e.backup) -Destination $e.target -Force; Say "  restored: $($e.target)" }
      elseif (Test-Path -LiteralPath $e.target) {
        # only remove a file this kit put there and nobody changed since
        if ((Sha $e.target) -eq $e.installedSha) { Remove-Item -LiteralPath $e.target -Force; Say "  removed: $($e.target)" } else { Say "  kept (changed since the install): $($e.target)" 'Yellow' }
      }
    } catch { $ok = $false; Say "  could not restore $($e.target): $($_.Exception.Message)" 'Red' }
  }
  if ($ok) { [System.IO.File]::WriteAllText((Join-Path $bdir 'restored.txt'), (Get-Date -Format 's')) }
  return $ok
}
function DoUninstall([string]$game, [string]$user) {
  if (@(Get-Process -Name $RunNames -ErrorAction SilentlyContinue).Count) { throw 'The game or its launcher is running. Close it first. Nothing was changed.' }
  $cands = @()
  foreach ($r in (BackupRoots $user)) { if (Test-Path -LiteralPath $r) { Get-ChildItem -LiteralPath $r -Directory | ForEach-Object { $m = Join-Path $_.FullName 'manifest.json'; if ((Test-Path -LiteralPath $m) -and -not (Test-Path -LiteralPath (Join-Path $_.FullName 'restored.txt'))) { $j = Get-Content -LiteralPath $m -Raw | ConvertFrom-Json; if ("$($j.game)".TrimEnd('\') -eq $game.TrimEnd('\')) { $cands += $_ } } } } }
  if ($cands.Count -eq 0) { Say "No backup of this kit found for $game. Nothing to undo. (By hand: delete ts3native.asi and ts3native.ini from Game\Bin and Mods\Packages\ts3fixes.package.)"; return $false }
  $b = ($cands | Sort-Object Name -Descending)[0]
  Say "This puts back the files saved in $($b.FullName) (the newest install of $($cands.Count) not yet undone)."
  if (-not $Yes) { $a = Ask 'Go ahead? (Y/N)'; if ($a -notmatch $Rx.Yes) { Say 'Cancelled. Nothing was changed.'; return $false } }
  $ok = RestoreBackup $b.FullName
  if ($ok) { Say 'Restored. The backup folder is kept.' 'Green' } else { Say 'Some files could not be restored (see above). Run TS3Kit.bat as administrator if the game is under Program Files.' 'Yellow' }
  return $ok
}
function AskGame {
  $games = @(FindGames | Where-Object { $_.exists })
  Say ''; Say 'Game folders found:'
  $i = 0; foreach ($g in $games) { $i++; $ed = Try1 { (PeInfo (Join-Path $g.path 'Game\Bin\TS3W.exe')).edition } '?'; Say "  $i. $($g.path)   ($ed)" }
  $a = Read-Host 'Type a number, or paste the full path of the game folder (the copy you test on), or Enter to cancel'
  if (-not $a) { return '' }
  if ($a -match $Rx.Number -and [int]$a -ge 1 -and [int]$a -le $games.Count) { return $games[[int]$a - 1].path }
  $p = $a.Trim('"').TrimEnd('\'); if ($p -match $Rx.GameBinDir) { $p = Split-Path -Parent (Split-Path -Parent $p) }
  return $p
}

function SelfTest {
  $fail = 0
  function Check([bool]$c, [string]$what) { if ($c) { Write-Host "  ok   $what" } else { Write-Host "  FAIL $what" -ForegroundColor Red; $script:stFail++ } }
  $script:stFail = 0
  # regex cases: name, a real line it must match, a real line it must not (-match, case-insensitive as the kit uses it)
  $RxCases = @(
    @('S3ppOff', "[patches.AdaptiveWait]`r`nenabled = true", "[patches.AdaptiveWait]`r`nenabled = false"),
    @('Eol', "a`r`nb", 'ab'),
    @('SnapBinFile', 'ts3native.log', 'TS3W.exe'),
    @('SnapRemixFile', 'remix-dxvk.log', 'd3d9.dll'),
    @('Any', 'S3SS.toml', ''),
    @('MaskOtherUser', 'D:\Users\Bob\Documents\Electronic Arts', 'C:\Users\Public\Documents'),
    @('AnswerSep', '1,y', '1'),
    @('FileNameBad', '%USERPROFILE%\x.ini', 'Options.ini'),
    @('IniSection', '[patches.AdaptiveWait]', 'enabled = true'),
    @('IniKeyValue', 'tickRateLimit = 480', '[patches.SmoothPatchPrecise]'),
    @('IniKey', 'enabled = true', '# frameRateLimit 60 caps the game'),
    @('SimsWord', 'The Sims 3', 'Steam'),
    @('Sims3Name', 'The Sims 3 Late Night', 'The Sims 4'),
    @('UninstallSims3', 'The Sims 3 Late Night', 'The Sims 4'),
    @('VdfPath', '		"path"		"D:\\SteamLibrary"', '		"label"		""'),
    @('DocsDir', 'OneDrive - Personal', 'Downloads'),
    @('UserSims3Dir', 'Die Sims 3', 'The Sims 4'),
    @('LastPathPart', 'PCI\VEN_10DE&DEV_249D\4&1', 'NoBackslash'),
    @('Ts3OrSims', 'C:\Games\The Sims 3\Game\Bin\TS3W.exe', 'C:\Windows\explorer.exe'),
    @('VcRedist', 'Microsoft Visual C++ 2015-2022 Redistributable (x86) - 14.40.33810', 'Microsoft Edge WebView2 Runtime'),
    @('GameEvent', 'Faulting application name: TS3W.exe, version: 1.67.2.24002', 'Faulting application name: chrome.exe'),
    @('Ws', 'a  b', 'ab'),
    @('Ts3wExe', 'C:\Games\The Sims 3\Game\Bin\TS3W.exe', 'C:\Games\The Sims 3\Game\Bin\Sims3Launcher.exe'),
    @('RunAsAdmin', '~ RUNASADMIN', '~ HIGHDPIAWARE'),
    @('ProgramFiles', 'C:\Program Files (x86)\Steam\steamapps\common\The Sims 3\Game\Bin', 'D:\Games\The Sims 3\Game\Bin'),
    @('LoaderDll', 'dinput8.dll', 'd3dx9_31.dll'),
    @('ApexAsi', 'ApexRadiance.asi', 'ApexRadiance.toml'),
    @('S3ssFile', 'Sims3SettingsSetter.asi', 'ts3native.asi'),
    @('S3ppFile', 'Sims3PerformancePatch.asi', 'Sims3SettingsSetter.asi'),
    @('FixesVersion', 'ts3fixes: ts3fixes 0.1.1 build 23e76546, 447 settings on/valued, 115 off', 'ts3fixes: settings read'),
    @('LogNote', 'SKIP pccheck ([PcCheck] Enabled=0)', 'OK   GC initial heap: 0x00E4E168 imm 0x2800 -> 0x16000'),
    @('VsExt', '.toml', '.dll'),
    @('BinExt', '.jsonl', '.asi'),
    @('ApexTxt', 'Apex Radiance README.txt', 'readme.txt'),
    @('RemixExt', '.conf', '.dds'),
    @('OptKeyValue', 'lastdevice = 0;10de;249d;3070', '[options]'),
    @('DcNumber', 'Number:          0', 'Chipset:         Vendor: 10de, Device: 1080, Board: 00000000, Chipset: 0000'),
    @('DcNameDriver', 'Name (driver):   NVIDIA GeForce RTX 3070 Laptop GPU', 'Name (database): Geforce GTX 580 [Found: 1, Matched: 1]'),
    @('DcNameDb', 'Name (database): Geforce GTX 580 [Found: 1, Matched: 1]', 'Name (driver):   NVIDIA GeForce RTX 3070 Laptop GPU'),
    @('DcVendorDevice', 'Chipset:         Vendor: 10de, Device: 1080, Board: 00000000, Chipset: 0000', 'Vendor:          NVIDIA'),
    @('DcDriverVersion', 'Driver version:  65535', 'Driver date:     2024'),
    @('DcTextureMemory', 'Texture memory:  1024MB <<OVERRIDE>>', 'Number:          0'),
    @('DcMatched', 'Geforce GTX 580 [Found: 1, Matched: 1]', 'Unknown [Found: 0, Matched: 0]'),
    @('LastDeviceSep', '0;10de;249d', '0'),
    @('UserCfgExt', '.bak', '.package'),
    @('CrashFile', 'xcpt_1234.txt', 'ScriptError_1.xml'),
    @('NotPlayUser', 'The Sims Medieval', 'The Sims 3'),
    @('RunExt', 'TS3W.exe', 'TS3W.exe.orig'),
    @('NotGameExe', 'CAW.exe CAW', 'TS3W.exe TS3W'),
    @('ClientExe', 'steam.exe', 'steamwebhelper.exe'),
    @('ClientArgs', '-applaunch 47890 The Sims 3', '-applaunch 1222670 The Sims 4'),
    @('SteamApps', 'C:\Program Files (x86)\Steam\steamapps\common\The Sims 3', 'D:\Games\The Sims 3'),
    @('OriginName', 'C:\Program Files (x86)\Origin\Origin.exe', 'C:\Program Files\Electronic Arts\EA Desktop\EA Desktop\EADesktop.exe'),
    @('Yes', 'y', 'n'),
    @('Number', '2', '2a'),
    @('TomlKeyValue', 'frameRateLimit = 60', '[patches.SmoothPatchPrecise]'),
    @('WindowsDir', 'C:\Windows\System32\ntdll.dll', 'C:\Games\The Sims 3\Game\Bin\ts3native.asi'),
    @('CfgFile', 'ts3native.ini', 'ts3native.log'),
    @('IniTomlFile', 'S3SS.toml', 'S3SS.log'),
    @('CollectKey', 'C', 'x'),
    @('SessionFile', 'ScriptError_1.xml', 'TS3W.exe'),
    @('LastDeviceChange', 'Options.ini lastdevice 0;10de;249d -> 0;10de;1080', 'Options.ini lastdevicex 1'),
    @('CopyName', 'The Sims 3 Backup', 'The Sims 3'),
    @('Diag', 'D', 'N'),
    @('LoaderDllInstall', 'winmm.dll', 'binkw32.dll'),
    @('FixesPkg', 'ts3fixes.package', 'NRaas_Overwatch.package'),
    @('PkgVersion', 'ts3fixes 0.2.13-exp', 'ts3fixes 1.0'),
    @('GameBinDir', 'D:\Games\The Sims 3\Game\Bin', 'D:\Games\The Sims 3\Game')
  )
  foreach ($c in $RxCases) { Check (("$($c[1])" -match $Rx[$c[0]]) -and -not ("$($c[2])" -match $Rx[$c[0]])) "regex $($c[0])" }
  Check (@($Rx.Keys | Where-Object { $k = $_; -not @($RxCases | Where-Object { $_[0] -eq $k }).Count }).Count -eq 0) every $Rx regex has cases
  $m = MergeToml "[patches.A]`r`nenabled = false`r`nkeep = 7`r`n`r`n[mine.x]`r`nv = 1`r`n" "[patches.A]`nenabled = true`nrate = 60`n`n[patches.B]`nenabled = true`n"
  Check ($m -match '(?s)\[patches\.A\]\r\nenabled = true\r\nkeep = 7\r\nrate = 60\r\n\r\n\[mine\.x\]\r\nv = 1') 'merge sets our keys, keeps theirs, adds a missing key inside its section'
  Check ($m -match '(?s)\[mine\.x\].*\[patches\.B\]\r\nenabled = true\r\n$') 'merge appends a missing section'
  Check ((MergeToml $m "[patches.A]`nenabled = true`nrate = 60`n`n[patches.B]`nenabled = true`n") -eq $m) 'merge twice = same text'
  $t = S3ppTune "[patches.Mimalloc]`nenabled = true`n`n[patches.OversizedThreadStackFix]`nenabled = true`n`n[patches.AdaptiveWait]`r`nenabled = true`r`n"
  Check ($t -eq "[patches.Mimalloc]`nenabled = true`n`n[patches.OversizedThreadStackFix]`nenabled = false`n`n[patches.AdaptiveWait]`r`nenabled = false`r`n") 'S3PP: AdaptiveWait and OversizedThreadStackFix off, others kept'
  Check ((MergeToml "[patches.AdaptiveWait]`r`nenabled = true`r`n" (S3ppTune "[patches.AdaptiveWait]`nenabled = true`n")) -eq "[patches.AdaptiveWait]`r`nenabled = false`r`n") 'S3PP: a player''s AdaptiveWait = true is turned off'
  Check ((TextSha (Join-Path $payload 'S3SS-tunings.toml')) -eq $RcTomlSha) 'payload S3SS-tunings.toml matches its pin'
  Check ((MergeToml '' "[a.b]`nx = 1`n") -eq "[a.b]`r`nx = 1`r`n") 'merge into an empty file'
  InitMask 'C:\Users\<user> Doe' 'Jane Doe' 'JANE-PC' 'S-1-5-21-1-2-3-1001'
  Check ((Mask 'C:\Users\<user> Doe\Documents\x.ini on JANE-PC') -eq '%USERPROFILE%\Documents\x.ini on <pc>') 'mask: profile path and computer name'
  Check ((Mask '"C:\\Users\\<user> Doe\\Documents"') -eq '"%USERPROFILE%\\Documents"') 'mask: JSON-escaped path'
  Check ((Mask 'User: jane doe') -eq 'User: <user>') 'mask: bare user name, any case'
  Check ((Mask 'D:\Users\Bob\Documents\Electronic Arts') -eq 'D:\Users\<other-user>\Documents\Electronic Arts') 'mask: another profile'
  Check ((Mask 'C:\Users\Public\Documents and Janet') -eq 'C:\Users\Public\Documents and Janet') 'mask: leaves Public and longer words alone'
  $tmp = Join-Path ([System.IO.Path]::GetTempPath()) "ts3kit-selftest-$PID.exe"
  $b = New-Object byte[] 4096; $b[0] = 0x4D; $b[1] = 0x5A; $b[0x3C] = 0x80; $b[0x80] = 0x50; $b[0x81] = 0x45
  [BitConverter]::GetBytes([uint32]0x52D872DA).CopyTo($b, 0x88); [BitConverter]::GetBytes([uint16]0x0122).CopyTo($b, 0x96); [BitConverter]::GetBytes([uint32]0xE5B000).CopyTo($b, 0x80 + 24 + 56)
  [System.IO.File]::WriteAllBytes($tmp, $b)
  try { $pe = PeInfo $tmp; Check ($pe.edition -eq 'retail 1.67' -and $pe.largeAddressAware) 'exe header: retail 1.67 with Large Address Aware' } finally { Remove-Item -LiteralPath $tmp -Force }
  Check ((@('Sims 3 Backup', 'The Sims 3 Backup', 'The Sims 3 - Copy', 'The Sims 3 (2)', 'The Sims 3 old', 'Copia de The Sims 3', 'The Sims 3 bak') | Where-Object { -not (IsCopyName $_) }).Count -eq 0 -and
    (@('The Sims 3', 'Die Sims 3', 'Sims 3', 'The Sims 3 Gold') | Where-Object { IsCopyName $_ }).Count -eq 0) 'copy / backup folder names'
  $fx = Join-Path ([System.IO.Path]::GetTempPath()) "ts3kit-selftest-$PID"
  try {
    function Fx([string]$name, [int]$ageDays, [bool]$opts = $true, [string]$docs = 'Docs') {
      $p = Join-Path $fx "$docs\Electronic Arts\$name"; [void](New-Item -ItemType Directory -Force -Path (Join-Path $p 'Saves\Town.sims3'))
      if ($opts) { Set-Content -LiteralPath (Join-Path $p 'Options.ini') -Value '[options]' }
      $t = (Get-Date).AddDays(-$ageDays); Get-ChildItem -LiteralPath $p -Recurse -Force | ForEach-Object { $_.LastWriteTime = $t }; (Get-Item -LiteralPath $p).LastWriteTime = $t
      return [ordered]@{ path = $p; expected = ($docs -eq 'Docs') }
    }
    $main = Fx 'The Sims 3' 5; $bak = Fx 'The Sims 3 Backup' 1; $bak2 = Fx 'Sims 3 Backup' 0
    $r = @(RankUsers @($bak2, $bak, $main))
    Check ($r.Count -eq 1 -and $r[0].path -eq $main.path) 'user folder: "The Sims 3" wins over newer "Sims 3 Backup" / "The Sims 3 Backup" (sorted first)'
    Check (@(RankUsers @($bak2, $bak)).Count -eq 0) 'user folder: only backups = none picked'
    $die = Fx 'Die Sims 3' 2
    $r = @(RankUsers @($main, $die))
    Check ($r.Count -eq 2 -and $r[0].path -eq $die.path) 'user folder: two real-looking folders = both offered (ask), newest first'
    $plain = Fx 'Sims 3' 0; $noopt = Fx 'Les Sims 3' 9 $false; $other = Fx 'The Sims 3' 0 $true 'OtherDocs'
    $r = @(RankUsers @($plain, $main, $other))
    Check ($r.Count -eq 1 -and $r[0].path -eq $main.path) 'user folder: game''s own name in the game''s Documents beats a renamed folder and another Documents folder'
    $r = @(RankUsers @($noopt, $main))
    Check ($r.Count -eq 1 -and $r[0].path -eq $main.path) 'user folder: Options.ini decides between game-named folders'
    $asi = Join-Path $fx 'v.asi'; [System.IO.File]::WriteAllBytes($asi, [System.Text.Encoding]::ASCII.GetBytes("x0.36.2`0ts3native 0.36.276-abdiag2`0release: 0.36.276-abdiag2`0"))
    Check ((AsiVersion $asi) -eq '0.36.276-abdiag2' -and (AsiVersion (Join-Path $fx 'none.asi')) -eq '?') 'asi version read-back'
  } finally { if (Test-Path -LiteralPath $fx) { Remove-Item -LiteralPath $fx -Recurse -Force } }
  if ($script:stFail) { Write-Host "SelfTest: $($script:stFail) FAIL" -ForegroundColor Red; exit 1 } else { Write-Host 'SelfTest: 0 FAIL' }
}

# ---------- main ----------
if ($Mode -eq 'SelfTest') { SelfTest; exit 0 }
$sid = Try1 { [Security.Principal.WindowsIdentity]::GetCurrent().User.Value } ''
InitMask $env:USERPROFILE $env:USERNAME $env:COMPUTERNAME $sid
try {
  switch ($Mode) {
    'Diag' { [void](RunDiag @()) }
    'Capture' { if (-not $GameDir) { throw 'Capture needs -GameDir "<game folder>"' }; [void](RunCapture $GameDir.TrimEnd('\') $Launch) }
    'Install' { if (-not $GameDir) { throw 'Install needs -GameDir "<game folder>"' }; $u = $UserDir; if (-not $u -and (-not $NoSystem -or $DocsDir)) { $u = PickUser }; if (-not (DoInstall $GameDir.TrimEnd('\') $u ([bool]$S3SS))) { exit 2 } }
    'Uninstall' { if (-not $GameDir) { throw 'Uninstall needs -GameDir "<game folder>"' }; $u = $UserDir; if (-not $u -and (-not $NoSystem -or $DocsDir)) { $u = PickUser }; if (-not (DoUninstall $GameDir.TrimEnd('\') $u)) { exit 2 } }
    'Menu' {
      while ($true) {
        Write-Host ''
        Write-Host "TS3 kit $KitVersion (ts3native retail 1.67 test build)" -ForegroundColor Cyan
        Write-Host '  1. Diagnose (read-only): writes a report zip to your Desktop'
        Write-Host '  2. Launch the game your usual way and capture (read-only): compares your settings files before and after, collects all logs'
        Write-Host '  3. Install the retail test build and ts3fixes.package (asks before it writes, keeps a backup)'
        Write-Host '  4. Uninstall: put the earlier files back'
        Write-Host '  5. Quit'
        $c = Read-Host 'Choose 1-5'
        try {
          if ($c -eq '1') { $x = Read-Host 'If you test on a copied game folder, paste its path here (or just press Enter)'; [void](RunDiag @($x)) }
          elseif ($c -eq '2') {
            $g = AskGame; if (-not $g) { continue }
            [void](RunCapture $g '')
          }
          elseif ($c -eq '3') {
            $g = AskGame; if (-not $g) { continue }
            $u = $UserDir; if (-not $u) { $u = PickUser }
            $s = Read-Host 'Also apply the recommended Sims3SettingsSetter (S3SS) tunings to S3SS.toml? Your other S3SS settings stay. (Y/N)'
            [void](DoInstall $g $u ($s -match $Rx.Yes))
          }
          elseif ($c -eq '4') { $g = AskGame; if (-not $g) { continue }; $u = $UserDir; if (-not $u) { $u = PickUser }; [void](DoUninstall $g $u) }
          elseif ($c -eq '5' -or $c -eq '') { break }
        } catch { Say "Stopped: $($_.Exception.Message)" 'Red' }
      }
    }
  }
} catch { Say "Stopped: $($_.Exception.Message)" 'Red'; exit 1 }
