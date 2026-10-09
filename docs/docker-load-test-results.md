# Docker Desktop load test — October 9, 2026

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
