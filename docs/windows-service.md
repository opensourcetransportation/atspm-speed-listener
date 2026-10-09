# Windows service deployment

The Windows x64 package is self-contained: no separate .NET installation is needed.
Use Windows Server 2019, 2022, or 2025 x64. It includes native Windows service
lifetime support and reads appsettings.json from the executable directory.

Configure appsettings.json for your deployment before installation. Supply database
connection settings through your approved secrets process. The packaged ATSPM
library validates all four database contexts; configure each required context and
disable migrations for a listener-only deployment. Repository defaults do not
contain deployment credentials.

Set `SpeedListenerConfiguration.EventTimeZoneId` to match your existing event-log
timestamps. It defaults to `UTC`; use a zone such as `America/Denver` for a database
that stores that agency's local wall-clock time. Verify this choice before cutover;
changing it does not convert existing records.

If you include credentials in a deployment ZIP, restrict access to both the ZIP
and installed directory. Keep credential-bearing packages out of source control.

Install paths are explicit parameters. Service names default to the application
name `AtspmSpeedListener`, but can be changed with `-ServiceName`; use the same name
for installation, upgrades and diagnostics. Installation records that name in the
service's `ATSPM_SERVICE_NAME` environment variable so the host uses the matching
Windows service identity. The shared application event-log source remains
`AtspmSpeedListener` in the `Atspm` log. Firewall rule names default to
`<ServiceName>-UDP` and can be overridden. `LocalService` is the default installer
account; select another built-in account with `-ServiceAccount` or pass a dedicated
account with `-Credential (Get-Credential)`. Provision dedicated accounts with the
Log on as a service right through your organization's account policy. Upgrades and
diagnostics preserve the registered identity. Paths, sample
addresses and service names in examples are placeholders to replace for your agency.

## 1. Extract and check connectivity

Open PowerShell **as Administrator** on the test server. Adjust the ZIP path:

```powershell
Unblock-File C:\Temp\ATSPM-SpeedListener-win-x64.zip
Expand-Archive C:\Temp\ATSPM-SpeedListener-win-x64.zip -DestinationPath C:\Services\ATSPM-SpeedListener-win-x64
Set-Location C:\Services\ATSPM-SpeedListener-win-x64
$databaseHost = Read-Host 'Database host'
$databasePort = [int](Read-Host 'Database TCP port')
Test-NetConnection $databaseHost -Port $databasePort
```

`TcpTestSucceeded` must be true. Use the port for your configured database engine
(for example, PostgreSQL commonly uses TCP 5432). When using a direct Cloud SQL
address, configure it in your deployment settings. The Windows server's outbound public IP must be permitted
in Cloud SQL authorized networks, and its network must allow outbound TCP 5432.
Use only the server's specific IP, not a broad public range. A VPN/private route or
Cloud SQL Auth Proxy is an alternative, but requires changing the configured host.
Existing TLS connection options are preserved from the secrets.

## 2. Register and start the service

Replace the example sensor subnet with the actual sensor IP addresses or CIDR
ranges. The installer restricts file access, registers the event source, creates
the service under the selected account, and opens the configured UDP port only for the specified sources.

```powershell
# Configure SpeedListenerConfiguration.UdpPort for your agency before installation.
$installPath = 'C:\Services\ATSPM-SpeedListener-win-x64'
.\Install-Service.ps1 -InstallPath $installPath -SensorRemoteAddress '192.0.2.0/24'
Start-Service AtspmSpeedListener
Start-Sleep -Seconds 10
Get-Service AtspmSpeedListener
$port = (Get-Content (Join-Path $installPath 'appsettings.json') -Raw | ConvertFrom-Json).SpeedListenerConfiguration.UdpPort
Get-NetUDPEndpoint -LocalPort $port
Get-WinEvent -FilterHashtable @{LogName='Atspm'; ProviderName='AtspmSpeedListener'} -MaxEvents 20 |
    Select-Object TimeCreated, LevelDisplayName, Message
```

If your organization's script policy blocks the installer, use its approved signing
or execution process. The installer does not change execution policy.

Point test sensors to the Windows server's address on the configured UDP port. Upstream firewalls
must also allow that traffic. A running service alone is not proof of readiness:
look for loaded device mappings and the listener-started message, then increasing
received/published counters in the minute summaries. The configuration database
must contain a current location version whose identifier matches the first four
characters of the packet detector ID. Its lowest-ID SpeedSensor device is used;
DeviceIdentifier does not need to match the packet. Missing location/device mappings
are dropped. Avoid two listeners writing the same sensor stream to
these databases, and remember test packets become real event-log records.

## 3. Cloud Logging (optional)

Database access uses your configured database credentials. Set
`Logging.Google.Enabled` to `false` if Cloud Logging is not configured.
Windows Event Log includes listener summaries.

To also send logs to GCP, give the server's service identity Application Default
Credentials with `roles/logging.logWriter` in your GCP project, then set
`Logging.Google.Enabled` to `true` in appsettings.json and restart the service.
On Compute Engine, prefer the VM's attached service account with suitable scopes.
For an external server, use your organization's workload identity setup. If using
a credential file, keep it outside this package, grant the registered service account read access,
and set `GOOGLE_APPLICATION_CREDENTIALS` for the service (not just your interactive
PowerShell session). Your interactive gcloud login does not configure the service's identity.

## Rejected-packet diagnostics

