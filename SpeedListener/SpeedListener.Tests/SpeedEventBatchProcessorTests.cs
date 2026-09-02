using Microsoft.Extensions.Logging.Abstractions;
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
    public async Task ProcessAsync_WhenChannelCompletes_PublishesPartialBatchGroupedByDevice()
    {
        var publisher = new RecordingPublisher();
        var processor = CreateProcessor(publisher);
        var channel = Channel.CreateUnbounded<SpeedEvent>();
        var first = new DateTime(2026, 9, 2, 18, 0, 0, DateTimeKind.Utc);
        channel.Writer.TryWrite(Event("D1", first, 30));
        channel.Writer.TryWrite(Event("D1", first.AddSeconds(1), 31));
        channel.Writer.TryWrite(Event("D2", first.AddSeconds(2), 32));
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
        channel.Writer.TryWrite(Event("D1", DateTime.UtcNow, 30));
        channel.Writer.TryWrite(Event("D2", DateTime.UtcNow, 31));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await publisher.Published.Task.WaitAsync(timeout.Token);
        channel.Writer.Complete();
        await processing;

        Assert.NotEmpty(publisher.Batches);
    }

    private static SpeedEventBatchProcessor CreateProcessor(RecordingPublisher publisher, int batchSize = 10)
    {
        var configuration = Options.Create(new SpeedListenerConfiguration
        {
            BatchSize = batchSize,
            FlushInterval = TimeSpan.FromMinutes(1),
            ArchiveParallelism = 1
        });
        return new SpeedEventBatchProcessor(
            new StubMappingProvider(), publisher, configuration, TimeProvider.System,
            new SpeedListenerMetrics(TimeProvider.System),
            NullLogger<SpeedEventBatchProcessor>.Instance);
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
                ["D1"] = new(1, "L1"),
                ["D2"] = new(2, "L2")
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

        public Task PublishAsync(EventBatchEnvelope message, CancellationToken cancellationToken = default) =>
            PublishAsync([message], 1, cancellationToken);

        public Task PublishAsync(IReadOnlyList<EventBatchEnvelope> batch, int parallelism,
            CancellationToken cancellationToken = default, int? maxAttempts = null)
        {
            Batches.Add(batch);
            AttemptBudgets.Add(maxAttempts);
            Published.TrySetResult();
            return Task.CompletedTask;
        }
    }
}
