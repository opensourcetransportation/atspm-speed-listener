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

The service reads the legacy packet layout used by the ATSPM Speed Listener pull
request: MPH at byte 8, KPH at byte 9, a six-byte ASCII detector identifier at
bytes 10-15, and an optional timestamp suffix. Parsed events are mapped to ATSPM
`SpeedSensor` devices, grouped by device, converted into hourly compressed event
logs, and upserted through the packaged event-log repository.

The in-memory channel is bounded. When it is full, the newest event is dropped
and counted in rate-limited summary logs. UDP itself is not reliable, and this release has no durable spool, so
process crashes or sustained database outages can also lose events. Run only one
listener against a production sensor stream and event-log database because the
ATSPM upsert path is a read-modify-write operation.

## Build and test

```powershell
dotnet test SpeedListener/SpeedListener.sln
```

The container image starts the `listener` command by default and should expose
the configured UDP port. On `SIGTERM`, receipt stops and the queued events are
drained up to `ShutdownFlushTimeout`.

See the [migration design](docs/speed-listener-design.md) and
[implementation plan](docs/speed-listener-implementation-plan.md) for the source
mapping, operational decisions, and monolith cleanup work.
