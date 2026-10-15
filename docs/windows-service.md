# Windows service deployment

The Windows x64 package is self-contained: no separate .NET installation is needed.
Use Windows Server 2019, 2022, or 2025 x64. It includes native Windows service
lifetime support and reads appsettings.json from the executable directory.
These installation/upgrade examples assume a **complete multi-file publish folder**
with SpeedListener.exe, SpeedListener.dll, dependency/runtime manifests and the
maintained scripts under deploy/. The service test packages supplied with this
branch have that layout. The current release-assets workflow instead produces
single-file runtime archives; those are not interchangeable with the complete
folder required by Upgrade-Service.ps1. If building from source, publish with
`-p:PublishSingleFile=false` and use deployment scripts from this repository.

Configure appsettings.json for your deployment before installation. Use strict
JSON without comments for Windows PowerShell 5.1 deployment scripts, which read
it through ConvertFrom-Json; remove any commented-out sample sections. The .NET
host accepts comments, but that does not make them portable across script hosts. Supply database
connection settings through your approved secrets process. The packaged ATSPM
library validates all four database contexts; configure each required context and
disable migrations for a listener-only deployment. Repository defaults do not
contain deployment credentials.

The listener derives each mapped location's zone from its latitude/longitude.
`SpeedListenerConfiguration.EventTimeZoneId` is the fallback only when coordinates
are missing, invalid or `(0, 0)`; it defaults to the Windows server local zone.
Set that fallback explicitly to the agency zone when deploying outside that zone
or in containers. Valid coordinates take precedence even if an older configuration
sets UTC. Each mapping refresh reports the number of locations using fallback.
All event timestamps and hourly keys are stored as local wall time without time-zone
metadata. Verify coordinates and fallback before cutover; this does not convert
existing records.

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

## Extract and check connectivity

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

## Register and start the service

Replace the example sensor subnet with the actual sensor IP addresses or CIDR
ranges. The installer restricts file access, registers the event source, creates
the service under the selected account, and opens the configured UDP port only for the specified sources.

```powershell
# Configure SpeedListenerConfiguration.UdpPort for your agency before installation.
$installPath = 'C:\Services\ATSPM-SpeedListener-win-x64'
.\deploy\Install-Service.ps1 -InstallPath $installPath -SensorRemoteAddress '192.0.2.0/24'
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
ASCII digits of the packet detector ID. Its lowest-ID **Active** SpeedSensor is used;
DeviceIdentifier does not need to match the packet. Missing location/device mappings
are skipped and counted in `UnknownSinceLast`/unknown-detector warnings. A parsed
packet is not proof of an eligible device mapping. Avoid two listeners writing the same sensor stream to
these databases, and remember test packets become real event-log records.

## Mapping and time-zone checks

A missing mapping can mean no eligible location version, an identifier other than
four numeric digits, or no Active SpeedSensor on the selected version. Unknown,
Inactive, Decommissioned, Testing and Staging statuses do not participate. If the
current version lacks an active sensor, a historical device is not substituted.
A deleted version is excluded before selection, allowing the prior non-deleted
version to become current. LoggingEnabled alone does not make a device eligible.

Configuration changes are picked up on the next demand-driven cache refresh,
default five minutes, while batches are being processed. A restart reloads immediately.
Expect the affected unknown warnings to stop; their counts cover the last summary
period only. Loaded-map count and `EventsPublished` provide supporting evidence.

Warning `SpeedListenerTimeZoneFallback` means selected locations have unusable
coordinates or an unresolved zone and are using the configured fallback. Debug
`SpeedListenerLocationTimeZone` identifies those locations individually. Ensure
coordinates and fallback match the existing database's local-time convention.
Changing them affects future events and does not repair historical rows.
`SpeedListenerTimeZoneMismatch` identifies resolved zones whose rules differ from
the configured fallback. Inspect the listed locations' coordinates; a difference
can also be intentional. Details are capped at 20 locations per refresh, with a
summary for additional locations. The Windows package converts IANA identifiers
to native Windows zones before lookup, so ICU IANA support is not required.

Any secured installation directory is supported; Program Files is not required.
The service needs read/execute access to runtime/configuration and write access
to an enabled diagnostic capture file. The installer and diagnostics helper
provision their documented permissions. To allow every sensor source in the local
firewall rule, use `-SensorRemoteAddress 'Any'`; this does not change upstream rules.

## Cloud Logging (optional)

Database access uses your configured database credentials. Set
`Logging.Google.Enabled` to `false` if Cloud Logging is not configured.
Windows Event Log defaults to Warning and Error. To also include listener
summaries, set `Logging.EventLog.LogLevel.SpeedListener` to `Information`.

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
Every rejection is logged at Debug, without sampling or throttling. Information,
Warning and Error have no per-packet payload output; the summary and aggregate
loss warning count every rejection. Successful packets do not produce per-packet
logs. `Truncated=True` identifies a payload longer than the 64-byte preview.
`XS` speed messages with six numeric detector digits are supported, with or without
the six-byte sensor prefix. Valid `X1` actuation messages (including the documented multi-drop prefix) are
classified as actuation and are not speed events. Basic untagged XS messages need
an exact endpoint mapping; otherwise they increment `UnmappedSpeed`. Malformed X1
and unsupported binary `Z4`/track-file `XT` messages remain rejected.

For console output from a Windows service, run as Administrator:

```powershell
.\deploy\Enable-StartupDiagnostics.ps1 -InstallPath $installPath -ServiceName 'AtspmSpeedListener'
```

It configures `ATSPM_STARTUP_LOG` for that service and grants the registered service
account write access to the log. An optional `-LogPath` changes its location. The
script preserves whether the service was running or stopped.
The diagnostics helper defaults to an unlimited append-only capture so Debug
records are retained. Set `-MaxFileSizeMB 1024` for a 1 GiB cap, or use
`-MaxFileSizeMB 0` explicitly for no cap. It records the limit in the service's
`ATSPM_STARTUP_LOG_MAX_BYTES` environment variable. If you set `ATSPM_STARTUP_LOG`
directly without that limit variable, the default is 10 MiB. With a positive
limit, older contents are cleared when full; no backups are created. Use a
separate capture path for each process. Disable diagnostics after troubleshooting
by removing that environment entry and restarting the service. Operational logs
continue through the normal ATSPM logging providers.

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
applies to a complete archive/save attempt, and success reports archived
device-hour contributions, event count and fully archived envelope count.

```powershell
Stop-Service AtspmSpeedListener
Start-Service AtspmSpeedListener
# After a settings change:
Restart-Service AtspmSpeedListener
```

With the default settings, allow the 45-second drain plus the host's five-second
shutdown margin; forced termination can
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
$source = 'C:\Temp\ATSPM-SpeedListener-new'
& (Join-Path $source 'deploy\Upgrade-Service.ps1') -SourcePath $source `
    -InstallPath 'C:\Services\ATSPM-SpeedListener-win-x64' `
    -ServiceName 'AtspmSpeedListener' -TimeoutSeconds 90
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
