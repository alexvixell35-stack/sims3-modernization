@echo off
rem Packs the ExitDiag logs (and the newest Apex / ts3native / EA crash files) into one zip. Double-click it after the crash.
powershell -NoProfile -ExecutionPolicy Bypass -Command "$t = Get-Content -Raw -LiteralPath '%~f0'; Invoke-Expression ($t -split '\r?\n:PS\r?\n')[-1]"
pause
exit /b
:PS
$user = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Electronic Arts\The Sims 3'
$diag = Join-Path $user 'ExitDiag'
if (-not (Test-Path -LiteralPath $diag)) { Write-Host "No ExitDiag folder in Documents\Electronic Arts\The Sims 3. Is zzzExitDiag.asi in Game\Bin, and did the game start?"; return }
$stage = Join-Path $env:TEMP ('ExitDiag-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $stage | Out-Null
Get-ChildItem -LiteralPath $diag -File | Where-Object { $_.Name -ne 'collect-list.txt' } | Sort-Object LastWriteTime -Descending | Select-Object -First 30 | Copy-Item -Destination $stage
$list = Join-Path $diag 'collect-list.txt'
if (Test-Path -LiteralPath $list) {
  $i = 0
  foreach ($p in Get-Content -LiteralPath $list) {
    if (-not $p) { continue }
    $i++
    $files = @(Get-ChildItem -Path $p -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 5)
    if ($files.Count) { $d = New-Item -ItemType Directory -Path (Join-Path $stage "extra\$i"); $files | Copy-Item -Destination $d }
  }
}
$zip = Join-Path $user ('ExitDiag-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.zip')
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip -Force
Remove-Item -LiteralPath $stage -Recurse -Force
Write-Host "Wrote $zip"
Start-Process explorer.exe -ArgumentList "/select,`"$zip`""
