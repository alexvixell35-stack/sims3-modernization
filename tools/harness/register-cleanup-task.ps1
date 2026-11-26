# Registers the daily cleanup (tools/harness/cleanup.ts all --apply: merged worktrees, build intermediates, old saves, dumps with extracted stacks, docs/283)
# as a Windows scheduled task. PowerShell because Task Scheduler registration is Windows-only (the job itself is TypeScript).
#   powershell -NoProfile -File tools/harness/register-cleanup-task.ps1 [-At 12:30] [-Unregister]
# Own task, not a step of "TS3 Apex Sync" (register-apex-sync-task.ps1 on feature/apex-sync-1119: its own task, daily 12:15 + hourly retries):
# if that task is ever folded into the 12:00 tools update, add `node tools/harness/cleanup.ts all --apply` after its sync step instead.
# Skips anything a running chain/queue/process names; log: probe-out/cleanup/<date>.log.
param([string]$At = '12:30', [string]$Root = '', [switch]$Unregister) # -Root: the main checkout (a worktree copy of this script would point the task at a deletable folder)
$name = 'TS3 Cleanup Daily'
if ($Unregister) { Unregister-ScheduledTask -TaskName $name -Confirm:$false; "removed $name"; return }
$root = if ($Root) { (Resolve-Path $Root).Path } else { (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path }
$node = (Get-Command node).Source
$action = New-ScheduledTaskAction -Execute $node -Argument '--no-warnings tools/harness/cleanup.ts all --apply' -WorkingDirectory $root
$trigger = New-ScheduledTaskTrigger -Daily -At $At
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Hours 2)
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Description 'Daily disk cleanup (docs/283)' -Force | Out-Null
"registered '$name': daily $At, cwd $root"
