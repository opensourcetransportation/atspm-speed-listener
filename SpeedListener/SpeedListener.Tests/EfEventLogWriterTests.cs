using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
using SpeedListener.Services;
using SpeedListener.Publishing;
using Utah.Udot.Atspm.Data;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Tests;

public sealed class EfEventLogWriterTests
{
    [Fact]
    public async Task Upsert_RoundTripsAcrossContexts_PreservesDistinctIdenticalEvents()
    {
        var options = new DbContextOptionsBuilder<EventLogContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        foreach (var detector in new[] { "502620", "502618", "502618" })
        {
            await using var context = new InMemoryEventLogContext(options);
            await new EfEventLogWriter(context).UpsertAsync(Row(detector), CancellationToken.None);
        }
        await using var verify = new InMemoryEventLogContext(options);
        var stored = Assert.IsType<CompressedEventLogs<SpeedEvent>>(await verify.Set<CompressedEventLogBase>().SingleAsync());
        Assert.Equal(3, ((CompressedEventLogBase)stored).Data.Count());
    }

    [Fact]
    public async Task Publisher_CommitAcknowledgementLost_RetryDoesNotDuplicateStoredEvents()
    {
        var lostAck = new LoseFirstAcknowledgement();
        var dbOptions = new DbContextOptionsBuilder<EventLogContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(lostAck).Options;
        await using var services = new ServiceCollection()
            .AddScoped<EventLogContext>(_ => new InMemoryEventLogContext(dbOptions))
            .AddScoped<IEventLogWriter, EfEventLogWriter>().BuildServiceProvider();
        var metrics = new SpeedListenerMetrics(TimeProvider.System);
        var publisher = new DatabaseEventPublisher(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SpeedListenerConfiguration { MaxWriteAttempts = 2 }), metrics,
            NullLogger<DatabaseEventPublisher>.Instance);
        var row = Row("502620");
        await publisher.PublishAsync(new EventBatchEnvelope
        {
            DeviceId = row.DeviceId, LocationIdentifier = row.LocationIdentifier, DataType = nameof(SpeedEvent),
            Start = row.Start, End = row.End, Items = row.Data.ToList()
        });
        await using var verify = new InMemoryEventLogContext(dbOptions);
        var stored = Assert.IsType<CompressedEventLogs<SpeedEvent>>(await verify.Set<CompressedEventLogBase>().SingleAsync());
        Assert.Single(((CompressedEventLogBase)stored).Data);
        Assert.Equal(1, metrics.Retries);
    }

    [Fact]
    public async Task Upsert_TimeoutCancellationReachesSaveChanges_LeavesNoCommittedRow()
    {
        var blocker = new BlockingSave();
        var options = new DbContextOptionsBuilder<EventLogContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(blocker).Options;
        await using var context = new EventLogContext(options);
        using var cancellation = new CancellationTokenSource();
        var write = new EfEventLogWriter(context).UpsertAsync(Row("502620"), cancellation.Token);
        await blocker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(cancellation.Token, blocker.Token);
        Assert.Empty(await context.Set<CompressedEventLogBase>().ToListAsync());
    }

    [Fact]
    public async Task Upsert_IdenticalEventsAcrossBatchesSurvive_AndReplayingSameLogicalRowIsIdempotent()
    {
        var options = new DbContextOptionsBuilder<EventLogContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var first = Row("502620");
        ((CompressedEventLogBase)first).Data = first.Data.Concat(first.Data).ToList();
        var next = Row("502620");
        foreach (var row in new[] { first, next, next })
        {
            await using var context = new InMemoryEventLogContext(options);
            await new EfEventLogWriter(context).UpsertAsync(row, CancellationToken.None);
        }
        await using var verify = new InMemoryEventLogContext(options);
        var stored = Assert.IsType<CompressedEventLogs<SpeedEvent>>(await verify.Set<CompressedEventLogBase>().SingleAsync());
        Assert.Equal(3, ((CompressedEventLogBase)stored).Data.Count());
    }

    [Fact]
    public async Task Publisher_LostUpdateAcknowledgement_PreservesIdenticalEventMultiplicity()
    {
        var name = Guid.NewGuid().ToString();
        var root = new Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot();
        var seedOptions = new DbContextOptionsBuilder<EventLogContext>().UseInMemoryDatabase(name, root).Options;
        await using (var seed = new InMemoryEventLogContext(seedOptions))
            await new EfEventLogWriter(seed).UpsertAsync(Row("502620"), CancellationToken.None);
        var options = new DbContextOptionsBuilder<EventLogContext>().UseInMemoryDatabase(name, root)
            .AddInterceptors(new LoseFirstAcknowledgement()).Options;
        await using var services = new ServiceCollection().AddScoped<EventLogContext>(_ => new InMemoryEventLogContext(options))
            .AddScoped<IEventLogWriter, EfEventLogWriter>().BuildServiceProvider();
        var metrics = new SpeedListenerMetrics(TimeProvider.System);
        var publisher = new DatabaseEventPublisher(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SpeedListenerConfiguration { MaxWriteAttempts = 2 }), metrics, NullLogger<DatabaseEventPublisher>.Instance);
        var row = Row("502620");
        await publisher.PublishAsync(new EventBatchEnvelope
        {
            DeviceId = row.DeviceId, LocationIdentifier = row.LocationIdentifier, DataType = nameof(SpeedEvent),
            Start = row.Start, End = row.End, Items = row.Data.Concat(row.Data).ToList()
        });
        await using var verify = new InMemoryEventLogContext(seedOptions);
        var stored = Assert.IsType<CompressedEventLogs<SpeedEvent>>(await verify.Set<CompressedEventLogBase>().SingleAsync());
        Assert.Equal(3, ((CompressedEventLogBase)stored).Data.Count());
        Assert.Equal(1, metrics.Retries);
    }

    [Fact]
    public async Task RetryingCommittedRowAfterOutsideAppendRecognizesItsPrefix()
    {
        var options = new DbContextOptionsBuilder<EventLogContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var row = Row("502620");
        await using (var first = new InMemoryEventLogContext(options))
            await new EfEventLogWriter(first).UpsertAsync(row, CancellationToken.None);
        await using (var outside = new InMemoryEventLogContext(options))
            await new EfEventLogWriter(outside).UpsertAsync(Row("502618"), CancellationToken.None);
        await using var retry = new InMemoryEventLogContext(options);
        await new EfEventLogWriter(retry).UpsertAsync(row, CancellationToken.None);
        await using var verify = new InMemoryEventLogContext(options);
        Assert.Equal(2, (await verify.Set<CompressedEventLogBase>().SingleAsync()).Data.Count());
    }

    [Fact]
    public void PackagedCompressionConverterRoundTripPreservesIdenticalEvents()
    {
        var options = new DbContextOptionsBuilder<EventLogContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        using var context = new InMemoryEventLogContext(options);
        var property = context.Model.FindEntityType(typeof(CompressedEventLogBase))!
            .FindProperty(nameof(CompressedEventLogBase.Data))!;
        var converter = property.GetTypeMapping().Converter!;
        var row = Row("502620");
        ((CompressedEventLogBase)row).Data = Enumerable.Repeat(row.Data.Single(), 3).ToList();
        // EF prefers the packaged backing field, which retains List<SpeedEvent>.
        // Calling the base getter instead materializes List<EventLogModelBase>
        // and changes the collection metadata expected by the vendor binder.
        Assert.NotNull(property.FieldInfo);
        var encoded = converter.ConvertToProvider(property.FieldInfo.GetValue(row));
        var decoded = Assert.IsAssignableFrom<IEnumerable<EventLogModelBase>>(converter.ConvertFromProvider(encoded));
        Assert.Equal(3, decoded.Count());
        Assert.All(decoded, item => Assert.IsType<SpeedEvent>(item));
        Assert.All(decoded, item => Assert.True(string.IsNullOrEmpty(item.LocationIdentifier)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostAcknowledgementAcrossCompressedSnapshotsPreservesDuplicates(bool seedExistingRow)
    {
        var name = Guid.NewGuid().ToString();
        var root = new Microsoft.EntityFrameworkCore.Storage.InMemoryDatabaseRoot();
        var seedOptions = new DbContextOptionsBuilder<EventLogContext>().UseInMemoryDatabase(name, root).Options;
        if (seedExistingRow)
            await using (var seed = new SerializedEventLogContext(seedOptions))
                await new EfEventLogWriter(seed).UpsertAsync(Row("502620"), CancellationToken.None);
        var options = new DbContextOptionsBuilder<EventLogContext>().UseInMemoryDatabase(name, root)
            .AddInterceptors(new LoseFirstAcknowledgement()).Options;
        await using var services = new ServiceCollection().AddScoped<EventLogContext>(_ => new SerializedEventLogContext(options))
            .AddScoped<IEventLogWriter, EfEventLogWriter>().BuildServiceProvider();
        var metrics = new SpeedListenerMetrics(TimeProvider.System);
        var publisher = new DatabaseEventPublisher(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SpeedListenerConfiguration { MaxWriteAttempts = 2 }), metrics, NullLogger<DatabaseEventPublisher>.Instance);
        var row = Row("502620");
        await publisher.PublishAsync(new EventBatchEnvelope
        {
            DeviceId = row.DeviceId, LocationIdentifier = row.LocationIdentifier, DataType = nameof(SpeedEvent),
            Start = row.Start, End = row.End, Items = row.Data.Concat(row.Data).ToList()
        });
        await using var verify = new SerializedEventLogContext(seedOptions);
        var stored = await verify.Set<CompressedEventLogBase>().SingleAsync();
        Assert.Equal(seedExistingRow ? 3 : 2, stored.Data.Count());
        Assert.Equal(1, metrics.Retries);
        Assert.Equal(0, metrics.WriteConflicts);
    }

    [Fact]
    public async Task NonAppendOutsideChangeProducesScopedConflictAndPreservesStoredData()
    {
        var options = new DbContextOptionsBuilder<EventLogContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var row = Row("502620");
        await using (var first = new InMemoryEventLogContext(options))
            await new EfEventLogWriter(first).UpsertAsync(row, CancellationToken.None);
        await using (var outside = new InMemoryEventLogContext(options))
        {
            var stored = await outside.Set<CompressedEventLogBase>().SingleAsync();
            stored.Data = Row("502618").Data.ToList();
            outside.Entry(stored).State = EntityState.Modified;
            await outside.SaveChangesAsync();
        }
        await using var retry = new InMemoryEventLogContext(options);
        await Assert.ThrowsAsync<EventLogWriteConflictException>(() => new EfEventLogWriter(retry).UpsertAsync(row, CancellationToken.None));
        await using var verify = new InMemoryEventLogContext(options);
        Assert.Equal("502618", ((SpeedEvent)(await verify.Set<CompressedEventLogBase>().SingleAsync()).Data.Single()).DetectorId);
    }

    private sealed class SerializedEventLogContext(DbContextOptions<EventLogContext> options) : EventLogContext(options)
    {
        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);
            var property = builder.Entity<CompressedEventLogBase>().Property(row => row.Data);
            var converter = property.Metadata.GetValueConverter()!;
            property.Metadata.SetValueComparer(new ValueComparer<IEnumerable<EventLogModelBase>>(
                (left, right) => left!.SequenceEqual(right!),
                events => events.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                events => CompressedSnapshot(events, converter)));
        }
    }

    private static IEnumerable<EventLogModelBase> CompressedSnapshot(IEnumerable<EventLogModelBase> events,
        Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter converter) =>
        (IEnumerable<EventLogModelBase>)converter.ConvertFromProvider(converter.ConvertToProvider(events.Cast<SpeedEvent>().ToList()))!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SqliteRealCompressionAndLostAcknowledgementPreserveEveryEvent(bool seedExistingRow)
    {
        await using var connection = new Microsoft.Data.Sqlite.SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var seedOptions = new DbContextOptionsBuilder<EventLogContext>().UseSqlite(connection).Options;
        await using (var seed = new EventLogContext(seedOptions))
        {
            await seed.Database.EnsureCreatedAsync();
            if (seedExistingRow) await new EfEventLogWriter(seed).UpsertAsync(Row("502620"), CancellationToken.None);
        }
        var options = new DbContextOptionsBuilder<EventLogContext>().UseSqlite(connection)
            .AddInterceptors(new LoseFirstAcknowledgement()).Options;
        await using var services = new ServiceCollection().AddScoped(_ => new EventLogContext(options))
            .AddScoped<IEventLogWriter, EfEventLogWriter>().BuildServiceProvider();
        var metrics = new SpeedListenerMetrics(TimeProvider.System);
        var publisher = new DatabaseEventPublisher(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SpeedListenerConfiguration { MaxWriteAttempts = 2 }), metrics, NullLogger<DatabaseEventPublisher>.Instance);
        var row = Row("502620");
        await publisher.PublishAsync(new EventBatchEnvelope
        {
            DeviceId = row.DeviceId, LocationIdentifier = row.LocationIdentifier, DataType = nameof(SpeedEvent),
            Start = row.Start, End = row.End, Items = row.Data.Concat(row.Data).ToList()
        });
        await using var verify = new EventLogContext(seedOptions);
        var stored = await verify.Set<CompressedEventLogBase>().SingleAsync();
        Assert.Equal(seedExistingRow ? 3 : 2, stored.Data.Count());
        Assert.Equal(1, metrics.Retries);
        Assert.Equal(1, metrics.RowsPublished);
        Assert.Equal(2, metrics.EventsPublished);
        Assert.Equal(0, metrics.WriteConflicts);
    }

    private static CompressedEventLogs<SpeedEvent> Row(string detector) => new()
    {
        LocationIdentifier = "5026", DeviceId = 1, DataType = typeof(SpeedEvent),
        Start = new DateTime(2026, 10, 7, 12, 0, 0), End = new DateTime(2026, 10, 7, 13, 0, 0),
        Data = new List<SpeedEvent> { new() { LocationIdentifier = "5026", DetectorId = detector,
            Timestamp = new DateTime(2026, 10, 7, 12, 30, 0), Mph = 30, Kph = 48 } }
    };

    private sealed class BlockingSave : SaveChangesInterceptor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            Started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return result;
        }
    }

    private sealed class LoseFirstAcknowledgement : SaveChangesInterceptor
    {
        private int _calls;
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,
            int result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1) throw new TimeoutException("Commit acknowledgement lost.");
            return ValueTask.FromResult(result);
        }
    }

    private sealed class InMemoryEventLogContext(DbContextOptions<EventLogContext> options) : EventLogContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // InMemory serializes comparer snapshots, while relational EF serializes
            // the actual property. Preserve List<SpeedEvent> in the test snapshot so
            // the packaged compression binder retains the concrete event type.
            modelBuilder.Entity<CompressedEventLogBase>().Property(row => row.Data).Metadata.SetValueComparer(
                new ValueComparer<IEnumerable<EventLogModelBase>>(
                    (left, right) => left!.SequenceEqual(right!),
                    events => events.Aggregate(0, (hash, item) => HashCode.Combine(hash, item.GetHashCode())),
                    events => events.Cast<SpeedEvent>().ToList()));
        }
    }
}
