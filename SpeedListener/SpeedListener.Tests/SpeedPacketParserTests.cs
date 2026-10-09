using SpeedListener.Parsing;
using SpeedListener.Receivers;
using System.Net;
using System.Text;

namespace SpeedListener.Tests;

public sealed class SpeedPacketParserTests
{
    private readonly SpeedPacketParser _parser = new();

    [Theory]
    [InlineData("5A34FFFFFF0004050F0451000100006B")]
    [InlineData("00000000000000002942373235333132")]
    [InlineData("5A413131363958532942343836313032")]
    public void Parse_UnrelatedOrInvalidPrefix_ReturnsFailure(string hex)
    {
        Assert.False(_parser.Parse(Datagram(Convert.FromHexString(hex))).IsSuccess);
    }

    [Fact]
    public void Parse_PrefixedNonNumericDetector_ReturnsFailure()
    {
        Assert.False(_parser.Parse(Datagram(Packet("ABC123", 25, 40))).IsSuccess);
    }

    [Fact]
    public void ParseMany_CombinedCapturedFormats_ReturnsBothEventsInOrder()
    {
        var packet = Convert.FromHexString("585329423732353331327E0D0D5A3031313639585329423438363130327E0D0D0000");

        var results = _parser.ParseMany(Datagram(packet));

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.IsSuccess));
        Assert.Equal(new[] { "725312", "486102" }, results.Select(r => r.Event!.DetectorId));
        Assert.All(results, r => Assert.Equal(41, r.Event!.Mph));
        Assert.False(_parser.Parse(Datagram(packet)).IsSuccess);
    }

    [Fact]
    public void ParseMany_InvalidFrameThenValidFrame_PreservesValidEvent()
    {
        var packet = Convert.FromHexString("585329427E0D0D585329423732353331327E0D0D");

        var results = _parser.ParseMany(Datagram(packet));

        Assert.Equal(2, results.Count);
        Assert.False(results[0].IsSuccess);
        Assert.Equal("725312", results[1].Event!.DetectorId);
    }

    [Fact]
    public void ParseMany_ValidFrameThenTruncatedFrame_ReportsTailFailure()
    {
        var results = _parser.ParseMany(Datagram(Convert.FromHexString("585329423732353331327E0D0D58532942")));

        Assert.Equal(2, results.Count);
        Assert.True(results[0].IsSuccess);
        Assert.False(results[1].IsSuccess);
    }

    [Fact]
    public void ParseMany_UnrelatedPayloadContainingXS_DoesNotResynchronizeInsidePayload()
    {
        var packet = Convert.FromHexString("5A34FFFFFF0004050F0451000100006B585329423732353331327E0D0D");

        Assert.False(Assert.Single(_parser.ParseMany(Datagram(packet))).IsSuccess);
    }

    [Theory]
    [InlineData("585329427E0D0D")]
    [InlineData("5A3031313639585329427E0D0D")]
    public void Parse_UntaggedSpeedPacket_ReturnsFailure(string hex)
    {
        Assert.False(_parser.Parse(Datagram(Convert.FromHexString(hex))).IsSuccess);
    }

    [Fact]
    public void Parse_CapturedCompactPacket_ReadsSpeedAndDetector()
    {
        var packet = Convert.FromHexString("585329423732353331327E0D0D");

        var result = _parser.Parse(Datagram(packet));

        Assert.True(result.IsSuccess);
        Assert.Equal("725312", result.Event!.DetectorId);
        Assert.Equal(41, result.Event.Mph);
        Assert.Equal(66, result.Event.Kph);
        Assert.Equal(new DateTime(2026, 9, 2, 18, 0, 0, DateTimeKind.Utc), result.Event.Timestamp);
    }

    [Fact]
    public void Parse_CapturedPrefixedPacket_PreservesLegacyLayout()
    {
        var result = _parser.Parse(Datagram(Convert.FromHexString("5A3031313639585329423438363130327E0D0D")));

        Assert.True(result.IsSuccess);
        Assert.Equal("486102", result.Event!.DetectorId);
        Assert.Equal(41, result.Event.Mph);
        Assert.Equal(66, result.Event.Kph);
    }

    [Fact]
    public void Parse_CompactPacketWithTimestamp_ReadsCompactOffsets()
    {
        var packet = Convert.FromHexString("58532942373235333132")
            .Concat(Encoding.ASCII.GetBytes("~2026-09-02T12:30:00-06:00\r\n")).ToArray();

        var result = _parser.Parse(Datagram(packet));

        Assert.True(result.IsSuccess);
        Assert.Equal("725312", result.Event!.DetectorId);
        Assert.Equal(41, result.Event.Mph);
        Assert.Equal(new DateTime(2026, 9, 2, 18, 30, 0, DateTimeKind.Utc), result.Event.Timestamp);
    }

    [Theory]
    [InlineData("58532942373235333132")]
    [InlineData("585329423732353331327E0D0D0000")]
    public void Parse_CompactPacketWithoutTimestamp_UsesReceiptTime(string hex)
    {
        var result = _parser.Parse(Datagram(Convert.FromHexString(hex)));

        Assert.True(result.IsSuccess);
        Assert.Equal("725312", result.Event!.DetectorId);
        Assert.Equal(66, result.Event.Kph);
        Assert.Equal(new DateTime(2026, 9, 2, 18, 0, 0, DateTimeKind.Utc), result.Event.Timestamp);
    }

    [Theory]
    [InlineData("58532942373235")]
    [InlineData("585329423732357E0D0D")]
    [InlineData("58532942373235FF3132")]
    public void Parse_TruncatedOrNonNumericCompactDetector_ReturnsFailure(string hex)
    {
        var result = _parser.Parse(Datagram(Convert.FromHexString(hex)));

        Assert.False(result.IsSuccess);
        Assert.Null(result.Event);
    }

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
        var packet = Packet("502620", 30, 48);

        var result = _parser.Parse(new UdpDatagram(packet, Loopback(), receivedAt));

        Assert.True(result.IsSuccess);
        Assert.Equal("502620", result.Event!.DetectorId);
        Assert.Equal(30, result.Event.Mph);
        Assert.Equal(48, result.Event.Kph);
        Assert.Equal(receivedAt.UtcDateTime, result.Event.Timestamp);
    }

    [Fact]
    public void Parse_AppendedTimestamp_ConvertsToUtc()
    {
        var basePacket = Packet("502620", 25, 40);
        var suffix = Encoding.ASCII.GetBytes("~2026-09-02T12:30:00-06:00\r\n");
        var packet = basePacket.Concat(suffix).ToArray();

        var result = _parser.Parse(Datagram(packet));

        Assert.Equal(new DateTime(2026, 9, 2, 18, 30, 0, DateTimeKind.Utc), result.Event!.Timestamp);
    }

    [Fact]
    public void Parse_NulPaddedTimestamp_ConvertsToUtc()
    {
        var basePacket = Packet("502620", 25, 40);
        var suffix = Encoding.ASCII.GetBytes("\0~\02026-09-02T18:30:00Z\0");

        var result = _parser.Parse(Datagram(basePacket.Concat(suffix).ToArray()));

        Assert.Equal(new DateTime(2026, 9, 2, 18, 30, 0, DateTimeKind.Utc), result.Event!.Timestamp);
    }

    [Fact]
    public void Parse_BlankDetectorId_ReturnsFailure()
    {
        var result = _parser.Parse(Datagram(Packet(string.Empty, 25, 40)));

        Assert.False(result.IsSuccess);
        Assert.Null(result.Event);
        Assert.Contains("blank detector identifier", result.Error);
    }

    [Fact]
    public void Parse_InvalidTimestamp_RejectsUnexpectedTrailingData()
    {
        var packet = Packet("502620", 25, 40)
            .Concat(Encoding.ASCII.GetBytes("~not-a-timestamp\r\n"))
            .ToArray();

        var result = _parser.Parse(Datagram(packet));

        Assert.False(result.IsSuccess);
        Assert.Contains("Unexpected bytes", result.Error);
    }

    [Fact]
    public void Parse_ConfiguredAgencyTimeZone_ConvertsReceiptAndOffsetSuffixToWallClock()
    {
        var parser = new SpeedPacketParser(options: Microsoft.Extensions.Options.Options.Create(
            new SpeedListener.Configuration.SpeedListenerConfiguration { EventTimeZoneId = "America/Denver" }));
        foreach (var (utc, hour) in new[] {
            (new DateTimeOffset(2026, 1, 2, 18, 0, 0, TimeSpan.Zero), 11),
            (new DateTimeOffset(2026, 7, 2, 18, 0, 0, TimeSpan.Zero), 12) })
        {
            var receipt = parser.Parse(new UdpDatagram(Packet("502620", 25, 40), Loopback(), utc));
            Assert.Equal(hour, receipt.Event!.Timestamp.Hour);
            Assert.Equal(DateTimeKind.Unspecified, receipt.Event.Timestamp.Kind);
            var suffixed = Packet("502620", 25, 40).Concat(Encoding.ASCII.GetBytes("~" + utc.ToString("O"))).ToArray();
            var explicitTime = parser.Parse(new UdpDatagram(suffixed, Loopback(), utc.AddDays(1)));
            Assert.Equal(receipt.Event.Timestamp, explicitTime.Event!.Timestamp);
        }
    }

    [Fact]
    public void ParseMany_JoinedWithoutTerminator_RejectsInsteadOfLosingSecondEvent()
    {
        var packet = Convert.FromHexString("58532942373235333132585329423732353331327E0D0D");
        Assert.False(Assert.Single(_parser.ParseMany(Datagram(packet))).IsSuccess);
    }

    [Theory]
    [InlineData("Z01478X10001~\r\r")]
    [InlineData("Z01396X10000~\r\r")]
    [InlineData("X1000A~\r\r")]
    public void Parse_DocumentedActuation_IsRecognizedWithoutSpeed(string text)
    {
        var result = _parser.Parse(Datagram(Encoding.ASCII.GetBytes(text)));
        Assert.True(result.IsActuation);
        Assert.False(result.IsSuccess);
        Assert.False(result.IsUnmappedSpeed);
        Assert.Null(result.Event);
        Assert.Null(result.Error);
    }

    [Theory]
    [InlineData("X1000G~\r\r")]
    [InlineData("X10001")]
    [InlineData("Z01478X100001~\r\r")]
    [InlineData("ZA1478X10001~\r\r")]
    [InlineData("X1 001~\r\r")]
    public void Parse_MalformedActuation_RemainsRejected(string text)
    {
        var result = _parser.Parse(Datagram(Encoding.ASCII.GetBytes(text)));
        Assert.False(result.IsActuation);
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Theory]
    [InlineData("585332507E0D0D")]
    [InlineData("5A3031343738585332507E0D0D")]
    public void Parse_UntaggedSpeed_RequiresExactEndpointAndUsesConfiguredTimeZone(string hex)
    {
        var packet = Datagram(Convert.FromHexString(hex));
        Assert.True(_parser.Parse(packet).IsUnmappedSpeed);
        var config = new SpeedListener.Configuration.SpeedListenerConfiguration
        {
            EventTimeZoneId = "America/Denver",
            UntaggedSpeedDetectorMappings = new() { ["127.0.0.1:10088"] = "502620" }
        };
        var parser = new SpeedPacketParser(Microsoft.Extensions.Options.Options.Create(config));
        var result = parser.Parse(packet);
        Assert.True(result.IsSuccess);
        Assert.Equal("502620", result.Event!.DetectorId);
        Assert.Equal(50, result.Event.Mph);
        Assert.Equal(80, result.Event.Kph);
        Assert.Equal(12, result.Event.Timestamp.Hour);
        Assert.Equal(DateTimeKind.Unspecified, result.Event.Timestamp.Kind);
        Assert.True(parser.Parse(packet with { RemoteEndPoint = new IPEndPoint(IPAddress.Loopback, 10089) }).IsUnmappedSpeed);
    }

    [Fact]
    public void ParseMany_ActuationThenSpeed_PreservesClassificationAndTaggedEvent()
    {
        var data = Encoding.ASCII.GetBytes("Z01478X10001~\r\r")
            .Concat(Convert.FromHexString("585329423530323632307E0D0D")).ToArray();
        var results = _parser.ParseMany(Datagram(data));
        Assert.Equal(2, results.Count);
        Assert.True(results[0].IsActuation);
        Assert.Equal("502620", results[1].Event!.DetectorId);
    }

    [Fact]
    public void Parse_Z4ContainingXS_IsStillRejectedEvenWithEndpointMapping()
    {
        var parser = new SpeedPacketParser(Microsoft.Extensions.Options.Options.Create(
            new SpeedListener.Configuration.SpeedListenerConfiguration
            { UntaggedSpeedDetectorMappings = new() { ["127.0.0.1:10088"] = "502620" } }));
        var result = parser.Parse(Datagram(Convert.FromHexString("5A34FFFFFF0004050F0451000100006B585332507E0D0D")));
        Assert.False(result.IsSuccess);
        Assert.False(result.IsActuation);
        Assert.False(result.IsUnmappedSpeed);
    }

    private static byte[] Packet(string detectorId, byte mph, byte kph)
    {
        var packet = new byte[16];
        Encoding.ASCII.GetBytes("Z00001XS").CopyTo(packet, 0);
        packet[8] = mph;
        packet[9] = kph;
        Encoding.ASCII.GetBytes(detectorId.PadRight(6)[..6]).CopyTo(packet, 10);
        return packet;
    }

    private static UdpDatagram Datagram(byte[] packet) =>
        new(packet, Loopback(), new DateTimeOffset(2026, 9, 2, 18, 0, 0, TimeSpan.Zero));

    private static IPEndPoint Loopback() => new(IPAddress.Loopback, 10088);
}
