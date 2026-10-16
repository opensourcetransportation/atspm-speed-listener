using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
using SpeedListener.LogMessages;
using SpeedListener.Services;
using SpeedListener.Workflows;
using System.Collections.Concurrent;
using System.Diagnostics;
using Utah.Udot.Atspm.Data.Models;

namespace SpeedListener.Publishing;

/// <summary>Archives speed-event envelopes directly to the ATSPM event-log database.</summary>
public sealed class DatabaseEventPublisher(
    IServiceScopeFactory scopeFactory,
    IOptions<SpeedListenerConfiguration> options,
    SpeedListenerMetrics metrics,
    ILogger<DatabaseEventPublisher> logger) : IEventPublisher<EventBatchEnvelope>
{
    private readonly ConcurrentDictionary<int, int> _consecutiveDeviceDrops = new();
    private readonly SpeedListenerLogMessages _log = new(logger);

    /// <inheritdoc/>
    public Task PublishAsync(EventBatchEnvelope message, CancellationToken cancellationToken = default) =>
        PublishAsync([message], 1, cancellationToken);

    /// <inheritdoc/>
    public async Task PublishAsync(IReadOnlyList<EventBatchEnvelope> batch, int parallelism,
        CancellationToken cancellationToken = default, int? maxAttempts = null)
    {
        if (batch.Count == 0) return;
        var attemptBudget = maxAttempts ?? options.Value.MaxWriteAttempts;
        var saved = new ConcurrentDictionary<(EventBatchEnvelope Envelope, DateTime Hour), byte>();
        var conflicts = new ConcurrentDictionary<(EventBatchEnvelope Envelope, DateTime Hour), byte>();
        var rows = new ConcurrentDictionary<(EventBatchEnvelope Envelope, DateTime Hour), CompressedEventLogBase>();

        try
        {
            await PublishWithRetryAsync(batch, parallelism, attemptBudget, saved, rows, conflicts, cancellationToken);
            ResetCleanDevices(batch, conflicts, new HashSet<int>());
        }
        catch (Exception ex) when (DatabaseFailureClassifier.Classify(ex) == DatabaseFailureKind.BatchData)
        {
            if (batch.Count == 1)
            {
                DropPoisonBatch(batch[0], ex);
                return;
            }

            _log.BatchRejected(batch.Count, ex);
            PoisonDeviceException? thresholdFailure = null;
            var completed = new List<EventBatchEnvelope>();
            var failedDevices = new HashSet<int>();
            foreach (var envelope in batch)
            {
                try
                {
                    await PublishWithRetryAsync([envelope], parallelism, attemptBudget, saved, rows, conflicts, cancellationToken);
                    completed.Add(envelope);
                }
                catch (Exception isolated) when (DatabaseFailureClassifier.Classify(isolated) == DatabaseFailureKind.BatchData)
                {
                    failedDevices.Add(envelope.DeviceId);
                    try { DropPoisonBatch(envelope, isolated); }
                    catch (PoisonDeviceException threshold) { thresholdFailure ??= threshold; }
                }
            }
            ResetCleanDevices(completed, conflicts, failedDevices);
            if (thresholdFailure is not null) throw thresholdFailure;
        }
    }

    private async Task PublishWithRetryAsync(IReadOnlyList<EventBatchEnvelope> batch, int parallelism,
        int maxAttempts, ConcurrentDictionary<(EventBatchEnvelope Envelope, DateTime Hour), byte> saved,
        ConcurrentDictionary<(EventBatchEnvelope Envelope, DateTime Hour), CompressedEventLogBase> rows,
        ConcurrentDictionary<(EventBatchEnvelope Envelope, DateTime Hour), byte> conflicts,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Value.WriteTimeout);
            var started = Stopwatch.GetTimestamp();
            try
            {
                await ExecuteWorkflowAsync(batch, parallelism, saved, rows, conflicts, timeout.Token);
                var latency = Stopwatch.GetElapsedTime(started);
                var conflictedEnvelopes = conflicts.Keys.Select(key => key.Envelope).ToHashSet();
                var archivedCount = batch.Count(envelope => !conflictedEnvelopes.Contains(envelope));
                var committed = saved.Keys.Where(key => batch.Contains(key.Envelope) && !conflicts.ContainsKey(key)).ToArray();
                if (committed.Length > 0)
                {
                    metrics.RecordPublished(archivedCount, latency);
                    _log.EnvelopesArchived(archivedCount, committed.Length,
                        committed.Sum(key => rows[key].Data.LongCount()), latency.TotalMilliseconds);
                }
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < maxAttempts && IsRetryable(ex, timeout, cancellationToken))
            {
                metrics.RecordRetry();
                var delay = TimeSpan.FromMilliseconds(Math.Min(200 * Math.Pow(2, attempt - 1), 30_000) + Random.Shared.Next(0, 100));
                _log.DatabaseWriteRetry(attempt, delay, ex);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                metrics.RecordPublishFailure();
                _log.DatabaseWriteFailed(ClassifyAttempt(ex, timeout, cancellationToken), batch.Count, attempt, ex);
                throw;
            }
        }
    }

    private async Task ExecuteWorkflowAsync(IReadOnlyList<EventBatchEnvelope> batch, int parallelism,
        ConcurrentDictionary<(EventBatchEnvelope Envelope, DateTime Hour), byte> saved,
        ConcurrentDictionary<(EventBatchEnvelope Envelope, DateTime Hour), CompressedEventLogBase> rows,
        ConcurrentDictionary<(EventBatchEnvelope Envelope, DateTime Hour), byte> conflicts,
        CancellationToken cancellationToken)
    {
        var workflow = new EventBatchEnvelopeWorkflow(scopeFactory, parallelism, cancellationToken,
            options.Value.DatabaseWriteParallelism,
            (envelope, row) => saved.ContainsKey((envelope, row.Start)),
            (envelope, row) =>
            {
                var key = (envelope, row.Start);
                if (saved.TryAdd(key, 0) && !conflicts.ContainsKey(key))
                    metrics.RecordCommittedRow(row.Data.LongCount());
            },
            (envelope, row) => rows.GetOrAdd((envelope, row.Start), row),
            (envelope, row, conflict) =>
            {
                if (!conflicts.TryAdd((envelope, row.Start), 0)) return;
                var count = row.Data.LongCount();
                metrics.RecordWriteConflict(count);
                _log.EventLogWriteConflict(row.DeviceId, row.Start, count, conflict);
            });
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

    private void ResetCleanDevices(IEnumerable<EventBatchEnvelope> completed,
        ConcurrentDictionary<(EventBatchEnvelope Envelope, DateTime Hour), byte> conflicts, ISet<int> failedDevices)
    {
        var conflictedDevices = conflicts.Keys.Select(key => key.Envelope.DeviceId).ToHashSet();
        foreach (var deviceId in completed.Select(envelope => envelope.DeviceId).Distinct())
            if (!conflictedDevices.Contains(deviceId) && !failedDevices.Contains(deviceId))
                _consecutiveDeviceDrops.TryRemove(deviceId, out _);
    }

    private void DropPoisonBatch(EventBatchEnvelope envelope, Exception exception)
    {
        metrics.RecordPoisonBatch();
        var drops = _consecutiveDeviceDrops.AddOrUpdate(envelope.DeviceId, 1, static (_, count) => count + 1);
        _log.PoisonEnvelopeDropped(envelope.DeviceId, envelope.LocationIdentifier, drops, exception);

        if (drops >= options.Value.PoisonDeviceFailureThreshold)
            throw new PoisonDeviceException(envelope.DeviceId, drops, exception);
    }
}
