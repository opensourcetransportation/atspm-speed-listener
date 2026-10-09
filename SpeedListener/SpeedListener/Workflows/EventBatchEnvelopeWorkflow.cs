using Microsoft.Extensions.DependencyInjection;
using SpeedListener.Publishing;
using SpeedListener.WorkflowSteps;
using System.Threading.Tasks.Dataflow;
using System.Collections.Concurrent;
using Utah.Udot.Atspm.Data.Models;

namespace SpeedListener.Workflows;

/// <summary>
/// Archives envelopes in parallel and writes independent devices concurrently.
/// </summary>
public sealed class EventBatchEnvelopeWorkflow
{
    /// <summary>Creates the local workflow without requiring changes to the packaged ATSPM workflow types.</summary>
    public EventBatchEnvelopeWorkflow(
        IServiceScopeFactory scopeFactory,
        int parallelProcesses = 50,
        CancellationToken cancellationToken = default,
        int databaseWriteParallelism = 8,
        Func<EventBatchEnvelope, CompressedEventLogBase, bool>? alreadySaved = null,
        Action<EventBatchEnvelope, CompressedEventLogBase>? onSaved = null)
    {
        if (databaseWriteParallelism <= 0)
            throw new ArgumentOutOfRangeException(nameof(databaseWriteParallelism));
        var deviceLocks = new ConcurrentDictionary<int, SemaphoreSlim>();
        Archive = new TransformManyBlock<EventBatchEnvelope, ArchivedRow>(
            envelope => ArchiveEnvelopeDataEvents.Archive(envelope, cancellationToken)
                .Select(row => new ArchivedRow(envelope, row)),
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = parallelProcesses,
                CancellationToken = cancellationToken
            });

        Save = new ActionBlock<ArchivedRow>(async archived =>
        {
            var compressed = archived.Row;
            if (alreadySaved?.Invoke(archived.Envelope, compressed) == true) return;
            // Upsert is read/merge/write. Never overlap writes for the same device,
            // including repeated envelopes for one hourly row.
            var deviceLock = deviceLocks.GetOrAdd(compressed.DeviceId, _ => new SemaphoreSlim(1, 1));
            await deviceLock.WaitAsync(cancellationToken);
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var writer = scope.ServiceProvider.GetRequiredService<IEventLogWriter>();
                await writer.UpsertAsync(compressed, cancellationToken);
                onSaved?.Invoke(archived.Envelope, compressed);
            }
            finally
            {
                deviceLock.Release();
            }
        }, new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = databaseWriteParallelism,
            CancellationToken = cancellationToken
        });

        Archive.LinkTo(Save, new DataflowLinkOptions { PropagateCompletion = true });
    }

    /// <summary>Gets the parallel envelope archive block.</summary>
    public TransformManyBlock<EventBatchEnvelope, ArchivedRow> Archive { get; }

    /// <summary>Gets the persistence block, serialized per device.</summary>
    public ActionBlock<ArchivedRow> Save { get; }

    /// <summary>Retains the originating envelope for retry progress tracking.</summary>
    public sealed record ArchivedRow(EventBatchEnvelope Envelope, CompressedEventLogBase Row);

    /// <summary>Sends an envelope into the workflow.</summary>
    public Task<bool> SendAsync(EventBatchEnvelope envelope, CancellationToken cancellationToken = default) =>
        Archive.SendAsync(envelope, cancellationToken);

    /// <summary>Signals that no additional envelopes will be sent.</summary>
    public void Complete() => Archive.Complete();

    /// <summary>Completes successfully only after every accepted envelope has been persisted.</summary>
    public Task Completion => Save.Completion;
}
