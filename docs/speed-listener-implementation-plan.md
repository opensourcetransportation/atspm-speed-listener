# Speed Listener Migration Implementation Plan

This plan implements the architecture in [`speed-listener-design.md`](./speed-listener-design.md). It deliberately combines two actions: reuse supported ATSPM NuGet packages for platform contracts and data access, and move all listener-specific runtime logic out of ATSPM shared libraries into this repository.

The output boundary is the ATSPM event-log database. The HTTP Data API, Kafka, and Pub/Sub publish paths from PR #217 are not migrated, no authentication mechanism is required, and metrics instrumentation and health-check endpoints are out of scope.

## Phase 0: Contract discovery and baseline

- [x] Record the PR #217 commit used as the migration source: `2650aabbd84f3b0085262407c2f51c444c9cb48c` on branch `AVE-2526-Speed-Listener-DL`.
- [x] Confirm the listener-owned versus package-owned split. Verified present in 5.3.1: `SpeedEvent`, `CompressedEventLogs<T>`, `EventLogContext`, `Device`, `DeviceTypes`, `DeviceStatus`, `IDeviceRepository`, `IEventLogRepository`, `ISpeedEventLogRepository`, `IEventLogRepositoryExtensions.Upsert<T>`, `SaveArchivedEventLogs`, `AddAtspmDbContext`, `AddAtspmEFConfigRepositories`, `AddAtspmEFEventLogRepositories`. Verified absent and therefore listener-owned or dropped: receiver, parser, batch listener, listener configuration, envelope, publishers, `EventBatchEnvelopeWorkflow`, `ArchiveEnvelopeDataEvents`.
- [x] Confirm the storage contract: primary key `{ LocationIdentifier, DeviceId, DataType, Start, End }`, `LocationIdentifier` limited to 10 characters, `DataType` to 32, `Data` persisted through a compressing value converter, and `Upsert` implemented as read-decompress-union-recompress-write.
- [ ] Pin a compatible ATSPM package set (starting with the existing `Utah.Udot.Atspm.Infrastructure` 5.3.1 reference) and record direct versus transitive dependencies.
- [ ] Add compile-time characterization tests/usages for the package-owned contracts above, including a round-trip through `Upsert` so a package upgrade that changes union semantics fails the build rather than production.
- [ ] Confirm the `DateTimeKind` convention used by the deployed event-log tables and whether `Npgsql.EnableLegacyTimestampBehavior` is required, then record the single convention this service will use. `Timestamp` participates in `SpeedEvent` value equality, so a mismatch with `TransferSpeedEventsService` produces silent duplicate rows.
- [ ] Obtain sanitized packet captures from each deployed sensor/firmware variant and document timestamp/header variants. If captures are not available in time, proceed with synthetic fixtures under one explicitly recorded timestamp format and treat real-capture validation as a phase 7 gate rather than a phase 0 blocker.
- [ ] Audit the current speed-sensor device inventory for `DeviceIdentifier` values longer than six characters, six-character prefix collisions, and `LocationIdentifier` values longer than ten characters. Each is a sensor that will silently fail to ingest.
- [ ] Run and record the repository's clean baseline build and tests.

Exit criteria: the package/local-code ownership matrix, wire format, storage contract, timestamp convention, and device-inventory audit are recorded; fixtures are approved for tests.

## Phase 1: Configuration and host composition

