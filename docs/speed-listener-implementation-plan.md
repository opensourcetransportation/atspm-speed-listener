# Speed Listener Migration Implementation Plan

This plan implements the architecture in [`speed-listener-design.md`](./speed-listener-design.md).

It is a **migration, not a redesign**. The functional scope is what PR #217 does, minus the Kafka, Pub/Sub, and HTTP Data API publish paths. The envelope, the archive/save workflow, the hourly compressed-log bucketing, and `IEventLogRepository.Upsert` are carried across unchanged. The only behavioral corrections are the prototype lifecycle and reliability defects enumerated in design section 5.

Out of scope throughout: authentication, metrics instrumentation, health-check endpoints, and any change to the upsert or storage model.

## Phase 0: Contract discovery and baseline

- [x] Record the PR #217 commit used as the migration source: `2650aabbd84f3b0085262407c2f51c444c9cb48c` on branch `AVE-2526-Speed-Listener-DL`.
- [x] Confirm the package versus local-code split. Verified published and consumable in 5.3.1: `SpeedEvent`, `CompressedEventLogs<T>`, `EventLogContext`, `Device`, `DeviceTypes`, `IDeviceRepository`, `IEventLogRepository`, `ISpeedEventLogRepository`, `IEventLogRepositoryExtensions.Upsert<T>`, `SaveArchivedEventLogs`, `AddAtspmDbContext`, `AddAtspmEFConfigRepositories`, `AddAtspmEFEventLogRepositories`. Verified absent and therefore moved into this repository: `EventBatchEnvelope`, `IEventPublisher<T>`, `EventBatchEnvelopeWorkflow`, `ArchiveEnvelopeDataEvents`, `DatabaseEventPublisher`, `IUdpReceiver`, `UdpReceiver`, `RawSpeedPacketParser`, `SpeedBatchListenerBase`, `UDPSpeedBatchListener`, `EventListenerConfiguration`.
- [x] Confirm the storage contract the migrated path writes into: primary key `{ LocationIdentifier, DeviceId, DataType, Start, End }`, `LocationIdentifier` limited to 10 characters, `DataType` to 32, `Data` behind a compressing value converter, `Upsert` implemented as read-decompress-union-recompress-write. No change to any of this is planned; it is recorded so tests assert the right things.
- [ ] Pin a compatible ATSPM package set (starting with the existing `Utah.Udot.Atspm.Infrastructure` 5.3.1 reference) and record direct versus transitive dependencies.
- [ ] Add compile-time characterization tests or usages for the package-owned contracts above, including one round trip through `Upsert`, so a package upgrade that changes union semantics fails the build rather than production.
- [ ] Confirm the `DateTimeKind` convention used by the deployed event-log tables and whether `Npgsql.EnableLegacyTimestampBehavior` is required, then record the single convention this service will use. `Timestamp` participates in `SpeedEvent` value equality, so a mismatch with `TransferSpeedEventsService` produces silent duplicate rows.
- [ ] Obtain sanitized packet captures from each deployed sensor or firmware variant and document timestamp and header variants. If captures are not available in time, proceed with synthetic fixtures under one explicitly recorded timestamp format and treat real-capture validation as a phase 7 gate rather than a phase 0 blocker.
- [ ] Note for the record any speed-sensor `DeviceIdentifier` longer than six characters or sharing a six-character prefix with another. The wire format carries exactly six ASCII bytes, so such devices cannot be matched by an incoming packet. No runtime validation is added for this; the audit exists so an unmapped sensor in shadow mode is explainable.
- [ ] Run and record the repository's clean baseline build and tests.

Exit criteria: the ownership matrix, wire format, storage contract, timestamp convention, and device-identifier audit are recorded; fixtures are approved for tests.

## Phase 1: Configuration and host composition

