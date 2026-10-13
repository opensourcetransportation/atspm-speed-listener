#pragma warning disable CS1591
using Microsoft.Extensions.Logging;
using SpeedListener.Publishing;
using System.Net;

namespace SpeedListener.LogMessages;

/// <summary>Source-generated structured log messages for speed-listener operations.</summary>
public partial class SpeedListenerLogMessages(ILogger logger)
{
    [LoggerMessage(EventId = 3001, EventName = "UDP Listener Bound", Level = LogLevel.Information,
        Message = "UDP speed listener bound to port {port}")]
    public partial void UdpBound(int port);

    [LoggerMessage(EventId = 3002, EventName = "Packet Rejected", Level = LogLevel.Debug,
        Message = "Rejected speed packet from {remoteEndPoint}: {reason} DatagramLength={datagramLength}; PayloadHex={payloadHex}; Truncated={truncated}")]
    public partial void PacketRejected(EndPoint remoteEndPoint, string? reason, int datagramLength, string payloadHex, bool truncated);

    [LoggerMessage(EventId = 3003, EventName = "Listener Started", Level = LogLevel.Information,
        Message = "Speed listener started on UDP port {port}")]
    public partial void ListenerStarted(int port);

    [LoggerMessage(EventId = 3004, EventName = "Shutdown Drain Timeout", Level = LogLevel.Error,
        Message = "Speed listener shutdown exceeded its drain deadline with {remaining} queued or in-flight events")]
    public partial void ShutdownDrainTimeout(int remaining);

    [LoggerMessage(EventId = 3005, EventName = "Listener Stopped", Level = LogLevel.Information,
        Message = "Speed listener stopped. Received={received} Parsed={parsed} Rejected={rejected} Dropped={dropped} Actuation={actuation} UnmappedSpeed={unmappedSpeed}")]
    public partial void ListenerStopped(long received, long rejected, long dropped, long parsed, long actuation, long unmappedSpeed);

    [LoggerMessage(EventId = 3006, EventName = "Listener Summary", Level = LogLevel.Information,
        Message = "SpeedListenerSummary Received={received} Parsed={parsed} Rejected={rejected} UnknownSinceLast={unknownSinceLast} ChannelDepth={channelDepth} Dropped={dropped} BatchesPublished={batchesPublished} EnvelopesPublished={envelopesPublished} PublishLatencyMs={publishLatencyMs} Retries={retries} PublishFailures={publishFailures} PoisonBatches={poisonBatches} MappingAgeSeconds={mappingAgeSeconds} MappingRefreshFailures={mappingRefreshFailures} Actuation={actuation} UnmappedSpeed={unmappedSpeed} WriteConflicts={writeConflicts} ConflictEvents={conflictEvents} RowsPublished={rowsPublished} EventsPublished={eventsPublished}")]
    public partial void Summary(long received, long parsed, long rejected, long unknownSinceLast, int channelDepth,
        long dropped, long batchesPublished, long envelopesPublished, double publishLatencyMs, long retries,
        long publishFailures, long poisonBatches, double mappingAgeSeconds, long mappingRefreshFailures, long actuation, long unmappedSpeed, long writeConflicts, long conflictEvents, long rowsPublished, long eventsPublished);

    [LoggerMessage(EventId = 3007, EventName = "Listener Loss Summary", Level = LogLevel.Warning,
        Message = "SpeedListenerLossSummary RejectedSinceLast={rejectedSinceLast} UnknownSinceLast={unknownSinceLast} DroppedSinceLast={droppedSinceLast}")]
    public partial void LossSummary(long rejectedSinceLast, long unknownSinceLast, long droppedSinceLast);

    [LoggerMessage(EventId = 3008, EventName = "Packet Classified", Level = LogLevel.Debug,
        Message = "Classified packet from {remoteEndPoint} as {kind}: {reason} DatagramLength={datagramLength}; PayloadHex={payloadHex}; Truncated={truncated}")]
    public partial void NonSpeedPacket(EndPoint remoteEndPoint, string kind, string? reason, int datagramLength, string payloadHex, bool truncated);

    [LoggerMessage(EventId = 3009, EventName = "Unmapped Speed Summary", Level = LogLevel.Warning,
        Message = "SpeedListenerMappingSummary UnmappedSpeedSinceLast={count}; configure UntaggedSpeedDetectorMappings for untagged XS source endpoints")]
    public partial void UnmappedSpeedSummary(long count);

    [LoggerMessage(EventId = 3010, EventName = "Mappings Loaded", Level = LogLevel.Information,
        Message = "Loaded {count} speed-sensor device mappings; skipped {invalidCount} invalid and {duplicateCount} duplicate rows")]
    public partial void MappingsLoaded(int count, int invalidCount, int duplicateCount);

