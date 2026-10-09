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
    public async Task Upsert_RoundTripsAcrossContexts_AccumulatesAndDeduplicatesReplay()
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
        Assert.Equal(2, stored.Data.Count);
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
        Assert.Single(stored.Data);
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