- [ ] Rename the draft configuration type. The file `Configuration/SpeedListenerConfiguration.cs` is already named correctly, but the class and its `[ConfigurationSection]` attribute are still `EventListenerConfiguration`; rename all three so the bound section is `SpeedListenerConfiguration`.
- [ ] Add the settings and defaults from design section 7, using PascalCase properties and `TimeSpan` values. Do not carry forward `ApiBaseUrl`, `ApiEndPoint`, or `threads`.
- [ ] Add startup validation for port, buffer size, capacities, `BatchSize <= ChannelCapacity`, timeouts, refresh interval, retry count, and known `DeviceStatuses` values.
- [ ] Add a validation rule asserting `WriteTimeout * MaxWriteAttempts` plus backoff fits within `ShutdownFlushTimeout`, or document and implement single-attempt shutdown writes.
- [ ] Configure `HostOptions.ShutdownTimeout` from `ShutdownFlushTimeout` plus headroom. The five-second default will otherwise kill the process mid-flush.
- [ ] Split/refactor `HostBootstrapper` so emitter and listener commands register and validate only their own dependencies.
- [ ] Implement `ListenerCommand` options/handler and preserve JSON plus environment-variable configuration overrides.
- [ ] Register listener dependencies with correct lifetimes, including `AddAtspmEFEventLogRepositories`.
- [ ] Give `SpeedListenerBackgroundService.ExecuteAsync` a no-op implementation that awaits cancellation, replacing the `NotImplementedException`. Registering the hosted service while it still throws would make `listener` crash on start for every intermediate pull request until phase 6.
- [ ] Add configuration-validation and command-composition tests.

Exit criteria: `listener` builds a host and starts without faulting, invalid configuration fails before socket binding, and emitter tests remain green.

## Phase 2: Packet transport and parsing

- [ ] Move the behavior from ATSPM `Infrastructure/Services/Receivers/IUdpReceiver.cs` and `UdpReceiver.cs` into local receiver types; do not add project/source references back to the monolith.
- [ ] Introduce an immutable UDP datagram value containing bytes, remote endpoint, and receipt timestamp.
- [ ] Add `IUdpDatagramReceiver` and a cancellation-aware `UdpClient` implementation.
- [ ] Set `SO_RCVBUF` from `ReceiveBufferBytes` and, where the platform exposes it, log the socket drop counter at shutdown.
- [ ] Move the behavior from ATSPM `Infrastructure/Services/Listeners/RawSpeedPacketParser.cs` into the local parser.
- [ ] Add `ISpeedPacketParser` with a result type that distinguishes valid events from parse failures.
- [ ] Port the PR offsets and identifier normalization, then implement the timestamp rules recorded in phase 0. Do not copy the `DateTime.TryParse` plus `SpecifyKind(..., Utc)` behavior.
- [ ] Log observed values of the unvalidated header byte at offset 7 so the field can be characterized before a later change validates it.
- [ ] Add table-driven parser tests using synthetic boundaries and sanitized production fixtures.
- [ ] Add loopback UDP integration tests using an ephemeral port.

Exit criteria: fixtures parse deterministically to speed events under the recorded timestamp convention; cancellation closes the socket without an error log.

## Phase 3: Device mapping

- [ ] Define a minimal local `DeviceMapping` record with device ID and location identifier.
- [ ] Implement `IDeviceMappingProvider` using the NuGet-provided `IDeviceRepository`, `Device`, `DeviceTypes`, and database registration extensions; do not copy their source into this repository.
- [ ] Filter to `SpeedSensor` devices whose `DeviceStatus` is in the configured set, and normalize identifiers with `OrdinalIgnoreCase` semantics. This status filter is a deliberate deviation from prototype behavior; see design section 4.1.
- [ ] Add a refreshable cache using short-lived data-access scopes.
- [ ] Detect and loudly report blank identifiers, duplicate identifiers, identifiers longer than six characters, six-character prefix collisions, missing locations, and location identifiers longer than ten characters.
- [ ] Add tests for initial load, refresh, stale-cache fallback, each validation rule above, and unknown sensors.

Exit criteria: one database read produces an immutable lookup, batches do not query the entire device table, refresh failure keeps the last valid mapping while logging degraded state, and every unmappable device configuration is reported at load rather than discovered at write time.

## Phase 4: Event log writer

