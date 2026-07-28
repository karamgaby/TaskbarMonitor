# Removes the TaskbarMonitor scheduled task and stops any running instance.
$ErrorActionPreference = 'SilentlyContinue'
Stop-ScheduledTask -TaskName 'TaskbarMonitor'
Unregister-ScheduledTask -TaskName 'TaskbarMonitor' -Confirm:$false
Get-Process -Name 'TaskbarMonitor' | Stop-Process -Force
Write-Host "TaskbarMonitor task removed."
