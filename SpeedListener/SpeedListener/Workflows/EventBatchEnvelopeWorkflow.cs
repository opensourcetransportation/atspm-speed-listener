using Microsoft.Extensions.DependencyInjection;
using SpeedListener.Publishing;
using SpeedListener.WorkflowSteps;
using System.Threading.Tasks.Dataflow;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Extensions;
using Utah.Udot.Atspm.Repositories.EventLogRepositories;

namespace SpeedListener.Workflows;

/// <summary>
/// TPL Dataflow workflow that archives envelopes in parallel and persists compressed logs through one writer.
/// </summary>
public sealed class EventBatchEnvelopeWorkflow
{
    /// <summary>Creates the local workflow without requiring changes to the packaged ATSPM workflow types.</summary>
    public EventBatchEnvelopeWorkflow(
        IServiceScopeFactory scopeFactory,
        int parallelProcesses = 50,
        CancellationToken cancellationToken = default)
    {
        Archive = new TransformManyBlock<EventBatchEnvelope, CompressedEventLogBase>(
            envelope => ArchiveEnvelopeDataEvents.Archive(envelope, cancellationToken),
            new ExecutionDataflowBlockOptions
            {
                MaxDegreeOfParallelism = parallelProcesses,
                CancellationToken = cancellationToken
            });

        Save = new ActionBlock<CompressedEventLogBase>(async compressed =>
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IEventLogRepository>();
            await repository.Upsert(compressed);
        }, new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 1,
            EnsureOrdered = true,
            CancellationToken = cancellationToken
        });

        Archive.LinkTo(Save, new DataflowLinkOptions { PropagateCompletion = true });
    }

    /// <summary>Gets the parallel envelope archive block.</summary>
    public TransformManyBlock<EventBatchEnvelope, CompressedEventLogBase> Archive { get; }

    /// <summary>Gets the single-writer persistence block.</summary>
    public ActionBlock<CompressedEventLogBase> Save { get; }

    /// <summary>Sends an envelope into the workflow.</summary>
    public Task<bool> SendAsync(EventBatchEnvelope envelope, CancellationToken cancellationToken = default) =>
        Archive.SendAsync(envelope, cancellationToken);

    /// <summary>Signals that no additional envelopes will be sent.</summary>
    public void Complete() => Archive.Complete();

    /// <summary>Completes successfully only after every accepted envelope has been persisted.</summary>
    public Task Completion => Save.Completion;
}
