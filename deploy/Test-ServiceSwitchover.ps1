#Requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InstallPath,
    [Parameter(Mandatory = $true)][ValidatePattern('^[A-Za-z0-9_-]+$')][string]$ExistingServiceName,
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$CandidateServiceName = 'AtspmSpeedListener',
    [Parameter(Mandatory = $true)][ValidateRange(1,65535)][int]$TargetPort,
    [ValidateRange(1,3600)][int]$DurationSeconds = 60,
    [ValidateRange(1,300)][int]$TimeoutSeconds = 60,
    [string]$FirewallRuleName,
    [string]$LogPath
)
$ErrorActionPreference = 'Stop'
if ($ExistingServiceName -eq $CandidateServiceName) { throw 'Existing and candidate service names must differ.' }
$install = (Resolve-Path -LiteralPath $InstallPath).Path.TrimEnd('\')
$settingsPath = Join-Path $install 'appsettings.json'
$originalBytes = [IO.File]::ReadAllBytes($settingsPath)
$originalConfig = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
$originalPort = [int]$originalConfig.SpeedListenerConfiguration.UdpPort
if ($originalPort -lt 1 -or $originalPort -gt 65535 -or $originalPort -eq $TargetPort) {
    throw 'The candidate must have a valid configured port different from TargetPort.'
}
$existing = Get-CimInstance Win32_Service -Filter "Name='$ExistingServiceName'"
$candidate = Get-CimInstance Win32_Service -Filter "Name='$CandidateServiceName'"
if (!$existing -or $existing.State -ne 'Running') { throw 'The existing service must be running before this test.' }
if (!$candidate -or $candidate.State -notin @('Running','Stopped')) { throw 'The candidate service must be running or stopped.' }
$candidateWasRunning = $candidate.State -eq 'Running'
$exePath = if ($candidate.PathName -match '^"([^"]+)"') { $Matches[1] } else { ($candidate.PathName -split '\s+')[0] }
if ([IO.Path]::GetFullPath($exePath) -ne (Join-Path $install 'SpeedListener.exe')) {
    throw 'The candidate service executable does not match InstallPath.'
}
if (!(Get-NetUDPEndpoint -LocalPort $TargetPort -ErrorAction SilentlyContinue | Where-Object OwningProcess -eq $existing.ProcessId)) {
    throw 'The existing service must own TargetPort before this test.'
}
if (!$FirewallRuleName) { $FirewallRuleName = "$CandidateServiceName-UDP" }
$rule = Get-NetFirewallRule -Name $FirewallRuleName -ErrorAction Stop
$portFilter = $rule | Get-NetFirewallPortFilter
if ($portFilter.Protocol -notin @('UDP','17')) { throw 'The candidate firewall rule must use UDP.' }
$originalPorts = @($portFilter.LocalPort)
if (!$LogPath) { $LogPath = Join-Path $install 'startup-diagnostic.log' }
$lineCount = if (Test-Path -LiteralPath $LogPath) { @(Get-Content -LiteralPath $LogPath).Count } else { 0 }
$timeout = [TimeSpan]::FromSeconds($TimeoutSeconds)

function Stop-TestService([string]$Name) {
    $current = Get-Service -Name $Name
    if ($current.Status -ne 'Stopped') {
        if ($current.Status -ne 'StopPending') { $current.Stop() }
        $current.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, $timeout)
    }
}
function Wait-TestPortFree([int]$Port) {
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        if (!(Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue)) { return }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    throw "UDP $Port remains occupied; no process was forcibly stopped."
}
function Start-TestService([string]$Name, [int]$Port) {
    $current = Get-Service -Name $Name
    if ($current.Status -eq 'StopPending') {
        $current.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, $timeout)
    }
    if ((Get-Service -Name $Name).Status -ne 'Running') { Start-Service -Name $Name }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $registration = Get-CimInstance Win32_Service -Filter "Name='$Name'"
        if ($registration.State -eq 'Stopped') { throw "$Name stopped during startup." }
        if ($registration.State -eq 'Running' -and
            (Get-NetUDPEndpoint -LocalPort $Port -ErrorAction SilentlyContinue | Where-Object OwningProcess -eq $registration.ProcessId)) { return }
        Start-Sleep -Seconds 1
    } while ((Get-Date) -lt $deadline)
    throw "$Name did not bind UDP $Port within $TimeoutSeconds seconds."
}

try {
    Write-Host "Temporarily replacing $ExistingServiceName with $CandidateServiceName on UDP $TargetPort."
    Stop-TestService $CandidateServiceName
    Stop-TestService $ExistingServiceName
    Wait-TestPortFree $TargetPort
    $testConfig = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
    $testConfig.SpeedListenerConfiguration.UdpPort = $TargetPort
    $testConfig | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $settingsPath -Encoding UTF8
    $rule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -LocalPort $TargetPort | Out-Null
    Start-TestService $CandidateServiceName $TargetPort
    Write-Host "Candidate bound to UDP $TargetPort. Testing for $DurationSeconds seconds."
    $remaining = $DurationSeconds
    while ($remaining -gt 0) {
        $interval = [Math]::Min(15, $remaining)
        Start-Sleep -Seconds $interval
        $remaining -= $interval
        if ((Get-CimInstance Win32_Service -Filter "Name='$CandidateServiceName'").State -ne 'Running') {
            throw 'The candidate service stopped during the test.'
        }
        if ($remaining -gt 0) { Write-Host "$remaining seconds remaining..." }
    }
}
finally {
    Write-Host 'Restoring the original settings, firewall port, and service states...'
    $restorationErrors = [Collections.Generic.List[string]]::new()
    # Attempt each independent restoration step even if another step fails.
    try { Stop-TestService $CandidateServiceName } catch { $restorationErrors.Add($_.Exception.Message) }
    try { [IO.File]::WriteAllBytes($settingsPath, $originalBytes) } catch { $restorationErrors.Add($_.Exception.Message) }
    try { $rule | Get-NetFirewallPortFilter | Set-NetFirewallPortFilter -LocalPort $originalPorts | Out-Null }
    catch { $restorationErrors.Add($_.Exception.Message) }
    try {
        if ((Get-Service -Name $ExistingServiceName).Status -ne 'Running') { Wait-TestPortFree $TargetPort }
        Start-TestService $ExistingServiceName $TargetPort
    }
    catch { $restorationErrors.Add($_.Exception.Message) }
    if ($candidateWasRunning) {
        try {
            Wait-TestPortFree $originalPort
            Start-TestService $CandidateServiceName $originalPort
        }
        catch { $restorationErrors.Add($_.Exception.Message) }
    }
    if ($restorationErrors.Count) { throw ('Restoration needs attention: ' + ($restorationErrors -join '; ')) }
    Write-Host 'Restoration complete.'
    if (Test-Path -LiteralPath $LogPath) { Get-Content -LiteralPath $LogPath | Select-Object -Skip $lineCount }
}
