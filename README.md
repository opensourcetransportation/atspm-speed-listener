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

Discover current speed devices from the configuration database and generate one
synthetic speed event per device per second for 60 seconds:

```powershell
.\SpeedListener.exe generate --host 127.0.0.1 --port 12000 --duration 60
```

Preview the target device IDs without sending packets:

```powershell
.\SpeedListener.exe generate --port 12000 --list-targets
```

Use your test listener's actual port. Configuration is read from the executable's
directory and `Configuration/`, with the same database settings as the listener.
The generator queries configuration only; UDP events sent to a listener can become
real archived records. See [the load-testing guide](docs/load-testing.md) for rates,
coverage, isolated tests and result interpretation.

The older sample emitter is also available:

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
| `EventTimeZoneId` | `UTC` | Time zone for stored events; set to the agency database's convention, for example `America/Denver` |
| `ChannelCapacity` | `100000` | Maximum queued parsed events |
| `BatchSize` | `5000` | Size-triggered flush threshold |
| `FlushInterval` | `00:00:30` | Maximum age of a partial batch |
| `ShutdownFlushTimeout` | `00:00:45` | Shared drain deadline; must allow one publish's attempts plus retry delays; a full backlog may not drain |
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
Normal listener logs default to Information and framework/ATSPM dependency logs
to Warning. Every rejected packet is logged at Debug when enabled; normal levels
have no packet payloads. Optional startup capture supports configurable limits
or unlimited troubleshooting capture. See [logging and retention](docs/logging.md)
for sink limits and Docker configuration.

## Processing behavior

Routing uses the first four characters of the packet detector identifier as the
location identifier (for example, `502620` routes to `5026`). The current location
version has the latest `Start` at or before the refresh time (UTC). A current
deletion suppresses the mapping; older versions are not resurrected. Equal dates
use the highest location ID and count the remaining tied rows as duplicates. Its lowest-ID SpeedSensor
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
logs, and upserted through cancellable EF operations using the packaged ATSPM
context, models, primary keys and compression. The local writer preserves ATSPM's
value-equality union because the packaged repository methods do not accept
cancellation tokens. Successfully acknowledged hourly rows are skipped on retry;
uncertain commits are safe to replay because value-equal events are deduplicated.

Receipt and suffix timestamps are interpreted as instants in UTC, then converted
to `EventTimeZoneId` for storage. UTC remains the default. For an agency database
that stores local wall-clock times, configure its zone before ingesting data;
non-UTC storage uses `DateTimeKind.Unspecified`. Match the existing database's
convention, including its treatment of repeated hours when daylight saving ends.
Changing the setting does not migrate existing records.

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
