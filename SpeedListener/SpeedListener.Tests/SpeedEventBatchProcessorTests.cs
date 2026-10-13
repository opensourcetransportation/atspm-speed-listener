using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
using SpeedListener.Publishing;
using SpeedListener.Services;
using System.Threading.Channels;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Tests;

public sealed class SpeedEventBatchProcessorTests
{
    [Fact]
    public async Task ProcessAsync_ChannelsAtSameLocation_ShareDeviceAndKeepFullDetectorIds()
    {
        var publisher = new RecordingPublisher();
        var processor = CreateProcessor(publisher);
        var channel = Channel.CreateUnbounded<SpeedEvent>();
        foreach (var id in new[] { "502620", "502618", "502622" })
            channel.Writer.TryWrite(Event(id, DateTime.UtcNow, 30));
        channel.Writer.Complete();
        await processor.ProcessAsync(channel.Reader, CancellationToken.None);
        var envelope = Assert.Single(Assert.Single(publisher.Batches));
        Assert.Equal(1, envelope.DeviceId);
        Assert.Equal("5026", envelope.LocationIdentifier);
        Assert.Equal(new[] { "502620", "502618", "502622" },
            envelope.Items.Select(item => item.DetectorId));
    }

    [Theory]
    [InlineData("")]
    [InlineData("502")]
    [InlineData("999920")]
    public async Task ProcessAsync_ShortOrUnmappedPrefix_IsCountedWithoutPublishing(string detectorId)
    {
        var publisher = new RecordingPublisher();
        var metrics = new SpeedListenerMetrics(TimeProvider.System);
        var processor = CreateProcessor(publisher, metrics: metrics);
        var channel = Channel.CreateUnbounded<SpeedEvent>();
        channel.Writer.TryWrite(Event(detectorId, DateTime.UtcNow, 30));
        channel.Writer.Complete();
        await processor.ProcessAsync(channel.Reader, CancellationToken.None);
        Assert.Empty(publisher.Batches);
        Assert.Equal(1, metrics.Unknown);
    }

    [Fact]
    public async Task ProcessAsync_WhenChannelCompletes_PublishesPartialBatchGroupedByDevice()
    {
        var publisher = new RecordingPublisher();
        var processor = CreateProcessor(publisher);
        var channel = Channel.CreateUnbounded<SpeedEvent>();
        var first = new DateTime(2026, 9, 2, 18, 0, 0, DateTimeKind.Utc);
        channel.Writer.TryWrite(Event("502620", first, 30));
        channel.Writer.TryWrite(Event("502620", first.AddSeconds(1), 31));
        channel.Writer.TryWrite(Event("527148", first.AddSeconds(2), 32));
        channel.Writer.Complete();

        await processor.ProcessAsync(channel.Reader, CancellationToken.None);

        Assert.Single(publisher.Batches);
        Assert.Equal(2, publisher.Batches[0].Count);
        Assert.Equal(1, publisher.AttemptBudgets[0]);
        var firstEnvelope = Assert.Single(publisher.Batches[0], envelope => envelope.DeviceId == 1);
        Assert.Equal(first, firstEnvelope.Start);
        Assert.Equal(first.AddSeconds(1), firstEnvelope.End);
        Assert.Equal(2, firstEnvelope.Items.Count());
    }

    [Fact]
    public async Task ProcessAsync_WhenBatchSizeReached_PublishesBeforeCompletion()
    {
        var publisher = new RecordingPublisher();
        var processor = CreateProcessor(publisher, batchSize: 2);
        var channel = Channel.CreateUnbounded<SpeedEvent>();
        var processing = processor.ProcessAsync(channel.Reader, CancellationToken.None);
        channel.Writer.TryWrite(Event("502620", DateTime.UtcNow, 30));
        channel.Writer.TryWrite(Event("527148", DateTime.UtcNow, 31));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await publisher.Published.Task.WaitAsync(timeout.Token);
        channel.Writer.Complete();
        await processing;

        Assert.NotEmpty(publisher.Batches);
    }

