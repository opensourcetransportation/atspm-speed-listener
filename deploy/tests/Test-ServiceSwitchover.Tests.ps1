# Exercises configurable ports/names and restoration without real services or firewall changes.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (-not ('System.ServiceProcess.ServiceControllerStatus' -as [type])) { Add-Type -AssemblyName System.ServiceProcess }
$path = Join-Path (Split-Path $PSScriptRoot -Parent) 'Test-ServiceSwitchover.ps1'
$test = [scriptblock]::Create((Get-Content -LiteralPath $path -Raw).Replace('#Requires -RunAsAdministrator', ''))
$root = Join-Path ([IO.Path]::GetTempPath()) ('switchover-' + [Guid]::NewGuid().ToString('N').Substring(0,8))

function Get-Service {
    param([string]$Name)
    if (!$script:services.ContainsKey($Name)) { throw 'Unexpected service access.' }
    $script:services[$Name]
}
function Get-CimInstance {
    param([string]$ClassName, [string]$Filter)
    $name = $Filter.Substring(6).TrimEnd("'")
    $service = Get-Service $name
    [pscustomobject]@{ State = $service.Status; ProcessId = $service.ProcessId; PathName = ('"{0}" listener' -f (Join-Path $script:install 'SpeedListener.exe')) }
}
function Get-NetUDPEndpoint {
    [CmdletBinding()]
    param([int]$LocalPort)
    foreach ($service in $script:services.Values) {
        if ($service.Status -eq 'Running' -and $service.BoundPort -eq $LocalPort) {
            [pscustomobject]@{ OwningProcess = $service.ProcessId }
        }
    }
}
function Start-Service {
    param([string]$Name)
    $service = Get-Service $Name
    if ($Name -eq 'AgencyCandidate') {
        $script:candidateStarts++
        if ($script:failCandidateStart -and $script:candidateStarts -eq 1) { throw 'Simulated candidate startup failure.' }
        $service.BoundPort = (Get-Content (Join-Path $script:install 'appsettings.json') -Raw | ConvertFrom-Json).SpeedListenerConfiguration.UdpPort
    }
    else { $service.BoundPort = $script:targetPort }
    $service.Status = 'Running'
}
function Get-NetFirewallRule {
    [CmdletBinding()]
    param([string]$Name)
    if ($Name -ne 'AgencyCandidate-UDP') { throw 'Unexpected firewall rule access.' }
    [pscustomobject]@{ Name = $Name }
}
function Get-NetFirewallPortFilter {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline = $true)]$Rule)
    process { [pscustomobject]@{ Protocol = 'UDP'; LocalPort = $script:ports } }
}
function Set-NetFirewallPortFilter {
    [CmdletBinding()]
    param([Parameter(ValueFromPipeline = $true)]$Filter, $LocalPort)
    process { $script:ports = @($LocalPort) }
}
function Start-Sleep {
    param([int]$Seconds)
    if ($script:failDuringRun -and $script:candidateStarts -eq 1) {
        $script:services.AgencyCandidate.Status = 'Stopped'
        $script:services.AgencyCandidate.BoundPort = 0
    }
}
function New-Scenario([string]$Name, [string]$CandidateState = 'Running', [int]$Target = 14000, [int]$CandidatePort = 15000) {
    $script:install = Join-Path $root $Name
    New-Item -ItemType Directory -Path $script:install -Force | Out-Null
    $configPath = Join-Path $script:install 'appsettings.json'
    [IO.File]::WriteAllText($configPath, ('{"SpeedListenerConfiguration":{"UdpPort":' + $CandidatePort + '}}'), [Text.Encoding]::Unicode)
    $script:originalBytes = [Convert]::ToBase64String([IO.File]::ReadAllBytes($configPath))
    $script:targetPort = $Target
    $script:ports = @("$CandidatePort", "$($CandidatePort + 1)")
    $script:originalPorts = $script:ports -join ','
    $script:services = @{
        AgencyExisting = [pscustomobject]@{ Name = 'AgencyExisting'; Status = 'Running'; BoundPort = $Target; ProcessId = 41 }
        AgencyCandidate = [pscustomobject]@{ Name = 'AgencyCandidate'; Status = $CandidateState; BoundPort = $CandidatePort; ProcessId = 42 }
    }
    foreach ($service in $script:services.Values) {
        $service | Add-Member ScriptMethod Stop {
            $script:stops++
            if ($script:failRestorationStop -and $this.Name -eq 'AgencyCandidate' -and $script:candidateStarts -eq 1) {
                throw 'Simulated restoration stop failure.'
            }
            $this.Status = 'Stopped'; $this.BoundPort = 0
        }
        $service | Add-Member ScriptMethod WaitForStatus {
            param($Status, $Timeout)
            if ($this.Status -ne $Status.ToString()) { throw 'Unexpected wait state.' }
        }
    }
    $script:stops = 0
    $script:candidateStarts = 0
    $script:failCandidateStart = $false
    $script:failRestorationStop = $false
    $script:failDuringRun = $false
}
function Invoke-Test {
    & $test -InstallPath $script:install -ExistingServiceName 'AgencyExisting' -CandidateServiceName 'AgencyCandidate' `
        -TargetPort $script:targetPort -DurationSeconds 1 -TimeoutSeconds 1
}
function Assert-Equal($Expected, $Actual) { if ($Expected -cne $Actual) { throw "Expected $Expected; got $Actual" } }
function Assert-Preserved {
    Assert-Equal $script:originalBytes ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $script:install 'appsettings.json'))))
    Assert-Equal $script:originalPorts ($script:ports -join ',')
}
function Assert-Fails {
    $failed = $false
    try { Invoke-Test } catch { $failed = $true }
    if (!$failed) { throw 'Expected test failure.' }
}

New-Scenario 'normal'
Invoke-Test
Assert-Preserved
Assert-Equal 'Running' $script:services.AgencyExisting.Status
Assert-Equal 'Running' $script:services.AgencyCandidate.Status
Assert-Equal 15000 $script:services.AgencyCandidate.BoundPort

New-Scenario 'alternate' 'Running' 23000 24000
Invoke-Test
Assert-Preserved
Assert-Equal 23000 $script:services.AgencyExisting.BoundPort
Assert-Equal 24000 $script:services.AgencyCandidate.BoundPort

New-Scenario 'stopped' 'Stopped'
Invoke-Test
Assert-Preserved
Assert-Equal 'Running' $script:services.AgencyExisting.Status
Assert-Equal 'Stopped' $script:services.AgencyCandidate.Status

New-Scenario 'startup-failure'
$script:failCandidateStart = $true
Assert-Fails
Assert-Preserved
Assert-Equal 'Running' $script:services.AgencyExisting.Status
Assert-Equal 'Running' $script:services.AgencyCandidate.Status

New-Scenario 'wrong-owner'
$script:services.AgencyExisting.BoundPort = 16000
Assert-Fails
Assert-Preserved
Assert-Equal 0 $script:stops

New-Scenario 'runtime-failure'
$script:failDuringRun = $true
Assert-Fails
Assert-Preserved
Assert-Equal 'Running' $script:services.AgencyExisting.Status
Assert-Equal 'Running' $script:services.AgencyCandidate.Status

New-Scenario 'restore-failure'
$script:failRestorationStop = $true
Assert-Fails
Assert-Preserved

Write-Host 'PASS: seven switchover scenarios with custom service names and ports. No real services or firewall rules were changed.'
