# Runs without Administrator access and never controls real services.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
if (-not ('System.ServiceProcess.ServiceControllerStatus' -as [type])) {
    Add-Type -AssemblyName System.ServiceProcess
}
$scriptPath = Join-Path (Split-Path $PSScriptRoot -Parent) 'Upgrade-Service.ps1'
$upgrade = [scriptblock]::Create((Get-Content -LiteralPath $scriptPath -Raw).Replace('#Requires -RunAsAdministrator', ''))
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('atspm-upgrade-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Path $testRoot | Out-Null

function Get-Service {
    param([string]$Name)
    if ($Name -ne $script:expectedServiceName) { throw 'Attempt to control another service.' }
    return $script:fakeService
}
function Get-CimInstance {
    param([string]$ClassName, [string]$Filter)
    if ($Filter -ne "Name='$script:expectedServiceName'") { throw 'Unexpected service query.' }
    [pscustomobject]@{ PathName = ('"{0}" listener' -f $script:registeredExe); State = $script:fakeService.Status; ProcessId = 42 }
}
function Start-Service {
    param([string]$Name)
    if ($Name -ne $script:expectedServiceName) { throw 'Attempt to start another service.' }
    $script:startCount++
    if ($script:failFirstStart -and $script:startCount -eq 1) { throw 'Simulated startup failure.' }
    $script:fakeService.Status = 'Running'
}
function Get-NetUDPEndpoint {
    [CmdletBinding()]
    param([int]$LocalPort)
    if ($LocalPort -ne $script:expectedPort) { throw 'Installed UDP port was changed.' }
    if ($script:failFirstBind -and $script:startCount -eq 1) { return }
    [pscustomobject]@{ OwningProcess = 42 }
}
function Copy-Item {
    param([string]$LiteralPath, [string]$Destination, [switch]$Force)
    if ($script:failCopy -and $LiteralPath -eq (Join-Path $script:source 'SpeedListener.runtimeconfig.json')) {
        throw 'Simulated copy failure.'
    }
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
}
function Assert-Equal($Expected, $Actual, [string]$Message) {
    if ($Expected -cne $Actual) { throw "Assertion failed: $Message" }
}
function New-Scenario([string]$Name, [string]$State = 'Running', [int]$Port = 15000, [string]$ServiceName = 'AtspmSpeedListener') {
    $script:expectedPort = $Port
    $script:expectedServiceName = $ServiceName
    $script:originalSettings = '{"SpeedListenerConfiguration":{"UdpPort":' + $Port + '}}'
    $root = Join-Path $testRoot $Name
    $script:source = Join-Path $root 'source'
    $script:install = Join-Path $root 'installed'
    New-Item -ItemType Directory -Path $script:source, $script:install -Force | Out-Null
    foreach ($name in @('SpeedListener.exe','SpeedListener.dll','SpeedListener.deps.json','SpeedListener.runtimeconfig.json')) {
        [IO.File]::WriteAllText((Join-Path $script:source $name), "new $name")
        [IO.File]::WriteAllText((Join-Path $script:install $name), "old $name")
    }
    [IO.File]::WriteAllText((Join-Path $script:source 'NewDependency.dll'), 'new dependency')
    [IO.File]::WriteAllText((Join-Path $script:source 'appsettings.json'), 'must never replace installed settings')
    [IO.File]::WriteAllText((Join-Path $script:install 'appsettings.json'), $script:originalSettings)
    [IO.File]::WriteAllText((Join-Path $script:install 'startup-diagnostic.log'), 'preserve log')
    New-Item -ItemType Directory -Path (Join-Path $script:source 'Configuration'), (Join-Path $script:install 'Configuration') | Out-Null
    [IO.File]::WriteAllText((Join-Path $script:source 'Configuration\Custom.dll'), 'must not replace configuration')
    [IO.File]::WriteAllText((Join-Path $script:install 'Configuration\Custom.dll'), 'preserve configuration')
    New-Item -ItemType Directory -Path (Join-Path $script:source 'fr') | Out-Null
    [IO.File]::WriteAllText((Join-Path $script:source 'fr\SpeedListener.resources.dll'), 'satellite assembly')
    $script:registeredExe = Join-Path $script:install 'SpeedListener.exe'
    $script:fakeService = [pscustomobject]@{ Status = $State }
    $script:fakeService | Add-Member ScriptMethod Stop { $script:stopCount++; $this.Status = 'Stopped' }
    $script:fakeService | Add-Member ScriptMethod WaitForStatus {
        param($Status, $Timeout)
        if ($this.Status -ne $Status.ToString()) { throw 'Unexpected service state.' }
    }
    $script:startCount = 0
    $script:stopCount = 0
    $script:failFirstStart = $false
    $script:failFirstBind = $false
    $script:failCopy = $false
}
function Assert-Preserved {
    Assert-Equal $script:originalSettings ([IO.File]::ReadAllText((Join-Path $script:install 'appsettings.json'))) 'Settings preserved'
    Assert-Equal 'preserve log' ([IO.File]::ReadAllText((Join-Path $script:install 'startup-diagnostic.log'))) 'Log preserved'
    Assert-Equal 'preserve configuration' ([IO.File]::ReadAllText((Join-Path $script:install 'Configuration\Custom.dll'))) 'Configuration preserved'
}
function Assert-UpgradeFails {
    $failed = $false
    try { & $upgrade -SourcePath $script:source -InstallPath $script:install -TimeoutSeconds 1 }
    catch { $failed = $true }
    if (!$failed) { throw 'Expected upgrade failure.' }
}

New-Scenario 'running'
& $upgrade -SourcePath $script:source -InstallPath $script:install -TimeoutSeconds 1
Assert-Preserved
Assert-Equal 'new SpeedListener.dll' ([IO.File]::ReadAllText((Join-Path $script:install 'SpeedListener.dll'))) 'DLL upgraded'
Assert-Equal 'satellite assembly' ([IO.File]::ReadAllText((Join-Path $script:install 'fr\SpeedListener.resources.dll'))) 'Nested runtime copied'
Assert-Equal 'Running' $script:fakeService.Status 'Running state restored'
$backup = Get-ChildItem -LiteralPath (Join-Path $script:install '.upgrade-backups') -Directory | Select-Object -First 1
Assert-Equal 'old SpeedListener.dll' ([IO.File]::ReadAllText((Join-Path $backup.FullName 'SpeedListener.dll'))) 'Old DLL backed up'

New-Scenario 'custom' 'Running' 25000 'RegionalSpeedListener'
& $upgrade -SourcePath $script:source -InstallPath $script:install -ServiceName $script:expectedServiceName -TimeoutSeconds 1
Assert-Preserved
Assert-Equal 'Running' $script:fakeService.Status 'Custom service restarted'

New-Scenario 'stopped' 'Stopped'
& $upgrade -SourcePath $script:source -InstallPath $script:install -TimeoutSeconds 1
Assert-Equal 'Stopped' $script:fakeService.Status 'Stopped state preserved'
Assert-Equal 0 $script:startCount 'Stopped service was not started'
Assert-Preserved

New-Scenario 'rollback'
$script:failFirstStart = $true
Assert-UpgradeFails
Assert-Equal 'old SpeedListener.dll' ([IO.File]::ReadAllText((Join-Path $script:install 'SpeedListener.dll'))) 'Old DLL restored'
Assert-Equal $false (Test-Path -LiteralPath (Join-Path $script:install 'NewDependency.dll')) 'New dependency removed'
Assert-Equal $false (Test-Path -LiteralPath (Join-Path $script:install 'fr\SpeedListener.resources.dll')) 'New nested asset removed'
Assert-Equal 'Running' $script:fakeService.Status 'Old service restarted'
Assert-Preserved

New-Scenario 'copy-failure'
$script:failCopy = $true
Assert-UpgradeFails
Assert-Equal 'old SpeedListener.dll' ([IO.File]::ReadAllText((Join-Path $script:install 'SpeedListener.dll'))) 'Copy failure restores old DLL'
Assert-Equal $false (Test-Path -LiteralPath (Join-Path $script:install 'NewDependency.dll')) 'Copy failure removes new asset'
Assert-Equal 'Running' $script:fakeService.Status 'Copy failure restores service'
Assert-Preserved

New-Scenario 'bind-failure'
$script:failFirstBind = $true
Assert-UpgradeFails
Assert-Equal 'old SpeedListener.dll' ([IO.File]::ReadAllText((Join-Path $script:install 'SpeedListener.dll'))) 'Bind failure restores old DLL'
Assert-Equal 'Running' $script:fakeService.Status 'Bind failure restores service'
Assert-Preserved

New-Scenario 'wrong-service-path'
$script:registeredExe = Join-Path $script:source 'SpeedListener.exe'
Assert-UpgradeFails
Assert-Equal 0 $script:stopCount 'Wrong service was not stopped'

New-Scenario 'incomplete-package'
Remove-Item -LiteralPath (Join-Path $script:source 'SpeedListener.deps.json')
Assert-UpgradeFails
Assert-Equal 0 $script:stopCount 'Incomplete package rejected before stopping'

New-Scenario 'overlapping-paths'
$script:source = $script:install
Assert-UpgradeFails
Assert-Equal 0 $script:stopCount 'Overlapping paths rejected before stopping'

Write-Host 'PASS: nine upgrade scenarios. No real services were controlled.'
Write-Host "Test files: $testRoot"
