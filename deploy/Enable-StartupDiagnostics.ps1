<#
.SYNOPSIS
Enables startup logging for the selected listener service.
.DESCRIPTION
Uses the registered service account and preserves its running or stopped state.
LogPath defaults to startup-diagnostic.log in the supplied installation directory.
The application caps this temporary capture at 10 MiB, clearing older contents
when full. Normal operational logging continues through the ATSPM providers.
.EXAMPLE
.\Enable-StartupDiagnostics.ps1 -InstallPath 'D:\Apps\SpeedListener' -ServiceName RegionalSpeedListener -LogPath 'D:\Diagnostics\listener.log'
#>

#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallPath,
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$ServiceName = 'AtspmSpeedListener',
    [string]$LogPath,
    [ValidateRange(1,300)][int]$TimeoutSeconds = 60
)
$ErrorActionPreference = 'Stop'
$install = (Resolve-Path -LiteralPath $InstallPath).Path.TrimEnd('\')
if (!$LogPath) { $LogPath = Join-Path $install 'startup-diagnostic.log' }
$LogPath = [IO.Path]::GetFullPath($LogPath)
$registration = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
if (!$registration -or $registration.State -notin @('Running','Stopped')) {
    throw 'The service must be installed and either running or stopped.'
}
$exePath = if ($registration.PathName -match '^"([^"]+)"') { $Matches[1] } else { ($registration.PathName -split '\s+')[0] }
if ([IO.Path]::GetFullPath($exePath) -ne (Join-Path $install 'SpeedListener.exe')) {
    throw 'The service executable does not match InstallPath.'
}
$account = if ($registration.StartName -eq 'LocalSystem') { '*S-1-5-18' } else { $registration.StartName }
New-Item -ItemType Directory -Path (Split-Path $LogPath -Parent) -Force | Out-Null
if (!(Test-Path -LiteralPath $LogPath)) { New-Item -ItemType File -Path $LogPath | Out-Null }
& icacls.exe $LogPath /grant "${account}:M"
if ($LASTEXITCODE -ne 0) { throw 'Could not grant the service account access to the diagnostic log.' }
$wasRunning = $registration.State -eq 'Running'
if ($wasRunning) {
    $service = Get-Service -Name $ServiceName
    $service.Stop()
    $service.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds($TimeoutSeconds))
}
try {
$registryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName"
$existingEnvironment = @((Get-ItemProperty -LiteralPath $registryPath -Name Environment -ErrorAction SilentlyContinue).Environment)
$newEnvironment = @($existingEnvironment | Where-Object { $_ -and $_ -notlike 'ATSPM_STARTUP_LOG=*' })
$newEnvironment += "ATSPM_STARTUP_LOG=$LogPath"
New-ItemProperty -LiteralPath $registryPath -Name Environment -PropertyType MultiString -Value $newEnvironment -Force | Out-Null
}
finally {
    if ($wasRunning) { Start-Service -Name $ServiceName }
}
Write-Host "Startup diagnostics: $LogPath"
Get-Content -LiteralPath $LogPath -Tail 100
