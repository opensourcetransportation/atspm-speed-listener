# Speed Listener Migration Design

Status: Proposed  
Target repository: `opensourcetransportation/atspm-speed-listener`  
Source: [`utahudot/udot-atspm` PR #217, "Speed Listener"](https://github.com/utahudot/udot-atspm/pull/217) (head `2650aabbd84f3b0085262407c2f51c444c9cb48c`, branch `AVE-2526-Speed-Listener-DL`)  
Primary runtime entry point: `SpeedListener.BackgroundServices.SpeedListenerBackgroundService`

## 1. Purpose

Move ownership of the speed-event listener runtime out of the ATSPM monolith and into this dedicated repository. The service receives UDP datagrams from speed sensors, parses them into ATSPM speed events, associates sensor identifiers with configured ATSPM devices, batches the events by device and hour, and writes them directly to the ATSPM event-log database as compressed event logs.

This migration preserves the wire behavior needed by existing deployments while improving lifecycle, cancellation, batching, testability, and production safety.

### 1.1 Output path decision

The listener writes **directly to the ATSPM event-log database**. The HTTP Data API path, the Kafka publisher, and the Pub/Sub publisher from PR #217 are removed from scope and will not be migrated.

This decision removes a substantial amount of the prototype's machinery. The prototype's `EventBatchEnvelope` carried events as a Newtonsoft `JToken` purely so they could survive JSON transport, and `ArchiveEnvelopeDataEvents` existed only to rehydrate that `JToken` back into `SpeedEvent` objects. With no serialization boundary, the listener already holds typed `SpeedEvent` instances and can construct `CompressedEventLogs<SpeedEvent>` directly. The envelope type, the JSON contract, the publisher abstraction, and the rehydration step are all deleted rather than migrated.

It also resolves two of the prototype's open questions outright: there is no API authentication to configure, and idempotency is provided by the repository upsert described in section 5.6.

## 2. Current state

This repository already has:

- a .NET 9 command-line host;
- an implemented `emitter` command used to generate sample sensor packets;
- an empty `listener` command;
- a stub `SpeedListenerBackgroundService` whose `ExecuteAsync` throws `NotImplementedException`;
- a draft configuration class; and
- a dependency on `Utah.Udot.Atspm.Infrastructure` 5.3.1.

Note that `Configuration/SpeedListenerConfiguration.cs` has been renamed on disk, but the class it contains and its `[ConfigurationSection]` attribute are both still named `EventListenerConfiguration`. Until the type and the attribute are renamed, the bound configuration section is `EventListenerConfiguration`, not `SpeedListenerConfiguration`.

PR #217 contains the working prototype spread across the monolith:

| Responsibility | Source implementation |
| --- | --- |
| Hosted-service lifecycle | `Atspm/EventListener/EventListenerWorker.cs` |
| Host and dependency registration | `Atspm/EventListener/Program.cs` |
| UDP receive loop | `Atspm/Infrastructure/Services/Receivers/UdpReceiver.cs` |
| UDP abstraction | `Atspm/Infrastructure/Services/Receivers/IUdpReceiver.cs` |
| Packet parsing | `Atspm/Infrastructure/Services/Listeners/RawSpeedPacketParser.cs` |
| Batching, device mapping, envelope creation | `Atspm/Infrastructure/Services/Listeners/SpeedBatchListenerBase.cs` |
| UDP-to-batch adapter | `Atspm/Infrastructure/Services/Listeners/UDPSpeedBatchListener.cs` |
| Database write path | `Atspm/Infrastructure/Messaging/Database/DatabaseEventPublisher.cs`, `Atspm/Infrastructure/Workflows/EventBatchEnvelopeWorkflow.cs`, `Atspm/Infrastructure/WorkflowSteps/ArchiveEnvelopeDataEvents.cs` |
| Listener tests | `Atspm/InfrastructureTests/Services/Listeners/UDPSpeedBatchListenerTests.cs` |

### 2.1 Verified package surface

The following were confirmed present in the pinned 5.3.1 packages and will be consumed, not copied:

| Type or member | Package |
| --- | --- |
| `SpeedEvent` (`DetectorId`, `Mph`, `Kph`, `Timestamp`, value equality) | `Utah.Udot.Atspm.Data` |
| `CompressedEventLogs<T>`, `CompressedEventLogBase` | `Utah.Udot.Atspm.Data` |
| `EventLogContext` with `DbSet<CompressedEventLogs<SpeedEvent>>` | `Utah.Udot.Atspm.Data` |
| `Device`, `Location`, `DeviceTypes`, `DeviceStatus` | `Utah.Udot.Atspm.Data` |
| `IDeviceRepository` | `Utah.Udot.Atspm` |
| `IEventLogRepository`, `ISpeedEventLogRepository` | `Utah.Udot.Atspm` |
| `IEventLogRepositoryExtensions.Upsert<T>` | `Utah.Udot.Atspm` |
| `SaveArchivedEventLogs` workflow step | `Utah.Udot.Atspm.Infrastructure` |
| `AddAtspmDbContext`, `AddAtspmEFConfigRepositories`, `AddAtspmEFEventLogRepositories` | `Utah.Udot.Atspm.Infrastructure` |

The following exist only in PR #217 and are **not** published. None of them will be migrated, because the direct-write design has no use for them: `EventBatchEnvelope`, `IEventPublisher<T>`, `EventBatchEnvelopeWorkflow`, `ArchiveEnvelopeDataEvents`, `DatabaseEventPublisher`, `HttpPublisher`, `KafkaPublisher`, `PubSubPublisher`, `IUdpReceiver`, `UdpReceiver`, `RawSpeedPacketParser`, `SpeedBatchListenerBase`, `UDPSpeedBatchListener`, `EventListenerConfiguration`.

The receiver, parser, batching, and configuration types in that list are listener-owned and move here. The envelope, publisher, and workflow types are dropped.

## 3. Scope

### In scope

- The `listener` CLI command and its host registration.
- `SpeedListenerBackgroundService` as the owner of the long-running listener lifecycle.
- Relocation of listener-specific configuration, receiver, parser, batching, and mapping logic from the ATSPM shared libraries.
- UDP socket receive behavior.
- Parsing the existing speed-sensor packet format.
- Bounded buffering and hour-aligned batching.
- Resolving a sensor identifier to an ATSPM speed-sensor device and location.
- Writing compressed speed event logs to the ATSPM event-log database.
- Graceful cancellation and a bounded final flush.
- Configuration validation, structured logging, tests, and container operation.
- Deployment guidance for running the listener independently of `udot-atspm`.

### Out of scope

- The HTTP Data API publish path, including `EventBatchEnvelope` and `HttpPublisher`.
- Kafka and Pub/Sub publishing.
- Authentication. No authentication mechanism is required for the database write path beyond the connection string.
- Metrics instrumentation and exporters. Observability for this migration is structured logging only; see section 10.
- Health-check endpoints. The prototype had none, and this service has no web host; see section 10.1.
- Changes to ATSPM reporting or speed aggregation.
- Database migrations or transfer of historical speed events. The existing `TransferSpeedEventsService` in `DatabaseInstaller` owns backfill.
- TCP sensor support. The current prototype is UDP-only.
- A durable local message spool. This can be added later if loss during prolonged database outages is unacceptable.
- Changes to the speed emitter except shared packet-contract fixtures.
- Copies or forks of ATSPM domain models, repositories, EF contexts, or database providers that are already available through supported NuGet packages.

## 4. Behavioral compatibility

The first production release must preserve these externally visible behaviors unless integration testing proves a correction is required:

- Listen on a configurable UDP port (prototype default: `10088`).
- Require at least 16 packet bytes.
- Read MPH from byte 8 and KPH from byte 9.
- Read a six-byte ASCII sensor identifier from bytes 10 through 15.
- Accept an optional ASCII timestamp after byte 15, with optional `~`, whitespace, CR, LF, and NUL padding.
- Use receipt time when no valid device timestamp is present.
- Match sensor identifiers case-insensitively after trimming padding.
- Only map devices whose ATSPM device type is `SpeedSensor`.
- Group outgoing events by mapped device and by clock hour.
- Persist each group as a `CompressedEventLogs<SpeedEvent>` row keyed on `{ LocationIdentifier, DeviceId, DataType, Start, End }`, with `Start`/`End` snapped to the hour boundary.

Packet fixtures captured from a real sensor are required before declaring wire compatibility complete. The prototype reads but does not validate the header byte at offset 7; the migration will retain that behavior initially and log observed header values so the field can be characterized.

### 4.1 Deviation: device status filtering

The prototype filters on device type only. This design additionally filters on `DeviceStatus`. `DeviceStatus` is a six-value enum (`Unknown`, `Decommissioned`, `Inactive`, `Active`, `Testing`, `Staging`), so "active" must be defined explicitly rather than assumed. The selected set is a decision in section 14. This is an intentional deviation from prototype behavior and must be verified against the configured device inventory before cutover, since narrowing the set silently stops ingesting a sensor.

### 4.2 Known limitation: event deduplication collapses genuine duplicates

`SpeedEvent` implements value equality over `{ LocationIdentifier, Timestamp, DetectorId, Mph, Kph }`, and the repository upsert unions old and new data through a `HashSet`. This gives free idempotency on retry, which is the reason the direct-write path needs no distributed transaction.

It also means two genuinely distinct vehicles detected by the same sensor at the same timestamp resolution at the same speed are indistinguishable and collapse into a single stored event. This is most likely when the receipt-time fallback is used, because receipt times cluster more tightly than device timestamps. The effect is undercounting at high flow rates.

This behavior is inherent to the ATSPM compressed-event-log storage model and is not introduced by this migration, but the direct-write path makes this service the place where it happens. It must be measured during the shadow-mode comparison in section 12 and documented. If undercounting proves material, the fix is upstream in the storage model, not in this listener.

## 5. Proposed architecture

```text
UDP socket
   |
   v
IUdpDatagramReceiver
   |
   v
ISpeedPacketParser ---> invalid-packet log
   |
   v
bounded Channel<SpeedEvent>
   |
   v
ISpeedEventBatchProcessor
   |                 |
   |                 +--> IDeviceMappingProvider (cached ATSPM device lookup)
   v
ISpeedEventLogWriter
   |
   v
IEventLogRepository.Upsert -> EventLogContext -> ATSPM event-log database
```

`SpeedListenerBackgroundService` composes this pipeline and owns its lifecycle. Transport, parsing, mapping, and writing remain separate services so each can be tested without a live socket or database.

Note that no TPL Dataflow workflow appears here. The prototype routed writes through `EventBatchEnvelopeWorkflow`, whose save step runs at `MaxDegreeOfParallelism = 1` and is therefore a sequential pipeline wrapped in concurrent machinery. A direct call from the batch processor to the writer is equivalent in throughput and considerably easier to test and reason about. `SaveArchivedEventLogs` from the packages may be reused if it proves convenient, but it is not required.

### 5.1 `SpeedListenerBackgroundService`

The background service will:

1. log effective non-secret startup settings;
2. start the UDP receive producer and batch-consumer tasks;
3. await both tasks so faults are observed by the host;
4. stop accepting packets when cancellation is requested;
5. complete the channel and allow the consumer to drain;
6. attempt a final write within `ShutdownFlushTimeout`; and
7. allow unexpected exceptions to fail the host, making container/service restart policies effective.

The service must not create a single long-lived dependency-injection scope containing a scoped EF repository. The prototype does exactly this: `EventListenerWorker.ExecuteAsync` opens one scope with `using var scope = _scopeFactory.CreateScope()` and resolves the scoped listener from it for the entire process lifetime. Database work must instead create short scopes through `IServiceScopeFactory` per unit of work.

### 5.2 UDP receiver

`IUdpDatagramReceiver` isolates `UdpClient`. Its production implementation binds during startup, uses cancellation-aware `ReceiveAsync`, and disposes the socket to unblock shutdown. Expected cancellation is not logged as an error.

The OS receive buffer must be configurable. At high packet rates the dominant loss mechanism is kernel socket-buffer overflow, which happens before the application observes anything; bounding the in-process channel does not address it. Set `SO_RCVBUF` from configuration and, where the platform exposes it, log the socket drop counter at shutdown.

UDP has no delivery guarantee. The receiver should process quickly and write parsed events to a bounded channel. The overflow policy must be explicit. The recommended default is to reject the newest event and log a rate-limited warning with a running dropped count rather than allow unbounded memory growth. Capacity must be configurable.

### 5.3 Packet parser

`ISpeedPacketParser` is a synchronous, side-effect-free parser returning a success/failure result. It normalizes the detector identifier once and reports a reason for malformed data without logging raw packet contents by default.

Timestamp parsing must use an explicit invariant format or `DateTimeOffset` rules agreed from real packet samples. The prototype's `DateTime.TryParse` plus `SpecifyKind(..., Utc)` behavior can reinterpret values incorrectly and should not be copied verbatim.

Note that the prototype host sets `AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true)`. Whichever `DateTimeKind` convention the event-log tables use, the listener must match it exactly and state it in one place, because `Timestamp` participates in the value equality that drives deduplication. A kind mismatch between this service and the backfill tooling produces silent duplicate rows.

### 5.4 Batch processor

A single consumer owns the mutable batch, eliminating the prototype's lock and fire-and-forget calls. It flushes when either:

- `BatchSize` events have accumulated; or
- `FlushInterval` has elapsed since the first event in the current batch; or
- the clock crosses an hour boundary, so a completed hourly bucket is closed promptly.

Only one write operation is active unless measured throughput demonstrates a need for controlled parallelism.

On shutdown, queued events are drained and the final partial batch is written within a configured timeout. A failed final write is logged with an event count and causes a non-successful shutdown; the service must not claim successful persistence.

### 5.5 Device mapping

`IDeviceMappingProvider` returns a case-insensitive lookup from normalized sensor identifier to `{ DeviceId, LocationIdentifier }` for speed-sensor devices in the configured status set.

Mappings are cached and refreshed on a configurable interval. This avoids the prototype's full database query for every batch. Duplicate identifiers, missing locations, and blank identifiers are configuration errors and must be surfaced explicitly.

Two identifier-shape rules must be validated at load, because the wire format provides exactly six ASCII bytes:

- A `DeviceIdentifier` longer than six characters can never be matched by an incoming packet. Reject it loudly at mapping load rather than letting the sensor silently go unmapped.
- Two devices whose identifiers share the first six characters collide. Treat this as a duplicate-identifier configuration error.

`LocationIdentifier` is constrained to 10 characters by the event-log schema; validate it at mapping load so the violation surfaces at startup rather than on the first write.

Unknown sensors are skipped and counted, with rate-limited warning logs.

### 5.6 Event log writer

`ISpeedEventLogWriter` converts a mapped batch into `CompressedEventLogs<SpeedEvent>` rows and persists them through `IEventLogRepository.Upsert`, resolving the repository from a short-lived scope.

Grouping is by `{ LocationIdentifier, DeviceId, clock hour }`, matching the prototype's `ArchiveEnvelopeDataEvents` behavior and the convention used by `TransferSpeedEventsService`, where `Start` is the hour boundary and `End` is `Start.AddHours(1)`. Snapping to the hour boundary rather than to observed min/max timestamps is what makes the primary key stable across flushes.

`Upsert` performs a read-modify-write: it looks up the existing row, decompresses `Data`, unions it with the incoming events through a `HashSet`, recompresses, and writes back. Two consequences drive the rest of this design.

**Write amplification.** Every flush rewrites the entire accumulated hour for that device. At a 5-second flush interval, a device-hour is rewritten 720 times, and the average rewrite carries half the hour's data, so total bytes written approach 360 times the data volume, multiplied by device count. The prototype's 5-second cadence was chosen for an HTTP endpoint and must not be carried over unexamined. `FlushInterval` is now a direct trade between data latency and database write load, and its default is set accordingly in section 7. It must be re-derived from the load test in phase 7.

**Read-modify-write races.** The prototype serialized this with `MaxDegreeOfParallelism = 1`, which protects a single process only. Two listener instances writing the same device-hour will lose events to last-write-wins. Single-writer operation is therefore a correctness requirement, not just a duplicate-avoidance preference, and constrains the cutover in section 12.

Because `Upsert` is idempotent over identical events, a retry after an ambiguous failure is safe. Retry transient database failures with bounded exponential backoff and jitter. Do not retry constraint or schema violations.

## 6. Data and ownership boundaries

### 6.1 Consume from ATSPM NuGet packages

Use supported ATSPM NuGet packages for reusable platform capabilities. The current direct reference is `Utah.Udot.Atspm.Infrastructure` 5.3.1; its transitive dependencies include the core ATSPM and database-provider packages. Pin one compatible package version across the dependency graph and upgrade it deliberately.

The listener consumes, rather than copies, everything listed in section 2.1. Unlike the API-based design this replaces, the listener now **does** require `ISpeedEventLogRepository`/`IEventLogRepository`, `EventLogContext`, and `AddAtspmEFEventLogRepositories`, because the event-log database is its output boundary.

### 6.2 Move into this repository

| PR #217 source | Destination responsibility in this repository | Migration action |
| --- | --- | --- |
| `Infrastructure/Configuration/EventListenerConfiguration.cs` | `Configuration/SpeedListenerConfiguration.cs` | Rename the class and its `[ConfigurationSection]` attribute; replace the draft with validated listener options |
| `Infrastructure/Services/Receivers/IUdpReceiver.cs` | `Receivers/IUdpDatagramReceiver.cs` | Move/rename the abstraction |
| `Infrastructure/Services/Receivers/UdpReceiver.cs` | `Receivers/UdpDatagramReceiver.cs` | Move and correct cancellation/socket lifecycle; add receive-buffer configuration |
| `Infrastructure/Services/Listeners/RawSpeedPacketParser.cs` | `Parsing/SpeedPacketParser.cs` | Move behind a testable parser contract |
| `Infrastructure/Services/Listeners/SpeedBatchListenerBase.cs` | `Services/SpeedEventBatchProcessor.cs` | Move and replace locking/fire-and-forget behavior with the bounded channel design |
| `Infrastructure/Services/Listeners/UDPSpeedBatchListener.cs` | `SpeedListenerBackgroundService` plus receiver/parser composition | Move behavior; do not retain unnecessary inheritance |
| `Infrastructure/Workflows/EventBatchEnvelopeWorkflow.cs`, `WorkflowSteps/ArchiveEnvelopeDataEvents.cs`, `Messaging/Database/DatabaseEventPublisher.cs` | `Writing/SpeedEventLogWriter.cs` | Reimplement as a direct typed writer; drop the envelope, the `JToken` rehydration, the publisher abstraction, and the Dataflow workflow |
| `EventListener/EventListenerWorker.cs` | `BackgroundServices/SpeedListenerBackgroundService.cs` | Replace the stub with the migrated orchestration; do not carry over the process-lifetime DI scope |
| `EventListener/Program.cs` | `ListenerCommand` and listener host registration | Fold executable bootstrapping into this repository's CLI host; drop `AddEventPublishers` and the `IngestApi` HTTP client |

Not migrated at all: `Messaging/Http/HttpPublisher.cs`, `Messaging/Kafka/KafkaPublisher.cs`, `Messaging/PubSub/PubSubPublisher.cs`, `Messaging/EventBatchEnvelope.cs`, `Application/Services/IEventPublisher.cs`.

Note that `DatabaseEventPublisher.PublishAsync(IReadOnlyList<...>, int, CancellationToken)` catches and logs every exception, returning normally after a failed write. That is silent data loss and must not be reproduced; see section 9.

All moved code must use `SpeedListener` namespaces and this repository's standard license header. It should be adapted into the component boundaries in this design rather than copied verbatim, because several prototype lifecycle and reliability behaviors need correction.

## 7. Configuration contract

Use a single `SpeedListenerConfiguration` section. Environment-variable examples use the .NET double-underscore convention. Database connection configuration is supplied through the existing ATSPM `DatabaseConfiguration` conventions consumed by `AddAtspmDbContext` and is not duplicated here.

| Setting | Required/default | Purpose |
| --- | --- | --- |
| `UdpPort` | `10088` | UDP bind port, 1-65535 |
| `ReceiveBufferBytes` | `4194304` | `SO_RCVBUF` size for the listening socket |
| `ChannelCapacity` | `100000` | Maximum parsed events waiting for batching |
| `BatchSize` | `5000` | Maximum events in an in-memory batch |
| `FlushInterval` | `00:05:00` | Maximum age of a non-empty partial batch |
| `ShutdownFlushTimeout` | `00:00:30` | Maximum shutdown drain/flush time |
| `DeviceMappingRefreshInterval` | `00:05:00` | Mapping-cache lifetime |
| `DeviceStatuses` | `[ "Active" ]` | Device statuses eligible for mapping |
| `WriteTimeout` | `00:00:30` | Timeout for one database write attempt |
| `MaxWriteAttempts` | `3` | Total attempts for transient database failures |

Example:

```json
{
  "SpeedListenerConfiguration": {
    "UdpPort": 10088,
    "ReceiveBufferBytes": 4194304,
    "ChannelCapacity": 100000,
    "BatchSize": 5000,
    "FlushInterval": "00:05:00",
    "ShutdownFlushTimeout": "00:00:30",
    "DeviceMappingRefreshInterval": "00:05:00",
    "DeviceStatuses": [ "Active" ],
    "WriteTimeout": "00:00:30",
    "MaxWriteAttempts": 3
  }
}
```

Three defaults changed from the prototype and require justification.

`BatchSize` was `50_000`. At any plausible sensor packet rate the size trigger could never fire before the time trigger, making it dead configuration; 50,000 events in five seconds is 10,000 packets per second. A value in the low thousands can actually trigger.

`FlushInterval` was five seconds. Section 5.6 explains why that cadence is inappropriate for an upsert-based write path: it produces roughly 360 times write amplification per device-hour. Five minutes gives twelve rewrites per device-hour, or roughly 6.5 times amplification, at the cost of up to five minutes of ingest latency. This is the single most important number to validate under load in phase 7.

`threads` from the prototype is removed. It was lower-case, ambiguous, and fed `EventBatchEnvelopeWorkflow`'s `MaxDegreeOfParallelism` on the archive step only; the save step was hard-coded to 1, so it never increased write concurrency. Because concurrent writers to the same device-hour are now a correctness hazard, parallel writing must not be reintroduced without a partitioning scheme that guarantees one writer per device-hour.

Validate options at startup. Ports, positive capacities, `BatchSize <= ChannelCapacity`, timeouts, retry limits, and known `DeviceStatuses` values must fail fast with actionable messages.

The generic host's `ShutdownTimeout` defaults to five seconds and will terminate the process before a 30-second final flush completes. Configure `HostOptions.ShutdownTimeout` from `ShutdownFlushTimeout` plus headroom, or the shutdown acceptance criterion in section 13 cannot be met regardless of implementation quality.

`WriteTimeout` multiplied by `MaxWriteAttempts` plus backoff must fit inside `ShutdownFlushTimeout`, or the final flush cannot complete one full retry cycle. Either reduce the attempt budget for the shutdown path or document that shutdown writes are single-attempt.

## 8. Command and dependency injection

The `listener` command will start a listener-specific generic host. Listener and emitter option validation must be separate; the listener must not require emitter host, protocol, or interval settings.

The listener host registers:

- validated `SpeedListenerConfiguration`;
- `HostOptions.ShutdownTimeout` derived from `ShutdownFlushTimeout`;
- ATSPM configuration and event-log database services from the pinned NuGet packages, via `AddAtspmDbContext`, `AddAtspmEFConfigRepositories`, and `AddAtspmEFEventLogRepositories`;
- `IUdpDatagramReceiver` as a singleton owned by the host;
- parser as a singleton/stateless service;
- device mapping with safe, short-lived data-access scopes;
- the event log writer, resolving repositories per write from `IServiceScopeFactory`;
- batch processor; and
- `SpeedListenerBackgroundService` as the hosted service.

Refactor the current emitter-specific `HostBootstrapper.RunHostAsync<TService>` into separate listener/emitter registration paths or a shared host builder plus command-specific service-registration callbacks.

## 9. Failure handling and delivery semantics

| Failure | Required behavior |
| --- | --- |
| Malformed packet | Skip, increment in-process counter, structured debug/warning log without packet payload |
| Unknown sensor | Skip, increment in-process counter, rate-limited warning |
| Mapping database unavailable at startup | Fail startup so the service can be restarted |
| Mapping refresh fails after startup | Continue with last known mapping, log degraded state, retry later |
| Channel full | Apply configured drop policy and log a rate-limited warning with a running dropped count |
| Transient database failure | Bounded retry; preserve the current batch in memory |
| Constraint/schema violation | Do not retry. Log the batch's location, device, and hour with the event count, drop the batch, and continue. Do not fail the process. |
| Sustained database failure past the retry budget | Fail the service after logging the batch identity and event count; do not silently discard |
| Host cancellation | Stop receive, drain channel, bounded final flush |
| Unexpected receive/consumer fault | Propagate to host; do not run partially failed |

The distinction between the last three rows matters. A single malformed batch that the database will never accept must not crash-loop the service: the process would restart, receive the same input, and die again, taking down ingest for every other sensor. Only systemic failure fails the process.

During a database outage the writer's retry loop blocks the single consumer, so the channel continues to fill and will begin shedding new events under the configured drop policy. This is intended, but it means a database outage causes data loss at the head of the pipeline within roughly `ChannelCapacity / arrival rate` seconds of the outage starting. That budget should be stated in deployment documentation alongside the retry configuration.

The v1 service is memory-backed. A process crash, machine loss, or sustained outage beyond retry limits can lose UDP events. That limitation must appear in deployment documentation. A durable queue/spool is a separate reliability enhancement.

## 10. Observability

Observability for this migration is **structured logging only**. No metrics instrumentation, exporter, or metrics dependency is in scope. Counters named below are in-process values included in log messages, not published metrics.

Use structured logs with event IDs for startup, bind success/failure, packet rejection, unknown sensor, mapping refresh, batch flush, write retry/failure, and shutdown result. Never log credentials, connection strings, or full raw packets.

Emit a periodic summary log at a configurable interval carrying at minimum:

- datagrams received;
- packets parsed and rejected;
- unknown-sensor events;
- channel depth and dropped events;
- batches and rows written;
- write latency, retries, and failures; and
- mapping age and refresh failures.

These are the same quantities the shadow-mode comparison in section 12 depends on, so the summary log must be machine-parseable.

### 10.1 Health checks

No health-check endpoint is in scope. PR #217's `EventListener` exposed none: it is a `Host.CreateDefaultBuilder` worker with `UseWindowsService` and no web host. `AddAtspmDbContext` registers `AddHealthChecks().AddDbContextCheck<>()` transitively, so the health-check services are present in the container, but nothing maps or serves them, and no ATSPM worker-style service does. Adding an endpoint would mean introducing an ASP.NET Core host solely for that purpose.

Liveness is therefore process liveness. The service must fail the host process when the producer or consumer task terminates unexpectedly, per section 5.1, so container and Windows service restart policies remain effective. Readiness is signalled by a single structured startup-complete log emitted only after UDP binding and the initial mapping load succeed. Document both for operators.

If a health endpoint is wanted later, it is an additive change and should be proposed separately with its hosting cost stated.

## 11. Testing strategy

### Unit tests

- Parser boundary lengths, sensor padding, MPH/KPH offsets, timestamps, invalid ASCII/data, and fallback receipt time.
- Batch flush by size, by interval, on hour boundary, and on shutdown.
- Grouping into hour-aligned buckets and `Start`/`End` snapping.
- Case-insensitive mapping, unknown sensor, duplicate mapping, over-length identifier, six-character prefix collision, over-length location identifier, and refresh fallback.
- Writer upsert behavior: new row insert, existing row union, retry idempotency, transient retry, constraint violation drop, permanent failure.
- Background-service startup, cancellation, final flush, and fault propagation.
- Configuration validation, including the shutdown-budget consistency rule.

Tests must use deterministic clocks and task-completion signals; do not use arbitrary `Task.Delay(50)` synchronization from the prototype tests.

### Integration tests

- Bind an ephemeral loopback UDP port, send fixture datagrams, and assert rows written to a SQLite or containerized event-log database.
- Exercise multiple sensors in one batch and verify one row per device-hour.
- Send two batches within the same device-hour and verify the second upserts into the first rather than replacing it. This is the highest-value single test in the suite; getting it wrong loses an hour of data per device.
- Replay an identical batch and verify no duplicate events accumulate.
- Cancel with a partial batch and verify the final write.
- Simulate transient database failure, constraint violation, and recovery.
- Verify the container listens on the configured port and exits cleanly on `SIGTERM`.

### Compatibility tests

- Compare rows written by this service against rows produced by the PR prototype for the same input.
- Replay sanitized real sensor packets through both the PR prototype and the new parser and compare normalized events.
- Compare a written row against one produced by `TransferSpeedEventsService` for the same period to confirm identical `DateTimeKind`, `Start`/`End`, and key conventions.

## 12. Deployment and rollout

Build and publish this repository as a separate image/process. The runtime needs inbound UDP access to `UdpPort` and read/write access to the ATSPM configuration and event-log databases. It no longer needs outbound HTTPS access to the Data API.

Roll out in shadow mode first: receive mirrored or test traffic and write to a non-production event-log database. Compare packet, mapped-event, row, and stored-event counts with the existing listener, and specifically measure the deduplication effect described in section 4.2.

Mirroring UDP traffic requires network-layer duplication such as port mirroring or a forwarding relay. The service cannot provide this; it is an infrastructure prerequisite that must be arranged before shadow mode can begin.

Cutover must be single-writer. Because `Upsert` is a read-modify-write against a shared row, two listeners writing the same device-hour concurrently will lose events, not merely duplicate them. Stop the old listener before starting the new one against the same stream. Keep the prior deployment artifact available for rollback.

After successful cutover, remove the EventListener executable and listener-specific Infrastructure code from the monolith PR/branch in a separate change. Before deleting `HttpPublisher`, `EventBatchEnvelope`, `IEventPublisher<T>`, and the Kafka/Pub/Sub publishers, confirm no other `udot-atspm` consumer depends on them; this design assumes listener-only ownership but that assumption has not been verified across the monolith.

## 13. Acceptance criteria

- `dotnet test` passes with unit and integration coverage for the listener pipeline.
- `SpeedListener listener` starts using JSON/environment configuration and remains running until cancellation.
- A known UDP fixture results in the expected `CompressedEventLogs<SpeedEvent>` row with correct hour-aligned `Start`/`End`.
- Two flushes within one device-hour accumulate rather than overwrite.
- A replayed identical batch produces no duplicate stored events.
- Size-, time-, hour-boundary-, and shutdown-triggered flushes are verified.
- Unknown/malformed input cannot crash the receive loop or grow memory without bound.
- A constraint violation drops one batch with a diagnostic log and does not terminate the process.
- Write failures are retried only when transient and are never silently discarded.
- Container shutdown drains or reports failure within `ShutdownFlushTimeout`, with `HostOptions.ShutdownTimeout` configured to allow it.
- Measured peak load stays within the targets agreed in section 14, including database write volume.
- Operational configuration and delivery limitations are documented in the README.

## 14. Decisions required before implementation completes

1. Confirm the exact timestamp format emitted by production sensors and whether it includes UTC/offset information, and confirm the `DateTimeKind` convention used by the event-log tables so this service matches the backfill tooling.
2. Select the `DeviceStatus` values eligible for mapping, and confirm the resulting device set against the current inventory. See section 4.1.
3. Agree the `FlushInterval` default against measured database write volume, and set the acceptable ingest-latency bound it implies. See section 5.6.
4. Set numeric targets for peak packet rate, CPU, memory, ingest latency, and acceptable drop rate, so section 13 can be judged.
5. Select and pin the ATSPM NuGet package version for implementation. 5.3.1 exposes every contract required by the direct-write path, as verified in section 2.1.
6. Confirm acceptable loss behavior during database outages and whether a durable spool is required before production.

Resolved by the direct-write decision and no longer open: Data API authentication (none required), Data API idempotency (provided by `Upsert` over value-equal events), and the Data API request/route/size contract.
