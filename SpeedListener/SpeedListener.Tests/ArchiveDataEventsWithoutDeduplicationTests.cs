using SpeedListener.WorkflowSteps;
using SpeedListener.Publishing;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Tests;

public sealed class ArchiveDataEventsWithoutDeduplicationTests
{
    [Fact]
    public void EnvelopePreservesIdenticalOccurrences()
    {
        var first = Event("5026", new DateTime(2026, 10, 13, 12, 30, 0));
        var equal = Event("5026", first.Timestamp);
        Assert.Equal(first, equal);
        var stored = Assert.Single(ArchiveDataEventsWithoutDeduplication.ArchiveEnvelope(
            Envelope(7, "5026", first, first, equal)));
        Assert.IsType<CompressedEventLogs<SpeedEvent>>(stored);
        Assert.Equal(3, stored.Data.Count());
        Assert.Equal(7, stored.DeviceId);
        Assert.Equal("5026", stored.LocationIdentifier);
        Assert.Equal(typeof(SpeedEvent), stored.DataType);
        Assert.Equal(3, stored.Data.Cast<SpeedEvent>().Count(item => item.DetectorId == "502620"));
    }

    [Fact]
    public void ArchiveKeepsLocationHourAndDeviceGrouping()
    {
        var time = new DateTime(2026, 10, 13, 12, 30, 0);
        var envelopes = new[] {
            Envelope(7, "5026", Event("5026", time), Event("5026", time), Event("5026", time.AddHours(1))),
            Envelope(8, "5026", Event("5026", time)),
            Envelope(7, "5271", Event("5271", time))
        };
        var rows = envelopes.SelectMany(envelope =>
            ArchiveDataEventsWithoutDeduplication.ArchiveEnvelope(envelope)).ToArray();
        Assert.Equal(4, rows.Length);
        Assert.Equal(5, rows.Sum(row => row.Data.Count()));
        Assert.Equal(2, rows.Single(row => row.DeviceId == 7 && row.LocationIdentifier == "5026"
            && row.Start.Hour == 12).Data.Count());
        Assert.All(rows, row => Assert.Equal(typeof(SpeedEvent), row.DataType));
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
            ArchiveDataEventsWithoutDeduplication.ArchiveEnvelope(Envelope(7, "5026"), cancellation.Token).ToList());
    }

    private static EventBatchEnvelope Envelope(int deviceId, string location, params SpeedEvent[] items) => new() {
        DeviceId = deviceId, LocationIdentifier = location, DataType = nameof(SpeedEvent),
        Start = items.FirstOrDefault()?.Timestamp ?? default,
        End = items.LastOrDefault()?.Timestamp ?? default, Items = items
    };

    private static SpeedEvent Event(string location, DateTime time) => new() {
        LocationIdentifier = location, DetectorId = "502620", Timestamp = time, Mph = 30, Kph = 48
    };
}
