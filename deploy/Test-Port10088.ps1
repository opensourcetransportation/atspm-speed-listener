#Requires -RunAsAdministrator
[CmdletBinding()]
param([string]$InstallPath = 'C:\Services\ATSPM-SpeedListener-win-x64')
$ErrorActionPreference = 'Stop'
$settingsPath = Join-Path $InstallPath 'appsettings.json'
$logPath = Join-Path $InstallPath 'startup-diagnostic.log'
$originalSettings = Get-Content -LiteralPath $settingsPath -Raw
$originalConfig = $originalSettings | ConvertFrom-Json
if ($originalConfig.SpeedListenerConfiguration.UdpPort -ne 10089) {
    throw 'Expected the new listener to be configured for 10089 before this test.'
}
foreach ($serviceName in @('SpeedListener', 'AtspmSpeedListener')) {
    if ((Get-Service -Name $serviceName).Status -ne 'Running') {
        throw "Expected $serviceName to be running before this test."
    }
}
$rule = Get-NetFirewallRule -Name 'AtspmSpeedListener-UDP'
$originalPorts = @(($rule | Get-NetFirewallPortFilter).LocalPort)
$lineCount = @(Get-Content -LiteralPath $logPath).Count
$stopDeadline = [TimeSpan]::FromSeconds(90)
function Stop-TestService([string]$Name) {
    Write-Host "Stopping $Name..."
    Stop-Service -Name $Name -ErrorAction Stop
    (Get-Service -Name $Name).WaitForStatus('Stopped', $stopDeadline)
}
function Wait-TestPortFree([int]$Port) {
    $releaseDeadline = (Get-Date).AddSeconds(30)
    do {
        $owners = @(Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue)
        if ($owners.Count -eq 0) { return }
        Write-Host "Waiting for UDP $Port to be released (PID $($owners.OwningProcess -join ', '))..."
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $releaseDeadline)
    throw "UDP $Port is still occupied after 30 seconds. No process was forcibly stopped."
}
try {
    Stop-TestService 'AtspmSpeedListener'
    Stop-TestService 'SpeedListener'
    Wait-TestPortFree 10088
    $testConfig = $originalSettings | ConvertFrom-Json
    $testConfig.SpeedListenerConfiguration.UdpPort = 10088
    $testConfig | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    $rule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -LocalPort 10088
    Write-Host 'Starting the new listener on 10088...'
    Start-Service -Name AtspmSpeedListener
    $readyDeadline = (Get-Date).AddSeconds(30)
    do {
        $service = Get-CimInstance Win32_Service -Filter "Name='AtspmSpeedListener'"
        $endpoint = Get-NetUDPEndpoint -LocalPort 10088 -ErrorAction SilentlyContinue |
            Where-Object OwningProcess -eq $service.ProcessId
        if ($endpoint) { break }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $readyDeadline)
    if (!$endpoint) { throw 'New listener did not bind UDP 10088.' }
    Write-Host "New listener bound to 10088, PID $($service.ProcessId). Testing for 60 seconds."
    foreach ($remaining in @(60, 45, 30, 15)) {
        Write-Host "$remaining seconds remaining..."
        Start-Sleep -Seconds 15
    }
}
finally {
    Write-Host 'Restoring the old listener on 10088 and the new listener on 10089...'
    Stop-TestService 'AtspmSpeedListener'
    $originalSettings | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    $rule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -LocalPort $originalPorts
    if ((Get-Service -Name SpeedListener).Status -ne 'Running') {
        Wait-TestPortFree 10088
        Start-Service -Name SpeedListener
    }
    Wait-TestPortFree 10089
    Start-Service -Name AtspmSpeedListener
    Write-Host 'Restoration complete.'
    Get-CimInstance Win32_Service | Where-Object Name -in @('SpeedListener', 'AtspmSpeedListener') |
        Select-Object Name, State, ProcessId | Format-Table -AutoSize
    Get-NetUDPEndpoint -LocalPort 10088,10089 -ErrorAction SilentlyContinue |
        Select-Object LocalPort, OwningProcess | Format-Table -AutoSize
    Write-Host 'Log entries from this test:'
    Get-Content -LiteralPath $logPath | Select-Object -Skip $lineCount
}
