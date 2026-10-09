<#
.SYNOPSIS
Captures UDP packets for a specified port and optional sensor address.
.DESCRIPTION
Replaces existing pktmon filters without changing listener services. Output goes
to the user's temporary directory unless OutputDirectory is supplied.
.EXAMPLE
.\Capture-RejectedPackets.ps1 -Port 12000 -SensorAddress '192.0.2.10' -DurationSeconds 30 -OutputDirectory 'D:\Diagnostics'
#>

#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$SensorAddress = '',
    [Parameter(Mandatory = $true)][ValidateRange(1,65535)][int]$Port,
    [ValidateRange(1,60)][int]$DurationSeconds = 60,
    [ValidateSet('nics','all')][string]$Components = 'nics',
    [string]$OutputDirectory = [IO.Path]::GetTempPath(),
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$FilterName = 'SpeedRejects',
    [ValidateRange(1,4096)][int]$MaxFileSizeMB = 64
)
$ErrorActionPreference = 'Stop'

function Invoke-PacketMonitor {
    param([string[]]$Arguments)
    & pktmon.exe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "pktmon failed with exit code $LASTEXITCODE."
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$stem = Join-Path $OutputDirectory ('speed-rejects-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$etlPath = $stem + '.etl'
$pcapPath = $stem + '.pcapng'
Write-Host 'This replaces packet-monitor capture filters. Listener services keep running.'
& pktmon.exe stop
Invoke-PacketMonitor -Arguments @('filter', 'remove')
$filterArguments = @('filter', 'add', $FilterName, '-t', 'UDP', '-p', "$Port")
if ($SensorAddress) { $filterArguments += @('-i', $SensorAddress) }
Invoke-PacketMonitor -Arguments $filterArguments
$captureStarted = $false
try {
    Invoke-PacketMonitor -Arguments @('start', '--capture', '--comp', $Components, '--pkt-size', '0', '--file-size', "$MaxFileSizeMB", '--file-name', $etlPath)
    $captureStarted = $true
    Write-Host "Capturing full UDP packets on port $Port for $DurationSeconds seconds. Sensor filter: '$SensorAddress' (blank means all sensors)."
    Start-Sleep -Seconds $DurationSeconds
}
finally {
    if ($captureStarted) { & pktmon.exe stop }
    & pktmon.exe filter remove $FilterName
}
Invoke-PacketMonitor -Arguments @('etl2pcap', $etlPath, '--out', $pcapPath)
Write-Host "Capture ready: $pcapPath"