- [ ] Implement `ISpeedEventLogWriter` and a `SpeedEventLogWriter` that groups mapped events by `{ LocationIdentifier, DeviceId, clock hour }` and builds `CompressedEventLogs<SpeedEvent>` directly. Do not introduce an envelope DTO, a JSON representation, or a publisher abstraction; the direct-write path has no serialization boundary.
- [ ] Snap `Start` to the hour boundary and `End` to `Start.AddHours(1)`, matching `TransferSpeedEventsService`. Stable keys across flushes are what make `Upsert` accumulate rather than fragment.
- [ ] Persist through `IEventLogRepository.Upsert`, resolving the repository from a short-lived `IServiceScopeFactory` scope per write.
- [ ] Apply `WriteTimeout` per attempt and bounded transient retries with jitter. `Upsert` is idempotent over value-equal events, so retry after an ambiguous failure is safe.
- [ ] Classify failures: retry transient errors, drop-with-diagnostics on constraint or schema violations, fail the service only on sustained failure past the retry budget. Do not reproduce `DatabaseEventPublisher`'s catch-log-and-return, which loses data silently.
- [ ] Add tests for new-row insert, union into an existing row, replayed-batch idempotency, hour-boundary splitting of a batch spanning two hours, timeout, transient retry, constraint-violation drop, and terminal failure.

Exit criteria: a second write into an occupied device-hour accumulates rather than overwrites, a replayed batch adds no rows or events, and no failure path discards data without a diagnostic log.

## Phase 5: Batching pipeline

- [ ] Move the useful behavior from ATSPM `SpeedBatchListenerBase` and `UDPSpeedBatchListener` into local composition-based services, then remove the inheritance structure.
- [ ] Add a bounded `Channel<SpeedEvent>` and implement and log the selected overflow policy.
- [ ] Implement a single-consumer batch processor with size-, interval-, and hour-boundary-triggered flushes.
- [ ] Resolve mappings and hand grouped events to the writer.
- [ ] Rate-limit unknown-sensor and channel-drop warnings, and maintain in-process rejection/drop counters for the periodic summary log.
- [ ] Retain a failed in-memory batch until the retry policy is exhausted; propagate terminal failure.
- [ ] Implement channel completion, drain, and bounded final flush.
- [ ] Add deterministic tests using a fake clock and completion signals, including a test that a batch spanning an hour boundary produces two rows.

Exit criteria: there are no unobserved/fire-and-forget write tasks, memory is bounded, partial batches flush on time and on shutdown, hourly buckets close promptly, and terminal write failures fail the pipeline.

## Phase 6: Hosted-service orchestration

- [ ] Replace the phase 1 no-op `ExecuteAsync` with orchestration that supervises the producer and consumer tasks.
- [ ] Do not carry over the prototype's process-lifetime DI scope from `EventListenerWorker`; all database work uses short-lived scopes.
- [ ] Ensure either unexpected task failure cancels the other side and propagates to the generic host, so restart policies remain effective. This is the liveness mechanism, since no health endpoint exists.
- [ ] Implement orderly cancellation: stop receive, complete channel, drain, final flush, exit.
- [ ] Add structured lifecycle logs, including a single startup-complete log emitted only after UDP binding and initial mapping load succeed, and the periodic summary log from design section 10.
- [ ] Add hosted-service tests for startup, normal cancellation, producer failure, consumer failure, and shutdown timeout.

Exit criteria: the service runs continuously under normal traffic, fails visibly on pipeline faults, and exits within the configured shutdown bound.

## Phase 7: End-to-end and operational readiness

