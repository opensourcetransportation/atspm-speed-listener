#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]] $SensorRemoteAddress,
    [string] $InstallPath = $PSScriptRoot
)
$ErrorActionPreference = 'Stop'
$InstallPath = (Resolve-Path -LiteralPath $InstallPath).Path
$exe = Join-Path $InstallPath 'SpeedListener.exe'
$settings = Join-Path $InstallPath 'appsettings.json'
if (!(Test-Path -LiteralPath $exe)) { throw "SpeedListener.exe is missing from $InstallPath" }
$config = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
$port = [int]$config.SpeedListenerConfiguration.UdpPort
if ($port -lt 1 -or $port -gt 65535) { throw 'Invalid UDP port.' }
if (Get-Service -Name AtspmSpeedListener -ErrorAction SilentlyContinue) {
    throw 'AtspmSpeedListener already exists. Stop it and follow the upgrade instructions.'
}
if (Get-NetFirewallRule -Name AtspmSpeedListener-UDP -ErrorAction SilentlyContinue) {
    throw 'The AtspmSpeedListener-UDP firewall rule already exists. Inspect it before retrying.'
}

# The package contains database credentials. Restrict the installed directory.
& icacls.exe $InstallPath /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)RX'
if ($LASTEXITCODE -ne 0) { throw 'Could not secure the installation directory.' }
if (![System.Diagnostics.EventLog]::SourceExists('AtspmSpeedListener')) {
    [System.Diagnostics.EventLog]::CreateEventSource('AtspmSpeedListener', 'Atspm')
}
New-Service -Name AtspmSpeedListener -DisplayName 'ATSPM Speed Listener' `
    -BinaryPathName ('"{0}" listener' -f $exe) -StartupType Automatic `
    -Description 'Receives UDP speed events and writes them to the ATSPM development databases.'
& sc.exe config AtspmSpeedListener obj= 'NT AUTHORITY\LocalService' start= delayed-auto
if ($LASTEXITCODE -ne 0) { throw 'Could not configure the service account.' }
New-NetFirewallRule -Name AtspmSpeedListener-UDP -DisplayName 'ATSPM Speed Listener UDP' `
    -Direction Inbound -Action Allow -Protocol UDP -LocalPort $port -Program $exe `
    -RemoteAddress $SensorRemoteAddress -Profile Any | Out-Null
Write-Host 'Service installed as LocalService. Start it with: Start-Service AtspmSpeedListener'
