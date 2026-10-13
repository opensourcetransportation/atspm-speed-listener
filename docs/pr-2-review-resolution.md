# PR 2 review resolution

All ten supplied findings were checked against the listener, the installed ATSPM
5.3.1 packages, and available ATSPM source. No live services or databases were
changed during this work.

| Finding | Result and change |
| --- | --- |
| Retries repeat committed envelopes | Repeated writes existed, but double-counting was not reproduced: the packaged Upsert unions events using value equality. Retries now track acknowledged rows by originating envelope and hour, and skip those rows during retries and poison isolation. The October 13 follow-up replaces value-equality union with retained before/after payloads, recognizing an uncertain committed result while preserving identical distinct events. |
| Poison threshold aborts later healthy envelopes | Confirmed. Threshold exceptions are deferred until the remaining isolated envelopes have been attempted. |
| Upsert ignores cancellation | Confirmed. The package repository API has no cancellation token. A local `EfEventLogWriter` uses the packaged context, model, primary keys and compression, passing the token to `FindAsync`, `AddAsync` and `SaveChangesAsync`, while preserving duplicate-event multiplicity through the base payload accessor. The shared packages remain unchanged; the later archive-step adaptation is local and retains upstream attribution. |
| Deleted locations resolve to old versions | Confirmed. Current version selection now includes deletions; a current deletion suppresses the location instead of falling back. An older deletion does not suppress a later active version. |
| Duplicate count stays zero | Confirmed. Rows tied at the current effective date now increment the duplicate counter. Historical versions are expected and are not counted as duplicate mappings. The highest location ID wins ties. |
| Refresh cancellation logs failure | Confirmed. Caller cancellation propagates without a warning or refresh-failure increment. |
| Shutdown validation assumes one batch | The budget was never a guarantee that an entire bounded queue could drain. Validation now includes capped retry delays for one publish, documents that minimum, and every batch started during shutdown receives the shutdown attempt limit. The shared cancellation token bounds the backlog, including EF writes. |
| UTC differs from local ATSPM time | UTC was an explicit design convention, so changing every deployment to the server's local zone would be unsafe. `EventTimeZoneId` now lets agencies select their database's convention. UTC remains the default; non-UTC events use local wall-clock values with unspecified Kind. Seasonal conversion tests cover receipt and offset-bearing suffix timestamps. |
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

Before deployment, select the time zone that matches existing event-log rows and
test the new writer against the agency's database provider. UDP and the bounded
in-memory queue remain nondurable. A shutdown deadline can still leave queued
events unwritten, and cancellation cannot undo a commit already accepted by the
server. One writer per device stream/database remains required.

## October 13 follow-up review

| Finding | Resolution and regression coverage |
| --- | --- |
| Colon-containing endpoint keys do not bind | Use a list of SourceAddress, SourcePort and DetectorId. A real JSON configuration binding test parses an untagged packet; legacy nonempty dictionary input and duplicate endpoints fail validation. |
| Mapping cutoff always UTC | Convert the clock to EventTimeZoneId before comparing effective dates. A Denver version-boundary test verifies no early switchover. |
| Offset-less sensor timestamp assumed UTC | Interpret it in EventTimeZoneId; explicit offsets still represent instants. DST gaps and repeated ambiguous times require an offset. |
| Generator lacks Npgsql compatibility | Set the timestamp compatibility switch before building generator services, matching the listener. |
| Union removes legitimate identical events | Append through the base payload collection and retain each logical row across retries. Both generic accessors and packaged Union are bypassed. Tests cover separate batches, identical events in one batch, lost insert/update acknowledgements, outside-write conflicts, and the actual compression converter/backing-field round trip. |
| Extra UDP sockets block upgrade | Prefer the configured observed port, otherwise a unique wildcard listener, and retain ExpectedPort for genuine ambiguity. Two extra-socket regression scenarios pass. |
| Unbounded unknown-detector warning cache | Remove the lifetime cache. Aggregate at most 256 detector details per summary period; all additional events remain counted and get an aggregate warning. Debug retains full batch details. |
| Invalid diagnostic size crashes startup | Validate the value without throwing; malformed, negative or overflowing values emit a diagnostic and use unlimited capture. |

The summary Unknown counter and per-detector warning EventCount now represent
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
| Unknown has different counter semantics | Keep the requested per-period behavior and rename the logged field UnknownSinceLast. Per-detector warning counts reset each summary; other summary counters remain cumulative. |

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

At the user's request, the speed-specific archive helper was replaced with
`ArchiveDataEventsWithoutDeduplication`, a local ATSPM-compatible workflow step.
It uses ATSPM's grouping and compressed row formats while bypassing the generic
Data accessor's HashSet conversion. Its envelope adapter is wired into the
listener workflow. All 171 tests pass, including relational insert/update and
retry tests proving every valid identical occurrence remains stored.
