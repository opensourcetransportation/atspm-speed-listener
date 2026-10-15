# PR 2 review resolution

This is a chronological record of review work. Validation counts below belong
to their individual revisions; they are not all current test totals. The latest
recorded result is **219 Release C# tests passed on Windows and Linux**, with live PostgreSQL/SQLite
storage checks and model/schema/compression coverage for SQL Server/MySQL/Oracle.
The current behavior and remaining gates are summarized in
[the design](speed-listener-design.md) and
[implementation status](speed-listener-implementation-plan.md). Earlier fixes
were sometimes superseded after checking ATSPM semantics; those corrections are
called out here. Historical code-review validation did not modify agency databases;
subsequent live provider probes used disposable local test storage.

| Finding | Result and change |
| --- | --- |
| Retries repeat committed envelopes | Repeated writes existed, but double-counting was not reproduced: the packaged Upsert unions events using value equality. Retries now track acknowledged rows by originating envelope and hour, and skip those rows during retries and poison isolation. The follow-up replaces value-equality union with append semantics and fixed-size before/after fingerprints, recognizing an uncertain committed result while preserving identical distinct events. |
| Poison threshold aborts later healthy envelopes | Confirmed. Threshold exceptions are deferred until the remaining isolated envelopes have been attempted. |
| Upsert ignores cancellation | Confirmed. The package repository API has no cancellation token. A local `EfEventLogWriter` uses the packaged context, model, primary keys and compression, passing the token to `FindAsync`, `AddAsync` and `SaveChangesAsync`, while preserving duplicate-event multiplicity through the base payload accessor. The NuGet binaries remain unchanged. The local archive step is derived from ATSPM's `Atspm/Infrastructure/WorkflowSteps/ArchiveDataEvents.cs` and retains its upstream Apache license header. |
| Deleted locations resolve to old versions | Earlier finding withdrawn after checking ATSPM current-version semantics. Deleted versions are excluded before picking the latest effective version; the previous eligible version becomes current. Active speed sensors are selected on that version. |
| Duplicate count stays zero | Confirmed. Rows tied at the current effective date now increment the duplicate counter. Historical versions are expected and are not counted as duplicate mappings. The highest location ID wins ties. |
| Refresh cancellation logs failure | Confirmed. Caller cancellation propagates without a warning or refresh-failure increment. |
| Shutdown validation assumes one batch | The budget was never a guarantee that an entire bounded queue could drain. Validation now includes capped retry delays for one publish, documents that minimum, and every batch started during shutdown receives the shutdown attempt limit. The shared cancellation token bounds the backlog, including EF writes. |
| UTC differs from local ATSPM time | Superseded by coordinate-derived zones: valid coordinates determine each location's zone; EventTimeZoneId is fallback only and defaults to server local. All stored values use Unspecified Kind, including UTC. Seasonal conversion tests cover receipt and sensor suffix timestamps. |
| Upgrade uses only appsettings port | Confirmed. A running service's actual UDP binding determines readiness, including environment, mounted configuration and command-line overrides. `-ExpectedPort` provides an explicit choice. Ambiguous bindings are rejected before stopping the service. |
| JToken event round trip | Confirmed. Envelopes now retain typed `IReadOnlyList<SpeedEvent>` collections and archive directly into typed hourly lists, removing JSON conversion and reflection from that stage. |

Validation: 81 Release application tests pass, including compressed round trips
across contexts, partial commits, lost commit acknowledgements, poison thresholds,
save cancellation, refresh cancellation, deletion selection and shutdown backlog
attempt limits. The InMemory persistence fixture preserves the concrete event
list in its comparer snapshot because that provider serializes snapshots rather
than the actual property used by relational EF. This exercises packaged
compression, but does not substitute for a live relational-provider test.

Deployment validation covers 12 upgrade/rollback, seven switchover and six
installer/capture scenarios under both PowerShell 7 and Windows PowerShell 5.1.
These use mocked system commands and do not alter actual services or firewall
rules.

Before deployment, verify coordinates and fallback match existing event-log wall
time, and test the new writer against the agency's database provider. UDP and the bounded
in-memory queue remain nondurable. A shutdown deadline can still leave queued
events unwritten, and cancellation cannot undo a commit already accepted by the
server. One writer per device stream/database remains required.

## October 13 follow-up review

