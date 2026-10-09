# Load testing with synthetic speed events

The `generate` command reads the configured ATSPM configuration database, discovers
current-version speed devices, and sends synthetic tagged XS UDP packets to a test
listener. It starts no listener, performs no configuration writes, and writes no
event-log rows directly. The receiving listener follows its normal parse, map,
batch and archive path. Synthetic events therefore become real archived records
when that listener uses a database writer; use a test event-log database.

Run it from a console on the test server or a separate load-generating computer.
It does not stop or change either Windows service. Set the destination host and
port explicitly; all examples below use placeholder port `12000`.

## Discover devices before sending

The command uses appsettings.json from its executable directory, mounted files in
`Configuration/`, and normal .NET environment configuration. Supply database
credentials through the same deployment process used by the listener. No cloud
project, server address or agency-specific service is hardcoded.

```powershell
.\SpeedListener.exe generate --port 12000 --list-targets
```

Discovery reuses the listener's current-version mapping, including deletion and
effective-date rules. It then queries speed devices only on those current location
versions. Historical and future versions do not become generator targets. Each
target reports its location, device ID, and synthetic detector ID.

One synthetic detector is formed as the four numeric location digits plus a
two-digit channel (`01` by default, changed with `--channel`). It is not necessary
to look up a device's name or IP address: the listener routes by detector prefix.
The selected speed device is the lowest-ID SpeedSensor on the current location
version, exactly as in production ingestion. If multiple speed devices exist on
the same current version, their packets cannot target distinct devices with this
mapping; `ExtraDevicesAtSameLocation` reports the additional devices. Nonnumeric
location prefixes are reported separately. No historical device is substituted.

## Send events for every discovered device

Default: one event per second per selected device, cycling through targets in
sorted location order for up to 60 seconds:

```powershell
.\SpeedListener.exe generate --host 127.0.0.1 --port 12000 --duration 60
```

Increase load to 10 events per device per second for five minutes:

```powershell
.\SpeedListener.exe generate --host '<test-server>' --port 12000 `
    --rate-per-device 10 --duration 300
```

For 1,200 selected devices, these settings request 12,000 events/second in total.
Alternatively choose an aggregate rate across all devices:

```powershell
.\SpeedListener.exe generate --host '<test-server>' --port 12000 `
    --rate 5000 --duration 60
```

Use either `--rate` or `--rate-per-device`. The maximum aggregate rate is 1,000,000
events/second; this is an input limit, not a throughput guarantee. The sender uses
one persistent UDP socket and reports the achieved rate. OS timer resolution,
network capacity and generator CPU can limit throughput or produce short bursts.
`MaxLatenessMs` shows the largest delay behind the requested schedule.

`--count` additionally limits event count; the first limit reached ends the run.
Ctrl+C stops it and prints a final summary. If count or duration is too small to
visit every target, the command reports that before sending.

## Packet choices and repeatability

`--format prefixed` (default) sends `Z00000XS` frames; `--format compact` sends
`XS` frames. Each event has a six-digit numeric detector ID and the `~ CR CR`
terminator. The default synthetic UTC timestamp suffix makes timestamps distinct
at the requested schedule and avoids accidental value-equality collapse during
fast tests. The listener converts those instants using its `EventTimeZoneId`.

Use `--no-timestamps` to exercise the normal sensor format with listener receipt
times. Multiple otherwise equal events can then collapse under ATSPM's existing
value-equality storage rule. Timestamped generator traffic is larger than ordinary
sensor traffic, so choose the format that matches the measurement you need.

Speeds vary from 20 through 80 MPH by default. `--min-mph`, `--max-mph`, and
`--seed` control the generated sequence. The maximum MPH is 158 so converted KPH
fits its one-byte field. Reusing a seed repeats the speed sequence, while each new
run starts at the current UTC time. Database targets are discovered once per run.

## Explicit targets for an isolated test

Supplying detector IDs bypasses database discovery. This is useful for testing the
sender against a local UDP capture socket or targeting a known subset:

```powershell
.\SpeedListener.exe generate --host 127.0.0.1 --port 12000 `
    --detectors '123401,234501' --rate 100 --count 1000 --duration 60
```

`--detectors-file 'D:\Tests\detectors.txt'` accepts six-digit detector IDs separated
by commas or whitespace, with `#` comments. IDs from the file and `--detectors` are
combined and deduplicated. An explicitly supplied empty or invalid list fails;
it does not fall back to database discovery. For archived events, the receiving
listener must have mappings for those location prefixes.

## Evaluate results

`GeneratorSummary` reports `Sent`, `SendFailures`, bytes, elapsed time, achieved
events/second, schedule lateness, and `TargetsTouched` versus `TargetsTotal`.
Touched means the sender attempted a successful UDP send for that target; it does
not acknowledge receipt or persistence. A send error stops the test and returns
exit code 1; Ctrl+C returns 130.

Compare counter changes on the listener, especially `Received`, `Parsed`,
`Rejected`, `Unknown`, `Dropped`, `ChannelDepth`, `PublishFailures` and archived
envelopes. One generator event is one datagram; multiple events share each hourly
envelope, so envelope counts should not equal event counts. Allow the listener's
configured flush interval to elapse and its queue to drain before comparing stored
event totals. Use an otherwise isolated stream or capture starting counters so
ordinary sensor traffic does not obscure the comparison. UDP sends alone cannot
prove that a firewall passed the traffic or that the database archived it.
