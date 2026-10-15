# Implementation status and remaining release work

Updated October 15, 2026. This replaces the original migration proposal checklist;
Git history preserves that plan. Current architecture is documented in
[the design](speed-listener-design.md), and operator commands are in
[the Windows guide](windows-service.md) and [deployment reference](../deploy/README.md).

## Implemented and verified locally

- [x] .NET 9 listener host, native Windows service lifetime, ATSPM volume/database/logging registration, and actionable configuration validation.
- [x] IPv4/IPv6 UDP reception, mapped-source normalization, cancellation and receiver abstraction.
- [x] Tagged prefixed/compact XS parsing, captured short packet layouts, joined frames and malformed-tail preservation.
- [x] Documented X1 actuation classification and explicit endpoint mappings for basic untagged XS. Z4 and XT remain unsupported.
- [x] Current-version mapping that excludes deleted versions before selection, selects the lowest-ID Active speed device, and reports invalid/tied identifiers.
- [x] Four-digit numeric location routing documented as a deployment prerequisite; full detector IDs retained for reports.
- [x] Server-local timestamps and effective-date comparisons; optional uniform zone override; coordinates ignored.
- [x] Unspecified wall-clock storage for events/hour keys, offset and offset-less suffix handling, DST coverage and Npgsql compatibility.
- [x] Bounded channel, size/age flushes, unknown-event summaries and backpressure/drop accounting.
- [x] Typed local hourly archive step with ATSPM attribution and duplicate preservation.
- [x] Cancellable EF writer, in-process retry fingerprints, acknowledged-row tracking, scoped conflicts and poison isolation.
- [x] Independent-device concurrency with per-device serialization within a workflow; one-writer deployment requirement documented.
- [x] Bounded shutdown drain, reduced shutdown retry budget, fault propagation and residue accounting.
- [x] Generic installation, upgrade/rollback, diagnostics, capture and temporary switchover scripts with mocked tests.
- [x] Database-discovered load generator, explicit targets, paced traffic and offline target preview.
- [x] Source-generated logs, bounded per-period unknown details, optional unlimited Debug capture and configured Docker retention.
- [x] CI definitions for Linux .NET build/tests and mocked Windows deployment tests under both PowerShell versions; container/release publishing definitions exist.
- [x] Model/schema/compression tests for all five relational providers, real SQLite persistence tests and disposable PostgreSQL insert/append probe.
- [x] Self-contained Windows publishing and offline generator-preview smoke check.

Latest recorded application validation: 210 Release tests passed on Windows and Linux; 12 server-zone tests also passed in Windows NLS mode. Earlier counts
in [the review history](pr-2-review-resolution.md) describe earlier revisions.
The [October 9 Docker runs](docker-load-test-results.md) demonstrated short-run
throughput on that revision; they are not current sustained-capacity evidence.
The October 15 follow-up reconciled 100,955 events in a five-minute run before the
user deferred the two-hour test. The sustained gate below remains open.

## Required before claiming sustained production readiness

- [ ] Run the opt-in storage probe against actual SQL Server, MySQL and Oracle test engines. Current coverage for these is model/schema/compression only.
- [ ] Run at least two hours of paced expected-peak traffic, including full-hour row growth, hourly boundaries, final drain and persisted-count reconciliation. Follow [the load-testing guide](load-testing.md).
- [ ] Include realistic per-device distribution and a hot-device scenario; record recent late-hour write times, CPU, memory, queue depth and errors rather than only lifetime averages.
- [ ] Validate sensor formats and local wall-clock behavior against the agency's current stream and existing event-log/report conventions. Unsupported formats require their vendor specification.
- [ ] Verify the actual network path, sensor destination port, firewall and service account on the deployment server. Loopback/Docker tests do not establish that path.
- [ ] Complete the agency's production soak, alerting and rollback exercise. UDP/queued state is nondurable and one writer per device-hour is required.
- [ ] Review current dependency, license and vulnerability results before release; no clean security audit is claimed by the application tests.

## Separate ATSPM work

- [ ] Review consumers before removing the old listener and unused HTTP/Kafka/PubSub publisher code from the monolith.
- [ ] Perform cleanup in a separately reviewed ATSPM change after cutover; retain shared domain/EF/provider contracts.

The ATSPM date-range report repositories already preserve stored occurrences by
reading the base payload collection. A speed-report override is not required for
this listener's duplicate preservation. Direct typed compressed-row access and
standard ATSPM import/archive/update paths can still deduplicate; changing those
other paths is outside this repository's implemented scope.
