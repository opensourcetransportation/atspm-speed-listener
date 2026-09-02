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
    ILogger<SpeedListenerBackgroundService> logger) : BackgroundService
{
    private long _received;
    private long _rejected;
    private long _dropped;

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
            Interlocked.Increment(ref _received);
            var result = parser.Parse(datagram);
            if (!result.IsSuccess)
            {
                Interlocked.Increment(ref _rejected);
                logger.LogWarning("Rejected speed packet from {RemoteEndPoint}: {Reason}",
                    datagram.RemoteEndPoint, result.Error);
            }
            else if (!channel.Writer.TryWrite(result.Event!))
            {
                Interlocked.Increment(ref _dropped);
                logger.LogWarning("Speed-event channel is full; dropping newest event for detector {DetectorId}",
                    result.Event!.DetectorId);
            }

            return ValueTask.CompletedTask;
        }, receiveCancellation.Token);
        var consumer = batchProcessor.ProcessAsync(channel.Reader, processingCancellation.Token);

        logger.LogInformation("Speed listener started on UDP port {Port}", options.Value.UdpPort);
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
                var remaining = channel.Reader.CanCount ? channel.Reader.Count : -1;
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
            _received, _rejected, _dropped);
    }
}
