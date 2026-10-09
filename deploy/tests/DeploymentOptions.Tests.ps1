# Verifies agency-specific installation/capture options without system mutations.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$deploy = Split-Path $PSScriptRoot -Parent
$installer = [scriptblock]::Create((Get-Content (Join-Path $deploy 'Install-Service.ps1') -Raw).Replace('#Requires -RunAsAdministrator', ''))
$capture = [scriptblock]::Create((Get-Content (Join-Path $deploy 'Capture-RejectedPackets.ps1') -Raw).Replace('#Requires -RunAsAdministrator', ''))
$root = Join-Path ([IO.Path]::GetTempPath()) ('options-' + [Guid]::NewGuid().ToString('N').Substring(0,8))
function Assert($Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Get-Service { param($Name) }
function Get-NetFirewallRule { param($Name) }
function icacls.exe { $script:aclArguments = @($args); $global:LASTEXITCODE = 0 }
function sc.exe { $script:scArguments = @($args); $global:LASTEXITCODE = 0 }
function New-Service {
    param($Name, $DisplayName, $BinaryPathName, $StartupType, $Description, $Credential)
    $script:createdService = $PSBoundParameters
}
function New-ItemProperty {
    param($LiteralPath, $Name, $PropertyType, $Value, [switch]$Force)
    $script:serviceEnvironment = $Value
}
function New-NetFirewallRule {
    param($Name, $DisplayName, $Direction, $Action, $Protocol, $LocalPort, $Program, $RemoteAddress, $Profile)
    $script:firewall = $PSBoundParameters
}
function pktmon.exe {
    $script:packetCalls.Add(@($args))
    $global:LASTEXITCODE = if ($script:failCapture -and $args[0] -eq 'start') { 1 } else { 0 }
}
function Start-Sleep { param($Seconds) }
try {
    New-Item -ItemType Directory -Path $root | Out-Null
    Set-Content -LiteralPath (Join-Path $root 'SpeedListener.exe') -Value 'test fixture'
    Set-Content -LiteralPath (Join-Path $root 'appsettings.json') -Value '{"SpeedListenerConfiguration":{"UdpPort":27000}}'
    # Avoid event-log changes by removing this platform API block from the harness.
    $installerText = $installer.ToString()
    $installerText = [regex]::Replace($installerText, "(?s)if \(!\[System.Diagnostics.EventLog\]::SourceExists\('AtspmSpeedListener'\)\) \{.*?\r?\n\}", '')
    $installer = [scriptblock]::Create($installerText)
    foreach ($identity in @('LocalService','NetworkService','LocalSystem','Dedicated')) {
        $options = @{ InstallPath = $root; SensorRemoteAddress = @('192.0.2.0/24','198.51.100.10'); ServiceName = 'RegionalListener'; DisplayName = 'Regional Listener'; FirewallRuleName = 'RegionalInbound' }
        if ($identity -eq 'Dedicated') {
            $options.Credential = [pscredential]::new('EXAMPLE\listener', (ConvertTo-SecureString 'fixture-only' -AsPlainText -Force))
        } else { $options.ServiceAccount = $identity }
        & $installer @options
        Assert ($script:createdService.Name -eq 'RegionalListener') 'Custom service name was lost.'
        Assert ($script:createdService.DisplayName -eq 'Regional Listener') 'Custom display name was lost.'
        Assert ($script:serviceEnvironment -contains 'ATSPM_SERVICE_NAME=RegionalListener') 'Custom Windows host identity was lost.'
        Assert ($script:firewall.Name -eq 'RegionalInbound' -and $script:firewall.LocalPort -eq 27000) 'Custom firewall name or configured port was lost.'
        Assert ($script:firewall.RemoteAddress.Count -eq 2) 'Sensor sources were lost.'
        Assert ($script:aclArguments -contains '*S-1-5-18:(OI)(CI)F') 'SYSTEM full control was lost.'
        if ($identity -eq 'Dedicated') {
            Assert ($script:createdService.Credential.UserName -eq 'EXAMPLE\listener') 'Dedicated credentials were not passed to service registration.'
            Assert ($script:aclArguments -contains 'EXAMPLE\listener:(OI)(CI)RX') 'Dedicated account cannot read runtime files.'
            Assert ($script:scArguments -notcontains 'obj=') 'Dedicated account was overwritten.'
        } else {
            Assert (!$script:createdService.ContainsKey('Credential')) 'Unexpected credentials for a built-in identity.'
            $expectedAccount = if ($identity -eq 'LocalSystem') { 'LocalSystem' } else { "NT AUTHORITY\$identity" }
            Assert ($script:scArguments -contains $expectedAccount) 'Wrong built-in account.'
            if ($identity -eq 'LocalSystem') {
                Assert ($script:aclArguments -notcontains '*S-1-5-18:(OI)(CI)RX') 'SYSTEM permissions were downgraded.'
            } else {
                $sid = if ($identity -eq 'LocalService') { '*S-1-5-19' } else { '*S-1-5-20' }
                Assert ($script:aclArguments -contains "${sid}:(OI)(CI)RX") 'Built-in account cannot read runtime files.'
            }
        }
        Write-Host "PASS: installer $identity with custom agency options"
    }
    foreach ($failure in @($false,$true)) {
        $script:packetCalls = [Collections.Generic.List[object]]::new()
        $script:failCapture = $failure
        $caught = $null
        try {
            & $capture -Port 28000 -SensorAddress '192.0.2.10' -DurationSeconds 1 -Components all -OutputDirectory $root -FilterName RegionalCapture -MaxFileSizeMB 128
        } catch { $caught = $_ }
        Assert (($null -ne $caught) -eq $failure) 'Unexpected capture outcome.'
        $filter = @($script:packetCalls | Where-Object { $_[0] -eq 'filter' -and $_[1] -eq 'add' })[0]
        Assert ($filter -contains 'RegionalCapture' -and $filter -contains '28000' -and $filter -contains '192.0.2.10') 'Capture ignored custom filter, port or sensor.'
        $start = @($script:packetCalls | Where-Object { $_[0] -eq 'start' })[0]
        Assert ($start -contains 'all' -and $start -contains '128') 'Capture ignored components or size.'
        $cleanup = @($script:packetCalls | Where-Object { $_[0] -eq 'filter' -and $_[1] -eq 'remove' -and $_[2] -eq 'RegionalCapture' })
        Assert ($cleanup.Count -eq 1) 'Custom capture filter was not cleaned up.'
        Write-Host "PASS: custom capture options (startup failure=$failure)"
    }
    Write-Host 'All 6 deployment option scenarios passed.'
}
finally {
    # The generated path is confined to the selected temporary directory.
    $resolved = [IO.Path]::GetFullPath($root)
    if (!$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()), [StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path.' }
    if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
