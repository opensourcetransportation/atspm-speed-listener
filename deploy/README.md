# Reusable deployment scripts

These are the maintained scripts for any agency deployment. They do not select a
GCP project, database host, sensor IP, production port, or existing legacy service.
Configure database and optional cloud logging settings through your agency's
configuration and secrets process. Application names such as `SpeedListener.exe`
and the shared event-log source identify the product rather than an agency.

| Script | Deployment inputs |
| --- | --- |
| `Install-Service.ps1` | Required install directory and sensor addresses; configurable service/display/firewall names; built-in service account or dedicated credentials; UDP port read from settings |
| `Upgrade-Service.ps1` | Required source and install directories; configurable service name, timeout and expected port; observes a running service's effective UDP binding and preserves installed settings, identity and firewall |
| `Enable-StartupDiagnostics.ps1` | Required install directory; configurable service name, log path and timeout; uses the registered account |
| `Capture-RejectedPackets.ps1` | Required UDP port; optional sensor, duration, network components, output directory, filter name and capture size |
| `Test-ServiceSwitchover.ps1` | Required install directory, existing service name and target port; configurable candidate service, duration, timeout, firewall rule and log path |

All five scripts include PowerShell help. From the repository root:

```powershell
Get-Help .\deploy\Install-Service.ps1 -Full
Get-Help .\deploy\Test-ServiceSwitchover.ps1 -Examples
```

Use these source scripts when preparing deployment packages. The ignored `build/`
directory may contain historical troubleshooting scripts or previously generated
packages; it is not a maintained script source. Do not redistribute those old
scripts as current deployment tools.

The scripts under `tests/` use temporary fixtures and mocked system commands. They
do not control real services or change firewall rules. CI runs them under both
PowerShell 7 and Windows PowerShell 5.1.

See [the Windows service guide](../docs/windows-service.md) for the deployment workflow.
