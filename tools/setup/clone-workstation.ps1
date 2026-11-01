# Clone the public repo onto a new Windows workstation and install what the tools need (git, Node 22+).
# PowerShell, not TypeScript: it runs before Node exists on the machine (bootstrap; Windows-only by design).
#   powershell -ExecutionPolicy Bypass -File clone-workstation.ps1 [-Target <folder>]
param([string]$Target = "$HOME\IdeaProjects\sims-3-modernization")
$ErrorActionPreference = 'Stop'

function Need($cmd, $wingetId) {
  if (Get-Command $cmd -ErrorAction SilentlyContinue) { return }
  Write-Host "$cmd not found: installing $wingetId with winget"
  winget install --id $wingetId -e --accept-source-agreements --accept-package-agreements
  $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
  if (-not (Get-Command $cmd -ErrorAction SilentlyContinue)) { throw "$cmd still missing; open a new terminal and run this script again" }
}

Need git 'Git.Git'
Need node 'OpenJS.NodeJS.LTS'
$v = [int]((node --version).TrimStart('v').Split('.')[0])
if ($v -lt 22) { Write-Warning "Node $v found; the tools need Node 22.18 or newer (winget upgrade OpenJS.NodeJS.LTS)" }

if (Test-Path (Join-Path $Target '.git')) {
  Write-Host "$Target already has a clone: pulling main"
  git -C $Target pull --ff-only
} else {
  New-Item -ItemType Directory -Force (Split-Path $Target) | Out-Null
  git clone https://github.com/rodneypantonial/sims3-modernization.git $Target
}
Write-Host "repo ready: $Target"
Write-Host "next: run usb-restore.ps1 from the USB drive with -Repo `"$Target`" to unpack saves, logs and game-copy"
