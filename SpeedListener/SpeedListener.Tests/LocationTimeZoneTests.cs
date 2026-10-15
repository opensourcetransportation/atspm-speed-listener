using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
using SpeedListener.LoadTesting;
using SpeedListener.Parsing;
using SpeedListener.Receivers;
using SpeedListener.Services;
using System.Net;
using System.Text;
using Utah.Udot.Atspm.Data;
using Utah.Udot.Atspm.Data.Enums;
using Utah.Udot.Atspm.Data.Models;

namespace SpeedListener.Tests;

public sealed class LocationTimeZoneTests
{
    [Theory]
    [InlineData(40.7128, -74.0060, 7, 14)]
    [InlineData(40.7128, -74.0060, 1, 13)]
    [InlineData(40.7608, -111.8910, 7, 12)]
    [InlineData(40.7608, -111.8910, 1, 11)]
    [InlineData(33.4484, -112.0740, 7, 11)]
    [InlineData(33.4484, -112.0740, 1, 11)]
    public async Task CoordinatesOverrideFallbackForTaggedAndUntaggedPackets(double latitude, double longitude, int month, int expectedHour)
    {
        await using var services = Services();
        await Seed(services, Device(1, "5026", latitude, longitude));
        var config = new SpeedListenerConfiguration { EventTimeZoneId = "UTC",
            UntaggedSpeedDetectorMappings = new() { new() { SourceAddress = "127.0.0.1", SourcePort = 2103, DetectorId = "502620" } } };
        var provider = Provider(services, config);
        await provider.GetMappingsAsync(CancellationToken.None);
        var parser = new SpeedPacketParser(Options.Create(config), provider);
        var receipt = new DateTimeOffset(2026, month, 15, 18, 0, 0, TimeSpan.Zero);
        var endpoint = new IPEndPoint(IPAddress.Loopback, 2103);
        var frames = new[] {
            SpeedTestPacket.Create("502620", 30, SpeedTestPacketFormat.Prefixed, null),
            SpeedTestPacket.Create("502620", 30, SpeedTestPacketFormat.Compact, receipt),
            Convert.FromHexString("58531E307E0D0D")
        };
        foreach (var frame in frames)
        {
            var parsed = parser.Parse(new UdpDatagram(frame, endpoint, receipt));
            Assert.True(parsed.IsSuccess, parsed.Error);
            Assert.Equal(new DateTime(2026, month, 15, expectedHour, 0, 0), parsed.Event!.Timestamp);
            Assert.Equal(DateTimeKind.Unspecified, parsed.Event.Timestamp.Kind);
        }
    }

    [Fact]
    public async Task JoinedFramesUseTheirOwnLocationZones()
    {
        await using var services = Services();
        await Seed(services, Device(1, "5026", 40.7128, -74.0060), Device(2, "5271", 40.7608, -111.8910));
        var config = new SpeedListenerConfiguration { EventTimeZoneId = "UTC" };
        var provider = Provider(services, config);
        await provider.GetMappingsAsync(CancellationToken.None);
        var receipt = new DateTimeOffset(2026, 7, 15, 18, 0, 0, TimeSpan.Zero);
        var frames = SpeedTestPacket.Create("502620", 30, SpeedTestPacketFormat.Compact, receipt)
            .Concat(SpeedTestPacket.Create("527148", 30, SpeedTestPacketFormat.Compact, receipt)).ToArray();
        var results = new SpeedPacketParser(Options.Create(config), provider).ParseMany(
            new UdpDatagram(frames, new IPEndPoint(IPAddress.Loopback, 1), receipt));
        Assert.Equal(new[] { 14, 12 }, results.Select(result => result.Event!.Timestamp.Hour));
        Assert.All(results, result => Assert.Equal(DateTimeKind.Unspecified, result.Event!.Timestamp.Kind));
    }

    [Fact]
    public async Task OffsetlessSensorTimeUsesLocationZoneRatherThanFallbackZone()
    {
        await using var services = Services();
        await Seed(services, Device(1, "5026", 33.4484, -112.0740));
        var config = new SpeedListenerConfiguration { EventTimeZoneId = "America/New_York" };
        var provider = Provider(services, config);
        await provider.GetMappingsAsync(CancellationToken.None);
        var parser = new SpeedPacketParser(Options.Create(config), provider);
        var frame = SpeedTestPacket.Create("502620", 30, SpeedTestPacketFormat.Compact, null);
        frame = frame[..^3].Concat(Encoding.ASCII.GetBytes("~2026-03-08T02:30:00~\r\r")).ToArray();
        // New York skips this spring-forward wall time; Phoenix does not.
        var result = parser.Parse(new UdpDatagram(frame, new IPEndPoint(IPAddress.Loopback, 2103),
            new DateTimeOffset(2026, 3, 8, 9, 30, 0, TimeSpan.Zero)));
        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(new DateTime(2026, 3, 8, 2, 30, 0), result.Event!.Timestamp);
        Assert.Equal(DateTimeKind.Unspecified, result.Event.Timestamp.Kind);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(91, 0)]
    [InlineData(0, 181)]
    [InlineData(double.NaN, -111)]
    [InlineData(40, double.PositiveInfinity)]
    public async Task UnusableCoordinatesUseConfiguredFallback(double latitude, double longitude)
    {
        await using var services = Services();
        await Seed(services, Device(1, "5026", latitude, longitude));
        var config = new SpeedListenerConfiguration { EventTimeZoneId = "America/Denver" };
        var provider = Provider(services, config);
        var map = await provider.GetMappingsAsync(CancellationToken.None);
        Assert.Equal("America/Denver", map["5026"].TimeZone!.Id);
        var frame = SpeedTestPacket.Create("502620", 30, SpeedTestPacketFormat.Compact, null);
        var parsed = new SpeedPacketParser(Options.Create(config), provider).Parse(new UdpDatagram(frame,
            new IPEndPoint(IPAddress.Loopback, 1), new DateTimeOffset(2026, 7, 15, 18, 0, 0, TimeSpan.Zero)));
        Assert.Equal(12, parsed.Event!.Timestamp.Hour);
        Assert.Equal(DateTimeKind.Unspecified, parsed.Event.Timestamp.Kind);
    }

