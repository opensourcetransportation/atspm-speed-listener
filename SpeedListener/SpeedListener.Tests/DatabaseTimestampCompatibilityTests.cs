using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SpeedListener.Publishing;
using SpeedListener.WorkflowSteps;
using Utah.Udot.Atspm.Data;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Tests;

public sealed class DatabaseTimestampCompatibilityTests
{
    [Theory]
    [InlineData("PostgreSql", "timestamp")]
    [InlineData("SqlServer", "datetime2")]
    [InlineData("MySql", "datetime")]
    [InlineData("Oracle", "TIMESTAMP")]
    [InlineData("Sqlite", "TEXT")]
    public void ProviderModelUsesTimezoneNaiveHourKeysAndLosslessCompressedEventTimes(string provider, string expectedType)
    {
        AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);
        var builder = new DbContextOptionsBuilder<EventLogContext>();
        switch (provider)
        {
            case "PostgreSql": builder.UseNpgsql("Host=localhost;Database=unused;Username=unused"); break;
            case "SqlServer": builder.UseSqlServer("Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true"); break;
            case "MySql": builder.UseMySql("Server=localhost;Database=unused;User=unused", new MySqlServerVersion(new Version(8, 0, 36))); break;
            case "Oracle": builder.UseOracle("Data Source=localhost/unused;User Id=unused;Password=unused"); break;
            case "Sqlite": builder.UseSqlite("Data Source=:memory:"); break;
        }
        using var context = new EventLogContext(builder.Options);
        var entity = context.Model.FindEntityType(typeof(CompressedEventLogBase))!;
        foreach (var name in new[] { "Start", "End" })
        {
            var mapping = entity.FindProperty(name)!.GetRelationalTypeMapping();
            Assert.StartsWith(expectedType, mapping.StoreType, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("with time zone", mapping.StoreType, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("datetimeoffset", mapping.StoreType, StringComparison.OrdinalIgnoreCase);
            // Exercise the provider's schema generation without a live connection.
        }
        Assert.False(string.IsNullOrEmpty(context.Database.GenerateCreateScript()));
        var time = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified).AddTicks(1234567);
        var events = new List<SpeedEvent> {
            new() { DetectorId = "502620", Timestamp = time, Mph = 30, Kph = 48 },
            new() { DetectorId = "502620", Timestamp = time, Mph = 30, Kph = 48 }
        };
        var converter = entity.FindProperty(nameof(CompressedEventLogBase.Data))!.GetTypeMapping().Converter!;
        var bytes = Assert.IsType<byte[]>(converter.ConvertToProvider(events));
        var stored = Assert.IsAssignableFrom<IEnumerable<EventLogModelBase>>(converter.ConvertFromProvider(bytes)).Cast<SpeedEvent>().ToArray();
        Assert.Equal(2, stored.Length);
        Assert.All(stored, item => {
            Assert.Equal(time.Ticks, item.Timestamp.Ticks);
            Assert.Equal(DateTimeKind.Unspecified, item.Timestamp.Kind);
        });
    }

    [Fact]
    public async Task SQLiteArchiveAndAppendRoundTripKeepWallClockWithoutTimezoneOrDuplicateLoss()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<EventLogContext>().UseSqlite(connection).Options;
        var wallTime = new DateTime(2026, 11, 1, 1, 30, 0, DateTimeKind.Unspecified).AddTicks(7654321);
        for (var i = 0; i < 2; i++)
        {
            var row = Assert.Single(ArchiveDataEventsWithoutDeduplication.ArchiveEnvelope(new EventBatchEnvelope {
                DeviceId = 7, LocationIdentifier = "5026", DataType = nameof(SpeedEvent), Start = wallTime, End = wallTime,
                Items = new[] { new SpeedEvent { DetectorId = "502620", Timestamp = wallTime, Mph = 30, Kph = 48 } }
            }));
            await using var context = new EventLogContext(options);
            await context.Database.EnsureCreatedAsync();
            await new EfEventLogWriter(context).UpsertAsync(row, CancellationToken.None);
        }
        await using var verify = new EventLogContext(options);
        var stored = await verify.CompressedEvents.AsNoTracking().SingleAsync();
        Assert.Equal(DateTimeKind.Unspecified, stored.Start.Kind);
        Assert.Equal(DateTimeKind.Unspecified, stored.End.Kind);
        Assert.Equal(2, stored.Data.Count());
        Assert.All(stored.Data, item => {
            Assert.Equal(wallTime.Ticks, item.Timestamp.Ticks);
            Assert.Equal(DateTimeKind.Unspecified, item.Timestamp.Kind);
        });
    }

    [Fact]
    public void ArchiveStripsKindWithoutChangingWallClockDigits()
    {
        var timestamp = new DateTime(2026, 10, 15, 12, 30, 0, DateTimeKind.Utc);
        var row = Assert.Single(ArchiveDataEventsWithoutDeduplication.ArchiveEnvelope(new EventBatchEnvelope {
            DeviceId = 7, LocationIdentifier = "5026", DataType = nameof(SpeedEvent), Start = timestamp, End = timestamp,
            Items = new[] { new SpeedEvent { DetectorId = "502620", Timestamp = timestamp, Mph = 30, Kph = 48 } }
        }));
        Assert.Equal(timestamp.Ticks, Assert.Single(row.Data).Timestamp.Ticks);
        Assert.Equal(DateTimeKind.Unspecified, Assert.Single(row.Data).Timestamp.Kind);
        Assert.Equal(DateTimeKind.Unspecified, row.Start.Kind);
        Assert.Equal(DateTimeKind.Unspecified, row.End.Kind);
    }
}
