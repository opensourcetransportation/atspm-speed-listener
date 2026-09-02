#pragma warning disable CS1591
namespace SpeedListener.Services;

/// <summary>Thread-safe in-process counters used by structured summary logging.</summary>
public sealed class SpeedListenerMetrics(TimeProvider timeProvider)
{
    private long _received, _parsed, _rejected, _unknown, _dropped;
    private long _batchesPublished, _envelopesPublished, _publishLatencyTicks;
    private long _retries, _publishFailures, _poisonBatches, _mappingRefreshFailures;
    private long _mappingRefreshedAtTicks;

    public long Received => Interlocked.Read(ref _received);
    public long Parsed => Interlocked.Read(ref _parsed);
    public long Rejected => Interlocked.Read(ref _rejected);
    public long Unknown => Interlocked.Read(ref _unknown);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long BatchesPublished => Interlocked.Read(ref _batchesPublished);
    public long EnvelopesPublished => Interlocked.Read(ref _envelopesPublished);
    public double AveragePublishLatencyMilliseconds => BatchesPublished == 0
        ? 0
        : Interlocked.Read(ref _publishLatencyTicks) / (double)TimeSpan.TicksPerMillisecond / BatchesPublished;
    public long Retries => Interlocked.Read(ref _retries);
    public long PublishFailures => Interlocked.Read(ref _publishFailures);
    public long PoisonBatches => Interlocked.Read(ref _poisonBatches);
    public long MappingRefreshFailures => Interlocked.Read(ref _mappingRefreshFailures);
    public TimeSpan? MappingAge
    {
        get
        {
            var ticks = Interlocked.Read(ref _mappingRefreshedAtTicks);
            return ticks == 0 ? null : timeProvider.GetUtcNow() - new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }

    public void RecordReceived() => Interlocked.Increment(ref _received);
    public void RecordParsed() => Interlocked.Increment(ref _parsed);
    public void RecordRejected() => Interlocked.Increment(ref _rejected);
    public void RecordUnknown(long count) => Interlocked.Add(ref _unknown, count);
    public void RecordDropped() => Interlocked.Increment(ref _dropped);
    public void RecordPublished(int envelopes, TimeSpan latency)
    {
        Interlocked.Increment(ref _batchesPublished);
        Interlocked.Add(ref _envelopesPublished, envelopes);
        Interlocked.Add(ref _publishLatencyTicks, latency.Ticks);
    }
    public void RecordRetry() => Interlocked.Increment(ref _retries);
    public void RecordPublishFailure() => Interlocked.Increment(ref _publishFailures);
    public void RecordPoisonBatch() => Interlocked.Increment(ref _poisonBatches);
    public void RecordMappingRefresh() => Interlocked.Exchange(ref _mappingRefreshedAtTicks, timeProvider.GetUtcNow().UtcTicks);
    public void RecordMappingRefreshFailure() => Interlocked.Increment(ref _mappingRefreshFailures);
}
#pragma warning restore CS1591