| Finding | Resolution and regression coverage |
| --- | --- |
| Colon-containing endpoint keys do not bind | Use a list of SourceAddress, SourcePort and DetectorId. A real JSON configuration binding test parses an untagged packet; legacy nonempty dictionary input and duplicate endpoints fail validation. |
| Mapping cutoff always UTC | Initial fix used EventTimeZoneId; the later coordinate-zone change compares each candidate location's local clock. Denver and multi-zone version-boundary tests verify no early switchover. |
| Offset-less sensor timestamp assumed UTC | Interpret it in the selected location zone; explicit offsets represent instants. Nonexistent spring-forward times require an offset; repeated fall-back times now select the occurrence nearest receipt. |
| Generator lacks Npgsql compatibility | Set the timestamp compatibility switch before building generator services, matching the listener. |
| Union removes legitimate identical events | Append through the base payload collection and retain each logical row across retries. Both generic accessors and packaged Union are bypassed. Tests cover separate batches, identical events in one batch, lost insert/update acknowledgements, outside-write conflicts, and the actual compression converter/backing-field round trip. |
| Extra UDP sockets block upgrade | Prefer the configured observed port, otherwise a unique wildcard listener, and retain ExpectedPort for genuine ambiguity. Two extra-socket regression scenarios pass. |
| Unbounded unknown-detector warning cache | Remove the lifetime cache. Aggregate at most 256 detector details per summary period; all additional events remain counted and get an aggregate warning. Debug retains full batch details. |
| Invalid diagnostic size crashes startup | Validate the value without throwing; malformed, negative or overflowing values emit a diagnostic and use unlimited capture. |

The summary `UnknownSinceLast` counter and per-detector warning EventCount represent
only the preceding SummaryInterval, normally one minute. Both reset atomically
on each summary; graceful shutdown reports pending partial-period details.

Validation: 148 Release application tests passed; 14 upgrade/rollback, 7
switchover and 6 installer/capture scenarios passed under PowerShell 7 and Windows PowerShell 5.1. Script
tests mock service/firewall operations. No GCP configuration or event-log data
was changed for this review work. These changes do not add database tables or
provide durable cross-process replay; use one writer per stream/database.

## Second October 13 follow-up

| Finding | Resolution |
| --- | --- |
| Replay equality includes ignored event metadata | Compare fingerprints of persisted timestamp, detector and speed fields; exclude LocationIdentifier. Regression tests perform the real compression/decompression snapshot before retrying lost insert and update acknowledgements. |
| Outside change stops service | Recognize a committed prefix followed by later appends. Unresolvable changes raise a scoped conflict handled by the workflow: log Error 3045, count WriteConflicts/ConflictEvents and skip that device-hour. Other hours/devices continue; conflicted envelopes are not reported as fully archived. |
| Merge plan retains full hourly copies | Retain counts and two SHA-256 fingerprints instead of before/after event lists. The actual merged payload still needs materialization for the packaged compressed collection format. |
| Fall-back local timestamps rejected | Select the repeated-time occurrence closest to packet receipt and retain its local wall-clock value. Nonexistent spring-forward times still require an offset. |
| Generator validation never runs | Eagerly resolve validated options before returning the built discovery host; CLI reports validation failures without querying devices. |
| IP parser accepts typo addresses | Require canonical dotted-quad IPv4, including mapped IPv6 tails; also accept proper IPv6 literals. Reject short, numeric, hex and leading-zero IPv4 forms. |
| Unknown has different counter semantics | Keep the requested per-period behavior and rename the logged field UnknownSinceLast. Per-detector warning counts reset each summary; other event/publication/failure counters remain cumulative. Queue depth, mapping age and publish latency are gauges/averages. |

Lost-ack evidence now includes actual compressed payload round trips, including
omitted LocationIdentifier metadata, rather than relying only on live lists.
The row key supplies location identity. Retry state remains scoped to one
in-process publish and deployments still require one writer per stream/database.

Second follow-up validation: 167 Release C# tests passed, including lost insert
and update acknowledgements using a real temporary SQLite relational database
with the unmodified packaged EventLogContext, plus compressed in-memory
snapshots. The self-contained Windows generator rejects invalid configuration
before database discovery; offline target preview sends no packets. Deployment
scripts are unchanged from the previously verified 27 scenarios.

## Explicit duplicate-preserving archive step

`ArchiveDataEventsWithoutDeduplication` is derived from ATSPM's
`Atspm/Infrastructure/WorkflowSteps/ArchiveDataEvents.cs`, retaining its upstream
Apache license header. This is a local source adaptation; the NuGet binaries are
unchanged. The production workflow uses only `ArchiveEnvelope`, now specialized
for `SpeedEvent`. The unused general-purpose dataflow/device-event-array API has
been removed. Typed lists, rows and timelines avoid reflection and dynamic
dispatch while preserving hourly grouping and every identical occurrence.
All 176 Release C# tests passed after specialization, including relational
insert/update and retry tests that exercise this production path.

