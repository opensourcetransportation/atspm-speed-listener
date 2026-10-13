using SpeedListener.WorkflowSteps;
using SpeedListener.Publishing;
using System.Threading.Tasks.Dataflow;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Tests;

public sealed class ArchiveDataEventsWithoutDeduplicationTests
{
    [Fact]
    public async Task AtspmCompatibleDataflowStepPreservesIdenticalOccurrences()
    {
        var step = new ArchiveDataEventsWithoutDeduplication();
        var device = new Device { Id = 7 };
        var first = Event("5026", new DateTime(2026, 10, 13, 12, 30, 0));
        var equal = Event("5026", first.Timestamp);
        Assert.Equal(first, equal);
        Assert.True(await step.SendAsync(new[] {
            Tuple.Create(device, (EventLogModelBase)first),
            Tuple.Create(device, (EventLogModelBase)first),
            Tuple.Create(device, (EventLogModelBase)equal)
        }));
        step.Complete();
        var rows = new List<CompressedEventLogBase>();
        await foreach (var row in step.ReceiveAllAsync()) rows.Add(row);
        await step.Completion;
        var stored = Assert.Single(rows);
        Assert.Equal(3, stored.Data.Count());
        Assert.Equal(7, stored.DeviceId);
        Assert.Equal("5026", stored.LocationIdentifier);
        Assert.Equal(typeof(SpeedEvent), stored.DataType);
        Assert.Equal(3, stored.Data.Cast<SpeedEvent>().Count(item => item.DetectorId == "502620"));
    }

    [Fact]
    public void ArchiveKeepsAtspmLocationHourDeviceAndTypeGrouping()
    {
        var time = new DateTime(2026, 10, 13, 12, 30, 0);
        var a = new Device { Id = 7 };
        var b = new Device { Id = 8 };
        var input = new[] {
            Tuple.Create(a, (EventLogModelBase)Event("5026", time)),
            Tuple.Create(a, (EventLogModelBase)Event("5026", time)),
            Tuple.Create(a, (EventLogModelBase)Event("5026", time.AddHours(1))),
            Tuple.Create(b, (EventLogModelBase)Event("5026", time)),
            Tuple.Create(a, (EventLogModelBase)Event("5271", time)),
            Tuple.Create(a, (EventLogModelBase)new IndianaEvent { LocationIdentifier = "5026", Timestamp = time })
        };
        var rows = ArchiveDataEventsWithoutDeduplication.Archive(input).ToArray();
        Assert.Equal(5, rows.Length);
        Assert.Equal(6, rows.Sum(row => row.Data.Count()));
        Assert.Equal(2, rows.Single(row => row.DeviceId == 7 && row.LocationIdentifier == "5026"
            && row.Start.Hour == 12 && row.DataType == typeof(SpeedEvent)).Data.Count());
        Assert.Single(rows, row => row.DataType == typeof(IndianaEvent));
    }

    [Fact]
    public void EnvelopeAdapterPreservesOrderMultiplicityAndAuthoritativeLocation()
    {
        var time = new DateTime(2026, 10, 13, 12, 30, 0);
        var first = Event("old", time);
        var next = Event("old", time); next.DetectorId = "502622";
        var rows = ArchiveDataEventsWithoutDeduplication.ArchiveEnvelope(new EventBatchEnvelope {
            DeviceId = 7, LocationIdentifier = "5026", DataType = nameof(SpeedEvent),
            Start = time, End = time, Items = new[] { first, first, next }
        });
        var row = Assert.Single(rows);
        Assert.Equal(new[] { "502620", "502620", "502622" }, row.Data.Cast<SpeedEvent>().Select(item => item.DetectorId));
        Assert.All(row.Data, item => Assert.Equal("5026", item.LocationIdentifier));
    }

    [Fact]
    public void ArchiveHonorsCancellationBeforeProducingRows()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            ArchiveDataEventsWithoutDeduplication.Archive(Array.Empty<Tuple<Device, EventLogModelBase>>(), cancellation.Token).ToList());
    }

    private static SpeedEvent Event(string location, DateTime time) => new() {
        LocationIdentifier = location, DetectorId = "502620", Timestamp = time, Mph = 30, Kph = 48
    };
}
