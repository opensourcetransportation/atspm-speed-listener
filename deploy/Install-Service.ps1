<#
.SYNOPSIS
Installs the listener using the agency's configured UDP port and sensor sources.
.DESCRIPTION
Requires an explicit installation directory and sensor addresses. Creates a service
and firewall rule; database and cloud settings remain in the agency's configuration.
Use ServiceAccount for a built-in identity or Credential for a dedicated account.
.EXAMPLE
.\Install-Service.ps1 -InstallPath 'D:\Apps\SpeedListener' -SensorRemoteAddress '192.0.2.0/24' -ServiceName RegionalSpeedListener -ServiceAccount NetworkService
#>

#Requires -RunAsAdministrator
[CmdletBinding(DefaultParameterSetName = 'BuiltInAccount')]
param(
    [Parameter(Mandatory = $true)]
    [string[]] $SensorRemoteAddress,
    [Parameter(Mandatory = $true)][string] $InstallPath,
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$ServiceName = 'AtspmSpeedListener',
    [string]$DisplayName = 'ATSPM Speed Listener',
    [string]$FirewallRuleName,
    [Parameter(ParameterSetName = 'BuiltInAccount')]
    [ValidateSet('LocalService','NetworkService','LocalSystem')][string]$ServiceAccount = 'LocalService',
    [Parameter(Mandatory = $true, ParameterSetName = 'DedicatedAccount')]
    [pscredential]$Credential
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
$accountSid = switch ($ServiceAccount) {
    'LocalService' { '*S-1-5-19' }
    'NetworkService' { '*S-1-5-20' }
    'LocalSystem' { '*S-1-5-18' }
}
$accountName = if ($Credential) { $Credential.UserName } else { $ServiceAccount }
$accessIdentity = if ($Credential) { $Credential.UserName } else { $accountSid }
$accessRules = @('*S-1-5-18:(OI)(CI)F', '*S-1-5-32-544:(OI)(CI)F')
if ($accessIdentity -ne '*S-1-5-18') { $accessRules += "${accessIdentity}:(OI)(CI)RX" }
& icacls.exe $InstallPath /inheritance:r /grant:r @accessRules
if ($LASTEXITCODE -ne 0) { throw 'Could not secure the installation directory.' }
if (![System.Diagnostics.EventLog]::SourceExists('AtspmSpeedListener')) {
    [System.Diagnostics.EventLog]::CreateEventSource('AtspmSpeedListener', 'Atspm')
}
$serviceOptions = @{}
if ($Credential) { $serviceOptions.Credential = $Credential }
New-Service @serviceOptions -Name $ServiceName -DisplayName $DisplayName `
    -BinaryPathName ('"{0}" listener' -f $exe) -StartupType Automatic `
    -Description 'Receives UDP speed events and writes them to the configured ATSPM databases.'
New-ItemProperty -LiteralPath "HKLM:\SYSTEM\CurrentControlSet\Services\$ServiceName" `
    -Name Environment -PropertyType MultiString -Value @("ATSPM_SERVICE_NAME=$ServiceName") -Force | Out-Null
$serviceConfig = @('config', $ServiceName, 'start=', 'delayed-auto')
if (!$Credential) {
    $windowsAccount = if ($ServiceAccount -eq 'LocalSystem') { 'LocalSystem' } else { "NT AUTHORITY\$ServiceAccount" }
    $serviceConfig += @('obj=', $windowsAccount)
}
& sc.exe @serviceConfig
if ($LASTEXITCODE -ne 0) { throw 'Could not configure the service account.' }
New-NetFirewallRule -Name $FirewallRuleName -DisplayName "$DisplayName UDP" `
    -Direction Inbound -Action Allow -Protocol UDP -LocalPort $port -Program $exe `
    -RemoteAddress $SensorRemoteAddress -Profile Any | Out-Null
Write-Host "Service installed as $accountName. Start it with: Start-Service '$ServiceName'"
