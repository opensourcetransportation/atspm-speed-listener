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

using SpeedListener.Publishing;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Data.Models.EventLogModels;
using Utah.Udot.NetStandardToolkit.Common;

namespace SpeedListener.WorkflowSteps;

/// <summary>
/// Speed-envelope adaptation of ATSPM's ArchiveDataEvents that preserves every
/// occurrence, using packaged hourly rows and bypassing the generic HashSet accessor.
/// </summary>
public static class ArchiveDataEventsWithoutDeduplication
{
    /// <summary>Archives an envelope into typed hourly speed rows without deduplication.</summary>
    public static IEnumerable<CompressedEventLogBase> ArchiveEnvelope(
        EventBatchEnvelope envelope, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var item in envelope.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            item.LocationIdentifier = envelope.LocationIdentifier;
        }
        var groups = envelope.Items.GroupBy(item =>
            (item.Timestamp.Year, item.Timestamp.Month, item.Timestamp.Day, item.Timestamp.Hour));
        foreach (var group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var list = new List<SpeedEvent>(group.Count());
            foreach (var item in group)
            {
                cancellationToken.ThrowIfCancellationRequested();
                list.Add(item);
            }
            var timeline = new Timeline<StartEndRange>(list, TimeSpan.FromHours(1));
            var row = new CompressedEventLogs<SpeedEvent>
            {
                LocationIdentifier = envelope.LocationIdentifier,
                Start = timeline.Start,
                End = timeline.End,
                DataType = typeof(SpeedEvent),
                DeviceId = envelope.DeviceId
            };
            // The packaged generic accessor calls ToHashSet. Assign through the
            // base accessor to retain the concrete List<SpeedEvent> and every occurrence.
            ((CompressedEventLogBase)row).Data = list;
            yield return row;
        }
    }
}
