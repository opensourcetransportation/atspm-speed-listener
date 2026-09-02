using SpeedListener.Receivers;
using System.Globalization;
using System.Text;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Parsing;

/// <summary>Parses the legacy speed sensor packet format used by PR #217.</summary>
public sealed class SpeedPacketParser : ISpeedPacketParser
{
    /// <inheritdoc/>
    public SpeedPacketParseResult Parse(UdpDatagram datagram)
    {
        var data = datagram.Buffer;
        if (data.Length < 16)
            return SpeedPacketParseResult.Failure($"Expected at least 16 bytes but received {data.Length}.");

        var detectorId = Encoding.ASCII.GetString(data, 10, 6).Trim();
        if (string.IsNullOrWhiteSpace(detectorId))
            return SpeedPacketParseResult.Failure("The packet contains a blank detector identifier.");

        var timestamp = datagram.ReceivedAt.UtcDateTime;
        if (data.Length > 16)
        {
            var timestampText = Encoding.ASCII.GetString(data, 16, data.Length - 16)
                .TrimStart('~', '\r', '\n', ' ')
                .TrimEnd('\r', '\n', '\0', ' ');

            if (!string.IsNullOrWhiteSpace(timestampText) &&
                DateTimeOffset.TryParse(timestampText, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsedTimestamp))
                timestamp = parsedTimestamp.UtcDateTime;
        }

        return SpeedPacketParseResult.Success(new SpeedEvent
        {
            DetectorId = detectorId,
            Mph = data[8],
            Kph = data[9],
            Timestamp = timestamp
        });
    }
}
