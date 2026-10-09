<#
.SYNOPSIS
Upgrades a selected listener from a complete Windows publish folder.
.DESCRIPTION
Preserves agency configuration, service identity, environment and firewall rules.
Backs up runtime files and attempts rollback if copying or startup fails.
.PARAMETER ExpectedPort
Optional readiness port override. Otherwise a running service's effective UDP
binding is used; a stopped service's port is read from appsettings.json.
.EXAMPLE
.\Upgrade-Service.ps1 -SourcePath 'D:\Staging\SpeedListener' -InstallPath 'D:\Apps\SpeedListener' -ServiceName RegionalSpeedListener
#>

#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$InstallPath,
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$ServiceName = 'AtspmSpeedListener',
    [ValidateRange(1,300)][int]$TimeoutSeconds = 60,
    [ValidateRange(1,65535)][int]$ExpectedPort
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $SourcePath).Path.TrimEnd('\')
$install = (Resolve-Path -LiteralPath $InstallPath).Path.TrimEnd('\')
if ($source -eq $install -or $source.StartsWith($install + '\', [StringComparison]::OrdinalIgnoreCase) -or
    $install.StartsWith($source + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Extract the new publish folder outside the installation directory.'
}
if ($install -eq [IO.Path]::GetPathRoot($install).TrimEnd('\')) { throw 'The install directory cannot be a drive root.' }
foreach ($name in @('SpeedListener.exe','SpeedListener.dll','SpeedListener.deps.json','SpeedListener.runtimeconfig.json')) {
    if (!(Test-Path -LiteralPath (Join-Path $source $name) -PathType Leaf)) {
        throw "The source must be a complete Windows publish folder; missing $name."
    }
}
if (Get-ChildItem -LiteralPath $source -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) {
    throw 'The source folder must not contain symbolic links or junctions.'
}
$settingsPath = Join-Path $install 'appsettings.json'
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$registration = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
if (!$registration) { throw "Service $ServiceName is not installed." }
$exePath = if ($registration.PathName -match '^"([^"]+)"') { $Matches[1] } else { ($registration.PathName -split '\s+')[0] }
if ([IO.Path]::GetFullPath($exePath) -ne (Join-Path $install 'SpeedListener.exe')) {
    throw 'The registered service executable does not match InstallPath.'
}
$service = Get-Service -Name $ServiceName
if ($service.Status -notin @('Running','Stopped')) { throw 'Wait for the service to finish starting or stopping before upgrading.' }
$wasRunning = $service.Status -eq 'Running'
$port = if ($ExpectedPort) { $ExpectedPort } elseif ($wasRunning) {
    # Observe the effective binding, including environment, volume and CLI overrides.
    $bindings = @(Get-NetUDPEndpoint -OwningProcess $registration.ProcessId -ErrorAction SilentlyContinue)
    $ports = @($bindings.LocalPort | Sort-Object -Unique)
    if ($ports.Count -ne 1) { throw 'Cannot determine one effective UDP port. Supply -ExpectedPort.' }
    [int]$ports[0]
} else { [int]$settings.SpeedListenerConfiguration.UdpPort }
if ($port -lt 1 -or $port -gt 65535) { throw 'Installed UDP port is invalid. Supply -ExpectedPort for a configuration override.' }
$timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)

# Copy runtime assets only. Preserve appsettings*, Configuration/, logs, service
# account, environment and firewall rules. Nested satellite assemblies are included.
$files = @(Get-ChildItem -LiteralPath $source -Recurse -File | Where-Object {
    $relative = $_.FullName.Substring($source.Length + 1)
    $relative -notmatch '(^|\\)Configuration(\\|$)' -and
    ($_.Extension -in @('.dll','.exe','.pdb') -or $_.Name -in @('SpeedListener.deps.json','SpeedListener.runtimeconfig.json'))
} | ForEach-Object {
    $relative = $_.FullName.Substring($source.Length + 1)
    $destination = Join-Path $install $relative
    # Never follow a link in the installation tree while backing up or replacing files.
    $cursor = $destination
    while ($cursor.Length -ge $install.Length) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw "Installation contains a symbolic link or junction: $cursor"
            }
        }
        if ($cursor -eq $install) { break }
        $cursor = Split-Path $cursor -Parent
    }
    [pscustomobject]@{ Source = $_.FullName; Relative = $relative; Destination = $destination; Existed = (Test-Path -LiteralPath $destination -PathType Leaf) }
})

function Stop-UpgradeService {
    $current = Get-Service -Name $ServiceName
    if ($current.Status -ne 'Stopped') {
        if ($current.Status -ne 'StopPending') { $current.Stop() }
        $current.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, $timeout)
    }
}
function Start-UpgradeService {
    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Running, $timeout)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $current = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
        if ($current.State -ne 'Running') { throw "$ServiceName stopped during startup." }
        $endpoint = Get-NetUDPEndpoint -LocalPort $port -ErrorAction SilentlyContinue |
            Where-Object OwningProcess -eq $current.ProcessId
        if ($endpoint) { return }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    throw "$ServiceName did not bind UDP $port within $TimeoutSeconds seconds."
}

$backupRoot = Join-Path $install '.upgrade-backups'
if ((Test-Path -LiteralPath $backupRoot) -and
    ((Get-Item -LiteralPath $backupRoot -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'The backup directory must not be a symbolic link or junction.'
}
$backup = Join-Path $backupRoot ((Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
$changed = [Collections.Generic.List[object]]::new()
Write-Host "Upgrading only $ServiceName with readiness UDP port $port."
Stop-UpgradeService
try {
    New-Item -ItemType Directory -Path $backup -Force | Out-Null
    foreach ($file in $files) {
        if ($file.Existed) {
            $saved = Join-Path $backup $file.Relative
            New-Item -ItemType Directory -Path (Split-Path $saved -Parent) -Force | Out-Null
            Copy-Item -LiteralPath $file.Destination -Destination $saved
        }
    }
    $files | Select-Object Relative, Existed | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backup 'manifest.json') -Encoding UTF8
    foreach ($file in $files) {
        New-Item -ItemType Directory -Path (Split-Path $file.Destination -Parent) -Force | Out-Null
        $changed.Add($file)
        Copy-Item -LiteralPath $file.Source -Destination $file.Destination -Force
    }
    if ($wasRunning) { Start-UpgradeService }
    Write-Host "Upgrade complete. Backup: $backup"
    if (!$wasRunning) { Write-Host 'The service was stopped before the upgrade and remains stopped.' }
}
catch {
    $upgradeError = $_
    Write-Warning 'Upgrade failed. Restoring the previous runtime files.'
    try {
        Stop-UpgradeService
        foreach ($file in $changed) {
            if ($file.Existed) {
                Copy-Item -LiteralPath (Join-Path $backup $file.Relative) -Destination $file.Destination -Force
            }
            elseif (Test-Path -LiteralPath $file.Destination) {
                Remove-Item -LiteralPath $file.Destination -Force
            }
        }
        if ($wasRunning) { Start-UpgradeService }
        Write-Warning 'Previous runtime files and service state restored.'
    }
    catch {
        Write-Warning "Rollback could not complete. Backup: $backup. $($_.Exception.Message)"
    }
    throw $upgradeError
}
