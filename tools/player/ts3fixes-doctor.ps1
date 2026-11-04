# ts3fixes doctor for players: finds every copy of ts3fixes, our ts3native.asi and the game's script caches, reports what each
# one is, and (after a Y/N) moves old copies and stale caches into a backup folder on the Desktop, then installs the
# ts3fixes.package placed next to this script. Never deletes anything. Windows only by nature (The Sims 3 PC folders).
#   Run-ts3fixes-doctor.cmd                 report + fix (asks first)
#   Run-ts3fixes-doctor.cmd -ReportOnly     report only
#   Run-ts3fixes-doctor.cmd -GameDir "D:\Games\The Sims 3"
# PowerShell 5.1 compatible. No network, no AI.
#   Run-ts3fixes-doctor.cmd -UserDir "D:\OneDrive\Documents\Electronic Arts\The Sims 3"
param([string]$GameDir = '', [string]$UserDir = '', [switch]$ReportOnly, [switch]$Yes, [switch]$IgnoreRunning)

$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$desktop = [Environment]::GetFolderPath('Desktop')
$report = Join-Path $desktop "ts3fixes-doctor-$stamp.txt"
$userName = $env:USERNAME

function Scrub([string]$s) { if ($userName) { return $s.Replace($userName, '<user>') } else { return $s } }
function Say([string]$s) { $t = Scrub $s; Write-Host $t; Add-Content -Path $report -Value $t -Encoding UTF8 }
$issues = New-Object System.Collections.Generic.List[string]
function Issue([string]$s) { $issues.Add($s) }

# Byte search without loading huge files: stream in 4 MB chunks with an overlap (C#: a PowerShell loop is far too slow for GBs).
Add-Type -Language CSharp -TypeDefinition @'
public static class Ts3Scan {
  // bit k set = needle k found
  public static int Find(string path, byte[][] needles) {
    int found = 0, all = (1 << needles.Length) - 1;
    try {
      using (var fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite, 1 << 20)) {
        const int Chunk = 4 << 20, Keep = 256;
        var buf = new byte[Chunk + Keep]; int keep = 0;
        while (true) {
          int n = fs.Read(buf, keep, Chunk); if (n <= 0) break;
          int len = keep + n;
          for (int k = 0; k < needles.Length; k++) {
            if ((found & (1 << k)) != 0) continue;
            byte[] nd = needles[k]; byte b0 = nd[0];
            for (int i = 0; i <= len - nd.Length; i++) {
              if (buf[i] != b0) continue;
              int j = 1; while (j < nd.Length && buf[i + j] == nd[j]) j++;
              if (j == nd.Length) { found |= 1 << k; break; }
            }
          }
          if (found == all) break;
          keep = System.Math.Min(Keep, len); System.Array.Copy(buf, len - keep, buf, 0, keep);
        }
      }
    } catch { }
    return found;
  }
}
'@
function FileHas([string]$path, [byte[][]]$needles) {
  $bits = [Ts3Scan]::Find($path, $needles); $found = @{}
  for ($k = 0; $k -lt $needles.Count; $k++) { if ($bits -band (1 -shl $k)) { $found[$k] = $true } }
  return $found
}
$ascii = [Text.Encoding]::ASCII; $u16 = [Text.Encoding]::Unicode
$nTs3Fixes = @($ascii.GetBytes('Ts3Fixes'), $u16.GetBytes('ui tasks'))   # 0 = our assembly, 1 = v0.2.4+ marker

function Hash([string]$p) { try { return (Get-FileHash -Algorithm SHA256 -Path $p).Hash.Substring(0, 12) } catch { return '?' } }

Say "ts3fixes doctor $stamp"
# -IgnoreRunning is for our own tests on a copied folder (-UserDir) while a game runs.
if (-not $ReportOnly -and -not $IgnoreRunning -and (Get-Process -Name TS3W, TS3 -ErrorAction SilentlyContinue)) { Say 'The Sims 3 is running. Close the game first, then run this again.'; exit 1 }

# --- user folder(s): Documents\Electronic Arts\<The Sims 3, localized> with Mods or Saves
$docs = [Environment]::GetFolderPath('MyDocuments')
$ea = Join-Path $docs 'Electronic Arts'
# localized names all end in 3 ("The Sims 3", "Die Sims 3", "Les Sims 3"); this skips The Sims 4
$userDirs = @(Get-ChildItem $ea -Directory -ErrorAction SilentlyContinue | Where-Object {
  $_.Name -match '3$' -and ((Test-Path (Join-Path $_.FullName 'Saves')) -or (Test-Path (Join-Path $_.FullName 'Mods'))) })
