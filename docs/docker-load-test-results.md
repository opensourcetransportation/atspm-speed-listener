# Docker Desktop load-test results

## October 15 server-time two-hour run

**Passed for the tested uniform device distribution.** Listener/generator revision
`baacf4daeeca3c262749e805a4b7d9e8843b6029` ran on Docker Desktop against local
PostgreSQL 16. Before starting, the previous short run's 1,233 compressed rows
(100,955 events) were removed only from the isolated local test event database,
and its remaining row count was verified as zero. Configuration discovery read
the existing local ATSPM configuration database. No GCP data, agency event database
or remote Windows service was changed.

Both processes used `TZ=America/Denver` and `EventTimeZoneId=Local`; coordinates
were ignored. Traffic ran from approximately **4:40:48 PM to 6:40:48 PM Mountain
Daylight Time on October 15, 2026**. Drain, process shutdown and stored-payload
reconciliation completed at **6:42:00 PM Mountain**. Generator termination was
the configured duration, not a count limit or accelerated synthetic clock.

| Measurement | Result |
| --- | --- |
| Actual paced duration | 7,200.003 seconds |
| Achieved aggregate rate | 334 events/second, 20,040/minute |
| Discovered devices / targets touched | 1,233 / 1,233 |
| Sent / received / parsed / verified stored | 2,404,798 each |
| Rejected / unknown / dropped / unmapped speed | 0 |
| Send failures / retries / publish failures / poison batches | 0 |
| Write conflicts / conflict events | 0 |
| Final channel depth | 0 |
| Largest channel depth observed in summaries | 259; a sampled value, not a continuous maximum |
| Unique stored device-hour rows | 3,699 |
| Completed publication operations | 481 |
| Batch publication mean / p95 / maximum | 681.126 / 1,024.987 / 1,861.738 ms |
| Invalid checked payloads | 0 |
| Generator / listener exit code | 0 / 0 |

After the generator finished, the monitor allowed 45 seconds for the final flush
and stopped the listener with a 55-second shutdown allowance. It then read and
decompressed every stored SpeedEvent row in the isolated event database, counting
occurrences without deduplication or timestamp filtering. The count matched the
generator and listener counters exactly. Checks found no incorrect detector/location
prefix, speed outside the configured 20–80 mph range, or timestamp offset metadata.
Stored first/last wall times matched the Mountain-time run window. Both isolated
test processes stopped cleanly; the database and local evidence files were retained.

The run crossed two hourly boundaries and contained one complete hourly growth
cycle, with partial hours on either side:

| Stored local hour | Events |
| --- | ---: |
| October 15, 16:00 | 384,636 |
| October 15, 17:00 | 1,202,400 |
| October 15, 18:00 | 817,762 |
| **Total** | **2,404,798** |

Whole-row append cost increased through the complete 17:00 hour. The following
timings come from timestamped completed publication records, with 40 operations
in each window. They measure batch archive/save work, not per-event or end-to-end
latency; normal batches contained 5,000 events and the final batch contained 4,798.

| Mountain-time window | Mean | p95 | Maximum |
| --- | ---: | ---: | ---: |
| 17:00–17:10 | 457.919 ms | 511.958 ms | 817.452 ms |
| 17:50–18:00 | 1,039.310 ms | 1,114.810 ms | 1,299.368 ms |

The background monitor collected 125 resource samples, approximately once per
minute. Observed listener maxima were 194.9 MiB memory and 285.3% Docker CPU;
PostgreSQL maxima were 205.4 MiB and 73.45%. Docker CPU percentages can exceed 100%
when multiple cores are used. These are sampled maxima, not continuous peaks;
the PostgreSQL container also served the existing local ATSPM stack. Resource
results describe this shared Docker Desktop setup, not production sizing.

Mapping refreshes continued during traffic. Existing configuration validation
reported 9 invalid identifiers and 67 tied-version rows; the generator selected
the same 1,233 valid mappings, so these warnings did not cause unknown or lost
events in this run. Tied mappings are not duplicate speed-event occurrences.

This closes the **two-hour uniform-distribution test** for revision `baacf4d`,
including full-hour row growth, boundary crossing, drain and stored-count
reconciliation. It does not establish concentrated hot-device capacity, two
complete hourly cycles, live SQL Server/MySQL/Oracle behavior, the UDOT sensor/VPN
network path, Windows service capacity, prolonged outage recovery or production
soak readiness. Those checks remain separate release work.

## October 15 time-zone review short run

The two-hour test was stopped at the user's request and deferred. This run is
short-run verification only. The later server-time run above supplies the two-hour
uniform-distribution result; this earlier run does not.
The coordinate-derived build at 56920e8 used Docker Desktop and PostgreSQL 16, read the
existing local configuration database, and wrote to a fresh isolated event database.
No GCP or remote Windows service was involved. Both isolated processes were stopped
after the listener drained.

