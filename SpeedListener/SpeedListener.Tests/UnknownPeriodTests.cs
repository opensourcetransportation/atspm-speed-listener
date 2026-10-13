using SpeedListener.Services;

namespace SpeedListener.Tests;

public sealed class UnknownPeriodTests
{
    [Fact]
    public void SummaryCountersReset_AndDetectorDetailsAreBoundedWithoutLosingCounts()
    {
        var metrics = new SpeedListenerMetrics(TimeProvider.System);
        for (var i = 0; i < 1000; i++) metrics.RecordUnknown(2, i.ToString("D6"), "0000");
        metrics.RecordUnknown(3, "000000", "0000");
        var first = metrics.TakeUnknownPeriod();
        Assert.Equal(2003, first.Count);
        Assert.Equal(256, first.Details.Count);
        Assert.Equal(1488, first.AdditionalEventCount);
        Assert.Equal(5, first.Details[0].EventCount);
        Assert.Equal(2003, first.Details.Sum(detail => detail.EventCount) + first.AdditionalEventCount);
        Assert.Equal(0, metrics.TakeUnknownPeriod().Count);
        Assert.Equal(0, metrics.Unknown);
        metrics.RecordUnknown(7, "527132", "5271");
        var second = metrics.TakeUnknownPeriod();
        Assert.Equal(7, second.Count);
        Assert.Equal("527132", Assert.Single(second.Details).DetectorId);
    }

    [Fact]
    public async Task ConcurrentSnapshotsRetainEveryUnknownEventExactlyOnce()
    {
        var metrics = new SpeedListenerMetrics(TimeProvider.System);
        long logged = 0;
        var writer = Task.Run(() => { for (var i = 0; i < 10000; i++) metrics.RecordUnknown(1, "527132", "5271"); });
        while (!writer.IsCompleted) logged += metrics.TakeUnknownPeriod().Count;
        await writer;
        logged += metrics.TakeUnknownPeriod().Count;
        Assert.Equal(10000, logged);
    }
}
