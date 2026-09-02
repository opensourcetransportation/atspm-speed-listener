using Newtonsoft.Json.Linq;
using SpeedListener.Publishing;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Dataflow;
using Utah.Udot.Atspm.Data.Interfaces;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Data.Models.EventLogModels;
using Utah.Udot.NetStandardToolkit.Common;
using Utah.Udot.NetStandardToolkit.Workflows;

namespace SpeedListener.WorkflowSteps;

/// <summary>Converts envelopes into hourly compressed speed-event logs.</summary>
public sealed class ArchiveEnvelopeDataEvents(ExecutionDataflowBlockOptions? options = null)
    : TransformManyProcessStepBaseAsync<EventBatchEnvelope, CompressedEventLogBase>(
        options ?? new ExecutionDataflowBlockOptions())
{
    /// <inheritdoc/>
    protected override async IAsyncEnumerable<CompressedEventLogBase> Process(
        EventBatchEnvelope envelope,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var rawEvents = ((JArray)envelope.Items).ToObject<List<SpeedEvent>>() ?? [];
        rawEvents.ForEach(speedEvent => speedEvent.LocationIdentifier = envelope.LocationIdentifier);

        var groups = rawEvents.GroupBy(speedEvent => (
            speedEvent.LocationIdentifier,
            speedEvent.Timestamp.Year,
            speedEvent.Timestamp.Month,
            speedEvent.Timestamp.Day,
            speedEvent.Timestamp.Hour,
            DeviceId: envelope.DeviceId,
            Type: speedEvent.GetType()));

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            dynamic list = Activator.CreateInstance(typeof(List<>).MakeGenericType(group.Key.Type))!;
            foreach (var speedEvent in group)
                ((IList)list).Add(speedEvent);

            var timeline = new Timeline<StartEndRange>(list, TimeSpan.FromHours(1));
            dynamic compressed = Activator.CreateInstance(
                typeof(CompressedEventLogs<>).MakeGenericType(group.Key.Type))!;
            compressed.LocationIdentifier = group.Key.LocationIdentifier;
            compressed.Start = timeline.Start;
            compressed.End = timeline.End;
            compressed.DataType = group.Key.Type;
            compressed.DeviceId = group.Key.DeviceId;
            compressed.Data = list;
            yield return compressed;
        }

        await Task.CompletedTask;
    }
}
