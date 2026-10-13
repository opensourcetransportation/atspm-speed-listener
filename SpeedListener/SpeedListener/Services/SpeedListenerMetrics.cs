#pragma warning disable CS1591
namespace SpeedListener.Services;

/// <summary>Thread-safe in-process counters used by structured summary logging.</summary>
public sealed class SpeedListenerMetrics(TimeProvider timeProvider)
{
    private long _actuation, _unmappedSpeed;
    public long Actuation => Interlocked.Read(ref _actuation);
    public long UnmappedSpeed => Interlocked.Read(ref _unmappedSpeed);
    public void RecordActuation() => Interlocked.Increment(ref _actuation);
    public void RecordUnmappedSpeed() => Interlocked.Increment(ref _unmappedSpeed);

    internal const int MaxUnknownDetectorDetails = 256;
    private readonly object _unknownPeriodLock = new();
    private readonly Dictionary<string, UnknownDetectorDetail> _unknownDetails = new(StringComparer.Ordinal);
    private long _unknownPeriod, _unknownWithoutDetails;

    /// <summary>Consumes and resets unknown counts and details for the previous summary period atomically.</summary>
    public UnknownDetectorPeriod TakeUnknownPeriod()
    {
        lock (_unknownPeriodLock)
        {
            var snapshot = new UnknownDetectorPeriod(_unknownPeriod, _unknownDetails.Values.ToArray(), _unknownWithoutDetails);
            _unknownPeriod = 0;
            _unknownWithoutDetails = 0;
            _unknownDetails.Clear();
            return snapshot;
        }
    }

    private long _received, _parsed, _rejected, _dropped;
    private long _batchesPublished, _envelopesPublished, _publishLatencyTicks;
    private long _retries, _publishFailures, _poisonBatches, _mappingRefreshFailures;
    private long _rowsPublished, _eventsPublished;
    public long RowsPublished => Interlocked.Read(ref _rowsPublished);
    public long EventsPublished => Interlocked.Read(ref _eventsPublished);
    public void RecordCommittedRow(long eventCount)
    {
        Interlocked.Increment(ref _rowsPublished);
        Interlocked.Add(ref _eventsPublished, eventCount);
    }

    private long _mappingRefreshedAtTicks, _writeConflicts, _conflictEvents;
    public long WriteConflicts => Interlocked.Read(ref _writeConflicts);
    public long ConflictEvents => Interlocked.Read(ref _conflictEvents);
    public void RecordWriteConflict(long eventCount)
    {
        Interlocked.Increment(ref _writeConflicts);
        Interlocked.Add(ref _conflictEvents, eventCount);
    }

    public long Received => Interlocked.Read(ref _received);
    public long Parsed => Interlocked.Read(ref _parsed);
    public long Rejected => Interlocked.Read(ref _rejected);
    public long UnknownSinceLast { get { lock (_unknownPeriodLock) return _unknownPeriod; } }
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
    public void RecordUnknown(long count, string? detectorId = null, string? locationIdentifier = null)
    {
        lock (_unknownPeriodLock)
        {
            _unknownPeriod += count;
            if (detectorId is not null && _unknownDetails.TryGetValue(detectorId, out var existing))
                _unknownDetails[detectorId] = existing with { EventCount = existing.EventCount + count };
            else if (detectorId is not null && _unknownDetails.Count < MaxUnknownDetectorDetails)
                _unknownDetails.Add(detectorId, new UnknownDetectorDetail(detectorId, locationIdentifier ?? string.Empty, count));
            else _unknownWithoutDetails += count;
        }
    }
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
/// <summary>Unknown routing details bounded to one summary period.</summary>
public sealed record UnknownDetectorDetail(string DetectorId, string LocationIdentifier, long EventCount);
/// <summary>Atomic summary-period counters, including events exceeding the detail limit.</summary>
public sealed record UnknownDetectorPeriod(long Count, IReadOnlyList<UnknownDetectorDetail> Details, long AdditionalEventCount);
#pragma warning restore CS1591
