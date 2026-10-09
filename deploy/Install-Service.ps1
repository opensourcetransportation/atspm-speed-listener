#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]] $SensorRemoteAddress,
    [Parameter(Mandatory = $true)][string] $InstallPath,
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$ServiceName = 'AtspmSpeedListener',
    [string]$DisplayName = 'ATSPM Speed Listener',
    [string]$FirewallRuleName
)
$ErrorActionPreference = 'Stop'
$InstallPath = (Resolve-Path -LiteralPath $InstallPath).Path
$exe = Join-Path $InstallPath 'SpeedListener.exe'
$settings = Join-Path $InstallPath 'appsettings.json'
if (!(Test-Path -LiteralPath $exe)) { throw "SpeedListener.exe is missing from $InstallPath" }
$config = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
$port = [int]$config.SpeedListenerConfiguration.UdpPort
if ($port -lt 1 -or $port -gt 65535) { throw 'Invalid UDP port.' }
if (!$FirewallRuleName) { $FirewallRuleName = "$ServiceName-UDP" }
if (Get-Service -Name $ServiceName -ErrorAction SilentlyContinue) {
    throw "$ServiceName already exists. Follow the upgrade instructions."
}
if (Get-NetFirewallRule -Name $FirewallRuleName -ErrorAction SilentlyContinue) {
    throw "Firewall rule $FirewallRuleName already exists. Inspect it before retrying."
}

# Deployment settings may contain credentials. Restrict the installed directory.
& icacls.exe $InstallPath /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-19:(OI)(CI)RX'
if ($LASTEXITCODE -ne 0) { throw 'Could not secure the installation directory.' }
if (![System.Diagnostics.EventLog]::SourceExists('AtspmSpeedListener')) {
    [System.Diagnostics.EventLog]::CreateEventSource('AtspmSpeedListener', 'Atspm')
}
New-Service -Name $ServiceName -DisplayName $DisplayName `
    -BinaryPathName ('"{0}" listener' -f $exe) -StartupType Automatic `
    -Description 'Receives UDP speed events and writes them to the configured ATSPM databases.'
New-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" `
    -Name Environment -PropertyType MultiString -Value @("ATSPM_SERVICE_NAME=$ServiceName") -Force | Out-Null
& sc.exe config $ServiceName obj= 'NT AUTHORITY\LocalService' start= delayed-auto
if ($LASTEXITCODE -ne 0) { throw 'Could not configure the service account.' }
New-NetFirewallRule -Name $FirewallRuleName -DisplayName "$DisplayName UDP" `
    -Direction Inbound -Action Allow -Protocol UDP -LocalPort $port -Program $exe `
    -RemoteAddress $SensorRemoteAddress -Profile Any | Out-Null
Write-Host "Service installed as LocalService. Start it with: Start-Service '$ServiceName'"
