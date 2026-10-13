#region license
// Copyright 2026 Utah Departement of Transportation
// for Infrastructure - Utah.Udot.ATSPM.Infrastructure.WorkflowSteps/ArchiveDataEvents.cs
// 
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
#endregion

using System.Collections;
using System.Runtime.CompilerServices;
using System.Threading.Tasks.Dataflow;
using SpeedListener.Publishing;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Data.Models.EventLogModels;
using Utah.Udot.NetStandardToolkit.Common;
using Utah.Udot.NetStandardToolkit.Workflows;

namespace SpeedListener.WorkflowSteps;

/// <summary>
/// Local ATSPM ArchiveDataEvents equivalent that preserves every event occurrence.
/// Groups by location, calendar hour, device and event type, using the same
/// packaged row types and timeline; bypasses the generic Data HashSet accessor.
/// </summary>
public sealed class ArchiveDataEventsWithoutDeduplication
    : TransformManyProcessStepBaseAsync<Tuple<Device, EventLogModelBase>[], CompressedEventLogBase>
{
    /// <summary>Creates the ATSPM-compatible dataflow step.</summary>
    public ArchiveDataEventsWithoutDeduplication(ExecutionDataflowBlockOptions options = default!) : base(options) { }

    /// <inheritdoc/>
    protected override async IAsyncEnumerable<CompressedEventLogBase> Process(
        Tuple<Device, EventLogModelBase>[] input,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        foreach (var row in Archive(input, cancellationToken)) yield return row;
    }

    /// <summary>Archives ATSPM device/event batches without value-based deduplication.</summary>
    public static IEnumerable<CompressedEventLogBase> Archive(
        Tuple<Device, EventLogModelBase>[] input, CancellationToken cancellationToken = default) =>
        ArchiveCore(input, item => item.Item1.Id, item => item.Item2, cancellationToken);

    /// <summary>Adapts listener envelopes to the same archive operation without per-event tuple allocation.</summary>
    public static IEnumerable<CompressedEventLogBase> ArchiveEnvelope(
        EventBatchEnvelope envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var item in envelope.Items) item.LocationIdentifier = envelope.LocationIdentifier;
        return ArchiveCore(envelope.Items, _ => envelope.DeviceId, item => item, cancellationToken);
    }

    private static IEnumerable<CompressedEventLogBase> ArchiveCore<T>(
        IEnumerable<T> input, Func<T, int> deviceId, Func<T, EventLogModelBase> eventValue,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var groups = input.GroupBy(item =>
        {
            var value = eventValue(item);
            return (value.LocationIdentifier, value.Timestamp.Year, value.Timestamp.Month,
                value.Timestamp.Day, value.Timestamp.Hour, DeviceId: deviceId(item), EventType: value.GetType());
        });
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = (IList)Activator.CreateInstance(typeof(List<>).MakeGenericType(group.Key.EventType))!;
            foreach (var item in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                list.Add(eventValue(item));
            }
            // The concrete List<TEvent> is also required by ATSPM's compression binder.
            var timeline = new Timeline<StartEndRange>((dynamic)list, TimeSpan.FromHours(1));
            var row = (CompressedEventLogBase)Activator.CreateInstance(
                typeof(CompressedEventLogs<>).MakeGenericType(group.Key.EventType))!;
            row.LocationIdentifier = group.Key.LocationIdentifier;
            row.Start = timeline.Start;
            row.End = timeline.End;
            row.DataType = group.Key.EventType;
            row.DeviceId = group.Key.DeviceId;
            // This static base accessor assigns the List<TEvent> directly. The
            // packaged generic accessor would call ToHashSet and discard events.
            row.Data = (IEnumerable<EventLogModelBase>)list;
            yield return row;
        }
    }
}
