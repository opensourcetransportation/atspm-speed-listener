# Speed Listener Migration Design

Status: Proposed  
Target repository: `opensourcetransportation/atspm-speed-listener`  
Source: [`utahudot/udot-atspm` PR #217, "Speed Listener"](https://github.com/utahudot/udot-atspm/pull/217) (head `2650aabbd84f3b0085262407c2f51c444c9cb48c`, branch `AVE-2526-Speed-Listener-DL`)  
Primary runtime entry point: `SpeedListener.BackgroundServices.SpeedListenerBackgroundService`

## 1. Purpose

Move ownership of the speed-event listener runtime out of the ATSPM monolith and into this dedicated repository. The service receives UDP datagrams from speed sensors, parses them into ATSPM speed events, associates sensor identifiers with configured ATSPM devices, batches the events by device, and writes them to the ATSPM event-log database.

### 1.1 Scope posture

This is a **migration, not a redesign**. The functional scope is exactly what PR #217 does, minus the publish paths being dropped. The pipeline shape, the envelope, the archive/save workflow, and the compressed-event-log upsert are carried across as-is.

Two things change:

1. **The Kafka, Pub/Sub, and HTTP Data API publish paths are removed.** Only the database path is migrated. There is consequently no API base URL, no ingest route, no TLS handling, and no authentication mechanism to configure.
2. **Prototype lifecycle and reliability defects are corrected during the move.** These are enumerated in section 5 and are limited to defects that cause data loss, unbounded memory growth, or unobserved failures. They do not restructure the write path.

Explicitly unchanged: the `EventBatchEnvelope` contract, `EventBatchEnvelopeWorkflow`, `ArchiveEnvelopeDataEvents`, the hourly compressed-log bucketing, and `IEventLogRepository.Upsert`. The read-decompress-union-recompress-write sequence is out of scope for this migration.

## 2. Current state

This repository already has:

- a .NET 9 command-line host;
- an implemented `emitter` command used to generate sample sensor packets;
- an empty `listener` command;
- a stub `SpeedListenerBackgroundService` whose `ExecuteAsync` throws `NotImplementedException`;
- a draft configuration class; and
- a dependency on `Utah.Udot.Atspm.Infrastructure` 5.3.1.

Note that `Configuration/SpeedListenerConfiguration.cs` has been renamed on disk, but the class it contains and its `[ConfigurationSection]` attribute are both still named `EventListenerConfiguration`. Until the type and the attribute are renamed, the bound configuration section is `EventListenerConfiguration`.

PR #217 contains the working prototype spread across the monolith:

| Responsibility | Source implementation | Disposition |
| --- | --- | --- |
| Hosted-service lifecycle | `Atspm/EventListener/EventListenerWorker.cs` | Move |
| Host and dependency registration | `Atspm/EventListener/Program.cs` | Move |
| UDP receive loop | `Atspm/Infrastructure/Services/Receivers/UdpReceiver.cs` | Move |
| UDP abstraction | `Atspm/Infrastructure/Services/Receivers/IUdpReceiver.cs` | Move |
| Packet parsing | `Atspm/Infrastructure/Services/Listeners/RawSpeedPacketParser.cs` | Move |
| Batching, device mapping, envelope creation | `Atspm/Infrastructure/Services/Listeners/SpeedBatchListenerBase.cs` | Move |
| UDP-to-batch adapter | `Atspm/Infrastructure/Services/Listeners/UDPSpeedBatchListener.cs` | Move |
| Envelope contract | `Atspm/Infrastructure/Messaging/EventBatchEnvelope.cs` | Move |
| Publisher contract | `Atspm/Application/Services/IEventPublisher.cs` | Move |
| Database publisher | `Atspm/Infrastructure/Messaging/Database/DatabaseEventPublisher.cs` | Move |
| Archive/save workflow | `Atspm/Infrastructure/Workflows/EventBatchEnvelopeWorkflow.cs` | Move |
| Envelope-to-compressed-log step | `Atspm/Infrastructure/WorkflowSteps/ArchiveEnvelopeDataEvents.cs` | Move |
| Compressed-log persistence step | `Atspm/Infrastructure/WorkflowSteps/SaveArchivedEventLogs.cs` | Consume from package |
| HTTP publisher | `Atspm/Infrastructure/Messaging/Http/HttpPublisher.cs` | Drop |
| Kafka publisher | `Atspm/Infrastructure/Messaging/Kafka/KafkaPublisher.cs` | Drop |
| Pub/Sub publisher | `Atspm/Infrastructure/Messaging/PubSub/PubSubPublisher.cs` | Drop |
| Listener tests | `Atspm/InfrastructureTests/Services/Listeners/UDPSpeedBatchListenerTests.cs` | Move |

### 2.1 Verified package surface

Confirmed present in the pinned 5.3.1 packages and consumed rather than copied:

| Type or member | Package |
| --- | --- |
| `SpeedEvent`, `CompressedEventLogs<T>`, `CompressedEventLogBase` | `Utah.Udot.Atspm.Data` |
| `EventLogContext` with `DbSet<CompressedEventLogs<SpeedEvent>>` | `Utah.Udot.Atspm.Data` |
| `Device`, `Location`, `DeviceTypes` | `Utah.Udot.Atspm.Data` |
| `IDeviceRepository` | `Utah.Udot.Atspm` |
| `IEventLogRepository`, `ISpeedEventLogRepository` | `Utah.Udot.Atspm` |
| `IEventLogRepositoryExtensions.Upsert<T>` | `Utah.Udot.Atspm` |
| `SaveArchivedEventLogs` | `Utah.Udot.Atspm.Infrastructure` |
| `AddAtspmDbContext`, `AddAtspmEFConfigRepositories`, `AddAtspmEFEventLogRepositories` | `Utah.Udot.Atspm.Infrastructure` |

`SaveArchivedEventLogs` and `Upsert` being published is what allows the migration to keep the PR's write path without copying it: only the envelope, the workflow, the archive step, and the database publisher need to move.

Confirmed **absent** from the packages and therefore listener-owned: `EventBatchEnvelope`, `IEventPublisher<T>`, `EventBatchEnvelopeWorkflow`, `ArchiveEnvelopeDataEvents`, `DatabaseEventPublisher`, `IUdpReceiver`, `UdpReceiver`, `RawSpeedPacketParser`, `SpeedBatchListenerBase`, `UDPSpeedBatchListener`, `EventListenerConfiguration`.

## 3. Scope

### In scope

- The `listener` CLI command and its host registration.
- `SpeedListenerBackgroundService` as the owner of the long-running listener lifecycle.
- Relocation of the listener-specific configuration, receiver, parser, batching, envelope, publisher, and workflow types listed in section 2.
- UDP socket receive behavior.
- Parsing the existing speed-sensor packet format.
- Resolving a sensor identifier to an ATSPM speed-sensor device and location.
- Writing compressed speed event logs through the existing archive/save workflow.
- Graceful cancellation and a bounded final flush.
- Configuration validation, structured logging, tests, and container operation.
- Deployment guidance for running the listener independently of `udot-atspm`.

### Out of scope

- The HTTP Data API publish path, including `HttpPublisher` and the `IngestApi` HTTP client.
- Kafka and Pub/Sub publishing.
- Authentication. The database path needs no mechanism beyond the connection string.
- Any change to `IEventLogRepository.Upsert`, the compressed-event-log storage model, or the hourly bucketing performed by `ArchiveEnvelopeDataEvents`.
- Metrics instrumentation and exporters. Observability is structured logging only; see section 10.
- Health-check endpoints. The prototype had none, and this service has no web host; see section 10.1.
- Changes to ATSPM reporting or speed aggregation.
- Database migrations or transfer of historical speed events. `TransferSpeedEventsService` in `DatabaseInstaller` owns backfill.
- TCP sensor support. The prototype is UDP-only.
- A durable local message spool.
- Changes to the speed emitter except shared packet-contract fixtures.
- Copies or forks of ATSPM domain models, repositories, EF contexts, or database providers available through supported NuGet packages.

## 4. Behavioral compatibility

The first production release must preserve these externally visible behaviors unless integration testing proves a correction is required:

- Listen on a configurable UDP port (prototype default: `10088`).
- Require at least 16 packet bytes.
- Read MPH from byte 8 and KPH from byte 9.
- Read a six-byte ASCII sensor identifier from bytes 10 through 15.
- Accept an optional ASCII timestamp after byte 15, with optional `~`, whitespace, CR, LF, and NUL padding.
- Use receipt time when no valid device timestamp is present.
- Match sensor identifiers case-insensitively after trimming padding.
- Map only devices whose ATSPM device type is `SpeedSensor`. The prototype applies no device-status filter, and neither does this migration.
- Group outgoing events by mapped device.
- Build an envelope containing `DataType = "SpeedEvent"`, location identifier, device ID, the group's minimum and maximum timestamps, and the group's events.
- Pass envelopes through the archive workflow, which regroups them into hourly per-device buckets and upserts each as a `CompressedEventLogs<SpeedEvent>` row.

Packet fixtures captured from a real sensor are required before declaring wire compatibility complete. The prototype reads but does not validate the header byte at offset 7; the migration retains that behavior and logs observed values so the field can be characterized later.

### 4.1 Inherited properties, accepted as-is

Two properties of the storage path are documented here so they are understood rather than rediscovered. Neither is changed by this migration.

**Envelope `Start`/`End` are vestigial.** `SpeedBatchListenerBase` sets them from the group's minimum and maximum event timestamps, but `ArchiveEnvelopeDataEvents` ignores them and recomputes hour-aligned `Start`/`End` per bucket. The compressed-log row keys therefore come from the archive step, not the envelope. Preserve the envelope fields for contract compatibility, but do not rely on them.

**`Upsert` deduplicates by value equality.** `SpeedEvent` implements equality over `{ LocationIdentifier, Timestamp, DetectorId, Mph, Kph }`, and `Upsert` unions old and new data through a `HashSet`. This makes retries idempotent, which is why the write path needs no distributed transaction. It also means two genuinely distinct vehicles detected by the same sensor at the same timestamp resolution and speed collapse into one stored event. This is a property of the ATSPM storage model, is present in the prototype, and is out of scope to change. Note it during the shadow-mode comparison so the magnitude is known.

A third property constrains deployment rather than design: because `Upsert` is a read-modify-write against a shared row, two listeners writing the same device-hour concurrently lose events to last-write-wins. This makes single-writer cutover a correctness requirement, not just a duplicate-avoidance preference. See section 12.

## 5. Prototype defects corrected during the move

The pipeline shape is preserved. These specific behaviors are not, because each causes data loss, unbounded memory growth, or an unobserved failure. Nothing outside this list is redesigned.

**Fire-and-forget publishes.** `SpeedBatchListenerBase.Enqueue` calls `_ = SendBatchAsync(toSend)`, discarding the task. Faults are unobserved and shutdown cannot wait for in-flight work. Awaited on a single consumer instead.

**Swallowed failures.** `SendBatchAsync` catches and logs every publish exception then returns normally, and `DatabaseEventPublisher.PublishAsync(IReadOnlyList<...>, ...)` does the same. A failed write is indistinguishable from a successful one, and the batch is gone. Failures must be classified and surfaced; see section 9.

**Unbounded in-memory batch.** The batch is a `List<SpeedEvent>` guarded by a lock, with no capacity limit. If the write path stalls, memory grows without bound. Replaced with a bounded channel and an explicit overflow policy.

**No time-based flush.** The prototype flushes only when `BatchSize` (50,000) is reached, or on `Dispose`. Below that rate events remain buffered indefinitely, so a low-volume sensor's data does not land until shutdown. A `FlushInterval` is added.

**Unreachable batch size.** `BatchSize` of 50,000 is high enough that, combined with the absent time trigger, the flush is effectively shutdown-driven at realistic packet rates. Reduced to a value that can actually trigger.

**Process-lifetime DI scope.** `EventListenerWorker.ExecuteAsync` opens one scope with `using var scope = _scopeFactory.CreateScope()` and resolves the scoped listener from it for the process lifetime, holding a scoped EF repository open indefinitely. Database work uses short-lived scopes from `IServiceScopeFactory` per unit of work.

**Full device query per batch.** `SendBatchAsync` calls `_deviceRepository.GetList()` and materializes every speed-sensor device on every flush, then linear-scans it per group. The commented-out dictionary in the prototype shows the intent. Replaced with a refreshable cached lookup.

**Unconditional TLS bypass.** `Program.cs` configures `DangerousAcceptAnyServerCertificateValidator` on the ingest HTTP client. The client is removed entirely with the API path, so this does not migrate.

**Timestamp reinterpretation.** The parser's `DateTime.TryParse` plus `SpecifyKind(..., Utc)` can reinterpret values incorrectly. Replaced with an explicit format agreed from real packet samples. Because `Timestamp` participates in the value equality that drives deduplication, the convention must match `TransferSpeedEventsService` exactly or silent duplicate rows result. Note the prototype host also sets `AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true)`; whichever convention applies must be stated in one place.

**Host shutdown budget.** The generic host's `ShutdownTimeout` defaults to five seconds and will terminate the process before a longer final flush completes. Configured from `ShutdownFlushTimeout`.

**Terminal write failures.** A schema or constraint failure cannot be safely attributed to one event after envelopes enter the shared workflow. It therefore fails the service visibly rather than silently dropping a potentially valid batch. Invalid packets and mappings are rejected before persistence. A future poison-message policy requires a durable quarantine mechanism.

This applies only to failures caused by one batch's data. A schema or model mismatch is systemic: every batch fails identically, so dropping each one and continuing would run a service that looks healthy while discarding all data indefinitely, which is the same silent loss this section exists to remove. Systemic failures must fail the process. Section 9 draws the line.

## 6. Proposed architecture

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
SpeedEventBatchProcessor
   |                 |
   |                 +--> IDeviceMappingProvider (cached ATSPM device lookup)
   v
EventBatchEnvelope (one per device)
   |
   v
IEventPublisher<EventBatchEnvelope> -> DatabaseEventPublisher
   |
   v
EventBatchEnvelopeWorkflow
   |                 |
   |   ArchiveEnvelopeDataEvents --> hourly CompressedEventLogs<SpeedEvent>
   v
SaveArchivedEventLogs -> IEventLogRepository.Upsert -> event-log database
```

Everything from `EventBatchEnvelope` downward is the prototype's path, unchanged. `SpeedListenerBackgroundService` composes the pipeline and owns its lifecycle. Transport, parsing, mapping, and publishing remain separate services so each can be tested without a live socket or database.

`IEventPublisher<T>` is retained as the publisher contract even though only one implementation survives. It is the seam the tests use to exercise the batch processor without a database, and keeping it costs nothing.

### 6.1 `SpeedListenerBackgroundService`

The background service will:

1. log effective non-secret startup settings;
2. start the UDP receive producer and batch-consumer tasks;
3. await both tasks so faults are observed by the host;
4. stop accepting packets when cancellation is requested;
5. complete the channel and drain as much of it as `ShutdownFlushTimeout` allows, publishing batches in order under a reduced attempt budget;
6. on expiry, stop draining, count the events still queued plus any in-flight batch, log that residue at error level, and exit non-successfully; and
7. allow unexpected exceptions to fail the host, making container and service restart policies effective.

Step 5 is explicitly best-effort. A full channel cannot be drained within any deployable shutdown budget, so the design does not promise a complete drain; it promises that whatever is lost is counted and reported rather than discarded silently. A shutdown that drops events must never report success. See section 7 for the budget and the worst-case loss figure.

### 6.2 UDP receiver

`IUdpDatagramReceiver` isolates `UdpClient`. Its production implementation binds during startup, uses cancellation-aware `ReceiveAsync`, and disposes the socket to unblock shutdown. Expected cancellation is not logged as an error.

UDP has no delivery guarantee. The receiver processes quickly and writes parsed events to a bounded channel. The recommended overflow policy is to reject the newest event and log a rate-limited warning with a running dropped count. Capacity is configurable.

### 6.3 Packet parser

`ISpeedPacketParser` is a synchronous, side-effect-free parser returning a success/failure result. It normalizes the detector identifier once and reports a reason for malformed data without logging raw packet contents by default. Timestamp handling follows section 5.

### 6.4 Batch processor

A single consumer owns the batch, replacing the prototype's lock and fire-and-forget calls. It flushes when `BatchSize` events have accumulated, or when `FlushInterval` has elapsed since the first event in the current batch. It builds one `EventBatchEnvelope` per mapped device, exactly as `SendBatchAsync` does, and awaits the publisher.

On shutdown, the processor drains and publishes queued batches in order for as long as `ShutdownFlushTimeout` allows, using a reduced attempt budget so the fixed time covers as many batches as possible. When the budget expires it stops, counts the events still queued plus any in-flight batch, and logs that residue at error level. A failed or incomplete drain causes a non-successful shutdown; the service must never claim successful persistence for events it did not write.

### 6.5 Device mapping

`IDeviceMappingProvider` returns a case-insensitive lookup from normalized sensor identifier to `{ DeviceId, LocationIdentifier }` for `SpeedSensor` devices, matching the prototype's filter. Mappings are cached and refreshed on a configurable interval rather than re-queried per batch.

Duplicate identifiers, blank identifiers, and missing locations are configuration errors and are surfaced explicitly at load. Unknown sensors are skipped and counted, with rate-limited warning logs.

### 6.6 Database publisher

`DatabaseEventPublisher` moves across as-is except for the swallowed-exception defect in section 5. It continues to construct an `EventBatchEnvelopeWorkflow`, send envelopes into it, complete the input, and await the step completions.

Retry transient database failures with bounded exponential backoff and jitter. This is safe because `Upsert` is idempotent over value-equal events. Do not retry constraint or schema violations; propagate them so the service fails visibly.

The workflow's archive step takes a parallelism degree; its save step is hard-coded to `MaxDegreeOfParallelism = 1`. Do not raise save concurrency: concurrent writers to the same device-hour lose events, per section 4.1.

## 7. Configuration contract

Use a single `SpeedListenerConfiguration` section. Database connection configuration comes from the existing ATSPM `DatabaseConfiguration` conventions consumed by `AddAtspmDbContext` and is not duplicated here.

| Setting | Required/default | Purpose |
| --- | --- | --- |
| `UdpPort` | `10088` | UDP bind port, 1-65535 |
| `ChannelCapacity` | `100000` | Maximum parsed events waiting for batching |
| `BatchSize` | `5000` | Maximum events in an in-memory batch |
| `FlushInterval` | `00:00:30` | Maximum age of a non-empty partial batch |
| `ShutdownFlushTimeout` | `00:00:30` | Maximum shutdown drain/flush time |
| `DeviceMappingRefreshInterval` | `00:05:00` | Mapping-cache lifetime |
| `ArchiveParallelism` | `50` | Archive-step `MaxDegreeOfParallelism` in the workflow |
| `WriteTimeout` | `00:00:30` | Timeout for one publish attempt |
| `MaxWriteAttempts` | `3` | Total attempts for transient database failures |

```json
{
  "SpeedListenerConfiguration": {
    "UdpPort": 10088,
    "ChannelCapacity": 100000,
    "BatchSize": 5000,
    "FlushInterval": "00:00:30",
    "ShutdownFlushTimeout": "00:00:30",
    "DeviceMappingRefreshInterval": "00:05:00",
    "ArchiveParallelism": 50,
    "WriteTimeout": "00:00:30",
    "MaxWriteAttempts": 3
  }
}
```

Changes from the prototype's configuration:

- `ApiBaseUrl` and `ApiEndPoint` are removed with the HTTP path.
- `threads` is renamed to `ArchiveParallelism` and keeps its default of 50. The prototype's name was lower-case and ambiguous; it feeds only the archive step's `MaxDegreeOfParallelism`, not save concurrency.
- `BatchSize` drops from 50,000 to 5,000, and `FlushInterval` is new. See section 5.
- `ChannelCapacity`, `ShutdownFlushTimeout`, `DeviceMappingRefreshInterval`, `WriteTimeout`, and `MaxWriteAttempts` are new, supporting the corrections in section 5.

Validate options at startup. Ports, positive capacities, `BatchSize <= ChannelCapacity`, timeouts, and retry limits must fail fast with actionable messages.

`ShutdownFlushTimeout` is the deadline for the entire channel drain, not one batch. Expiry cancels processing, reports the remaining queued count, and fails shutdown. The generic host timeout includes additional headroom so it can observe and report that result.

That check is necessary but does not make the drain complete, and the configuration must not be read as if it did. A channel at `ChannelCapacity` holds twenty batches at the defaults, each of which may take a full attempt cycle; no plausible `ShutdownFlushTimeout` covers that, and raising it far enough would exceed what an orchestrator or service manager will wait before sending `SIGKILL`. `ShutdownFlushTimeout` is therefore a **bound on effort, not a guarantee of completion**, and section 6.1 specifies the best-effort drain it produces.

Two consequences follow. Shutdown writes should use a reduced attempt budget, single-attempt by default, so the fixed budget covers as many batches as possible rather than exhausting itself retrying one. And the worst-case shutdown loss is `ChannelCapacity` events plus one in-flight batch; state that number in deployment documentation next to the outage-loss budget in section 9.

`FlushInterval` is a trade between ingest latency and database write volume, because each flush upserts into the affected device-hour rows. Thirty seconds is a starting point to be re-derived from the load test in phase 7, not a tuned value.

## 8. Command and dependency injection

The `listener` command starts a listener-specific generic host. Listener and emitter option validation are separate; the listener must not require emitter host, protocol, or interval settings.

The listener host registers:

- validated `SpeedListenerConfiguration`;
- `HostOptions.ShutdownTimeout` derived from `ShutdownFlushTimeout`;
- ATSPM configuration and event-log database services via `AddAtspmDbContext`, `AddAtspmEFConfigRepositories`, and `AddAtspmEFEventLogRepositories`;
- `IUdpDatagramReceiver` as a singleton owned by the host;
- parser as a singleton/stateless service;
- device mapping with short-lived data-access scopes;
- `IEventPublisher<EventBatchEnvelope>` bound to `DatabaseEventPublisher`;
- batch processor; and
- `SpeedListenerBackgroundService` as the hosted service.

The prototype's `AddEventPublishers(context)` and its `IngestApi` HTTP client registration are not migrated.

Refactor the current emitter-specific `HostBootstrapper.RunHostAsync<TService>` into separate listener/emitter registration paths, or a shared host builder plus command-specific registration callbacks.

## 9. Failure handling and delivery semantics

| Failure | Required behavior |
| --- | --- |
| Malformed packet | Skip, increment in-process counter, structured debug/warning log without packet payload |
| Unknown sensor | Skip, increment in-process counter, rate-limited warning |
| Mapping database unavailable at startup | Fail startup so the service can be restarted |
| Mapping refresh fails after startup | Continue with last known mapping, log degraded state, retry later |
| Channel full | Apply configured drop policy and log a rate-limited warning with a running dropped count |
| Transient database failure | Bounded retry; preserve the current batch in memory |
| Constraint violation | Do not retry or discard silently. Log the batch identity and fail the service. |
| Schema or model mismatch | Fail the service immediately. Do not drop and continue. |
| Sustained database failure past the retry budget | Fail the service after logging batch identity and event count; do not silently discard |
| Host cancellation | Stop receive, drain what the shutdown budget allows, report the residue |
| Unexpected receive/consumer fault | Propagate to host; do not run partially failed |

The three failure-classification rows are deliberately distinct, and the distinction is about blast radius rather than severity.

A batch rejected because of its own contents affects one batch. Failing the process there would restart, re-receive the same input, and die again, taking down ingest for every healthy sensor. So it is dropped.

A schema or model mismatch — a missing column, a type or `DataType` conversion failure, an EF model error — affects every batch equally. Dropping each one and continuing produces a service that reports itself healthy while discarding one hundred percent of ingest, indefinitely and silently. That is precisely the catch-log-and-return defect section 5 removes, reintroduced through the failure policy. Fail the process. The resulting crash-loop is the correct signal: this is a deployment or migration fault that needs a human, and a crash-loop is how it gets noticed.

The middle row covers the case that resembles a poison batch but is really systemic at device scope, such as a `LocationIdentifier` exceeding the schema's ten characters. Every batch for that device will be rejected forever. Bound consecutive drops per device and fail the service once the threshold is crossed, so a permanently unwritable device surfaces instead of disappearing.

Implementations must classify by the provider's error rather than by exception type alone, since a single `DbUpdateException` covers all of these. Record the classification rule in one place and test each branch.

During a database outage the retry loop blocks the single consumer, so the channel fills and begins shedding new events under the drop policy. This is intended, but it means an outage causes loss at the head of the pipeline within roughly `ChannelCapacity` divided by arrival rate seconds. State that budget in deployment documentation alongside the retry configuration.


The v1 service is memory-backed. A process crash, machine loss, or sustained outage beyond retry limits can lose UDP events. That limitation must appear in deployment documentation. A durable queue or spool is a separate reliability enhancement.

## 10. Observability

Observability is **structured logging only**. No metrics instrumentation, exporter, or metrics dependency is in scope. Counters below are in-process values included in log messages, not published metrics.

Use structured logs with event IDs for startup, bind success/failure, packet rejection, unknown sensor, mapping refresh, batch flush, publish retry/failure, and shutdown result. Never log credentials, connection strings, or full raw packets.

Emit a periodic summary log carrying at minimum datagrams received, packets parsed and rejected, unknown-sensor events, channel depth and dropped events, batches and envelopes published, publish latency, retries and failures, and mapping age and refresh failures. The shadow-mode comparison in section 12 depends on these, so the summary must be machine-parseable.

### 10.1 Health checks

No health-check endpoint is in scope. PR #217's `EventListener` exposed none: it is a `Host.CreateDefaultBuilder` worker with `UseWindowsService` and no web host. `AddAtspmDbContext` registers `AddHealthChecks().AddDbContextCheck<>()` transitively, so the services are present in the container, but nothing maps or serves them, and no ATSPM worker-style service does. Adding an endpoint would mean introducing an ASP.NET Core host solely for that purpose.

Liveness is process liveness: the service fails the host process when the producer or consumer task terminates unexpectedly, per section 6.1, so restart policies remain effective. Readiness is a single structured startup-complete log emitted only after UDP binding and the initial mapping load succeed. Document both for operators. A health endpoint, if wanted later, is an additive change to propose separately.

## 11. Testing strategy

### Unit tests

- Parser boundary lengths, sensor padding, MPH/KPH offsets, timestamps, invalid ASCII/data, and fallback receipt time.
- Batch flush by size, by interval, and on shutdown.
- Grouping by device and envelope field population, including `Start`/`End` from group min/max.
- Case-insensitive mapping, unknown sensor, duplicate mapping, and refresh fallback.
- Publisher transient retry, constraint-violation drop, terminal failure, and cancellation.
- Background-service startup, cancellation, final flush, and fault propagation.
- Configuration validation, including the shutdown-budget consistency rule.

Tests must use deterministic clocks and task-completion signals; do not use the arbitrary `Task.Delay(50)` synchronization from the prototype tests.

### Integration tests

- Bind an ephemeral loopback UDP port, send fixture datagrams, and assert rows written to a SQLite or containerized event-log database.
- Exercise multiple sensors in one batch and verify one envelope per mapped device and one row per device-hour.
- Send two batches within the same device-hour and verify the second accumulates into the first rather than replacing it. This is the highest-value single test in the suite.
- Replay an identical batch and verify no duplicate events accumulate.
- Cancel with a partial batch and verify the final write.
- Simulate transient database failure, constraint violation, and recovery.
- Verify the container listens on the configured port and exits cleanly on `SIGTERM`.

### Compatibility tests

- Compare rows written by this service against rows produced by the PR prototype for the same input. This is the primary evidence that the migration preserved behavior.
- Replay sanitized real sensor packets through both the PR parser and the new parser and compare normalized events.
- Compare a written row against one produced by `TransferSpeedEventsService` for the same period to confirm identical `DateTimeKind`, `Start`/`End`, and key conventions.

## 12. Deployment and rollout

Build and publish this repository as a separate image or process. The runtime needs inbound UDP access to `UdpPort` and read/write access to the ATSPM configuration and event-log databases. It no longer needs outbound access to the Data API, Kafka, or Pub/Sub.

Roll out in shadow mode first: receive mirrored or test traffic and write to a non-production event-log database. Compare packet, mapped-event, envelope, row, and stored-event counts with the existing listener.

Mirroring UDP traffic requires network-layer duplication such as port mirroring or a forwarding relay. The service cannot provide this; it is an infrastructure prerequisite before shadow mode can begin.

Cutover must be single-writer. Because `Upsert` is a read-modify-write against a shared row, two listeners writing the same device-hour concurrently lose events rather than merely duplicating them. Stop the old listener before starting the new one against the same stream. Keep the prior deployment artifact available for rollback.

After successful cutover, remove the EventListener executable and listener-specific Infrastructure code from the monolith in a separate change. Before deleting the HTTP, Kafka, and Pub/Sub publishers and `IEventPublisher<T>`, confirm no other `udot-atspm` consumer depends on them; this design assumes listener-only ownership, which has not been verified across the monolith.

## 13. Acceptance criteria

- `dotnet test` passes with unit and integration coverage for the listener pipeline.
- `SpeedListener listener` starts using JSON/environment configuration and remains running until cancellation.
- A known UDP fixture results in the same compressed-event-log row the PR prototype produces for that input.
- Two flushes within one device-hour accumulate rather than overwrite.
- A replayed identical batch produces no duplicate stored events.
- Size-, time-, and shutdown-triggered flushes are verified.
- Unknown or malformed input cannot crash the receive loop or grow memory without bound.
- A constraint or schema violation is reported and terminates the service without being silently discarded.
- Publish failures are retried only when transient and are never silently discarded.
- Container shutdown completes within `ShutdownFlushTimeout`, with `HostOptions.ShutdownTimeout` configured to allow it.
- Shutdown with a channel too full to drain in the budget reports the undrained event count at error level and exits non-successfully, rather than reporting success.
- A schema or model mismatch fails the process rather than dropping batches and continuing.
- Measured peak load stays within the targets agreed in section 14.
- Operational configuration and delivery limitations are documented in the README.

## 14. Decisions required before implementation completes

1. Confirm the exact timestamp format emitted by production sensors and whether it includes UTC or offset information, and confirm the `DateTimeKind` convention used by the event-log tables so this service matches the backfill tooling.
2. Agree the `FlushInterval` and `BatchSize` defaults against measured database write volume, and the ingest-latency bound they imply.
3. Set numeric targets for peak packet rate, CPU, memory, ingest latency, and acceptable drop rate, so section 13 can be judged.
4. Select and pin the ATSPM NuGet package version. 5.3.1 exposes every contract the migrated path requires, as verified in section 2.1.
5. Confirm acceptable loss behavior during database outages and whether a durable spool is required before production.

Resolved by dropping the API path and no longer open: Data API authentication, idempotency, request contract, and TLS handling.
