# ATSPM Speed Listener

A .NET worker that receives speed-sensor datagrams and writes hourly compressed
speed-event logs directly to an ATSPM event-log database. Shared ATSPM domain,
repository, Entity Framework, and database-provider behavior comes from the
published ATSPM NuGet packages; listener-specific transport and processing code
lives in this repository.

## Commands

Run the UDP listener:

```powershell
dotnet run --project SpeedListener/SpeedListener -- listener
```

Override the UDP port:

```powershell
dotnet run --project SpeedListener/SpeedListener -- listener --port 10088
```

Generate sample packets with the test emitter:

```powershell
dotnet run --project SpeedListener/SpeedListener -- emitter --host 127.0.0.1 --port 10088
```

The listener supports UDP only. The emitter's TCP option is retained for test
utility compatibility, but there is no TCP listener.

## Configuration

Listener settings are read from `SpeedListenerConfiguration` in
`appsettings.json`. Environment variables use the standard .NET double-underscore
format, for example `SpeedListenerConfiguration__UdpPort=10088`.

| Setting | Default | Description |
| --- | ---: | --- |
| `UdpPort` | `10088` | UDP bind port |
| `ChannelCapacity` | `100000` | Maximum queued parsed events |
| `BatchSize` | `5000` | Size-triggered flush threshold |
| `FlushInterval` | `00:00:30` | Maximum age of a partial batch |
| `ShutdownFlushTimeout` | `00:00:45` | Drain deadline; must exceed `WriteTimeout * ShutdownMaxWriteAttempts` |
| `ShutdownMaxWriteAttempts` | `1` | Attempts per publish while draining |
| `DeviceMappingRefreshInterval` | `00:05:00` | ATSPM device-cache refresh interval |
| `ArchiveParallelism` | `50` | Parallelism for envelope compression |
| `DatabaseWriteParallelism` | `8` | Concurrent writers for different devices; writes to the same device remain serialized |
| `WriteTimeout` | `00:00:30` | Database write-attempt timeout |
| `MaxWriteAttempts` | `3` | Attempts for transient database failures |
| `PoisonDeviceFailureThreshold` | `3` | Consecutive data-attributable drops before failing one device scope |
| `SummaryInterval` | `00:01:00` | Structured summary and loss-warning interval |

ATSPM `DatabaseConfiguration` settings configure the configuration and event-log
databases through the NuGet-provided registration extensions. Do not commit
connection strings or credentials.

Operational logging follows the ATSPM EventLogUtility host pattern: console logging,
Google Cloud logging, ATSPM volume configuration, and the `Atspm` Windows Event Log
when event-source registration is available. Listener messages use source-generated
`LoggerMessage` methods with stable event IDs.

## Processing behavior

Routing uses the first four characters of the packet detector identifier as the
location identifier (for example, `502620` routes to `5026`). The current location
version is the non-deleted version with the latest `Start` at or before the refresh
time (UTC); equal dates use the highest location ID. Its lowest-ID SpeedSensor
device is selected. Older versions are not used when the current version lacks a
SpeedSensor. All channels at that location share the selected device; full detector
identifiers remain in the stored events. DeviceIdentifier and sensor IP are not
used for this lookup.

The service reads the legacy packet layout used by the ATSPM Speed Listener pull
request: MPH at byte 8, KPH at byte 9, a six-byte ASCII detector identifier at
bytes 10-15, and an optional timestamp suffix. Compact packets starting with `XS`
omit the six-byte prefix: MPH is at byte 2, KPH at byte 3, and the six-digit
detector identifier at bytes 4-9. Both formats use the receipt time unless a
timestamp suffix is supplied. Compact packets with incomplete or nonnumeric
detector identifiers are rejected. Prefixed messages require a `Z` plus five-digit
sensor prefix followed by `XS`; both formats require six numeric detector digits
for ATSPM routing. Untagged messages and unrelated protocols such as `Z4` are rejected.
Messages joined within one UDP datagram are parsed individually at the documented
`~\r\r` terminator, with individual invalid messages counted as rejections. A valid
message followed by an incomplete message preserves the valid event and rejects
the incomplete tail. Fragments are not reassembled across separate datagrams.
Unexpected trailing data that is neither a terminator nor a valid timestamp is rejected.
`Received` counts datagrams; `Parsed` counts queued events and `Rejected` counts
invalid messages, so one datagram can produce multiple parsed or rejected records.
Parsed events are mapped to ATSPM
`SpeedSensor` devices, grouped by device, converted into hourly compressed event
logs, and upserted through the packaged event-log repository.

The in-memory channel is bounded. When it is full, the newest event is dropped
and counted in rate-limited summary logs. UDP itself is not reliable, and this release has no durable spool, so
process crashes or sustained database outages can also lose events. Run only one
listener against a production sensor stream and event-log database because the
ATSPM upsert path is a read-modify-write operation.

## Build and test

For Windows test-server installation, see [the Windows service guide](docs/windows-service.md).
The [deployment script reference](deploy/README.md) lists the configurable tools
for installation, upgrades, diagnostics, packet capture and switchover testing.

```powershell
dotnet test SpeedListener/SpeedListener.sln
```

The container image starts the `listener` command by default and should expose
the configured UDP port. On `SIGTERM`, receipt stops and the queued events are
drained up to `ShutdownFlushTimeout`.

See the [migration design](docs/speed-listener-design.md) and
[implementation plan](docs/speed-listener-implementation-plan.md) for the source
mapping, operational decisions, and monolith cleanup work.