    [LoggerMessage(EventId = 3011, EventName = "Mapping Validation Warning", Level = LogLevel.Warning,
        Message = "Speed-sensor mapping validation found {invalidCount} invalid and {duplicateCount} duplicate rows; valid sensors remain active")]
    public partial void MappingValidationWarning(int invalidCount, int duplicateCount);

    [LoggerMessage(EventId = 3012, EventName = "Mapping Refresh Failed", Level = LogLevel.Warning,
        Message = "Device mapping refresh failed; continuing with the last successful mapping")]
    public partial void MappingRefreshFailed(Exception exception);

    [LoggerMessage(EventId = 3030, EventName = "Batch Processed", Level = LogLevel.Information,
        Message = "Processed batch of {eventCount} events into {envelopeCount} envelopes")]
    public partial void BatchProcessed(int eventCount, int envelopeCount);

    [LoggerMessage(EventId = 3031, EventName = "Unknown Detector", Level = LogLevel.Debug,
        Message = "SpeedListenerUnknownDetector DetectorId={detectorId} LocationIdentifier={locationIdentifier} EventCount={eventCount}; no current speed-device mapping")]
    public partial void UnknownDetector(string? detectorId, string locationIdentifier, long eventCount);

    [LoggerMessage(EventId = 3032, EventName = "Mapped Detector", Level = LogLevel.Debug,
        Message = "SpeedListenerMappedDetector DetectorId={detectorId} LocationIdentifier={locationIdentifier} DeviceId={deviceId} EventCount={eventCount}")]
    public partial void MappedDetector(string? detectorId, string locationIdentifier, int deviceId, long eventCount);

    [LoggerMessage(EventId = 3033, EventName = "Unknown Detector Warning", Level = LogLevel.Warning,
        Message = "SpeedListenerUnknownDetectorWarning DetectorId={detectorId} LocationIdentifier={locationIdentifier} EventCount={eventCount}; events skipped during the last summary period because no current speed-device mapping was available")]
    public partial void UnknownDetectorWarning(string detectorId, string locationIdentifier, long eventCount);

    [LoggerMessage(EventId = 3034, EventName = "Unknown Detector Detail Limit", Level = LogLevel.Warning,
        Message = "SpeedListenerUnknownDetectorDetailsLimited AdditionalUnknownEvents={eventCount} DetailLimit={detailLimit}; counts include all events, additional detector IDs remain available at Debug")]
    public partial void UnknownDetectorDetailsLimited(long eventCount, int detailLimit);

    [LoggerMessage(EventId = 3040, EventName = "Batch Rejected", Level = LogLevel.Warning,
        Message = "Database rejected a batch of {envelopeCount} envelopes; isolating the device-attributable failure")]
    public partial void BatchRejected(int envelopeCount, Exception exception);

    [LoggerMessage(EventId = 3041, EventName = "Envelopes Archived", Level = LogLevel.Information,
        Message = "Archived {rowCount} speed-event device-hour rows containing {eventCount} events; FullyArchivedEnvelopes={count} in {elapsedMilliseconds} ms")]
    public partial void EnvelopesArchived(int count, int rowCount, long eventCount, double elapsedMilliseconds);

    [LoggerMessage(EventId = 3042, EventName = "Database Write Retry", Level = LogLevel.Warning,
        Message = "Transient database write failure on attempt {attempt}; retrying in {delay}")]
    public partial void DatabaseWriteRetry(int attempt, TimeSpan delay, Exception exception);

    [LoggerMessage(EventId = 3043, EventName = "Database Write Failed", Level = LogLevel.Error,
        Message = "Database write failed with classification {classification} for {count} envelopes on attempt {attempt}")]
    public partial void DatabaseWriteFailed(DatabaseFailureKind classification, int count, int attempt,
        Exception exception);

    [LoggerMessage(EventId = 3044, EventName = "Poison Envelope Dropped", Level = LogLevel.Error,
        Message = "Dropped poison speed-event envelope for device {deviceId}, location {locationIdentifier}; consecutive drops {consecutiveDrops}")]
    public partial void PoisonEnvelopeDropped(int deviceId, string locationIdentifier, int consecutiveDrops,
        Exception exception);
    [LoggerMessage(EventId = 3045, EventName = "Device Hour Write Conflict", Level = LogLevel.Error,
        Message = "SpeedListenerWriteConflict DeviceId={deviceId} Hour={hour} EventCount={eventCount}; skipped this uncertain device-hour, other rows continue; these events may already be committed")]
    public partial void EventLogWriteConflict(int deviceId, DateTime hour, long eventCount, Exception exception);
}
#pragma warning restore CS1591
