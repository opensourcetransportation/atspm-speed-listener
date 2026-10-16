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
using Utah.Udot.Atspm.Data;
using Utah.Udot.Atspm.Data.Enums;
using Utah.Udot.Atspm.Data.Models;

namespace SpeedListener.Tests;
public sealed class LocationTimeZoneTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void EmptyZoneIsAnActionableArgumentError(string? id) =>
        Assert.ThrowsAny<ArgumentException>(() => LocationTimeZoneResolver.ResolveId(id!));

    [Theory]
    [InlineData("Local")]
    [InlineData("local")]
    public void LocalResolvesToServerZone(string id) =>
        Assert.Same(TimeZoneInfo.Local, LocationTimeZoneResolver.ResolveId(id));

    [Theory]
    [InlineData(-26.2041, 28.0473)]
    [InlineData(-72.01, 2.535)]
    [InlineData(40.7128, -74.0060)]
    [InlineData(0, 0)]
    [InlineData(91, 181)]
    public async Task CoordinatesCannotShiftServerLocalEvents(double latitude, double longitude)
    {
        await using var services = Services();
        await Seed(services, Device(1, "5026", latitude, longitude));
        var config = new SpeedListenerConfiguration();
        Assert.Equal("Local", config.EventTimeZoneId);
        var provider = Provider(services, config);
        var mappings = await provider.GetMappingsAsync(CancellationToken.None);
        Assert.Same(TimeZoneInfo.Local, mappings["5026"].TimeZone);
        var receipt = new DateTimeOffset(2026, 7, 15, 18, 0, 0, TimeSpan.Zero);
        foreach (var suffix in new DateTimeOffset?[] { null, receipt })
        {
            var result = new SpeedPacketParser(Options.Create(config), provider).Parse(new UdpDatagram(
                SpeedTestPacket.Create("502620", 30, SpeedTestPacketFormat.Compact, suffix),
                new IPEndPoint(IPAddress.Loopback, 1), receipt));
            Assert.True(result.IsSuccess, result.Error);
            Assert.Equal(TimeZoneInfo.ConvertTime(receipt, TimeZoneInfo.Local).DateTime, result.Event!.Timestamp);
            Assert.Equal(DateTimeKind.Unspecified, result.Event.Timestamp.Kind);
        }
    }

    [Fact]
    public async Task VersionCutoffUsesServerLocalClockRegardlessOfCoordinates()
    {
        await using var services = Services();
        var instant = new DateTimeOffset(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);
        var local = TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local).DateTime;
        var older = Device(1, "5026", -72.01, 2.535);
        var newer = Device(2, "5026", -26.2041, 28.0473);
        older.Location.Start = local.AddHours(-1);
        newer.Location.Start = local.AddHours(1);
        await Seed(services, older, newer);
        var mappings = await Provider(services, new SpeedListenerConfiguration()).GetMappingsAsync(CancellationToken.None);
        Assert.Equal(1, mappings["5026"].DeviceId);
    }

    [Fact]
    public async Task ExplicitOverrideAppliesUniformlyRatherThanUsingCoordinates()
    {
        await using var services = Services();
        await Seed(services, Device(1, "5026", -72.01, 2.535));
        var config = new SpeedListenerConfiguration { EventTimeZoneId = "UTC" };
        var provider = Provider(services, config);
        await provider.GetMappingsAsync(CancellationToken.None);
        var receipt = new DateTimeOffset(2026, 7, 15, 18, 0, 0, TimeSpan.Zero);
        var result = new SpeedPacketParser(Options.Create(config), provider).Parse(new UdpDatagram(
            SpeedTestPacket.Create("502620", 30, SpeedTestPacketFormat.Compact, null),
            new IPEndPoint(IPAddress.Loopback, 1), receipt));
        Assert.Equal(receipt.DateTime, result.Event!.Timestamp);
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
