using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
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
        RecordingPublisher publisher)
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
        var metrics = new SpeedListenerMetrics(TimeProvider.System);
        var processor = new SpeedEventBatchProcessor(
            mappings,
            publisher,
            options,
            TimeProvider.System,
            metrics,
            NullLogger<SpeedEventBatchProcessor>.Instance);

        return new SpeedListenerBackgroundService(
            receiver,
            new SuccessfulParser(),
            mappings,
            processor,
            options,
            metrics,
            NullLogger<SpeedListenerBackgroundService>.Instance);
    }

    private sealed class ControlledReceiver : IUdpDatagramReceiver
    {
        public TaskCompletionSource DatagramDelivered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Exception? Failure { get; init; }

        public async Task ReceiveAsync(
            Func<UdpDatagram, CancellationToken, ValueTask> onDatagram,
            CancellationToken cancellationToken)
        {
            await onDatagram(new UdpDatagram(
                new byte[16],
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
                DetectorId = "D7",
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
                ["D7"] = new(7, "L7")
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
