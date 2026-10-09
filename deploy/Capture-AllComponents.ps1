#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [string]$SensorAddress = '',
    [int]$Port = 10088,
    [ValidateRange(1,60)][int]$DurationSeconds = 60,
    [string]$OutputDirectory = 'C:\Temp'
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
$filterArguments = @('filter', 'add', 'SpeedRejects', '-t', 'UDP', '-p', "$Port")
if ($SensorAddress) { $filterArguments += @('-i', $SensorAddress) }
Invoke-PacketMonitor -Arguments $filterArguments
$captureStarted = $false
try {
    Invoke-PacketMonitor -Arguments @('start', '--capture', '--comp', 'all', '--pkt-size', '0', '--file-size', '64', '--file-name', $etlPath)
    $captureStarted = $true
    Write-Host "Capturing full UDP packets on port $Port for $DurationSeconds seconds. Sensor filter: '$SensorAddress' (blank means all sensors)."
    Start-Sleep -Seconds $DurationSeconds
}
finally {
    if ($captureStarted) { & pktmon.exe stop }
    & pktmon.exe filter remove SpeedRejects
}
Invoke-PacketMonitor -Arguments @('etl2pcap', $etlPath, '--out', $pcapPath)
Write-Host "Capture ready: $pcapPath"
