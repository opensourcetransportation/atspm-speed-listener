using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
using SpeedListener.Parsing;
using SpeedListener.Receivers;
using SpeedListener.Services;
using System.Threading.Channels;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.BackgroundServices;

/// <summary>Receives, parses, batches, and archives speed sensor events.</summary>
public sealed class SpeedListenerBackgroundService(
    IUdpDatagramReceiver receiver,
    ISpeedPacketParser parser,
    IDeviceMappingProvider mappingProvider,
    ISpeedEventBatchProcessor batchProcessor,
    IOptions<SpeedListenerConfiguration> options,
    SpeedListenerMetrics metrics,
    ILogger<SpeedListenerBackgroundService> logger) : BackgroundService
{
    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await mappingProvider.RefreshAsync(stoppingToken);
        var channel = Channel.CreateBounded<SpeedEvent>(new BoundedChannelOptions(options.Value.ChannelCapacity)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait
        });

        using var receiveCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        using var processingCancellation = new CancellationTokenSource();
        var producer = receiver.ReceiveAsync((datagram, _) =>
        {
            metrics.RecordReceived();
            var result = parser.Parse(datagram);
            if (!result.IsSuccess)
            {
                metrics.RecordRejected();
                logger.LogDebug("Rejected speed packet from {RemoteEndPoint}: {Reason}",
                    datagram.RemoteEndPoint, result.Error);
            }
            else if (!channel.Writer.TryWrite(result.Event!))
            {
                metrics.RecordDropped();
            }
            else metrics.RecordParsed();

            return ValueTask.CompletedTask;
        }, receiveCancellation.Token);
        var consumer = batchProcessor.ProcessAsync(channel.Reader, processingCancellation.Token);

        logger.LogInformation("Speed listener started on UDP port {Port}", options.Value.UdpPort);
        using var summaryCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var summary = LogSummariesAsync(channel.Reader, summaryCancellation.Token);
        try
        {
        var firstCompleted = await Task.WhenAny(producer, consumer);

        if (firstCompleted == consumer && !producer.IsCompleted)
        {
            receiveCancellation.Cancel();
            processingCancellation.Cancel();
            channel.Writer.TryComplete(consumer.Exception);
            try { await producer; } catch (OperationCanceledException) { }
            await consumer;
            throw new InvalidOperationException("The speed-event consumer stopped unexpectedly.");
        }

        Exception? producerFailure = null;
        try
        {
            await producer;
        }
        catch (Exception ex)
        {
            producerFailure = ex;
        }
        finally
        {
            channel.Writer.TryComplete(producerFailure);
        }

        if (stoppingToken.IsCancellationRequested)
        {
            processingCancellation.CancelAfter(options.Value.ShutdownFlushTimeout);
            try
            {
                await consumer;
            }
            catch (OperationCanceledException) when (processingCancellation.IsCancellationRequested)
            {
                var queued = channel.Reader.CanCount ? channel.Reader.Count : -1;
                var remaining = queued < 0 ? -1 : queued + batchProcessor.InFlightEventCount;
                logger.LogError("Speed listener shutdown exceeded its drain deadline with {Remaining} queued events", remaining);
                throw new TimeoutException("The speed listener could not drain its event channel before shutdown.");
            }
        }
        else
        {
            await consumer;
        }

        if (producerFailure is not null)
            throw new InvalidOperationException("The UDP receiver failed.", producerFailure);
        if (!stoppingToken.IsCancellationRequested)
            throw new InvalidOperationException("The UDP receiver stopped unexpectedly.");

        logger.LogInformation(
            "Speed listener stopped. Received {Received}, rejected {Rejected}, and dropped {Dropped} packets",
            metrics.Received, metrics.Rejected, metrics.Dropped);
        }
        finally
        {
            summaryCancellation.Cancel();
            try { await summary; } catch (OperationCanceledException) { }
        }
    }

    private async Task LogSummariesAsync(ChannelReader<SpeedEvent> reader, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.Value.SummaryInterval);
        long previousRejected = 0, previousUnknown = 0, previousDropped = 0;
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var rejected = metrics.Rejected;
            var unknown = metrics.Unknown;
            var dropped = metrics.Dropped;
            logger.LogInformation(
                "SpeedListenerSummary Received={Received} Parsed={Parsed} Rejected={Rejected} Unknown={Unknown} ChannelDepth={ChannelDepth} Dropped={Dropped} BatchesPublished={BatchesPublished} EnvelopesPublished={EnvelopesPublished} PublishLatencyMs={PublishLatencyMs} Retries={Retries} PublishFailures={PublishFailures} PoisonBatches={PoisonBatches} MappingAgeSeconds={MappingAgeSeconds} MappingRefreshFailures={MappingRefreshFailures}",
                metrics.Received, metrics.Parsed, metrics.Rejected, metrics.Unknown,
                reader.CanCount ? reader.Count : -1, metrics.Dropped, metrics.BatchesPublished,
                metrics.EnvelopesPublished, metrics.AveragePublishLatencyMilliseconds, metrics.Retries,
                metrics.PublishFailures, metrics.PoisonBatches, metrics.MappingAge?.TotalSeconds ?? -1,
                metrics.MappingRefreshFailures);
            if (rejected > previousRejected || unknown > previousUnknown || dropped > previousDropped)
                logger.LogWarning(
                    "SpeedListenerLossSummary RejectedSinceLast={RejectedSinceLast} UnknownSinceLast={UnknownSinceLast} DroppedSinceLast={DroppedSinceLast}",
                    rejected - previousRejected, unknown - previousUnknown, dropped - previousDropped);
            previousRejected = rejected;
            previousUnknown = unknown;
            previousDropped = dropped;
        }
    }
}