    [Fact]
    public async Task ProcessAsync_WhenFlushIntervalElapses_PublishesPartialBatchBeforeCompletion()
    {
        var publisher = new RecordingPublisher();
        var processor = CreateProcessor(publisher, flushInterval: TimeSpan.FromMilliseconds(50));
        var channel = Channel.CreateUnbounded<SpeedEvent>();
        var processing = processor.ProcessAsync(channel.Reader, CancellationToken.None);
        var speedEvent = Event("502620", new DateTime(2026, 9, 2, 18, 0, 0, DateTimeKind.Utc), 30);

        await channel.Writer.WriteAsync(speedEvent);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await publisher.Published.Task.WaitAsync(timeout.Token);

        Assert.Single(publisher.Batches);
        var envelope = Assert.Single(publisher.Batches[0]);
        Assert.Equal(1, envelope.DeviceId);
        Assert.Equal(speedEvent.Timestamp, envelope.Start);
        Assert.Null(publisher.AttemptBudgets[0]);

        channel.Writer.Complete();
        await processing;
    }

    [Fact]
    public async Task ProcessAsync_UnknownDetector_IsCountedAndNotPublished()
    {
        var publisher = new RecordingPublisher();
        var metrics = new SpeedListenerMetrics(TimeProvider.System);
        var processor = CreateProcessor(publisher, metrics: metrics);
        var channel = Channel.CreateUnbounded<SpeedEvent>();
        channel.Writer.TryWrite(Event("unknown", DateTime.UtcNow, 30));
        channel.Writer.Complete();

        await processor.ProcessAsync(channel.Reader, CancellationToken.None);

        Assert.Empty(publisher.Batches);
        Assert.Equal(1, metrics.Unknown);
        Assert.Equal(0, processor.InFlightEventCount);
    }

    [Fact]
    public async Task ProcessAsync_ShutdownBacklog_UsesShutdownAttemptBudgetForEveryBatch()
    {
        var publisher = new RecordingPublisher();
        var processor = CreateProcessor(publisher, batchSize: 10);
        var channel = Channel.CreateUnbounded<SpeedEvent>();
        for (var index = 0; index < 25; index++)
            channel.Writer.TryWrite(Event("502620", DateTime.UtcNow.AddSeconds(index), 30));
        channel.Writer.Complete();
        using var shutdown = new CancellationTokenSource();
        shutdown.Cancel();
        await processor.ProcessAsync(channel.Reader, CancellationToken.None, shutdown.Token);
        Assert.Equal(3, publisher.Batches.Count);
        Assert.All(publisher.AttemptBudgets, attempts => Assert.Equal(1, attempts));
        Assert.Equal(25, publisher.Batches.Sum(batch => batch.Sum(envelope => envelope.Items.Count)));
    }

    [Fact]
    public async Task ProcessAsync_PublisherFailure_PropagatesAndRetainsInFlightCount()
    {
        var expected = new InvalidOperationException("write failed");
        var publisher = new RecordingPublisher { Exception = expected };
        var processor = CreateProcessor(publisher);
        var channel = Channel.CreateUnbounded<SpeedEvent>();
        channel.Writer.TryWrite(Event("502620", DateTime.UtcNow, 30));
        channel.Writer.Complete();

        var actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => processor.ProcessAsync(channel.Reader, CancellationToken.None));

