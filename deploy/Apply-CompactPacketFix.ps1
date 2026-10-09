#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$InstallPath = 'C:\Services\ATSPM-SpeedListener-win-x64')
$ErrorActionPreference = 'Stop'
$sourceDll = Join-Path $PSScriptRoot 'SpeedListener.dll'
$targetDll = Join-Path $InstallPath 'SpeedListener.dll'
if (!(Test-Path -LiteralPath $sourceDll)) { throw 'Extract the complete compact-packet-fix ZIP first.' }
$settings = Get-Content -LiteralPath (Join-Path $InstallPath 'appsettings.json') -Raw | ConvertFrom-Json
if ($settings.SpeedListenerConfiguration.UdpPort -ne 10089) {
    throw 'Restore the new listener to port 10089 before applying this patch.'
}
$oldService = Get-CimInstance Win32_Service -Filter "Name='SpeedListener'"
if ($oldService.State -ne 'Running') { throw 'The old listener must be running before updating the new listener.' }
$oldEndpoint = Get-NetUDPEndpoint -LocalPort 10088 -ErrorAction SilentlyContinue |
    Where-Object OwningProcess -eq $oldService.ProcessId
if (!$oldEndpoint) { throw 'The old listener must own UDP port 10088 before updating the new listener.' }

Write-Host 'Stopping only AtspmSpeedListener. Allow up to 60 seconds for shutdown.'
$newService = Get-Service -Name AtspmSpeedListener
if ($newService.Status -ne 'Stopped') {
    if ($newService.Status -ne 'StopPending') { $newService.Stop() }
    $newService.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(60))
}
$backupPath = $targetDll + '.before-compact-packet-fix-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
Copy-Item -LiteralPath $targetDll -Destination $backupPath
Copy-Item -LiteralPath $sourceDll -Destination $targetDll -Force
Write-Host "DLL backup: $backupPath"
Write-Host 'Starting the new listener on its configured port 10089...'
Start-Service -Name AtspmSpeedListener
Start-Sleep -Seconds 10
Get-CimInstance Win32_Service -Filter "Name='SpeedListener' OR Name='AtspmSpeedListener'" |
    Select-Object Name, State, ProcessId | Format-Table -AutoSize
Get-NetUDPEndpoint -LocalPort 10088,10089 -ErrorAction SilentlyContinue |
    Select-Object LocalPort, OwningProcess | Format-Table -AutoSize
Write-Host 'Latest startup output:'
Get-Content -LiteralPath (Join-Path $InstallPath 'startup-diagnostic.log') -Tail 20
