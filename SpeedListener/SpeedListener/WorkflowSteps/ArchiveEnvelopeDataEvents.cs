using SpeedListener.Publishing;
using Utah.Udot.Atspm.Data.Interfaces;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Data.Models.EventLogModels;
using Utah.Udot.NetStandardToolkit.Common;

namespace SpeedListener.WorkflowSteps;

/// <summary>Converts envelopes into hourly compressed speed-event logs.</summary>
public static class ArchiveEnvelopeDataEvents
{
    /// <summary>Archives one envelope into its hourly compressed event-log rows.</summary>
    public static IEnumerable<CompressedEventLogBase> Archive(
        EventBatchEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        var rawEvents = envelope.Items;
        foreach (var speedEvent in rawEvents) speedEvent.LocationIdentifier = envelope.LocationIdentifier;

        var groups = rawEvents.GroupBy(speedEvent => (
            speedEvent.LocationIdentifier,
            speedEvent.Timestamp.Year,
            speedEvent.Timestamp.Month,
            speedEvent.Timestamp.Day,
            speedEvent.Timestamp.Hour,
            DeviceId: envelope.DeviceId));

        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = group.ToList();
            var timeline = new Timeline<StartEndRange>(list, TimeSpan.FromHours(1));
            yield return new CompressedEventLogs<SpeedEvent>
            {
                LocationIdentifier = group.Key.LocationIdentifier,
                Start = timeline.Start, End = timeline.End,
                DataType = typeof(SpeedEvent), DeviceId = group.Key.DeviceId, Data = list
            };
        }
    }
}
