using SpeedListener.Receivers;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SpeedListener.LogMessages;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Parsing;

/// <summary>Parses legacy prefixed speed packets and compact packets beginning with XS.</summary>
public sealed class SpeedPacketParser(ILogger<SpeedPacketParser>? logger = null,
    IOptions<SpeedListenerConfiguration>? options = null) : ISpeedPacketParser
{
    private readonly SpeedListenerLogMessages _log = new(logger ?? NullLogger<SpeedPacketParser>.Instance);
    private readonly TimeZoneInfo _eventTimeZone = TimeZoneInfo.FindSystemTimeZoneById(options?.Value.EventTimeZoneId ?? "UTC");

    /// <inheritdoc/>
    public SpeedPacketParseResult Parse(UdpDatagram datagram)
    {
        var results = ParseMany(datagram);
        return results.Count == 1 ? results[0]
            : SpeedPacketParseResult.Failure("The datagram contains multiple messages; use ParseMany.");
    }

    /// <inheritdoc/>
    public IReadOnlyList<SpeedPacketParseResult> ParseMany(UdpDatagram datagram)
    {
        var data = datagram.Buffer;
        var results = new List<SpeedPacketParseResult>();
        var offset = 0;
        while (offset < data.Length)
        {
            // Terminal servers may join messages terminated by ~ CR CR.
            // Never search for XS inside an unrelated protocol payload to manufacture an event.
            var end = offset;
            while (end + 2 < data.Length &&
                !(data[end] == (byte)'~' && data[end + 1] == 13 && data[end + 2] == 13)) end++;
            end = end + 2 < data.Length ? end + 3 : data.Length;
            results.Add(ParseMessage(new UdpDatagram(data[offset..end], datagram.RemoteEndPoint, datagram.ReceivedAt)));
            offset = end;
            while (offset < data.Length && data[offset] is 0 or 13 or 10 or 32) offset++;
        }
        if (results.Count == 0) results.Add(SpeedPacketParseResult.Failure("The datagram is empty."));
        return results;
    }

    private SpeedPacketParseResult ParseMessage(UdpDatagram datagram)
    {
        var data = datagram.Buffer;
        // Compact sensors omit the six-byte prefix before the XS header.
        // Identify that format explicitly; do not interpret arbitrary short packets as speed events.
        var compact = data.Length >= 2 && data[0] == (byte)'X' && data[1] == (byte)'S';
        var speedOffset = compact ? 2 : 8;
        var detectorOffset = speedOffset + 2;
        var timestampOffset = detectorOffset + 6;
        if (data.Length < timestampOffset)
            return SpeedPacketParseResult.Failure($"Expected at least {timestampOffset} bytes but received {data.Length}.");

        if (!compact && (data[0] != (byte)'Z' ||
            data[1..6].Any(b => b < (byte)'0' || b > (byte)'9') ||
            data[6] != (byte)'X' || data[7] != (byte)'S'))
            return SpeedPacketParseResult.Failure("Unsupported packet header; expected XS or Z plus five digits followed by XS.");

        _log.HeaderObserved(data[speedOffset - 1]);

        var detectorId = Encoding.ASCII.GetString(data, detectorOffset, 6).Trim();
        if (string.IsNullOrWhiteSpace(detectorId))
            return SpeedPacketParseResult.Failure("The packet contains a blank detector identifier.");
        if (detectorId.Length != 6 || detectorId.Any(c => c < '0' || c > '9'))
            return SpeedPacketParseResult.Failure("The speed packet must contain a six-digit detector identifier for ATSPM mapping.");

        var timestamp = datagram.ReceivedAt.UtcDateTime;
        var bodyLength = data.Length;
        if (bodyLength >= 3 && data[bodyLength - 3] == (byte)'~' && data[bodyLength - 2] == 13 && data[bodyLength - 1] == 13)
            bodyLength -= 3;
        if (bodyLength > timestampOffset)
        {
            var timestampText = Encoding.ASCII.GetString(data, timestampOffset, bodyLength - timestampOffset)
                .TrimStart('~', '\r', '\n', '\0', ' ')
                .TrimEnd('\r', '\n', '\0', ' ');

            if (!string.IsNullOrWhiteSpace(timestampText))
            {
                if (!DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTimestamp))
                    return SpeedPacketParseResult.Failure("Unexpected bytes after the detector identifier; expected a terminator or timestamp.");
                timestamp = parsedTimestamp.UtcDateTime;
            }
        }

        return SpeedPacketParseResult.Success(new SpeedEvent
        {
            DetectorId = detectorId,
            Mph = data[speedOffset],
            Kph = data[speedOffset + 1],
            Timestamp = _eventTimeZone.Equals(TimeZoneInfo.Utc) ? timestamp
                : DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(timestamp, _eventTimeZone), DateTimeKind.Unspecified)
        });
    }
}
