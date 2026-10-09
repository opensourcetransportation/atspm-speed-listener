using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedListener.BackgroundServices;
using SpeedListener.Configuration;
using SpeedListener.Parsing;
using SpeedListener.Publishing;
using SpeedListener.Receivers;
using SpeedListener.Services;
using System.Net;
using System.Threading.Channels;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Tests;

public sealed class SpeedListenerBackgroundServiceTests
{
    [Theory]
    [InlineData(LogLevel.Debug, 1000)]
    [InlineData(LogLevel.Information, 0)]
    [InlineData(LogLevel.Warning, 0)]
    [InlineData(LogLevel.Error, 0)]
    public async Task RejectedPacketFlood_KeepsEveryDebugRecordAndNoNormalLevelPayloads(LogLevel level, int expectedRecords)
    {
        var receiver = new ControlledReceiver
        {
            Payload = System.Text.Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("XS~\r\r", 1000)))
        };
        var metrics = new SpeedListenerMetrics(TimeProvider.System);
        var logger = new RecordingLogger(level);
        var service = CreateService(receiver, new StubMappingProvider(), new RecordingPublisher(),
            new SpeedPacketParser(), metrics, logger);
        await service.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await receiver.DatagramDelivered.Task.WaitAsync(timeout.Token);
        await service.StopAsync(timeout.Token);
        Assert.Equal(1000, metrics.Rejected);
        Assert.Equal(expectedRecords, logger.Events.Count(id => id == 3002));
        Assert.Equal(expectedRecords, logger.RejectedPackets.Count);
        Assert.DoesNotContain(3020, logger.Events);
        Assert.All(logger.RejectedPackets, sample =>
        {
            Assert.Equal(5000, sample["datagramLength"]);
            Assert.Equal(128, ((string)sample["payloadHex"]!).Length);
            Assert.Equal(true, sample["truncated"]);
            Assert.Contains("Expected at least", (string)sample["reason"]!);
        });
    }

    [Fact]
    public async Task StopAsync_MultipleMessagesInDatagram_ArchivesEachAndCountsRejection()
    {
        var receiver = new ControlledReceiver
        {
            Payload = Convert.FromHexString("585329427E0D0D585329423530323632307E0D0D58532A443530323632327E0D0D")
        };
        var publisher = new RecordingPublisher();
        var metrics = new SpeedListenerMetrics(TimeProvider.System);
        var service = CreateService(receiver, new StubMappingProvider(), publisher, new SpeedPacketParser(), metrics);

        await service.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await receiver.DatagramDelivered.Task.WaitAsync(timeout.Token);
        await service.StopAsync(timeout.Token);

        Assert.Equal(1, metrics.Received);
        Assert.Equal(2, metrics.Parsed);
        Assert.Equal(1, metrics.Rejected);
        var envelope = Assert.Single(Assert.Single(publisher.Batches));
        Assert.Equal(2, envelope.Items.Count());
    }

    [Fact]
    public async Task StopAsync_AfterReceivingEvent_DrainsPartialBatchWithShutdownAttemptBudget()
    {
        var receiver = new ControlledReceiver();
        var mappings = new StubMappingProvider();
        var publisher = new RecordingPublisher();
        var service = CreateService(receiver, mappings, publisher);

        await service.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await receiver.DatagramDelivered.Task.WaitAsync(timeout.Token);
        await service.StopAsync(timeout.Token);

        Assert.Equal(1, mappings.RefreshCount);
        var envelope = Assert.Single(Assert.Single(publisher.Batches));
        Assert.Equal(7, envelope.DeviceId);
        Assert.Equal("L7", envelope.LocationIdentifier);
        Assert.Single(envelope.Items);
        Assert.Equal(1, Assert.Single(publisher.AttemptBudgets));
    }

    [Fact]
    public async Task ExecuteTask_WhenReceiverFails_PropagatesPipelineFailure()
    {
        var expected = new IOException("socket failed");
        var receiver = new ControlledReceiver { Failure = expected };
        var service = CreateService(receiver, new StubMappingProvider(), new RecordingPublisher());

        await service.StartAsync(CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await receiver.DatagramDelivered.Task.WaitAsync(timeout.Token);

        var actual = await Assert.ThrowsAsync<IOException>(
            () => service.ExecuteTask!.WaitAsync(timeout.Token));

        Assert.Same(expected, actual);
    }

    private static SpeedListenerBackgroundService CreateService(
        ControlledReceiver receiver,
        StubMappingProvider mappings,
        RecordingPublisher publisher,
        ISpeedPacketParser? parser = null,
        SpeedListenerMetrics? metrics = null,
        ILogger<SpeedListenerBackgroundService>? logger = null)
    {
        var options = Options.Create(new SpeedListenerConfiguration
        {
            ChannelCapacity = 10,
            BatchSize = 10,
            FlushInterval = TimeSpan.FromMinutes(1),
            ShutdownFlushTimeout = TimeSpan.FromSeconds(2),
            ShutdownMaxWriteAttempts = 1,
            ArchiveParallelism = 1,
            SummaryInterval = TimeSpan.FromHours(1)
        });
        metrics ??= new SpeedListenerMetrics(TimeProvider.System);
        var processor = new SpeedEventBatchProcessor(
            mappings,
            publisher,
            options,
            TimeProvider.System,
            metrics,
            NullLogger<SpeedEventBatchProcessor>.Instance);

        return new SpeedListenerBackgroundService(
            receiver,
            parser ?? new SuccessfulParser(),
            mappings,
            processor,
            options,
            metrics,
            logger ?? NullLogger<SpeedListenerBackgroundService>.Instance);
    }

    private sealed class RecordingLogger(LogLevel minimumLevel) : ILogger<SpeedListenerBackgroundService>
    {
        public System.Collections.Concurrent.ConcurrentQueue<int> Events { get; } = new();
        public System.Collections.Concurrent.ConcurrentQueue<Dictionary<string, object?>> RejectedPackets { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= minimumLevel;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Events.Enqueue(eventId.Id);
            if (eventId.Id == 3002 && state is IEnumerable<KeyValuePair<string, object?>> properties)
                RejectedPackets.Enqueue(properties.ToDictionary(pair => pair.Key, pair => pair.Value));
        }
    }

    private sealed class ControlledReceiver : IUdpDatagramReceiver
    {
        public TaskCompletionSource DatagramDelivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Failure { get; init; }
        public byte[] Payload { get; init; } = new byte[16];

        public async Task ReceiveAsync(
            Func<UdpDatagram, CancellationToken, ValueTask> onDatagram,
            CancellationToken cancellationToken)
        {
            await onDatagram(new UdpDatagram(
                Payload,
                new IPEndPoint(IPAddress.Loopback, 10088),
                DateTimeOffset.UtcNow), cancellationToken);
            DatagramDelivered.TrySetResult();
            if (Failure is not null) throw Failure;
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        public void Dispose()
        {
        }
    }

    private sealed class SuccessfulParser : ISpeedPacketParser
    {
        public SpeedPacketParseResult Parse(UdpDatagram datagram) =>
            SpeedPacketParseResult.Success(new SpeedEvent
            {
                DetectorId = "502620",
                Timestamp = datagram.ReceivedAt.UtcDateTime,
                Mph = 30,
                Kph = 48
            });
    }

    private sealed class StubMappingProvider : IDeviceMappingProvider
    {
        private static readonly IReadOnlyDictionary<string, DeviceMapping> Mappings =
            new Dictionary<string, DeviceMapping>(StringComparer.OrdinalIgnoreCase)
            {
                ["5026"] = new(7, "L7")
            };

        public int RefreshCount { get; private set; }

        public Task RefreshAsync(CancellationToken cancellationToken)
        {
            RefreshCount++;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyDictionary<string, DeviceMapping>> GetMappingsAsync(
            CancellationToken cancellationToken) => Task.FromResult(Mappings);
    }

    private sealed class RecordingPublisher : IEventPublisher<EventBatchEnvelope>
    {
        public List<IReadOnlyList<EventBatchEnvelope>> Batches { get; } = [];
        public List<int?> AttemptBudgets { get; } = [];

        public Task PublishAsync(EventBatchEnvelope message, CancellationToken cancellationToken = default) =>
            PublishAsync([message], 1, cancellationToken);

        public Task PublishAsync(
            IReadOnlyList<EventBatchEnvelope> batch,
            int parallelism,
            CancellationToken cancellationToken = default,
            int? maxAttempts = null)
        {
            Batches.Add(batch);
            AttemptBudgets.Add(maxAttempts);
            return Task.CompletedTask;
        }
    }
}