if ($UserDir) { $userDirs = @(Get-Item -LiteralPath $UserDir -ErrorAction SilentlyContinue) }
if ($userDirs.Count -eq 0) { Say "No Sims 3 user folder found under $ea (use -UserDir `"<folder>`")"; Issue 'Sims 3 user folder not found: nothing in Documents was checked.' }
foreach ($u in $userDirs) {
  Say "user folder: $($u.FullName)"
  if ((Test-Path (Join-Path $u.FullName 'Mods')) -and -not (Test-Path (Join-Path $u.FullName 'Mods\Resource.cfg'))) {
    Issue "Mods\Resource.cfg is missing in $($u.FullName): the game loads no mods from Mods\Packages without it." }
}

# --- game folder: -GameDir, this script's folder, registry, Steam libraries, common EA paths
$cands = New-Object System.Collections.Generic.List[string]
if ($GameDir) { $cands.Add($GameDir) }
$cands.Add($here); $cands.Add((Split-Path $here -Parent))
foreach ($k in 'HKLM:\SOFTWARE\WOW6432Node\Sims\The Sims 3', 'HKLM:\SOFTWARE\Sims\The Sims 3') {
  try { $v = (Get-ItemProperty $k -ErrorAction Stop).'Install Dir'; if ($v) { $cands.Add($v) } } catch { }
}
try {
  $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath
  if ($steam) {
    $libs = @($steam)
    $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
    if (Test-Path $vdf) { foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) { $libs += $m.Groups[1].Value.Replace('\\', '\') } }
    foreach ($l in $libs) { $cands.Add((Join-Path $l 'steamapps\common\The Sims 3')) }
  }
} catch { }
foreach ($p in "${env:ProgramFiles(x86)}\Electronic Arts\The Sims 3", "$env:ProgramFiles\EA Games\The Sims 3", "${env:ProgramFiles(x86)}\Origin Games\The Sims 3") { $cands.Add($p) }
$games = @()
foreach ($c in $cands) {
  if (-not $c) { continue }
  foreach ($bin in (Join-Path $c 'Game\Bin'), $c) {
    if ((Test-Path (Join-Path $bin 'TS3W.exe')) -or (Test-Path (Join-Path $bin 'TS3.exe'))) {
      $root = if ((Split-Path $bin -Leaf) -eq 'Bin') { Split-Path (Split-Path $bin -Parent) -Parent } else { $bin }
      $full = (Resolve-Path $root).Path
      if ($games -notcontains $full) { $games += $full }
    }
  }
}
if ($games.Count -eq 0 -and -not $Yes) {
  Write-Host ''
  Write-Host 'Could not find your game folder. Paste the folder that contains TS3W.exe (for example D:\Games\The Sims 3\Game\Bin),'
  $typed = Read-Host 'or press Enter to skip'
  $typed = $typed.Trim('"', ' ')
  if ($typed) {
    foreach ($bin in (Join-Path $typed 'Game\Bin'), $typed) {
      if ((Test-Path (Join-Path $bin 'TS3W.exe')) -or (Test-Path (Join-Path $bin 'TS3.exe'))) {
        $games += if ((Split-Path $bin -Leaf) -eq 'Bin') { (Resolve-Path (Split-Path (Split-Path $bin -Parent) -Parent)).Path } else { (Resolve-Path $bin).Path }
        break
      }
    }
    if ($games.Count -eq 0) { Say "No TS3W.exe or TS3.exe in $typed" }
  }
}
if ($games.Count -eq 0) { Say 'Game folder not found automatically. Run again with -GameDir "<your The Sims 3 folder>" to include it.'; Issue 'Game folder not found: ts3native.asi and copies inside the game folder were not checked.' }
foreach ($g in $games) { Say "game folder: $g" }

# --- the new package shipped next to this script
$newPkg = Join-Path $here 'ts3fixes.package'
$newHash = if (Test-Path $newPkg) { Hash $newPkg } else { '' }
if ($newHash) { Say "new package next to this script: ts3fixes.package sha $newHash" } else { Say 'no ts3fixes.package next to this script (report only for the install step)' }

# --- scan
$moves = New-Object System.Collections.Generic.List[object]
$keepNew = $null
$copies = 0
Say ''
Say '== copies of ts3fixes (by name or by content) =='
$roots = @($userDirs | ForEach-Object { $_.FullName }) + @($games)
foreach ($r in $roots) {
  $files = Get-ChildItem $r -Recurse -File -Include *.package, *.dbc, *.sims3pack -ErrorAction SilentlyContinue | Where-Object {
    $_.FullName -notmatch '\\Saves\\' -and $_.Name -notmatch '^(scriptCache|CASPartCache|compositorCache|simCompositorCache|socialCache)\.package$' }
  foreach ($f in $files) {
    $byName = $f.Name -match 'ts3fixes'
    if (-not $byName -and $f.Length -gt 300MB -and $f.Extension -ne '.dbc') { continue }
    if (-not $byName -and $f.Extension -eq '.package' -and $f.FullName -match '\\Game\\|\\GameData\\') { continue }   # EA's own packs
    $hit = FileHas $f.FullName $nTs3Fixes
    if (-not ($byName -or $hit.ContainsKey(0))) { continue }
    $ver = if ($hit.ContainsKey(1)) { 'v0.2.4 or newer' } else { 'OLDER than v0.2.4' }
    $h = Hash $f.FullName
    $same = ($newHash -and $h -eq $newHash)
    $kind = if ($f.Extension -eq '.dbc') { 'DCCache file (holds installed Sims3Pack content; report only)' } elseif ($same) { 'identical to the new package' } else { $ver }
    Say ("  {0}  ({1:N0} KB, sha {2}): {3}" -f $f.FullName, ($f.Length / 1KB), $h, $kind)
    $copies++
    if ($f.Extension -eq '.dbc') { Issue "An installed-content cache holds a copy of ts3fixes: $($f.FullName). The game loads it even when Mods\Packages has a newer one. Not moved automatically (it can hold other content too); send this report."; continue }
    if (-not $hit.ContainsKey(1) -and -not $same) { Issue "Old ts3fixes (before v0.2.4): $($f.FullName)" }
    if ($f.FullName -match '\\Overrides\\') { Issue "ts3fixes in Mods\Overrides: $($f.FullName) (it belongs in Mods\Packages)" }
    if (-not $byName) { Issue "ts3fixes under another file name: $($f.FullName)" }
    $inPackages = $f.DirectoryName -match '\\Mods\\Packages$'
    if ($same -and $inPackages -and -not $keepNew) { $keepNew = $f.FullName; continue }
    $moves.Add($f.FullName)
  }
}
if ($copies -eq 0) { Say '  none found'; Issue 'No ts3fixes.package found: the script part of the mod is not installed.' }
if ($copies -gt 1) { Issue "$copies copies of ts3fixes found: the game may load an old one. Keep exactly one, in Mods\Packages." }

Say ''
Say '== ts3native.asi / ini in game Bin =='
foreach ($g in $games) {
  $bin = Join-Path $g 'Game\Bin'
  foreach ($a in Get-ChildItem $bin -File -Filter 'ts3native*' -ErrorAction SilentlyContinue | Where-Object { $_.Extension -in '.asi', '.ini' }) {
    $v = ''
    if ($a.Extension -eq '.asi') {
      # the binary holds older version strings too (feature notes): report the highest
      try { $txt = [Text.Encoding]::GetEncoding(28591).GetString([IO.File]::ReadAllBytes($a.FullName))
            $best = [regex]::Matches($txt, '0\.36\.(\d+)') | ForEach-Object { [int]$_.Groups[1].Value } | Sort-Object -Descending | Select-Object -First 1
            if ($best -ne $null) { $v = " version 0.36.$best" } } catch { } }
    Say ("  {0} ({1:N0} KB){2}" -f $a.FullName, ($a.Length / 1KB), $v)
    if ($a.Extension -eq '.asi') { $asiSeen = $true }
  }
  if (-not $asiSeen) { Issue "No ts3native.asi in ${bin}: the native part of the mod is not installed." }
  $asiSeen = $false
}

Say ''
Say '== game script/content caches (safe to remove; the game rebuilds them) =='
foreach ($u in $userDirs) {
  foreach ($n in 'scriptCache.package', 'CASPartCache.package', 'compositorCache.package', 'simCompositorCache.package', 'socialCache.package') {
    $p = Join-Path $u.FullName $n
    if (Test-Path $p) { Say ("  {0} ({1:N0} KB)" -f $p, ((Get-Item $p).Length / 1KB)); $moves.Add($p) }
  }
  $sc = Join-Path $u.FullName 'scriptCache.package'
  if ((Test-Path $sc) -and (Get-Item $sc).Length -gt 0) { Issue 'scriptCache.package exists: it can keep an old ts3fixes running after an update. Clearing the caches is part of the fix.' }
}

Say ''
Say "== Issues found: $($issues.Count) =="
if ($issues.Count -eq 0) { Say '  none' }
$n = 0; foreach ($s in $issues) { $n++; Say "  $n. $s" }

# One zip on the Desktop for the player to send back: this report + the game's ts3native.log (user name scrubbed).
function PackLogs {
  $out = Join-Path $desktop "ts3fixes-doctor-$stamp.zip"
  $tmp = Join-Path $env:TEMP "ts3fixes-doctor-$stamp"
  New-Item -ItemType Directory -Path $tmp -Force | Out-Null
  Copy-Item -LiteralPath $report -Destination $tmp
  $gi = 0
  foreach ($g in $games) {
    $gi++
    $log = Join-Path $g 'Game\Bin\ts3native.log'
    if (-not (Test-Path $log)) { continue }
    try {   # shared read: the game keeps the log open while it runs
      $fs = [IO.File]::Open($log, 'Open', 'Read', 'ReadWrite'); $sr = New-Object IO.StreamReader($fs); $txt = $sr.ReadToEnd(); $sr.Close()
      $name = if ($gi -eq 1) { 'ts3native.log' } else { "ts3native-$gi.log" }
      Scrub $txt | Set-Content -Path (Join-Path $tmp $name) -Encoding UTF8
    } catch { Add-Content -Path (Join-Path $tmp 'ts3native-log-missing.txt') -Value "could not read $(Scrub $log): $($_.Exception.Message)" }
  }
  Compress-Archive -Path (Join-Path $tmp '*') -DestinationPath $out -Force
  Remove-Item -LiteralPath $tmp -Recurse -Force
  Write-Host ''
  Write-Host "Send this file back to us (Discord or the GitHub issue): $out"
}

Say ''
if ($ReportOnly) { Say "Report only. Report saved to $report"; PackLogs; exit 0 }
if ($moves.Count -eq 0 -and ($keepNew -or -not $newHash)) { Say "Nothing to move. Report saved to $report"; PackLogs; exit 0 }

$backup = Join-Path $desktop "ts3fixes-backup-$stamp"
Say "Plan: move $($moves.Count) file(s) to $backup (old ts3fixes copies and the caches above), then install the new ts3fixes.package into Mods\Packages."
$ans = if ($Yes) { 'Y' } else { Read-Host 'Go ahead? (Y/N)' }
if ($ans -notmatch '^[Yy]') { Say "Nothing changed. Report saved to $report"; PackLogs; exit 0 }

New-Item -ItemType Directory -Path $backup -Force | Out-Null
$i = 0
foreach ($m in $moves) {
  $i++
  $dest = Join-Path $backup ("{0:D2}_{1}" -f $i, (Split-Path $m -Leaf))
  try { Move-Item -LiteralPath $m -Destination $dest -ErrorAction Stop; Add-Content (Join-Path $backup 'where-they-came-from.txt') "$dest <- $m"; Say "  moved $m" }
  catch { Say "  could not move $m : $($_.Exception.Message)" }
}
if ($newHash -and -not $keepNew) {
  foreach ($u in $userDirs) {
    $pk = Join-Path $u.FullName 'Mods\Packages'
    if (Test-Path (Join-Path $u.FullName 'Mods')) {
      New-Item -ItemType Directory -Path $pk -Force | Out-Null
      Copy-Item -LiteralPath $newPkg -Destination (Join-Path $pk 'ts3fixes.package') -Force
      Say "  installed the new ts3fixes.package into $pk"
    }
  }
}
Say ''
Say "Done. To undo, move the files in $backup back (where-they-came-from.txt lists each one)."
Say "Report saved to $report."
PackLogs
Write-Host 'Then start the game once, try the family picker, quit, and run this doctor again (answer N): that zip carries the new game log.'
