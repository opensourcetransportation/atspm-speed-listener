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

The listener supports UDP only. On IPv6-capable systems it binds a dual-stack
`::` socket, receiving IPv4 and IPv6 on the same port. IPv4-mapped sources are
normalized to IPv4 for source-endpoint matching. On IPv4-only systems it binds
IPv4 and rejects pure IPv6 source mappings during validation. The emitter's TCP option is retained for test
utility compatibility, but there is no TCP listener.

## Configuration

Listener settings are read from `SpeedListenerConfiguration` in
`appsettings.json`. Environment variables use the standard .NET double-underscore
format, for example `SpeedListenerConfiguration__UdpPort=10088`.

| Setting | Default | Description |
| --- | ---: | --- |
| `UdpPort` | `10088` | UDP bind port |
| `EventTimeZoneId` | Server local time zone | Fallback for unusable coordinates; valid location coordinates determine the event zone |
| `UntaggedSpeedDetectorMappings` | `[]` | Explicit source IP:port to six-digit detector mapping for basic XS messages |
| `ChannelCapacity` | `100000` | Maximum queued parsed events |
| `BatchSize` | `5000` | Size-triggered flush threshold |
| `FlushInterval` | `00:00:30` | Maximum age of a partial batch |
| `ShutdownFlushTimeout` | `00:00:45` | Shared drain deadline; must allow one publish's attempts plus retry delays; a full backlog may not drain |
| `ShutdownMaxWriteAttempts` | `1` | Attempts per publish while draining |
| `DeviceMappingRefreshInterval` | `00:05:00` | ATSPM device-cache refresh interval |
| `ArchiveParallelism` | `50` | Parallelism for transforming envelopes into hourly rows; EF compresses payloads during saving |
| `DatabaseWriteParallelism` | `8` | Concurrent writers for different devices; writes to the same device remain serialized |
| `WriteTimeout` | `00:00:30` | Database write-attempt timeout |
| `MaxWriteAttempts` | `3` | Attempts for transient database failures |
| `PoisonDeviceFailureThreshold` | `3` | Consecutive data-attributable drops for one device before failing the listener |
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
version has the latest non-deleted `Start` at or before the refresh time in
the location's coordinate-derived time zone (or `EventTimeZoneId` fallback). Equal dates use the highest location ID and count the remaining
tied rows as duplicates. Its lowest-ID active SpeedSensor device is selected.
Older versions are not used when the current non-deleted version lacks an active
SpeedSensor. All channels at that location share the selected device; full detector
identifiers remain in the stored events. DeviceIdentifier and sensor IP are not
used for this lookup.

Deployment prerequisite: this protocol routes a six-digit detector ID as exactly
four ASCII location digits followed by two channel digits. ATSPM supports location
identifiers up to ten characters, but other lengths and nonnumeric identifiers
are not addressable by this routing scheme. They are reported as invalid mappings;
they are never truncated into potentially conflicting locations. The latest
non-deleted version effective at the location wall clock is selected, then its
lowest-ID active SpeedSensor. Deleting a version allows the previous eligible
version to become current, matching ATSPM; an inactive device never wins selection.