- [ ] Rename the draft configuration type. The file `Configuration/SpeedListenerConfiguration.cs` is already named correctly, but the class and its `[ConfigurationSection]` attribute are still `EventListenerConfiguration`; rename all three so the bound section is `SpeedListenerConfiguration`.
- [ ] Add the settings and defaults from design section 7. Drop `ApiBaseUrl` and `ApiEndPoint` with the HTTP path; rename `threads` to `ArchiveParallelism`, keeping its default of 50.
- [ ] Add startup validation for port, capacities, `BatchSize <= ChannelCapacity`, timeouts, and retry count.
- [ ] Add a validation rule asserting `ShutdownFlushTimeout` covers at least one publish attempt cycle at the shutdown attempt budget. Do not treat this as making the drain complete; it only guarantees one batch can finish.
- [ ] Configure `HostOptions.ShutdownTimeout` from `ShutdownFlushTimeout` plus headroom. The five-second default will otherwise kill the process mid-flush.
- [ ] Split or refactor `HostBootstrapper` so emitter and listener commands register and validate only their own dependencies.
- [ ] Implement `ListenerCommand` options and handler, preserving JSON and environment-variable configuration overrides.
- [ ] Register listener dependencies with correct lifetimes, including `AddAtspmEFEventLogRepositories`. Do not migrate `AddEventPublishers` or the `IngestApi` HTTP client registration.
- [ ] Give `SpeedListenerBackgroundService.ExecuteAsync` a no-op implementation that awaits cancellation, replacing the `NotImplementedException`. Registering the hosted service while it still throws would make `listener` crash on start for every intermediate pull request until phase 6.
- [ ] Add configuration-validation and command-composition tests.

Exit criteria: `listener` builds a host and starts without faulting, invalid configuration fails before socket binding, and emitter tests remain green.

## Phase 2: Packet transport and parsing

- [ ] Move `IUdpReceiver` and `UdpReceiver` into local receiver types as `IUdpDatagramReceiver` and `UdpDatagramReceiver`; do not add project or source references back to the monolith.
- [ ] Introduce an immutable UDP datagram value containing bytes, remote endpoint, and receipt timestamp.
- [ ] Make the receive loop cancellation-aware and dispose the socket to unblock shutdown; do not log expected cancellation as an error.
- [ ] Move `RawSpeedPacketParser` into the local parser behind `ISpeedPacketParser`, with a result type that distinguishes valid events from parse failures.
- [ ] Port the PR offsets and identifier normalization unchanged, then implement the timestamp rules recorded in phase 0. Do not copy the `DateTime.TryParse` plus `SpecifyKind(..., Utc)` behavior.
- [ ] Log observed values of the unvalidated header byte at offset 7 so the field can be characterized before any later change validates it.
- [ ] Add table-driven parser tests using synthetic boundaries and sanitized production fixtures.
- [ ] Add loopback UDP integration tests using an ephemeral port.

Exit criteria: fixtures parse deterministically under the recorded timestamp convention and match the PR parser's output for the same input; cancellation closes the socket without an error log.

## Phase 3: Device mapping

- [ ] Define a minimal local `DeviceMapping` record with device ID and location identifier. The prototype already declares this type in `SpeedBatchListenerBase.cs` but never uses it; the commented-out dictionary there shows the intended shape.
- [ ] Implement `IDeviceMappingProvider` using the NuGet-provided `IDeviceRepository`, `Device`, and `DeviceTypes`; do not copy their source into this repository.
- [ ] Filter to `SpeedSensor` devices and normalize identifiers with `OrdinalIgnoreCase` semantics, matching the prototype's `Trim().ToUpperInvariant()` comparison. Apply no device-status filter; the prototype does not.
- [ ] Add a refreshable cache using short-lived data-access scopes, replacing the prototype's full `GetList()` materialization and linear scan on every flush.
- [ ] Surface blank identifiers, duplicate identifiers, and missing locations at load.
- [ ] Add tests for initial load, refresh, stale-cache fallback, duplicate identifiers, and unknown sensors.

Exit criteria: one database read produces an immutable lookup, batches do not query the entire device table, and refresh failure keeps the last valid mapping while logging degraded state.

## Phase 4: Envelope, publisher, and workflow

