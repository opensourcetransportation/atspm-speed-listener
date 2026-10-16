using Microsoft.EntityFrameworkCore;
using SpeedListener.Publishing;
using SpeedListener.WorkflowSteps;
using Utah.Udot.Atspm.Data;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

// Opt-in integration probe. Supply a NEW, EMPTY test database, never an agency database.
var provider = args.SingleOrDefault() ?? throw new ArgumentException("Specify PostgreSql, SqlServer, MySql, Oracle or Sqlite.");
var connection = Environment.GetEnvironmentVariable("ATSPM_PROVIDER_SMOKE_CONNECTION")
    ?? throw new ArgumentException("Set ATSPM_PROVIDER_SMOKE_CONNECTION to an empty disposable test database.");
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
var builder = new DbContextOptionsBuilder<EventLogContext>();
switch (provider)
{
    case "PostgreSql": builder.UseNpgsql(connection, config => config.CommandTimeout(15)); break;
    case "SqlServer": builder.UseSqlServer(connection, config => config.CommandTimeout(15)); break;
    case "MySql": builder.UseMySql(connection, ServerVersion.AutoDetect(connection), config => config.CommandTimeout(15)); break;
    case "Oracle": builder.UseOracle(connection, config => config.CommandTimeout(15)); break;
    case "Sqlite": builder.UseSqlite(connection, config => config.CommandTimeout(15)); break;
    default: throw new ArgumentException("Unsupported provider.");
}
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var options = builder.Options;
await using (var create = new EventLogContext(options))
{
    if (!await create.Database.EnsureCreatedAsync(timeout.Token))
        throw new InvalidOperationException("Probe requires a new empty database; existing schema was not changed.");
}
// Use an ambiguous local time and full fractional precision; it must remain literal wall time.
var time = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified).AddTicks(1234567);
for (var i = 0; i < 2; i++)
{
    var row = ArchiveDataEventsWithoutDeduplication.ArchiveEnvelope(new EventBatchEnvelope {
        DeviceId = 7, LocationIdentifier = "5026", DataType = nameof(SpeedEvent), Start = time, End = time,
        Items = new[] { new SpeedEvent { DetectorId = "502620", Timestamp = time, Mph = 30, Kph = 48 } }
    }).Single();
    await using var context = new EventLogContext(options);
    await new EfEventLogWriter(context).UpsertAsync(row, timeout.Token);
}
await using var verify = new EventLogContext(options);
var stored = await verify.CompressedEvents.AsNoTracking().SingleAsync(timeout.Token);
var events = stored.Data.ToArray();
if (events.Length != 2 || events.Any(item => item.Timestamp.Ticks != time.Ticks || item.Timestamp.Kind != DateTimeKind.Unspecified)
    || stored.Start != new DateTime(2026, 11, 1, 1, 0, 0) || stored.End != new DateTime(2026, 11, 1, 2, 0, 0)
    || stored.Start.Kind != DateTimeKind.Unspecified || stored.End.Kind != DateTimeKind.Unspecified)
    throw new InvalidOperationException("Timestamp or occurrence round trip did not match.");
Console.WriteLine($"Provider={provider} PersistedOccurrences=2 TimezoneNaive=True FractionalTicksPreserved=True HourKeysMatched=True");