        Assert.Same(expected, actual);
        Assert.Equal(1, processor.InFlightEventCount);
    }

    [Theory]
    [InlineData(LogLevel.Debug, 4)]
    [InlineData(LogLevel.Information, 0)]
    [InlineData(LogLevel.Warning, 0)]
    public async Task DetectorDiagnostics_DistinguishUnknownFromScottSites_OnlyAtDebug(LogLevel level, int expectedRecords)
    {
        var logger = new DiagnosticLogger(level);
        var metrics = new SpeedListenerMetrics(TimeProvider.System);
        var publisher = new RecordingPublisher();
        var processor = CreateProcessor(publisher, metrics: metrics, logger: logger);
        var channel = Channel.CreateUnbounded<SpeedEvent>();
        foreach (var id in new[] { "999920", "999920", "999922", "502620", "527148" })
            channel.Writer.TryWrite(Event(id, DateTime.UtcNow, 30));
        channel.Writer.Complete();
        await processor.ProcessAsync(channel.Reader, CancellationToken.None);
        Assert.Equal(3, metrics.Unknown);
        Assert.Equal(2, Assert.Single(publisher.Batches).Count);
        Assert.Equal(expectedRecords, logger.Records.Count);
        if (level == LogLevel.Debug)
        {
            var unknown = logger.Records.Where(record => record.Id == 3031).ToArray();
            Assert.Equal(new[] { "999920", "999922" }, unknown.Select(record => record.Data["detectorId"]));
            Assert.Equal(new long[] { 2, 1 }, unknown.Select(record => (long)record.Data["eventCount"]!));
            Assert.All(unknown, record => Assert.Equal("9999", record.Data["locationIdentifier"]));
            Assert.Equal(new[] { "502620", "527148" }, logger.Records.Where(record => record.Id == 3032)
                .Select(record => record.Data["detectorId"]));
        }
    }

    private sealed class DiagnosticLogger(LogLevel minimum) : ILogger<SpeedEventBatchProcessor>
    {
        public List<(int Id, Dictionary<string, object?> Data)> Records { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= minimum;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (id.Id is 3031 or 3032 && state is IEnumerable<KeyValuePair<string, object?>> pairs)
                Records.Add((id.Id, pairs.ToDictionary(pair => pair.Key, pair => pair.Value)));
        }
    }

    private static SpeedEventBatchProcessor CreateProcessor(
        RecordingPublisher publisher,
        int batchSize = 10,
        TimeSpan? flushInterval = null,
        SpeedListenerMetrics? metrics = null,
        ILogger<SpeedEventBatchProcessor>? logger = null)
    {
        var configuration = Options.Create(new SpeedListenerConfiguration
        {
            BatchSize = batchSize,
            FlushInterval = flushInterval ?? TimeSpan.FromMinutes(1),
            ArchiveParallelism = 1
        });
        return new SpeedEventBatchProcessor(
            new StubMappingProvider(), publisher, configuration, TimeProvider.System,
            metrics ?? new SpeedListenerMetrics(TimeProvider.System),
            logger ?? NullLogger<SpeedEventBatchProcessor>.Instance);
    }

    private static SpeedEvent Event(string detectorId, DateTime timestamp, int mph) => new()
    {
        DetectorId = detectorId,
        Timestamp = timestamp,
        Mph = mph,
        Kph = (int)(mph * 1.609)
    };

    private sealed class StubMappingProvider : IDeviceMappingProvider
    {
        private static readonly IReadOnlyDictionary<string, DeviceMapping> Mappings =
            new Dictionary<string, DeviceMapping>(StringComparer.OrdinalIgnoreCase)
            {
                ["5026"] = new(1, "5026"),
                ["5271"] = new(2, "5271")
            };

        public Task<IReadOnlyDictionary<string, DeviceMapping>> GetMappingsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Mappings);
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class RecordingPublisher : IEventPublisher<EventBatchEnvelope>
    {
        public List<IReadOnlyList<EventBatchEnvelope>> Batches { get; } = [];
        public List<int?> AttemptBudgets { get; } = [];
        public TaskCompletionSource Published { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Exception { get; init; }

        public Task PublishAsync(EventBatchEnvelope message, CancellationToken cancellationToken = default) =>
            PublishAsync([message], 1, cancellationToken);

        public Task PublishAsync(IReadOnlyList<EventBatchEnvelope> batch, int parallelism,
            CancellationToken cancellationToken = default, int? maxAttempts = null)
        {
            Batches.Add(batch);
            AttemptBudgets.Add(maxAttempts);
            Published.TrySetResult();
            return Exception is null ? Task.CompletedTask : Task.FromException(Exception);
        }
    }
}
