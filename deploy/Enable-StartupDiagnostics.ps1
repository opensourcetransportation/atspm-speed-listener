#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'
$serviceName = 'AtspmSpeedListener'
$logPath = Join-Path $PSScriptRoot 'startup-diagnostic.log'
Stop-Service $serviceName -ErrorAction Stop
if (!(Test-Path -LiteralPath $logPath)) {
    New-Item -ItemType File -Path $logPath | Out-Null
}
& icacls.exe $logPath /grant '*S-1-5-19:M'
if ($LASTEXITCODE -ne 0) { throw 'Could not grant LocalService access to the diagnostic log.' }
$registryPath = "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName"
$existingEnvironment = @((Get-ItemProperty -LiteralPath $registryPath -Name Environment -ErrorAction SilentlyContinue).Environment)
$newEnvironment = @($existingEnvironment | Where-Object { $_ -and $_ -notlike 'ATSPM_STARTUP_LOG=*' })
$newEnvironment += "ATSPM_STARTUP_LOG=$logPath"
New-ItemProperty -LiteralPath $registryPath -Name Environment -PropertyType MultiString -Value $newEnvironment -Force | Out-Null
& sc.exe start $serviceName
Start-Sleep -Seconds 5
& sc.exe queryex $serviceName
Write-Host "Startup diagnostics: $logPath"
Get-Content -LiteralPath $logPath -Tail 100
