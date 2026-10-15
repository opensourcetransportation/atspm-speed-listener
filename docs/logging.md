# Logging and retention

The listener follows ATSPM main's EventLogUtility host pattern:
`Host.CreateDefaultBuilder`, ATSPM `ApplyVolumeConfiguration`, `AddGoogle`, and
the `Atspm` Windows Event Log where available. Application messages use
source-generated `LoggerMessage` methods with stable event IDs, event names,
typed structured properties and separate exception arguments. No additional
operational file logger or logging framework is introduced.

Normal logging is per lifecycle, mapping refresh and batch, plus one periodic
summary. This follows ATSPM's decoder pattern of Information for completed work,
Debug for details and Warning/Error for failures. The listener defaults its own
category to Information and dependency categories to Warning. Provider-specific
overrides still apply. Windows Event Log defaults to Warning; use
`Logging.EventLog.LogLevel.SpeedListener=Information` if summaries are wanted there.

## Historical volume review (October 9, 2026)

The October 9 Docker test's 155,876 events produced a 30,303-byte text capture,
including startup/model warnings and summaries every ten seconds. Individual
summary records averaged 350 bytes; completed-batch and archive records each
averaged about 117 bytes. At 20,000 events/minute, batch size 5,000 and the normal
one-minute summary, this projects to roughly 1.9 MiB/day per text sink, including
the test configuration's recurring mapping warning. These byte measurements
predate the expanded row/event/conflict counters and location-zone messages;
remeasure current output for a retention budget. JSON envelopes, timestamps,
cloud metadata and exceptions add overhead. This is an estimate, not a retention
guarantee. Small configured batches or frequent summaries increase volume.

The previous per-packet Debug header log could generate 28.8 million records/day
at this rate, even for healthy traffic; it has been removed. The legacy sample
emitter's per-packet success message is now Debug rather than Information. The
load generator prints progress about once per second only for its bounded run;
redirected CLI output requires retention by its caller.

Rejected-packet Debug records retain the sender, reason, datagram length and up to
64 payload bytes as structured properties. Every rejection produces a Debug
record when enabled, without sampling or throttling. `Truncated=True` identifies
payloads longer than the 64-byte preview. Information, Warning and Error contain
no per-packet payloads; counters and aggregate loss warnings include all rejected
packets. Successful speed packets produce no per-packet logs. Recognized `X1` actuation
and unconfigured basic `XS` messages produce Debug classification records (3008),
including the same payload preview. Summaries include `Actuation` and `UnmappedSpeed`.
Actuation is expected non-speed traffic and causes no loss warning. Unmapped basic
speed messages produce one aggregate mapping warning per summary interval (3009),
so valid speed data lacking a configured detector identity remains visible. Debug volume can be much
larger than the healthy estimate; that is intentional to retain diagnostic records.

## Identify detector routing

At Debug, each flushed batch logs one record per detector ID: event 3031
(`SpeedListenerUnknownDetector`) for a missing current speed-device mapping, or
3032 (`SpeedListenerMappedDetector`) with the selected device ID. Both include
the first-four-digit location identifier and event count. Mapped records describe
routing, not a successful database commit; confirm archive logs separately.
These per-batch diagnostics do not add records at Information, Warning or Error.
Unknown detectors produce Warning event 3033 (`SpeedListenerUnknownDetectorWarning`)
once per detector per summary period. `EventCount` counts only events skipped in
that period; the summary's `UnknownSinceLast` count also resets each period. The default
period is one minute (`SummaryInterval`). Graceful shutdown reports any remaining
partial-period details. Mapped detectors still have no per-detector warnings.

Memory is bounded to 256 detector details per period. Event 3034 reports all
additional skipped events when that limit is reached, and the summary total
still includes every event. Debug retains all per-batch detector details without
sampling. Detector aggregation resets each period and does not retain IDs for
the lifetime of the process. There is no five-minute per-detector warning cache.

For example, search startup capture for `SpeedListenerUnknownDetector` and
`SpeedListenerMappedDetector`, then filter the desired `LocationIdentifier`.
No records for those locations means none were processed during that capture;
it does not prove the configuration is missing. Packet source addresses are not
retained in the batch and are not included in routing records.

## Retention is per sink

- Optional `ATSPM_STARTUP_LOG` capture supports `ATSPM_STARTUP_LOG_MAX_BYTES`.
  Zero means unlimited append-only capture; a positive byte count sets a cap.
  Malformed, negative or overflowing values emit a diagnostic and fall back to
  unlimited capture so startup succeeds and diagnostic data is preserved.
  The diagnostics helper defaults to unlimited troubleshooting capture and accepts
  `-MaxFileSizeMB` for a chosen cap (for example, 1024 for 1 GiB). A manually set
  capture path without a size setting defaults to 10 MiB. With a positive limit,
  older contents are cleared when full, including oversized files at startup.
  The service account must be able to write the capture file; the diagnostics
  helper grants that access. Disable temporary capture after use.
- Docker's default `json-file` driver can be unbounded. The generic
  [deployment compose file](../deploy/compose.yml) explicitly sets `max-size=10m`
  and `max-file=3` by default, retaining approximately 30 MB per listener container.
  `SPEED_LISTENER_LOG_MAX_SIZE` and `SPEED_LISTENER_LOG_MAX_FILES` permit larger
  Debug retention (for example, `1g` and `10`). For complete temporary Debug
  capture, add [the Debug override](../deploy/compose.debug.yml), which enables
  Debug and removes rotation options. Existing
  containers must be recreated to apply logging-driver options. Deleting or
  disabling startup capture does not limit Docker's separate logs.