The service reads the legacy packet layout used by the ATSPM Speed Listener pull
request: MPH at byte 8, KPH at byte 9, a six-byte ASCII detector identifier at
bytes 10-15, and an optional timestamp suffix. Compact packets starting with `XS`
omit the six-byte prefix: MPH is at byte 2, KPH at byte 3, and the six-digit
detector identifier at bytes 4-9. Both formats use the receipt time unless a
timestamp suffix is supplied. Compact packets with incomplete or nonnumeric
detector identifiers are rejected. Prefixed messages require a `Z` plus five-digit
sensor prefix followed by `XS`; both formats require six numeric detector digits
for ATSPM routing. Basic untagged `XS` messages are accepted only with an explicit
`UntaggedSpeedDetectorMappings` entry for the source IP and UDP port; otherwise they
increment `UnmappedSpeed` rather than `Rejected`. Valid `X1` actuation messages
(simple and `Z0` multi-drop) increment `Actuation` and emit no speed events. Binary
`Z4` remains unsupported and rejected pending its protocol specification.
See [supported Wavetronix protocols](docs/wavetronix-protocols.md) for configuration.
Messages joined within one UDP datagram are parsed individually at the documented
`~\r\r` terminator, with individual invalid messages counted as rejections. A valid
message followed by an incomplete message preserves the valid event and rejects
the incomplete tail. Fragments are not reassembled across separate datagrams.
Unexpected trailing data that is neither a terminator nor a valid timestamp is rejected.
`Received` counts datagrams; `Parsed` counts queued events and `Rejected` counts
invalid messages, so one datagram can produce multiple parsed or rejected records.
The summary's `UnknownSinceLast` and each unknown-detector warning count only the last
summary period, resetting on each summary. Detector detail aggregation is capped
at 256 IDs per period, with all additional skipped events counted in an overflow
warning. Debug retains all per-batch detector details.
Archiving uses the local `ArchiveDataEventsWithoutDeduplication` workflow step,
adapted from ATSPM's `ArchiveDataEvents` with its upstream license header retained.
The production envelope path groups speed events by calendar hour, using the
envelope's location and device. It constructs `List<SpeedEvent>` and
`CompressedEventLogs<SpeedEvent>` directly, without reflection or dynamic dispatch.
Assigning through the base payload accessor bypasses the generic model's HashSet
conversion. Every occurrence is preserved, including separate identical events
and repeated references. Retry recognition remains a separate write operation.

Parsed events are mapped to ATSPM
`SpeedSensor` devices, grouped by device, converted into hourly compressed event
logs, and upserted through cancellable EF operations using the packaged ATSPM
context, models, primary keys and compression. The local writer preserves every occurrence delivered to a successful write because the packaged generic Data accessors and
repository Union would otherwise remove equal events. The writer uses the base
payload accessor and cancellable EF operations. Successfully acknowledged hourly rows are skipped on retry;
uncertain in-process commits are checked against fixed-size fingerprints of the
persisted event fields. LocationIdentifier is row metadata, not part of an event
fingerprint. The retry plan retains fingerprints rather than full-hour snapshots; merging and
compression still materialize hourly payloads. Distinct events with identical values are preserved, including within
one batch and across separate batches. A retry reuses the same logical row and
recognizes its intended prefix after a commit, including subsequent appends by
another writer. An unresolvable outside change logs a device-hour conflict and
skips only that uncertain contribution; other hours and devices continue.
WriteConflicts and ConflictEvents expose those cases, which may already be
committed. RowsPublished and EventsPublished count each confirmed device-hour
contribution and its events once, including successful hours of partial envelopes
and commits recognized after a lost acknowledgement. EnvelopesPublished counts
fully archived envelopes. A conflict or poison failure prevents that device's
poison-drop history from being reset, including during isolated retries. No database schema change is
required; retry state lasts only for the current publish operation.

Receipt times and explicitly offset sensor timestamps are instants converted
to the mapped location's time zone, derived from latitude/longitude with
GeoTimeZone. Zones are cached with the current device mappings; there is no database
or coordinate lookup per packet. Location effective dates use the same derived
zone. Missing, invalid or `(0, 0)` coordinates use `EventTimeZoneId`, which defaults
to the server local zone; a bounded warning reports the fallback count each refresh.
Debug identifies each mapped location's zone and fallback status. An explicit
fallback setting does not override a valid coordinate-derived zone.

Offset-less sensor timestamps are interpreted as local time in that location zone.
Repeated fall-back times select the occurrence closest to packet receipt; all
occurrences are retained. Nonexistent spring-forward times require an explicit
offset. All stored event timestamps and hourly keys use `DateTimeKind.Unspecified`,
including UTC locations/fallbacks: wall-clock digits are stored without time-zone
metadata. Changing coordinates or the fallback does not migrate existing records.

The existing ATSPM date-range speed/controller report repositories preserve stored
occurrences through the base payload accessor. Direct typed compressed-row Data
access still uses a HashSet. Standard ATSPM archive/import/update paths may remove
occurrences before storage; this listener's write path bypasses that deduplication.

[Database-provider validation](docs/speed-listener-design.md#verification-and-remaining-gates)
covers model/schema/compression contracts for PostgreSQL, SQL Server, MySQL,
Oracle and SQLite. Live insert/append checks have passed for PostgreSQL and SQLite;
SQL Server/MySQL/Oracle live-engine checks remain outstanding. The opt-in
`tests/DatabaseProviderSmoke` probe requires an empty disposable database.

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