## IPv6 reception and conflict bookkeeping follow-up

The receiver now binds a dual-stack IPv6 socket when supported, otherwise IPv4.
Mapped IPv4 source addresses normalize consistently in configuration and received
packets; pure IPv6 mappings are rejected on IPv4-only systems. Real loopback
tests send IPv4 and IPv6 untagged messages to the same port and resolve both
source mappings, including mapped-IPv4 configuration forms.

Publication metrics count confirmed device-hour contributions and events at row
completion, guarded by the logical row tracker across retries and isolation.
Partial envelopes still contribute their good hours to RowsPublished and
EventsPublished; EnvelopesPublished retains its fully-archived meaning. Conflicts
cannot clear a device's poison history, either on the normal path or while
isolating a bad batch. Regression tests alternate poison failures with a partial
conflict and verify the original threshold is still reached. A separate device's
poison failure cannot cause the conflicted device to be treated as successful.

Validation: 176 Release C# tests passed; IPv6 loopback tests ran on an IPv6-capable
host. Deployment scripts remain unchanged from their prior validated scenarios.


## ATSPM current-version and deployment follow-up

Deleted location versions are now excluded before current-version selection,
matching ATSPM's dated latest-version query. Only Active speed devices participate
in listener mapping and generator discovery; inactive devices never displace an
active replacement. Tests cover deletion rollback, removal of all eligible versions,
all non-active statuses, and no historical-device fallback from an active version.

At this follow-up, omitted EventTimeZoneId changed to server local and shipped
JSON stopped forcing UTC. In the later coordinate-derived implementation below,
this setting is fallback only: valid coordinates take precedence. Upgrades preserve
explicit existing fallback values; verify them for locations with unusable coordinates.
Options validation now reports individual fields, endpoint-list indexes, and
cross-field shutdown budget requirements in both listener and generator hosts.
Four-digit numeric routing is documented as a protocol deployment prerequisite.
The design records why cancellable, duplicate-preserving writes and envelope
retry tracking require local workflow/repository adapters.

The previous short load tests remain short-run evidence only. Two hours of paced
peak traffic with growing hourly rows and stored-count reconciliation is still
required before claiming sustained capacity. No such result is claimed here. That follow-up passed 190 Release C# tests; the later coordinate-zone change
passed 214.


## Coordinate-derived location zones

Coordinate lookup now overrides the configured fallback for valid locations,
matching ATSPM's downloader coordinate-lookup approach. The selected location zone is cached
with device mappings and used both for packet timestamps and effective-date
cutoffs. Invalid/missing coordinates use EventTimeZoneId (default server local)
and produce an aggregate refresh warning. All persisted timestamp values and
hourly keys have Unspecified Kind, including UTC. No zone information is stored.

New coverage includes multi-zone receipt/offset/untagged packets, Utah/New York
seasonal changes, Arizona DST behavior, offset-less timestamps, invalid coordinates,
per-location version cutoffs, cached/refreshing zones and shared singleton wiring.
All five relational providers have timestamp model/schema/compression tests.
SQLite and disposable PostgreSQL additionally passed live insert/append round
trips with identical events and fractional timestamp precision. Live SQL Server,
MySQL and Oracle probes remain unrun; the opt-in DatabaseProviderSmoke tool supports
those engines without changing application registrations.

Validation: 214 Release C# tests passed; the opt-in PostgreSQL smoke probe also
passed against a fresh disposable PostgreSQL 16 container.

## Coordinate-zone review follow-up

Reused the toolkit's GetTimeZoneFromCoordinates and removed the direct GeoTimeZone
reference; it remains a transitive toolkit dependency. Windows IANA identifiers
are converted with TimeZoneConverter before native TimeZoneInfo lookup, avoiding
the ICU requirement. All 21 location-zone tests also pass with Windows NLS enabled;
CI now repeats that check. The complete 219-test suite passes on Windows and Linux.

Warnings identify coordinate-derived zones whose rules differ from EventTimeZoneId,
with at most 20 location details plus a summary per refresh. Tests verify bounded
details, matching-zone silence, coordinate precedence and invalid zone input.
Compose requires SPEED_LISTENER_TIME_ZONE and uses it for both TZ and the fallback.
Manual Compose validation confirmed both values and rejection when it is missing.

The requested two-hour test was deferred by the user after a short run. In 302.258
seconds at 334 events/second, all 100,955 sent events were received, parsed and
verified in decompressed PostgreSQL storage; none were rejected or dropped. Both
isolated test processes are stopped. This does not close the sustained-capacity gate.
See [the current short-run results](docker-load-test-results.md#october-15-time-zone-review-short-run).
