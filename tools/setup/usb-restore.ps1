# Unpack the RAR sets that `node tools/usb-backup.ts --rar` wrote to a USB drive onto another Windows machine.
# PowerShell, not TypeScript: it runs on a fresh machine before Node and the repo exist (Windows-only by design).
# Existing files are never overwritten (a save or setting already on this machine wins).
#   powershell -ExecutionPolicy Bypass -File usb-restore.ps1 [-Repo <clone folder>] [-Sets saves,mods,probe,gamecopy,downloads,memory] [-Settings]
# -Settings also restores the game's *.ini files (off by default: graphics settings belong to the other PC's hardware).
param(
  [string]$Repo = "$HOME\IdeaProjects\sims-3-modernization",
  [string[]]$Sets = @('saves', 'mods', 'probe', 'gamecopy', 'downloads', 'memory'),
  [switch]$Settings
)
$ErrorActionPreference = 'Stop'
$src = Join-Path $PSScriptRoot 'ts3-modernization-backup'
if (-not (Test-Path $src)) { throw "no backup folder at $src (run this script from the USB drive)" }

# 7-Zip or WinRAR, either extracts multi-volume RAR
$sevenZip = @("$env:ProgramFiles\7-Zip\7z.exe", "${env:ProgramFiles(x86)}\7-Zip\7z.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
$unrar = @("$env:ProgramFiles\WinRAR\UnRAR.exe", "${env:ProgramFiles(x86)}\WinRAR\UnRAR.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $sevenZip -and -not $unrar) {
  Write-Host 'no 7-Zip or WinRAR: installing 7-Zip with winget'
  winget install --id 7zip.7zip -e --accept-source-agreements --accept-package-agreements
  $sevenZip = "$env:ProgramFiles\7-Zip\7z.exe"
}

$docs = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Electronic Arts\The Sims 3'
# Claude Code keeps project memory under ~/.claude/projects/<repo path with : \ / turned into ->/memory
$claudeProject = ($Repo -replace '[:\\/]', '-')
$dest = @{
  saves     = Join-Path $docs 'Saves'
  settings  = $docs
  mods      = Join-Path $docs 'Mods'
  probe     = Join-Path $Repo 'probe-out'
  gamecopy  = Join-Path $Repo 'game-copy'
  downloads = Join-Path $HOME 'Downloads\ts3-fixes'
  memory    = Join-Path $HOME ".claude\projects\$claudeProject\memory"
}
$todo = @($Sets) + $(if ($Settings) { 'settings' } else { @() })

foreach ($set in $todo) {
  $first = Get-ChildItem $src -Filter "$set.part1.rar" -ErrorAction SilentlyContinue | Select-Object -First 1
  if (-not $first) { $first = Get-ChildItem $src -Filter "$set.rar" -ErrorAction SilentlyContinue | Select-Object -First 1 }
  if (-not $first) { Write-Warning "${set}: no archive on the drive, skipped"; continue }
  $to = $dest[$set]
  New-Item -ItemType Directory -Force $to | Out-Null
  Write-Host "${set}: $($first.Name) -> $to"
  if ($sevenZip) { & $sevenZip x $first.FullName "-o$to" -aos -y | Out-Null }   # -aos: skip files that exist
  else { & $unrar x -o- -y $first.FullName "$to\" | Out-Null }                     # -o-: never overwrite
  if ($LASTEXITCODE -gt 1) { Write-Warning "${set}: extractor exit code $LASTEXITCODE" } else { Write-Host "${set}: done" }
}
Write-Host 'restore finished. The game-copy/ and Ghidra project contain EA game bytes: personal use only, never upload them.'
if ($Sets -contains 'gamecopy') { Write-Host 'Note: our Ghidra project and native patches target the Steam TS3W.exe 1.67; an EA app 1.69 exe has different addresses.' }