- [ ] Add a local integration harness using an ephemeral UDP port, known device mappings, and a SQLite or containerized event-log database.
- [ ] Drive the harness from raw fixture datagrams rather than the existing emitter. `SpeedEmitterService.CreateSpeedPacket` builds a fixed 16-byte packet with no header byte and no trailing timestamp, so it exercises only the receipt-time fallback; it also supports TCP, which the UDP-only listener will not answer. Extending the emitter is an alternative but is currently out of scope.
- [ ] Test multi-sensor grouping, partial flush, hour-boundary rollover, malformed input, unknown sensors, database outage and recovery, and graceful termination.
- [ ] Verify against real sanitized captures if phase 0 could not obtain them earlier. This is the gate for declaring wire compatibility.
- [ ] Add listener settings and environment-variable examples to `appsettings.json` and the README without committing secrets.
- [ ] Update the Dockerfile/image metadata and document UDP port exposure, database dependencies, the absence of a health endpoint and what to monitor instead, and `SIGTERM` behavior.
- [ ] Add CI jobs for build, unit tests, integration tests, formatting, and container build.
- [ ] Run a sustained load test at expected peak packet rate. Measure database write volume and latency explicitly, not just CPU and memory, and tune `FlushInterval`, `BatchSize`, `ChannelCapacity`, and `ReceiveBufferBytes` from that evidence. `FlushInterval` is the dominant lever on write amplification; see design section 5.6.
- [ ] Measure the deduplication effect from design section 4.2 at peak flow: compare datagrams received against events stored for a single sensor.
- [ ] Run dependency/license and vulnerability checks.

Exit criteria: CI is green, the container passes end-to-end and shutdown tests, and measured peak load stays within the targets agreed in design section 14, including database write volume.

## Phase 8: Deployment and monolith cleanup

- [ ] Arrange network-layer UDP traffic duplication for shadow mode. The service cannot mirror its own traffic; this is an infrastructure prerequisite.
- [ ] Deploy to a non-production event-log database with mirrored/test traffic.
- [ ] Compare received, parsed, mapped, dropped, rows-written, and events-stored counts with the PR implementation.
- [ ] Exercise rollback and alerting procedures.
- [ ] Schedule a single-writer production cutover. Stop the old listener before starting the new one against the same stream: because `Upsert` is a read-modify-write against a shared row, concurrent writers lose events rather than merely duplicating them.
- [ ] Monitor the agreed soak period and capture operational baselines, including database write volume.
- [ ] Confirm no other `udot-atspm` consumer depends on `HttpPublisher`, `EventBatchEnvelope`, `IEventPublisher<T>`, `KafkaPublisher`, or `PubSubPublisher` before removing them.
- [ ] In a separate `udot-atspm` change, remove `EventListener`, `EventListenerConfiguration`, `IUdpReceiver`, `UdpReceiver`, `RawSpeedPacketParser`, `SpeedBatchListenerBase`, `UDPSpeedBatchListener`, `EventBatchEnvelope`, `IEventPublisher<T>`, `EventBatchEnvelopeWorkflow`, `ArchiveEnvelopeDataEvents`, `DatabaseEventPublisher`, and the HTTP/Kafka/Pub-Sub publishers from shared libraries.
- [ ] Retain ATSPM domain models, repository contracts, EF implementations, database providers, `SaveArchivedEventLogs`, and registration extensions in the ATSPM NuGet packages.

Exit criteria: the dedicated service is the sole production listener, service-level indicators remain healthy through the soak period, rollback is documented, and monolith cleanup is reviewed independently.

## Recommended pull-request slices

1. Configuration/host refactor, listener command, and the no-op hosted service.
2. UDP receiver, parser, fixtures, and tests.
3. Device mapping provider, cache, and identifier validation.
4. Event log writer and upsert/idempotency tests.
5. Channel batch processor and hosted-service orchestration.
6. End-to-end tests, container/CI, README, and operational guidance.
7. Separate monolith cleanup after production cutover, removing all listener-specific and publisher code from shared libraries while preserving package-owned platform contracts.

Each pull request should keep `dotnet test` green and include tests for its failure paths. Avoid a single copy-forward commit from PR #217: retaining the component boundaries above makes behavior reviewable and prevents prototype lifecycle defects from being migrated unnoticed.