Enable Debug for the console category
`Logging.Console.LogLevel.SpeedListener.BackgroundServices.SpeedListenerBackgroundService`
(the full category is a single JSON property name in `LogLevel`). Rejections include
the sender, reason, datagram length and up to 64 payload bytes as `PayloadHex`.
`XS` speed messages with six numeric detector digits are supported, with or without
the six-byte sensor prefix. Other headers, including the observed `X1` messages,
are rejected rather than interpreted as speeds.

For console output from a Windows service, run as Administrator:

```powershell
.\deploy\Enable-StartupDiagnostics.ps1 -InstallPath $installPath -ServiceName 'AtspmSpeedListener'
```

It configures `ATSPM_STARTUP_LOG` for that service and grants the registered service
account write access to the log. An optional `-LogPath` changes its location. The
script preserves whether the service was running or stopped.
The diagnostic file is append-only and has no rotation; disable diagnostics after
troubleshooting by removing that environment entry and restarting the service.

For an optional packet capture without stopping services:

```powershell
.\deploy\Capture-RejectedPackets.ps1 -Port $port -DurationSeconds 60 `
    -SensorAddress '192.0.2.10' -Components all
```

Omit `-SensorAddress` to include all sensors. `-Components nics` (the default)
captures only network adapters; `all` includes additional Windows network
components and may contain multiple appearances of the same packet. The script
replaces pktmon filters and writes `.etl` and `.pcapng` files to the current user's temporary directory by default; use `-OutputDirectory` to choose another folder.
Use `-FilterName` for a custom pktmon filter name and `-MaxFileSizeMB` to change the
circular capture size (default 64 MB).

## Optional switchover test

`Test-ServiceSwitchover.ps1` temporarily transfers a UDP port from an existing
Windows service to the candidate listener. Provide your agency's existing service
name and port; neither is assumed. Example values below are placeholders:

```powershell
.\deploy\Test-ServiceSwitchover.ps1 -InstallPath $installPath `
    -ExistingServiceName 'AgencyExistingListener' -CandidateServiceName 'AtspmSpeedListener' `
    -TargetPort 12000 -DurationSeconds 60
```

The candidate's original port is read from its settings. The existing service must
be running and own `TargetPort`, and the candidate must use a different port. The
script validates the candidate executable path, temporarily updates only its UDP
port and firewall port filter, and waits for the candidate to bind. It restores the
original settings bytes, firewall port filter and both service states even if the
test fails. If restoration cannot complete, it reports the errors. Other services
are not stopped and no process is forcibly terminated. Use `-FirewallRuleName` for
a rule with a custom name and `-LogPath` for a custom diagnostic log. Normal
installation and upgrades never run this test automatically.

## Stop, troubleshoot, upgrade, remove

Publishing uses eight concurrent database writers by default. Writes for a given
device remain serialized to preserve the repository's read/merge/write behavior.
`SpeedListenerConfiguration.DatabaseWriteParallelism` controls this limit; reduce
it if the database cannot support the concurrent load. The write deadline still
applies to a complete batch, and success is logged as `Archived ... envelopes`.

```powershell
Stop-Service AtspmSpeedListener
Start-Service AtspmSpeedListener
# After a settings change:
Restart-Service AtspmSpeedListener
```

Allow roughly 50 seconds for the configured shutdown drain; forced termination can
lose queued events. UDP and the in-memory queue provide no durable delivery guarantee.

If startup fails, check the Atspm and Application event logs. For direct console
diagnostics, stop the service first and run ` .\SpeedListener.exe listener ` from
an elevated shell in the installation folder; press Ctrl+C to stop. This uses the
same database settings and can write events if sensors are sending traffic.
Connection failures usually mean network access, credentials, TLS configuration,
or missing speed-sensor mappings. A zero mapping count prevents startup.

## Upgrade an existing service

Extract a complete new Windows x64 publish package outside the installation folder,
then run the reusable script as Administrator:

```powershell
.\deploy\Upgrade-Service.ps1 -SourcePath 'C:\Temp\ATSPM-SpeedListener-new' `
    -InstallPath 'C:\Services\ATSPM-SpeedListener-win-x64'
```

The script validates that the registered executable belongs to the installation,
stops only the selected service (`-ServiceName`, default `AtspmSpeedListener`) with a bounded graceful wait, and backs up every
runtime file it will replace under `.upgrade-backups` inside the secured install
folder. It upgrades executables, assemblies, symbols, and the dependency/runtime
manifests, including nested satellite assemblies. It preserves all `appsettings*`
files, the `Configuration` directory, logs, service identity, environment and
firewall rules. Configure any newly required settings separately before upgrading.

A previously running service is restarted and must own the effective UDP port
observed before the upgrade, including environment, volume or command-line
overrides. Supply `-ExpectedPort` when the service has multiple UDP bindings or you
need an explicit readiness port. For a stopped service, the script reads the port
from appsettings.json unless `-ExpectedPort` is supplied.
A previously stopped service remains stopped. If copying or startup fails, the
script restores the previous runtime files, removes newly added runtime files,
and attempts to restore the original service state. Rollback failures are reported
with the backup path. No process is forcibly terminated, and unrelated services are never stopped by this upgrade script.

Retain backups until the upgraded service is verified; remove old backups later
using your normal maintenance process.

To remove service registration and its firewall rule (files remain):

```powershell
Stop-Service AtspmSpeedListener
sc.exe delete AtspmSpeedListener
Remove-NetFirewallRule -Name AtspmSpeedListener-UDP
```

References: [Microsoft Windows service hosting](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service),
[Cloud SQL authorized networks](https://cloud.google.com/sql/docs/postgres/authorize-networks),
[Google Application Default Credentials](https://cloud.google.com/docs/authentication/application-default-credentials).