- [ ] Move `EventBatchEnvelope` into this repository as a local type, preserving its field shape: `LocationIdentifier`, `DeviceId`, `DataType`, `Start`, `End`, `Items`.
- [ ] Move `IEventPublisher<T>` as the publisher contract. Only the database implementation is migrated, but the interface is the seam the batch-processor tests use.
- [ ] Move `DatabaseEventPublisher`, `EventBatchEnvelopeWorkflow`, and `ArchiveEnvelopeDataEvents` into this repository unchanged in behavior. Continue to consume `SaveArchivedEventLogs` and `Upsert` from the packages.
- [ ] Do not migrate `HttpPublisher`, `KafkaPublisher`, `PubSubPublisher`, or the `IngestApi` HTTP client, and do not migrate `DangerousAcceptAnyServerCertificateValidator` with them.
- [ ] Replace `DatabaseEventPublisher.PublishAsync(IReadOnlyList<...>, ...)`'s catch-log-and-return with the failure classification in design section 9. Retry is safe without further analysis because `Upsert` is idempotent over value-equal events.
- [ ] Implement the classification in one place, keyed on the provider's error rather than exception type alone, since a single `DbUpdateException` covers every case: retry transient errors; drop one batch with error-level diagnostics for a constraint violation attributable to that batch's data; fail the service on a schema or model mismatch; fail the service when consecutive drops for one device cross a bounded threshold; fail the service when the retry budget is exhausted.
- [ ] Do not let a schema or model mismatch drop batches and continue. Every batch fails identically, so the service would report healthy while discarding all ingest indefinitely, reintroducing the catch-log-and-return defect this phase removes.
- [ ] Wire `ArchiveParallelism` to the workflow's archive step. Leave the save step at `MaxDegreeOfParallelism = 1`; raising it would let concurrent writers to the same device-hour lose events.
- [ ] Apply `WriteTimeout` per attempt and pass the host cancellation token through, replacing the prototype's `CancellationToken.None`.
- [ ] Add tests for envelope construction, workflow completion, transient retry, replayed-batch idempotency, single-batch constraint-violation drop, per-device repeated-violation escalation, schema-mismatch process failure, terminal failure, and cancellation.

Exit criteria: the migrated path produces the same compressed-event-log rows as the prototype for the same envelopes, and no failure path returns normally after losing data.

## Phase 5: Batching pipeline

- [ ] Move the behavior from `SpeedBatchListenerBase` and `UDPSpeedBatchListener` into local composition-based services, then remove the inheritance structure.
- [ ] Replace the unbounded `List<SpeedEvent>` and its lock with a bounded `Channel<SpeedEvent>`; implement and log the selected overflow policy.
- [ ] Implement a single-consumer batch processor with size- and interval-triggered flushes. The prototype has no time-based flush, so below `BatchSize` events remain buffered until shutdown.
- [ ] Replace `_ = SendBatchAsync(toSend)` with an awaited publish on the consumer, so faults are observed and shutdown can wait for in-flight work.
- [ ] Build one envelope per mapped device from the cached lookup, preserving the prototype's grouping and `Start`/`End` from group minimum and maximum timestamps.
- [ ] Rate-limit unknown-sensor and channel-drop warnings, and maintain in-process counters for the periodic summary log.
- [ ] Retain a failed in-memory batch until the retry policy is exhausted; propagate terminal failure.
- [ ] Implement channel completion and a best-effort drain bounded by `ShutdownFlushTimeout`, publishing queued batches in order under a reduced attempt budget so the fixed time covers as many batches as possible.
- [ ] On budget expiry, stop draining, count the events still queued plus any in-flight batch, log that residue at error level, and exit non-successfully. A full channel cannot be drained in any deployable budget, so shutdown must report what it lost rather than claim success.
- [ ] Add deterministic tests using a fake clock and completion signals, covering flush by size, flush by interval, flush on shutdown, and multi-sensor grouping.

Exit criteria: there are no unobserved publish tasks, memory is bounded, partial batches flush on time and on shutdown, and terminal publish failures fail the pipeline.

## Phase 6: Hosted-service orchestration

- [ ] Replace the phase 1 no-op `ExecuteAsync` with orchestration that supervises the producer and consumer tasks.
- [ ] Do not carry over the prototype's process-lifetime DI scope from `EventListenerWorker`; database work uses short-lived scopes from `IServiceScopeFactory`.
- [ ] Ensure either unexpected task failure cancels the other side and propagates to the generic host. This is the liveness mechanism, since no health endpoint exists.
- [ ] Implement orderly cancellation: stop receive, complete channel, drain, final flush, exit.
- [ ] Add structured lifecycle logs, including a single startup-complete log emitted only after UDP binding and initial mapping load succeed, and the periodic summary log from design section 10.
- [ ] Add hosted-service tests for startup, normal cancellation, producer failure, consumer failure, shutdown timeout, and shutdown with a channel too full to drain in the budget, asserting the undrained count is reported and the exit is non-successful.

Exit criteria: the service runs continuously under normal traffic, fails visibly on pipeline faults, and exits within the configured shutdown bound.

## Phase 7: End-to-end and operational readiness

