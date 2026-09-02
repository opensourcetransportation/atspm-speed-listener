using Microsoft.Extensions.DependencyInjection;
using SpeedListener.Publishing;
using SpeedListener.WorkflowSteps;
using System.Threading.Tasks.Dataflow;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Data.Models.EventLogModels;
using Utah.Udot.ATSPM.Infrastructure.WorkflowSteps;
using Utah.Udot.NetStandardToolkit.Workflows;

namespace SpeedListener.Workflows;

/// <summary>Archives event envelopes and saves the resulting compressed event logs.</summary>
public sealed class EventBatchEnvelopeWorkflow(
    IServiceScopeFactory scopeFactory,
    int parallelProcesses = 50,
    CancellationToken cancellationToken = default)
    : WorkflowBase<EventBatchEnvelope, CompressedEventLogBase>
{
    private bool _initialized;
    private readonly object _initializationLock = new();

    /// <summary>Gets the envelope archive step.</summary>
    public ArchiveEnvelopeDataEvents Archive { get; private set; } = default!;
    /// <summary>Gets the packaged compressed-log persistence step.</summary>
    public SaveArchivedEventLogs Save { get; private set; } = default!;

    /// <inheritdoc/>
    public override Task Initialize()
    {
        lock (_initializationLock)
        {
            if (_initialized) return Task.CompletedTask;
            Steps = [];
            var options = new DataflowBlockOptions { CancellationToken = cancellationToken };
            Input = new BroadcastBlock<EventBatchEnvelope>(value => value, options);
            Output = new BufferBlock<CompressedEventLogBase>(options);
            InstantiateSteps();
            Steps.Add(Input);
            AddStepsToTracker();
            LinkSteps();
            _initialized = true;
            return Task.CompletedTask;
        }
    }

    /// <inheritdoc/>
    protected override void InstantiateSteps()
    {
        Archive = new ArchiveEnvelopeDataEvents(new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = parallelProcesses,
            CancellationToken = cancellationToken
        });
        Save = new SaveArchivedEventLogs(scopeFactory, new ExecutionDataflowBlockOptions
        {
            MaxDegreeOfParallelism = 1,
            CancellationToken = cancellationToken
        });
    }

    /// <inheritdoc/>
    protected override void AddStepsToTracker()
    {
        Steps.Add(Archive);
        Steps.Add(Save);
    }

    /// <inheritdoc/>
    protected override void LinkSteps()
    {
        Input.LinkTo(Archive, new DataflowLinkOptions { PropagateCompletion = true });
        Archive.LinkTo(Save, new DataflowLinkOptions { PropagateCompletion = true });
        Save.LinkTo(Output, new DataflowLinkOptions { PropagateCompletion = true });
    }
}
