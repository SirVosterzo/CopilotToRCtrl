# Installs CopilotToRCtrl to Program Files and runs it elevated at logon via a scheduled task.
# Self-elevates (one UAC prompt). Run build.ps1 first.
$ErrorActionPreference = 'Stop'
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -User `"$($id.Name)`""
    exit
}

$user = if ($args.Count -ge 2 -and $args[0] -eq '-User') { $args[1] } else { $id.Name }
$src  = Join-Path $PSScriptRoot 'CopilotToRCtrl.exe'
$dir  = Join-Path $env:ProgramFiles 'CopilotToRCtrl'
$exe  = Join-Path $dir 'CopilotToRCtrl.exe'
$task = 'CopilotToRCtrl'

if (-not (Test-Path $src)) { throw "Missing $src - run build.ps1 first." }

Get-Process CopilotToRCtrl -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
New-Item -ItemType Directory -Force $dir | Out-Null
Copy-Item $src $exe -Force

$action    = New-ScheduledTaskAction -Execute $exe
$trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
             -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1) `
             -MultipleInstances IgnoreNew -Priority 4
Register-ScheduledTask -TaskName $task -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
Start-ScheduledTask -TaskName $task

Write-Host "Installed to $exe and registered scheduled task '$task' for $user."
Start-Sleep -Seconds 2