- [ ] Add a local integration harness using an ephemeral UDP port, known device mappings, and a SQLite or containerized event-log database.
- [ ] Drive the harness from raw fixture datagrams rather than the existing emitter. `SpeedEmitterService.CreateSpeedPacket` builds a fixed 16-byte packet with no header byte and no trailing timestamp, so it exercises only the receipt-time fallback; it also supports TCP, which the UDP-only listener will not answer. Extending the emitter is out of scope.
- [ ] Test multi-sensor grouping, partial flush, malformed input, unknown sensors, database outage and recovery, and graceful termination.
- [ ] Verify two flushes into the same device-hour accumulate rather than overwrite, and that a replayed batch adds no duplicate events.
- [ ] Verify against real sanitized captures if phase 0 could not obtain them earlier. This is the gate for declaring wire compatibility.
- [ ] Add listener settings and environment-variable examples to `appsettings.json` and the README without committing secrets.
- [ ] Update the Dockerfile and image metadata; document UDP port exposure, database dependencies, the absence of a health endpoint and what to monitor instead, `SIGTERM` behavior, and the two loss budgets: worst-case shutdown loss of `ChannelCapacity` events plus one in-flight batch, and outage loss beginning after roughly `ChannelCapacity` divided by arrival rate seconds.
- [ ] Add CI jobs for build, unit tests, integration tests, formatting, and container build.
- [ ] Run a sustained load test at expected peak packet rate. Measure database write volume alongside CPU and memory, and tune `FlushInterval`, `BatchSize`, and `ChannelCapacity` from that evidence. Each flush upserts into the affected device-hour rows, so `FlushInterval` trades ingest latency against write volume; its default is a starting point, not a tuned value.
- [ ] Run dependency, license, and vulnerability checks.

Exit criteria: CI is green, the container passes end-to-end and shutdown tests, and measured peak load stays within the targets agreed in design section 14.

## Phase 8: Deployment and monolith cleanup

- [ ] Arrange network-layer UDP traffic duplication for shadow mode. The service cannot mirror its own traffic; this is an infrastructure prerequisite.
- [ ] Deploy to a non-production event-log database with mirrored or test traffic.
- [ ] Compare received, parsed, mapped, dropped, envelope, row, and stored-event counts with the PR implementation.
- [ ] Exercise rollback and alerting procedures.
- [ ] Schedule a single-writer production cutover. Stop the old listener before starting the new one against the same stream: because `Upsert` is a read-modify-write against a shared row, concurrent writers lose events rather than merely duplicating them.
- [ ] Monitor the agreed soak period and capture operational baselines, including database write volume.
- [ ] Confirm no other `udot-atspm` consumer depends on `HttpPublisher`, `KafkaPublisher`, `PubSubPublisher`, `EventBatchEnvelope`, or `IEventPublisher<T>` before removing them.
- [ ] In a separate `udot-atspm` change, remove `EventListener`, `EventListenerConfiguration`, `IUdpReceiver`, `UdpReceiver`, `RawSpeedPacketParser`, `SpeedBatchListenerBase`, `UDPSpeedBatchListener`, `EventBatchEnvelope`, `IEventPublisher<T>`, `EventBatchEnvelopeWorkflow`, `ArchiveEnvelopeDataEvents`, `DatabaseEventPublisher`, and the HTTP, Kafka, and Pub/Sub publishers from shared libraries.
- [ ] Retain ATSPM domain models, repository contracts, EF implementations, database providers, `SaveArchivedEventLogs`, and registration extensions in the ATSPM NuGet packages.

Exit criteria: the dedicated service is the sole production listener, service-level indicators remain healthy through the soak period, rollback is documented, and monolith cleanup is reviewed independently.

## Recommended pull-request slices

1. Configuration and host refactor, listener command, and the no-op hosted service.
2. UDP receiver, parser, fixtures, and tests.
3. Device mapping provider and cache.
4. Envelope, publisher, workflow, and archive step, with failure classification and idempotency tests.
5. Channel batch processor and hosted-service orchestration.
6. End-to-end tests, container and CI, README, and operational guidance.
7. Separate monolith cleanup after production cutover, removing listener-specific and publisher code from shared libraries while preserving package-owned platform contracts.

Each pull request should keep `dotnet test` green and include tests for its failure paths. Avoid a single copy-forward commit from PR #217: keeping the component boundaries makes behavior reviewable and prevents the prototype lifecycle defects in design section 5 from being migrated unnoticed.
