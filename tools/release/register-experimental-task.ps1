# Registers the daily experimental pre-release job (tools/release/experimental.ts, docs/279) as a Windows scheduled task.
# PowerShell because Task Scheduler registration is Windows-only (the job itself is TypeScript).
#   powershell -NoProfile -File tools/release/register-experimental-task.ps1 [-At 12:30] [-Unregister]
# Runs daily at -At (PHT, after the 12:00 tools update), then every hour for 10 h: experimental.ts skips while a chain, queue,
# job or the game runs, and after the day's release, so the hourly repeats are the "retry later". Runs as the logged-on user
# (needs PERSONAL_GITHUB_TOKEN in the user environment), from the repo root this script lives in. Log: probe-out/experimental/.
param([string]$At = '12:30', [switch]$Unregister)
$name = 'TS3 Experimental Release'
if ($Unregister) { Unregister-ScheduledTask -TaskName $name -Confirm:$false; "removed $name"; return }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$node = (Get-Command node).Source
$action = New-ScheduledTaskAction -Execute $node -Argument 'tools/release/experimental.ts' -WorkingDirectory $root
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$trigger.Repetition = (New-ScheduledTaskTrigger -Once -At $At -RepetitionInterval (New-TimeSpan -Hours 1) -RepetitionDuration (New-TimeSpan -Hours 10)).Repetition
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 2)
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Description 'Daily experimental Sims 3 modernization pre-release (docs/279)' -Force | Out-Null
"registered '$name': daily $At, hourly retries for 10 h, cwd $root"
