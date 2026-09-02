using SpeedListener.Parsing;
using SpeedListener.Receivers;
using System.Net;
using System.Text;

namespace SpeedListener.Tests;

public sealed class SpeedPacketParserTests
{
    private readonly SpeedPacketParser _parser = new();

    [Fact]
    public void Parse_ShortPacket_ReturnsFailure()
    {
        var result = _parser.Parse(Datagram(new byte[15]));

        Assert.False(result.IsSuccess);
        Assert.Contains("at least 16", result.Error);
    }

    [Fact]
    public void Parse_ValidPacket_ReadsSpeedAndNormalizesDetectorId()
    {
        var receivedAt = new DateTimeOffset(2026, 9, 2, 18, 30, 0, TimeSpan.Zero);
        var packet = Packet("D1", 30, 48);

        var result = _parser.Parse(new UdpDatagram(packet, Loopback(), receivedAt));

        Assert.True(result.IsSuccess);
        Assert.Equal("D1", result.Event!.DetectorId);
        Assert.Equal(30, result.Event.Mph);
        Assert.Equal(48, result.Event.Kph);
        Assert.Equal(receivedAt.UtcDateTime, result.Event.Timestamp);
    }

    [Fact]
    public void Parse_AppendedTimestamp_ConvertsToUtc()
    {
        var basePacket = Packet("ABC123", 25, 40);
        var suffix = Encoding.ASCII.GetBytes("~2026-09-02T12:30:00-06:00\r\n");
        var packet = basePacket.Concat(suffix).ToArray();

        var result = _parser.Parse(Datagram(packet));

        Assert.Equal(new DateTime(2026, 9, 2, 18, 30, 0, DateTimeKind.Utc), result.Event!.Timestamp);
    }

    private static byte[] Packet(string detectorId, byte mph, byte kph)
    {
        var packet = new byte[16];
        packet[8] = mph;
        packet[9] = kph;
        Encoding.ASCII.GetBytes(detectorId.PadRight(6)[..6]).CopyTo(packet, 10);
        return packet;
    }

    private static UdpDatagram Datagram(byte[] packet) =>
        new(packet, Loopback(), new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.Zero));

    private static IPEndPoint Loopback() => new(IPAddress.Loopback, 10088);
}