    [Fact]
    public async Task VersionCutoffsUseEachLocationsCoordinatesAndZonesRemainCached()
    {
        var services = Services();
        var nyOld = Device(1, "5026", 40.7128, -74.0060);
        var nyNew = Device(2, "5026", 40.7128, -74.0060);
        var utOld = Device(3, "5271", 40.7608, -111.8910);
        var utNew = Device(4, "5271", 40.7608, -111.8910);
        nyNew.Location.Start = utNew.Location.Start = new DateTime(2026, 9, 3, 7, 0, 0);
        await Seed(services, nyOld, nyNew, utOld, utNew);
        var provider = Provider(services, new SpeedListenerConfiguration { EventTimeZoneId = "UTC" });
        var mappings = await provider.GetMappingsAsync(CancellationToken.None);
        Assert.Equal(2, mappings["5026"].DeviceId); // 08:00 New York.
        Assert.Equal(3, mappings["5271"].DeviceId); // 06:00 Utah.
        await services.DisposeAsync();
        Assert.Equal("America/New_York", provider.GetTimeZone("5026")!.Id);
        Assert.Equal("America/Denver", provider.GetTimeZone("5271")!.Id);
        Assert.Null(provider.GetTimeZone("9999"));
    }

    [Fact]
    public async Task MappingRefreshUpdatesCoordinateZoneSnapshot()
    {
        await using var services = Services();
        await Seed(services, Device(1, "5026", 40.7608, -111.8910));
        var clock = new Clock();
        var provider = new DeviceMappingProvider(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SpeedListenerConfiguration()), clock, new SpeedListenerMetrics(clock),
            NullLogger<DeviceMappingProvider>.Instance);
        await provider.GetMappingsAsync(CancellationToken.None);
        Assert.Equal("America/Denver", provider.GetTimeZone("5026")!.Id);
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ConfigContext>();
            var location = await context.Locations.SingleAsync();
            location.Latitude = 40.7128;
            location.Longitude = -74.0060;
            await context.SaveChangesAsync();
        }
        clock.Advance(TimeSpan.FromMinutes(6));
        await provider.RefreshAsync(CancellationToken.None);
        Assert.Equal("America/New_York", provider.GetTimeZone("5026")!.Id);
    }

    [Fact]
    public void UtcFallbackAlsoProducesTimezoneNaiveValues()
    {
        var parser = new SpeedPacketParser(Options.Create(new SpeedListenerConfiguration { EventTimeZoneId = "UTC" }));
        var result = parser.Parse(new UdpDatagram(SpeedTestPacket.Create("502620", 30, SpeedTestPacketFormat.Compact, null),
            new IPEndPoint(IPAddress.Loopback, 1), new DateTimeOffset(2026, 7, 15, 18, 0, 0, TimeSpan.Zero)));
        Assert.Equal(18, result.Event!.Timestamp.Hour);
        Assert.Equal(DateTimeKind.Unspecified, result.Event.Timestamp.Kind);
    }

    private static ServiceProvider Services()
    {
        var database = Guid.NewGuid().ToString();
        return new ServiceCollection().AddDbContext<ConfigContext>(options =>
            options.UseInMemoryDatabase(database)).BuildServiceProvider();
    }

    private static DeviceMappingProvider Provider(ServiceProvider services, SpeedListenerConfiguration config) => new(
        services.GetRequiredService<IServiceScopeFactory>(), Options.Create(config), new Clock(),
        new SpeedListenerMetrics(new Clock()), NullLogger<DeviceMappingProvider>.Instance);

    private static async Task Seed(ServiceProvider services, params Device[] devices)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ConfigContext>();
        context.Devices.AddRange(devices);
        await context.SaveChangesAsync();
    }

    private static Device Device(int id, string location, double latitude, double longitude) => new() {
        Id = id, DeviceIdentifier = $"sensor-{id}", DeviceType = DeviceTypes.SpeedSensor,
        DeviceStatus = DeviceStatus.Active, Ipaddress = "192.0.2.1",
        Location = new Location { Id = id, LocationIdentifier = location, PrimaryName = "Test", Note = "",
            Start = new DateTime(2020, 1, 1), Latitude = latitude, Longitude = longitude }
    };

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset instant = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => instant;
        public void Advance(TimeSpan elapsed) => instant += elapsed;
    }
}
