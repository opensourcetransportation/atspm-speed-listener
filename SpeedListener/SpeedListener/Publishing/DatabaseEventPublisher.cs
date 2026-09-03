using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
using SpeedListener.Services;
using SpeedListener.Workflows;
using System.Collections.Concurrent;
using System.Diagnostics;

namespace SpeedListener.Publishing;

/// <summary>Archives speed-event envelopes directly to the ATSPM event-log database.</summary>
public sealed class DatabaseEventPublisher(
    IServiceScopeFactory scopeFactory,
    IOptions<SpeedListenerConfiguration> options,
    SpeedListenerMetrics metrics,
    ILogger<DatabaseEventPublisher> logger) : IEventPublisher<EventBatchEnvelope>
{
    private readonly ConcurrentDictionary<int, int> _consecutiveDeviceDrops = new();

    /// <inheritdoc/>
    public Task PublishAsync(EventBatchEnvelope message, CancellationToken cancellationToken = default) =>
        PublishAsync([message], 1, cancellationToken);

    /// <inheritdoc/>
    public async Task PublishAsync(IReadOnlyList<EventBatchEnvelope> batch, int parallelism,
        CancellationToken cancellationToken = default, int? maxAttempts = null)
    {
        if (batch.Count == 0) return;
        var attemptBudget = maxAttempts ?? options.Value.MaxWriteAttempts;

        try
        {
            await PublishWithRetryAsync(batch, parallelism, attemptBudget, cancellationToken);
            foreach (var envelope in batch) _consecutiveDeviceDrops.TryRemove(envelope.DeviceId, out _);
        }
        catch (Exception ex) when (DatabaseFailureClassifier.Classify(ex) == DatabaseFailureKind.BatchData)
        {
            if (batch.Count == 1)
            {
                DropPoisonBatch(batch[0], ex);
                return;
            }

            logger.LogWarning(ex,
                "Database rejected a batch of {EnvelopeCount} envelopes; isolating the device-attributable failure",
                batch.Count);
            foreach (var envelope in batch)
            {
                try
                {
                    await PublishWithRetryAsync([envelope], parallelism, attemptBudget, cancellationToken);
                    _consecutiveDeviceDrops.TryRemove(envelope.DeviceId, out _);
                }
                catch (Exception isolated) when (DatabaseFailureClassifier.Classify(isolated) == DatabaseFailureKind.BatchData)
                {
                    DropPoisonBatch(envelope, isolated);
                }
            }
        }
    }

    private async Task PublishWithRetryAsync(IReadOnlyList<EventBatchEnvelope> batch, int parallelism,
        int maxAttempts, CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Value.WriteTimeout);
            var started = Stopwatch.GetTimestamp();
            try
            {
                await ExecuteWorkflowAsync(batch, parallelism, timeout.Token);
                var latency = Stopwatch.GetElapsedTime(started);
                metrics.RecordPublished(batch.Count, latency);
                logger.LogInformation("Archived {Count} speed-event envelopes in {ElapsedMilliseconds} ms",
                    batch.Count, latency.TotalMilliseconds);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsRetryable(ex, timeout, cancellationToken))
            {
                metrics.RecordRetry();
                var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 100));
                logger.LogWarning(ex, "Transient database write failure on attempt {Attempt}; retrying in {Delay}",
                    attempt, delay);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                metrics.RecordPublishFailure();
                logger.LogError(ex,
                    "Database write failed with classification {Classification} for {Count} envelopes on attempt {Attempt}",
                    ClassifyAttempt(ex, timeout, cancellationToken), batch.Count, attempt);
                throw;
            }
        }
    }

    private async Task ExecuteWorkflowAsync(IReadOnlyList<EventBatchEnvelope> batch, int parallelism,
        CancellationToken cancellationToken)
    {
        var workflow = new EventBatchEnvelopeWorkflow(scopeFactory, parallelism, cancellationToken);
        try
        {
            foreach (var envelope in batch)
            {
                if (!await workflow.SendAsync(envelope, cancellationToken))
                {
                    // A block declines only after completion, cancellation, or fault. Awaiting completion preserves
                    // the original provider/cancellation exception instead of replacing it with a synthetic fault.
                    await workflow.Completion;
                    throw new InvalidOperationException("The TPL workflow completed without accepting an envelope.");
                }
            }

            workflow.Complete();
            await workflow.Completion;
        }
        catch
        {
            workflow.Complete();
            try { await workflow.Completion; } catch { }
            throw;
        }
    }

    private static bool IsRetryable(Exception exception, CancellationTokenSource timeout,
        CancellationToken callerCancellation) =>
        ClassifyAttempt(exception, timeout, callerCancellation) == DatabaseFailureKind.Transient;

    private static DatabaseFailureKind ClassifyAttempt(Exception exception, CancellationTokenSource timeout,
        CancellationToken callerCancellation) =>
        exception is OperationCanceledException && timeout.IsCancellationRequested && !callerCancellation.IsCancellationRequested
            ? DatabaseFailureKind.Transient
            : DatabaseFailureClassifier.Classify(exception);

    private void DropPoisonBatch(EventBatchEnvelope envelope, Exception exception)
    {
        metrics.RecordPoisonBatch();
        var drops = _consecutiveDeviceDrops.AddOrUpdate(envelope.DeviceId, 1, static (_, count) => count + 1);
        logger.LogError(exception,
            "Dropped poison speed-event envelope for device {DeviceId}, location {LocationIdentifier}; consecutive drops {ConsecutiveDrops}",
            envelope.DeviceId, envelope.LocationIdentifier, drops);

        if (drops >= options.Value.PoisonDeviceFailureThreshold)
            throw new PoisonDeviceException(envelope.DeviceId, drops, exception);
    }
}
