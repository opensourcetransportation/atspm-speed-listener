using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
using SpeedListener.Workflows;
using System.Data.Common;
using System.Threading.Tasks.Dataflow;

namespace SpeedListener.Publishing;

/// <summary>Archives speed-event envelopes directly to the ATSPM event-log database.</summary>
public sealed class DatabaseEventPublisher(
    IServiceScopeFactory scopeFactory,
    IOptions<SpeedListenerConfiguration> options,
    ILogger<DatabaseEventPublisher> logger) : IEventPublisher<EventBatchEnvelope>
{
    /// <inheritdoc/>
    public Task PublishAsync(EventBatchEnvelope message, CancellationToken cancellationToken = default) =>
        PublishAsync([message], 1, cancellationToken);

    /// <inheritdoc/>
    public async Task PublishAsync(
        IReadOnlyList<EventBatchEnvelope> batch,
        int parallelism,
        CancellationToken cancellationToken = default)
    {
        if (batch.Count == 0) return;

        for (var attempt = 1; attempt <= options.Value.MaxWriteAttempts; attempt++)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.Value.WriteTimeout);
            try
            {
                var workflow = new EventBatchEnvelopeWorkflow(scopeFactory, parallelism, timeout.Token);
                await workflow.Initialize();
                foreach (var envelope in batch)
                    await workflow.Input.SendAsync(envelope, timeout.Token);

                workflow.Input.Complete();
                await Task.WhenAll(workflow.Steps.Select(step => step.Completion));
                logger.LogInformation("Archived {Count} speed-event envelopes", batch.Count);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < options.Value.MaxWriteAttempts && IsTransient(ex))
            {
                var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1) + Random.Shared.Next(0, 100));
                logger.LogWarning(ex, "Database write attempt {Attempt} failed; retrying in {Delay}", attempt, delay);
                await Task.Delay(delay, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unable to archive {Count} speed-event envelopes", batch.Count);
                throw;
            }
        }
    }

    private static bool IsTransient(Exception exception) => exception switch
    {
        TimeoutException => true,
        OperationCanceledException => true,
        DbException => true,
        AggregateException aggregate => aggregate.Flatten().InnerExceptions.Any(IsTransient),
        _ when exception.InnerException is not null => IsTransient(exception.InnerException),
        _ => false
    };
}