| Measurement | Result |
| --- | --- |
| Actual wall-clock duration | 302.258 seconds |
| Achieved rate | 334 events/second, 20,040/minute |
| Mapped devices/targets touched | 1,233 / 1,233 |
| Sent / received / parsed / verified stored | 100,955 each |
| Rejected / dropped / publish failures / conflicts | 0 |
| Stored device-hour rows | 1,233 |
| Invalid payload identities, speeds or offset metadata | 0 |
| Publication timing samples / p95 / maximum | 21 / 650.069 ms / 1,006.439 ms |

Reconciliation decompressed every SpeedEvent row in the otherwise empty test
event database and counted occurrences without deduplication or timestamp filtering.
One packet contained one event. Stored timestamps had no offset metadata. The
timings are per publication operation, not per event or end-to-end latency.
No full-hour growth cycle, late-hour latency or concentrated-device capacity was
validated. Generator exit 130 reflects deliberate cancellation; listener exit was 0.

The zone warning identified local test locations 6083 (Africa/Johannesburg), and
7026, 7027 and 7388 (Antarctica/Troll), against the configured America/Denver
fallback. Coordinates were not modified. Of the stored events, 100,627 used the
14:00 local hour and 328 used the 22:00 local hour. This illustrates why valid
but unexpected coordinates require operator review; that revision did not override them. The subsequent server-time policy ignores
coordinates and does not repair these isolated historical rows. This is a local-test snapshot, not a statement about current GCP data.

## Historical October 9 runs

Historical evidence for revision `9fb15900` of ATSPM and the listener build used
on October 9. Later mapping, duplicate-preservation, metrics and location-time-zone
changes are not covered by these runs. This is not the required two-hour sustained
capacity test; see [current status](speed-listener-implementation-plan.md) and
[load-test requirements](load-testing.md#sustained-capacity-release-gate).

ATSPM was built from `avenueconsultants/udot-atspm-development` main revision
`9fb15900ecea85fde27b792a9f42087eb87121e3`. The configuration, data, report and
identity APIs, web UI and database installer were built from that source.
Main's database migration completed successfully. The existing local PostgreSQL
databases, certificates and runtime settings were retained; all four databases
were backed up before migration. The development feature checkout was unchanged.
No GCP database or remote Windows service was used for this test.

The listener and generator used the local `ATSPM-Config` and `ATSPM-EventLogs`
databases in Docker Desktop. Discovery found 1,205 routable current-version speed
devices and zero additional speed devices on those selected location versions.
The listener reported 12 invalid location identifiers and 114 tied current-version
rows; its existing deterministic selection rules kept the valid mappings active.

| Test | Sender and packet format | Requested rate | Duration | Sent | Archived |
| --- | --- | --- | --- | --- | --- |
| Baseline | Separate Docker container; prefixed, timestamped | 18,000/min | 120 seconds | 36,000 | 36,000 |
| Sustained | Separate Docker container; compact, timestamped | 19,980/min | 300 seconds | 99,899 | 99,899 |
| Sensor wire format | Published Windows executable through Docker's published UDP port; 19-byte prefixed packets without timestamps | 19,980/min | 60 seconds | 19,977 | 19,977 |

Each run reached all 1,205 devices. Duration-limited pacing can finish a few events
short of rate multiplied by duration. The final listener totals were
`Received=155876 Parsed=155876`, with zero rejected, unknown or dropped events,
retries, publish failures, poison batches or mapping-refresh failures. Mapping
refresh succeeded during sustained traffic. The first run crossed an hourly
archive boundary.

Verification queried the actual compressed database rows, decompressed their
gzip JSON, and counted events inside each run's exact UTC timestamp window.
Every sender total matched its archive total. All decoded events had the expected
detector/location prefix and a speed within the generated 20–80 MPH range.
This checks persisted events, rather than equating envelope counts with events.

The last partial batch contained 876 events and archived successfully during
shutdown. Graceful Docker stop completed in 0.845 seconds without forced
termination. The listener was restarted and remains running on UDP 10089,
published on `127.0.0.1`. Both local UI URLs (`http://localhost:3000` and
`http://localhost:3080`) returned HTTP 200; all four API health endpoints
returned HTTP 200 / Healthy during load and after restart.

The PostgreSQL preview exposed a generator discovery defect: location effective
dates use `timestamp without time zone`, but its query cutoff had UTC DateTime
kind. The cutoff now preserves the UTC clock value with Unspecified kind,
matching the ATSPM column. The corrected Docker image and Windows package both
completed database discovery and ingestion tests. All 92 application tests passed.

The listener used normal batch size 5,000, 30-second flush interval and database
write parallelism 8; only the UDP port and diagnostic summary frequency were
overridden. Sampled listener memory during traffic was roughly 150–162 MiB.
This was an eight-minute synthetic test with uniform round-robin device traffic,
not a long-duration capacity benchmark or a verification of the UDOT network path.

Local source exports, deployment configuration, database backups, generator logs,
resource samples and verification scripts are under ignored
`build/docker-load-test` (the ATSPM source is in `build/atspm-main-source`). Those
runtime files include database settings and must remain untracked. Synthetic
events remain in the local test event-log database.
