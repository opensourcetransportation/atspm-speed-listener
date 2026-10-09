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

## Volume review

The October 9 Docker test's 155,876 events produced a 30,303-byte text capture,
including startup/model warnings and summaries every ten seconds. Individual
summary records averaged 350 bytes; completed-batch and archive records each
averaged about 117 bytes. At 20,000 events/minute, batch size 5,000 and the normal
one-minute summary, this projects to roughly 1.9 MiB/day per text sink, including
the test configuration's recurring mapping warning. JSON envelopes, timestamps,
cloud metadata and exceptions add overhead. This is an estimate, not a retention
guarantee. Small configured batches or frequent summaries increase volume.

The previous per-packet Debug header log could generate 28.8 million records/day
at this rate, even for healthy traffic; it has been removed. The legacy sample
emitter's per-packet success message is now Debug rather than Information. The
load generator prints progress about once per second only for its bounded run;
redirected CLI output requires retention by its caller.

Rejected-packet Debug samples retain the sender, reason, datagram length and up to
64 payload bytes as structured properties. At most
`RejectedPacketSamplesPerInterval` samples (default 10) are emitted per
`SummaryInterval` (default one minute), across all senders. Zero disables samples.
Counters and aggregate loss warnings include all rejected packets, including
unsampled packets. Successful packets produce no per-packet logs. Enable the
specific receiver category at Debug only when investigating packet formats.

## Retention is per sink

- Optional `ATSPM_STARTUP_LOG` capture is limited to 10 MiB. It reuses the same
  file, clearing old contents when the next encoded write would exceed the cap.
  Oversized existing captures are cleared at startup. This preserves the
  diagnostics script's existing file permissions without granting write access
  to the executable directory. It is a temporary capture, not a log archive.
- Docker's default `json-file` driver can be unbounded. The generic
  [deployment compose file](../deploy/compose.yml) explicitly sets `max-size=10m`
  and `max-file=3`, retaining approximately 30 MB per listener container. Existing
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
therefore be configured on every enabled sink, even with packet sampling.

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

## Verification

All 98 application tests passed, including Unicode diagnostic output, oversized
existing captures, oversized single writes, configurable rejection sampling and
structured rejection fields. The published Windows executable cleared an 11 MiB
capture to a 758-byte startup/help capture. A live Docker test counted 2,000
rejected messages but emitted only 20 Debug samples over two summary intervals.
Another 9,990 healthy events ran at 333/second with listener Debug enabled and
no per-packet header or payload logs. Docker inspect confirmed the deployed
`json-file` options `max-size=10m` and `max-file=3`.