- Windows Event Log retention is managed by Windows for the shared `Atspm` log.
  Inspect its limit with `Get-WinEvent -ListLog Atspm | Select-Object
  MaximumSizeInBytes,LogMode`. Choose size/overwrite policy through your normal
  Windows administration process; the application does not modify a shared log.
- Google Cloud Logging retention, exclusions and storage policy belong to the
  destination project's log bucket. Application filters control what is sent;
  the application does not alter cloud retention policy.

Database failure logs intentionally retain their exceptions and identities.
Persistent outages, poison data, framework Debug/Trace overrides and repeated
service restarts can increase volume beyond the healthy estimate. Retention must
therefore be chosen explicitly for every enabled sink; Debug capture can retain
all records without sampling or automatic deletion when unlimited retention is selected.

## Docker deployment

Set `SPEED_LISTENER_ENV_FILE` to the absolute path of an untracked environment
file containing your ATSPM `DatabaseConfiguration__...` settings. From the
repository root, run `docker compose -f deploy/compose.yml up -d --build`.
The default image is built locally; `SPEED_LISTENER_IMAGE` can select another
image tag. The default bind address is `127.0.0.1`; set
`SPEED_LISTENER_BIND_ADDRESS=0.0.0.0` to receive remote sensor traffic, and
`SPEED_LISTENER_PORT` to the desired UDP port (default 10088). Configure reachable
database hosts in the environment file; other Compose projects' container names
are not automatically available on this deployment's network.

The local Docker test deployment also has the 10m × 3 retention limit. Credentials,
runtime environment files, packet captures and generated log files remain ignored.

For unlimited Debug capture, run
`docker compose -f deploy/compose.yml -f deploy/compose.debug.yml up -d --build`.
The override uses Compose's `!reset` tag to remove normal rotation options;
use a Compose version that supports this tag. Return to the base file alone
and recreate the container to restore normal Information logging and retention.

## Historical verification (October 9, 2026)

At that revision, all 99 application tests passed, including Unicode diagnostic output, oversized
existing captures, oversized single writes and structured rejection fields.
A 1,000-message rejection test emits all 1,000 records at Debug and zero payload
records at Information, Warning or Error, while counting every rejection at every
level. Another 9,990 healthy events ran at 333/second with listener Debug enabled
and no per-packet header or payload logs. Docker inspect confirmed the normal
deployment's `json-file` options `max-size=10m` and `max-file=3`.
The live Docker rejection test also emitted all 1,000 Debug records. Published
Windows checks confirmed that an unlimited capture and a 20 MiB cap both retained
an existing 11 MiB capture. All six deployment option scenarios passed.

## Scoped write conflicts

`WriteConflicts` and `ConflictEvents` are lifetime counters. Error event 3045
identifies an uncertain device-hour contribution after an outside row change.
That contribution may already have committed; it is not appended a second time
when its outcome cannot be established. Other hours and devices continue, and
an envelope with any conflicted hour is excluded from `EnvelopesPublished`.
`RowsPublished` and `EventsPublished` independently count all confirmed row
contributions and events, including successful hours of partial envelopes and
commits confirmed after acknowledgement loss. They update at row completion,
so a later batch failure does not erase progress. Retries/isolation cannot count
the same logical row twice. A device's poison history resets only after a clean
publish; a conflict, even alongside a committed hour, cannot clear that history.

The summary now names its interval counter `UnknownSinceLast`, replacing the
previous mixed-semantics `Unknown` field. Other event/publication/failure counters remain cumulative since process start.
ChannelDepth is current queue depth, MappingAgeSeconds is age since successful
refresh, and PublishLatencyMs is a lifetime mean rather than a counter. Per-detector warning EventCount is
also per period; see the warning description above.


## Location zones and latency

Warning 3013 (`SpeedListenerTimeZoneFallback`) reports how many selected locations
use the configured fallback because coordinates/lookup are unusable. It appears
at most once per successful mapping refresh, not per packet. Debug 3014
(`SpeedListenerLocationTimeZone`) identifies each mapped location's zone and
fallback flag. Active-device/current-version selection determines which locations
appear; a missing detector mapping should also prompt a DeviceStatus check.

PublishLatencyMs averages recorded completed publication-attempt durations since
startup. It includes that attempt's archive/save work but excludes earlier failed
attempts and retry delays. It is not latency per event, queue residence time,
end-to-end storage delay or a percentile. Recent `Archived ... device-hour rows`
records show individual completed-attempt timings. Queue growth, drops, retries,
failures and conflicts provide context for judging whether the writer keeps up.
RowsPublished counts confirmed hourly contributions across flushes, not unique
hourly database rows; EventsPublished counts their occurrences. Fully archived
envelopes and rows are not interchangeable event-count measures.

Latest application validation, October 15: 214 Release tests passed. Historical
volume/Debug flood runs above were not rerun as part of the coordinate-zone change.


The mapping-validation message's `duplicate rows` means location versions tied
at the selected effective date. It is not a count of identical speed detections.
Invalid rows indicate unroutable location identifiers. Those configuration counts
are separate from Rejected, UnknownSinceLast and write-conflict metrics.

For a healthy observation window, compare Received/Parsed and EventsPublished
deltas after allowing partial batches to flush. Lifetime Parsed and EventsPublished
can differ because earlier unknown/poison/conflicted events were skipped, or
because a partial batch is still in flight; ChannelDepth=0 alone does not prove
all accepted events have committed. Fixing a mapping stops future skips but does
not recover previously skipped events or reset lifetime counters.
