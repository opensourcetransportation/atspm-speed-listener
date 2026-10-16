using System.Globalization;
using System.Text;

namespace SpeedListener.LoadTesting;

/// <summary>Creates parser-compatible synthetic tagged speed events.</summary>
public static class SpeedTestPacket
{
    /// <summary>Creates one terminated frame, optionally with a unique event time.</summary>
    public static byte[] Create(string detectorId, int mph, SpeedTestPacketFormat format, DateTimeOffset? timestamp)
    {
        if (detectorId.Length != 6 || detectorId.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("Detector ID must contain six digits.", nameof(detectorId));
        if (mph is < 0 or > 158) throw new ArgumentOutOfRangeException(nameof(mph));
        if (!Enum.IsDefined(format)) throw new ArgumentOutOfRangeException(nameof(format));
        var prefix = format == SpeedTestPacketFormat.Prefixed ? "Z00000XS" : "XS";
        var suffix = timestamp.HasValue
            ? "~" + timestamp.Value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) + "~\r\r"
            : "~\r\r";
        var buffer = new byte[prefix.Length + 2 + 6 + suffix.Length];
        Encoding.ASCII.GetBytes(prefix).CopyTo(buffer, 0);
        buffer[prefix.Length] = (byte)mph;
        buffer[prefix.Length + 1] = (byte)(mph * 1.609);
        Encoding.ASCII.GetBytes(detectorId).CopyTo(buffer, prefix.Length + 2);
        Encoding.ASCII.GetBytes(suffix).CopyTo(buffer, prefix.Length + 8);
        return buffer;
    }
}
