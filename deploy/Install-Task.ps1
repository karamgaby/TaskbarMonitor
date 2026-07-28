# Registers the TaskbarMonitor scheduled task: at logon, elevated (no UAC prompt), 15 s delay
# so explorer/taskbar/network exist first. Run this script elevated.
param(
    [string]$ExePath = (Join-Path (Split-Path $PSScriptRoot -Parent) 'publish\TaskbarMonitor.exe'),
    [switch]$StartNow
)

$ErrorActionPreference = 'Stop'

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'Run this script from an elevated PowerShell (RunLevel Highest registration requires it).'
}

if (-not (Test-Path $ExePath)) {
    Write-Error "Executable not found: $ExePath  (run 'dotnet publish' first, or pass -ExePath)"
}

# PawnIO is the signed kernel driver LibreHardwareMonitor uses for MSR reads (CPU package temp).
# Without it the app still runs but CPU temp shows '--'. It is NOT bundled (and WinRing0 must
# never be used — Defender flags it). Installer: https://pawnio.eu/
$pawnio = Get-Service -Name 'PawnIO' -ErrorAction SilentlyContinue
if (-not $pawnio) {
    Write-Warning 'PawnIO driver service NOT FOUND. CPU temperature will show "--". Install PawnIO from https://pawnio.eu/ to enable it.'
} elseif ($pawnio.Status -ne 'Running') {
    Write-Warning "PawnIO service present but $($pawnio.Status). CPU temperature may show '--' until it runs."
} else {
    Write-Host 'PawnIO driver service: Running (CPU temp available)' -ForegroundColor Green
}

$action    = New-ScheduledTaskAction -Execute $ExePath -WorkingDirectory (Split-Path $ExePath -Parent)
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User "$env:USERDOMAIN\$env:USERNAME"
$trigger.Delay = 'PT15S'
$taskPrincipal = New-ScheduledTaskPrincipal -UserId "$env:USERDOMAIN\$env:USERNAME" -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
             -ExecutionTimeLimit ([TimeSpan]::Zero) -StartWhenAvailable -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName 'TaskbarMonitor' -Action $action -Trigger $trigger `
    -Principal $taskPrincipal -Settings $settings -Force | Out-Null
Write-Host "Scheduled task 'TaskbarMonitor' registered: at logon of $env:USERNAME, 15 s delay, highest privileges." -ForegroundColor Green

if ($StartNow) {
    Start-ScheduledTask -TaskName 'TaskbarMonitor'
    Write-Host 'Task started.' -ForegroundColor Green
}
