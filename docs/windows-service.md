# Windows test server deployment

The Windows x64 package is self-contained: no separate .NET installation is needed.
Use Windows Server 2019, 2022, or 2025 x64. It includes native Windows service
lifetime support and reads appsettings.json from the executable directory.

Configure appsettings.json for your deployment before installation. Supply database
connection settings through your approved secrets process. The packaged ATSPM
library validates all four database contexts; configure each required context and
disable migrations for a listener-only deployment. Repository defaults do not
contain deployment credentials.

If you include credentials in a deployment ZIP, restrict access to both the ZIP
and installed directory. Keep credential-bearing packages out of source control.

## 1. Extract and check connectivity

Open PowerShell **as Administrator** on the test server. Adjust the ZIP path:

```powershell
Unblock-File C:\Temp\ATSPM-SpeedListener-win-x64.zip
Expand-Archive C:\Temp\ATSPM-SpeedListener-win-x64.zip -DestinationPath C:\Services\ATSPM-SpeedListener-win-x64
Set-Location C:\Services\ATSPM-SpeedListener-win-x64
Test-NetConnection '<database-host>' -Port 5432
```

`TcpTestSucceeded` must be true. When using a direct Cloud SQL address, configure it in your deployment settings. The Windows server's outbound public IP must be permitted
in Cloud SQL authorized networks, and its network must allow outbound TCP 5432.
Use only the server's specific IP, not a broad public range. A VPN/private route or
Cloud SQL Auth Proxy is an alternative, but requires changing the configured host.
Existing TLS connection options are preserved from the secrets.

## 2. Register and start the service

Replace the example sensor subnet with the actual sensor IP addresses or CIDR
ranges. The installer restricts file access, registers the event source, creates
the service under LocalService, and opens the configured UDP port only for the specified sources.

```powershell
# Set SpeedListenerConfiguration.UdpPort to 10089 for testing alongside the old service.
.\Install-Service.ps1 -SensorRemoteAddress '10.20.30.0/24'
Start-Service AtspmSpeedListener
Start-Sleep -Seconds 10
Get-Service AtspmSpeedListener
Get-NetUDPEndpoint -LocalPort 10089
Get-WinEvent -FilterHashtable @{LogName='Atspm'; ProviderName='AtspmSpeedListener'} -MaxEvents 20 |
    Select-Object TimeCreated, LevelDisplayName, Message
```

If your organization's script policy blocks the installer, use its approved signing
or execution process. The installer does not change execution policy.

Point test sensors to the Windows server's address on the configured UDP port (10089 for a side-by-side test). Upstream firewalls
must also allow that traffic. A running service alone is not proof of readiness:
look for loaded device mappings and the listener-started message, then increasing
received/published counters in the minute summaries. The configuration database
must contain a current location version whose identifier matches the first four
characters of the packet detector ID. Its lowest-ID SpeedSensor device is used;
DeviceIdentifier does not need to match the packet. Missing location/device mappings
are dropped. Avoid two listeners writing the same sensor stream to
these databases, and remember test packets become real event-log records.

## 3. Cloud Logging (optional)

Database access uses the configured PostgreSQL credentials. Set
`Logging.Google.Enabled` to `false` if Cloud Logging is not configured.
Windows Event Log includes listener summaries.

To also send logs to GCP, give the server's service identity Application Default
Credentials with `roles/logging.logWriter` in your GCP project, then set
`Logging.Google.Enabled` to `true` in appsettings.json and restart the service.
On Compute Engine, prefer the VM's attached service account with suitable scopes.
For an external server, use your organization's workload identity setup. If using
a credential file, keep it outside this package, grant LocalService read access,
and set `GOOGLE_APPLICATION_CREDENTIALS` for the service (not just your interactive
PowerShell session). Your local gcloud login is not the LocalService identity.

## Rejected-packet diagnostics

Enable Debug for the console category
`Logging.Console.LogLevel.SpeedListener.BackgroundServices.SpeedListenerBackgroundService`
(the full category is a single JSON property name in `LogLevel`). Rejections include
the sender, reason, datagram length and up to 64 payload bytes as `PayloadHex`.
`XS` speed messages with six numeric detector digits are supported, with or without
the six-byte sensor prefix. Other headers, including the observed `X1` messages,
are rejected rather than interpreted as speeds.

For console output from a Windows service, copy `Enable-StartupDiagnostics.ps1`
into the installation directory and run it as Administrator. It configures
`ATSPM_STARTUP_LOG` for that service and grants LocalService write access to the log.
The diagnostic file is append-only and has no rotation; disable diagnostics after
troubleshooting by removing that environment entry and restarting the service.

The deployment scripts also include an optional one-minute `Test-Port10088.ps1`
takeover test. It temporarily stops the old `SpeedListener` service, moves the new
listener to 10088, then restores the original settings and both services. Use it
only during an authorized test window. Normal installation does not run this test.

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

For upgrades, stop the service, securely back up appsettings.json, replace binaries
with the new publish output, restore the server's settings, and restart. Do not
overwrite server credentials with a different deployment's settings.

To remove service registration and its firewall rule (files remain):

```powershell
Stop-Service AtspmSpeedListener
sc.exe delete AtspmSpeedListener
Remove-NetFirewallRule -Name AtspmSpeedListener-UDP
```

References: [Microsoft Windows service hosting](https://learn.microsoft.com/en-us/dotnet/core/extensions/windows-service),
[Cloud SQL authorized networks](https://cloud.google.com/sql/docs/postgres/authorize-networks),
[Google Application Default Credentials](https://cloud.google.com/docs/authentication/application-default-credentials).
