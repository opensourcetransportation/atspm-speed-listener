using SpeedListener.Receivers;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Parsing;

/// <summary>Parses legacy prefixed speed packets and compact packets beginning with XS.</summary>
public sealed class SpeedPacketParser(IOptions<SpeedListenerConfiguration>? options = null) : ISpeedPacketParser
{
    private readonly IReadOnlyDictionary<string, string> _untaggedMappings = options?.Value.UntaggedSpeedDetectorMappings ?? new();
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
        // WX-501-0072 pp. 4-5: X1 + four hexadecimal digits + ~ CR CR.
        // The Z0 prefix carries a four-digit multi-drop address, not a detector ID.
        var multiDrop = data.Length >= 6 && data[0] == (byte)'Z' && data[1] == (byte)'0'
            && data[2..6].All(b => b >= (byte)'0' && b <= (byte)'9');
        var headerOffset = multiDrop ? 6 : 0;
        if (data.Length >= headerOffset + 2 && data[headerOffset] == (byte)'X' && data[headerOffset + 1] == (byte)'1')
        {
            if (data.Length == headerOffset + 9 && data[^3] == (byte)'~' && data[^2] == 13 && data[^1] == 13
                && data[(headerOffset + 2)..(headerOffset + 6)].All(b => b is >= 48 and <= 57 or >= 65 and <= 70 or >= 97 and <= 102)
                && ushort.TryParse(Encoding.ASCII.GetString(data, headerOffset + 2, 4),
                    NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out _))
                return SpeedPacketParseResult.Actuation();
            return SpeedPacketParseResult.Failure("Malformed X1 actuation message; expected four hexadecimal digits and ~ CR CR.");
        }
        // Compact sensors omit the six-byte prefix before the XS header.
        // Identify that format explicitly; do not interpret arbitrary short packets as speed events.
        var compact = data.Length >= 2 && data[0] == (byte)'X' && data[1] == (byte)'S';
        var speedOffset = compact ? 2 : 8;
        var detectorOffset = speedOffset + 2;
        var timestampOffset = detectorOffset + 6;
        // The documented basic XS message has two binary speed bytes and no tag.
        // Resolve only an explicit endpoint mapping, never the multi-drop address.
        var untagged = (compact || (multiDrop && data.Length >= 8 && data[6] == (byte)'X' && data[7] == (byte)'S'))
            && data.Length == speedOffset + 5 && data[^3] == (byte)'~' && data[^2] == 13 && data[^1] == 13;
        if (untagged)
        {
            if (!_untaggedMappings.TryGetValue(datagram.RemoteEndPoint.ToString() ?? string.Empty, out var mappedDetector))
                return SpeedPacketParseResult.UnmappedSpeed();
            if (mappedDetector.Length != 6 || mappedDetector.Any(c => c < '0' || c > '9'))
                return SpeedPacketParseResult.Failure("Configured untagged speed detector identifier must contain six digits.");
            return SpeedPacketParseResult.Success(new SpeedEvent
            {
                DetectorId = mappedDetector, Mph = data[speedOffset], Kph = data[speedOffset + 1],
                Timestamp = ConvertTimestamp(datagram.ReceivedAt.UtcDateTime)
            });
        }
        if (data.Length < timestampOffset)
            return SpeedPacketParseResult.Failure($"Expected at least {timestampOffset} bytes but received {data.Length}.");

        if (!compact && (data[0] != (byte)'Z' ||
            data[1..6].Any(b => b < (byte)'0' || b > (byte)'9') ||
            data[6] != (byte)'X' || data[7] != (byte)'S'))
            return SpeedPacketParseResult.Failure("Unsupported packet header; expected XS or Z plus five digits followed by XS.");

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
            Timestamp = ConvertTimestamp(timestamp)
        });
    }
    private DateTime ConvertTimestamp(DateTime timestamp) => _eventTimeZone.Equals(TimeZoneInfo.Utc) ? timestamp
        : DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(timestamp, _eventTimeZone), DateTimeKind.Unspecified);
}
