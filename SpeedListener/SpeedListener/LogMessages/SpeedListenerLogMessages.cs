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
        Message = "Rejected speed packet from {remoteEndPoint}: {reason}")]
    public partial void PacketRejected(EndPoint remoteEndPoint, string? reason);

    [LoggerMessage(EventId = 3003, EventName = "Listener Started", Level = LogLevel.Information,
        Message = "Speed listener started on UDP port {port}")]
    public partial void ListenerStarted(int port);

    [LoggerMessage(EventId = 3004, EventName = "Shutdown Drain Timeout", Level = LogLevel.Error,
        Message = "Speed listener shutdown exceeded its drain deadline with {remaining} queued or in-flight events")]
    public partial void ShutdownDrainTimeout(int remaining);

    [LoggerMessage(EventId = 3005, EventName = "Listener Stopped", Level = LogLevel.Information,
        Message = "Speed listener stopped. Received {received}, rejected {rejected}, and dropped {dropped} packets")]
    public partial void ListenerStopped(long received, long rejected, long dropped);

    [LoggerMessage(EventId = 3006, EventName = "Listener Summary", Level = LogLevel.Information,
        Message = "SpeedListenerSummary Received={received} Parsed={parsed} Rejected={rejected} Unknown={unknown} ChannelDepth={channelDepth} Dropped={dropped} BatchesPublished={batchesPublished} EnvelopesPublished={envelopesPublished} PublishLatencyMs={publishLatencyMs} Retries={retries} PublishFailures={publishFailures} PoisonBatches={poisonBatches} MappingAgeSeconds={mappingAgeSeconds} MappingRefreshFailures={mappingRefreshFailures}")]
    public partial void Summary(long received, long parsed, long rejected, long unknown, int channelDepth,
        long dropped, long batchesPublished, long envelopesPublished, double publishLatencyMs, long retries,
        long publishFailures, long poisonBatches, double mappingAgeSeconds, long mappingRefreshFailures);

    [LoggerMessage(EventId = 3007, EventName = "Listener Loss Summary", Level = LogLevel.Warning,
        Message = "SpeedListenerLossSummary RejectedSinceLast={rejectedSinceLast} UnknownSinceLast={unknownSinceLast} DroppedSinceLast={droppedSinceLast}")]
    public partial void LossSummary(long rejectedSinceLast, long unknownSinceLast, long droppedSinceLast);

    [LoggerMessage(EventId = 3010, EventName = "Mappings Loaded", Level = LogLevel.Information,
        Message = "Loaded {count} speed-sensor device mappings; skipped {invalidCount} invalid and {duplicateCount} duplicate rows")]
    public partial void MappingsLoaded(int count, int invalidCount, int duplicateCount);

    [LoggerMessage(EventId = 3011, EventName = "Mapping Validation Warning", Level = LogLevel.Warning,
        Message = "Speed-sensor mapping validation found {invalidCount} invalid and {duplicateCount} duplicate rows; valid sensors remain active")]
    public partial void MappingValidationWarning(int invalidCount, int duplicateCount);

    [LoggerMessage(EventId = 3012, EventName = "Mapping Refresh Failed", Level = LogLevel.Warning,
        Message = "Device mapping refresh failed; continuing with the last successful mapping")]
    public partial void MappingRefreshFailed(Exception exception);

    [LoggerMessage(EventId = 3020, EventName = "Packet Header Observed", Level = LogLevel.Debug,
        Message = "Observed speed-packet header byte {headerByte}")]
    public partial void HeaderObserved(byte headerByte);

    [LoggerMessage(EventId = 3030, EventName = "Batch Processed", Level = LogLevel.Information,
        Message = "Processed batch of {eventCount} events into {envelopeCount} envelopes")]
    public partial void BatchProcessed(int eventCount, int envelopeCount);

    [LoggerMessage(EventId = 3040, EventName = "Batch Rejected", Level = LogLevel.Warning,
        Message = "Database rejected a batch of {envelopeCount} envelopes; isolating the device-attributable failure")]
    public partial void BatchRejected(int envelopeCount, Exception exception);

    [LoggerMessage(EventId = 3041, EventName = "Envelopes Archived", Level = LogLevel.Information,
        Message = "Archived {count} speed-event envelopes in {elapsedMilliseconds} ms")]
    public partial void EnvelopesArchived(int count, double elapsedMilliseconds);

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
}
#pragma warning restore CS1591
