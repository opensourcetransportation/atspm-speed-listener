using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;
using SpeedListener.Configuration;
using SpeedListener.Publishing;
using System.Threading.Channels;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Services;

/// <summary>Builds and persists size- or time-bounded batches of speed events.</summary>
public sealed class SpeedEventBatchProcessor(
    IDeviceMappingProvider mappingProvider,
    IEventPublisher<EventBatchEnvelope> publisher,
    IOptions<SpeedListenerConfiguration> options,
    TimeProvider timeProvider,
    SpeedListenerMetrics metrics,
    ILogger<SpeedEventBatchProcessor> logger) : ISpeedEventBatchProcessor
{
    private int _inFlightEventCount;

    /// <inheritdoc/>
    public int InFlightEventCount => Volatile.Read(ref _inFlightEventCount);

    /// <inheritdoc/>
    public async Task ProcessAsync(ChannelReader<SpeedEvent> reader, CancellationToken cancellationToken)
    {
        var batch = new List<SpeedEvent>(options.Value.BatchSize);
        DateTimeOffset? batchStarted = null;

        while (true)
        {
            if (batch.Count == 0)
            {
                if (!await reader.WaitToReadAsync(cancellationToken)) break;
                batchStarted = timeProvider.GetUtcNow();
            }

            while (batch.Count < options.Value.BatchSize && reader.TryRead(out var speedEvent))
                batch.Add(speedEvent);

            if (batch.Count >= options.Value.BatchSize)
            {
                await FlushAsync(batch, cancellationToken);
                batchStarted = null;
                continue;
            }

            if (reader.Completion.IsCompleted) break;

            var elapsed = timeProvider.GetUtcNow() - batchStarted!.Value;
            var remaining = options.Value.FlushInterval - elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                await FlushAsync(batch, cancellationToken);
                batchStarted = null;
                continue;
            }

            using var interval = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            interval.CancelAfter(remaining);
            try
            {
                if (!await reader.WaitToReadAsync(interval.Token)) break;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await FlushAsync(batch, cancellationToken);
                batchStarted = null;
            }
        }

        while (reader.TryRead(out var remainingEvent)) batch.Add(remainingEvent);
        await FlushAsync(batch, cancellationToken, options.Value.ShutdownMaxWriteAttempts);
    }

    private async Task FlushAsync(List<SpeedEvent> batch, CancellationToken cancellationToken, int? maxAttempts = null)
    {
        if (batch.Count == 0) return;
        Volatile.Write(ref _inFlightEventCount, batch.Count);
        var completed = false;

        try
        {
            var mappings = await mappingProvider.GetMappingsAsync(cancellationToken);
            var envelopes = new List<EventBatchEnvelope>();
            foreach (var group in batch.GroupBy(speedEvent => speedEvent.DetectorId, StringComparer.OrdinalIgnoreCase))
            {
                var detectorId = group.Key?.Trim() ?? string.Empty;
                if (!mappings.TryGetValue(detectorId, out var mapping))
                {
                    metrics.RecordUnknown(group.LongCount());
                    continue;
                }

                var events = group.ToList();
                envelopes.Add(new EventBatchEnvelope
                {
                    DataType = nameof(SpeedEvent),
                    Start = events.Min(speedEvent => speedEvent.Timestamp),
                    End = events.Max(speedEvent => speedEvent.Timestamp),
                    LocationIdentifier = mapping.LocationIdentifier,
                    DeviceId = mapping.DeviceId,
                    Items = JToken.FromObject(events)
                });
            }

            if (envelopes.Count > 0)
                await publisher.PublishAsync(envelopes, options.Value.ArchiveParallelism, cancellationToken, maxAttempts);

            logger.LogInformation("Processed batch of {EventCount} events into {EnvelopeCount} envelopes",
                batch.Count, envelopes.Count);
            batch.Clear();
            completed = true;
        }
        finally
        {
            if (completed) Volatile.Write(ref _inFlightEventCount, 0);
        }
    }
}
