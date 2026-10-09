# PR 2 review resolution

All ten supplied findings were checked against the listener, the installed ATSPM
5.3.1 packages, and available ATSPM source. No live services or databases were
changed during this work.

| Finding | Result and change |
| --- | --- |
| Retries repeat committed envelopes | Repeated writes existed, but double-counting was not reproduced: the packaged Upsert unions events using value equality. Retries now track acknowledged rows by originating envelope and hour, and skip those rows during retries and poison isolation. A lost acknowledgement remains replayable through the same value-equality union. |
| Poison threshold aborts later healthy envelopes | Confirmed. Threshold exceptions are deferred until the remaining isolated envelopes have been attempted. |
| Upsert ignores cancellation | Confirmed. The package repository API has no cancellation token. A local `EfEventLogWriter` uses the packaged context, model, primary keys and compression, passing the token to `FindAsync`, `AddAsync` and `SaveChangesAsync`, while retaining union semantics. No package source was copied. |
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
