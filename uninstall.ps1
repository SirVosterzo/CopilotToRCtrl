# Removes the CopilotToRCtrl scheduled task and Program Files folder. Self-elevates.
$ErrorActionPreference = 'Stop'
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not ([Security.Principal.WindowsPrincipal]$id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -Wait -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit
}
Unregister-ScheduledTask -TaskName 'CopilotToRCtrl' -Confirm:$false -ErrorAction SilentlyContinue
Get-Process CopilotToRCtrl -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 300
Remove-Item -Recurse -Force (Join-Path $env:ProgramFiles 'CopilotToRCtrl') -ErrorAction SilentlyContinue
Write-Host 'CopilotToRCtrl uninstalled.'
Start-Sleep -Seconds 2
