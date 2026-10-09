using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeedListener.Commands;
using SpeedListener.Configuration;
using SpeedListener.LoadTesting;
using SpeedListener.Parsing;
using SpeedListener.Receivers;
using SpeedListener.Services;
using System.CommandLine;
using System.Net;
using System.Net.Sockets;
using Utah.Udot.Atspm.Data;
using Utah.Udot.Atspm.Data.Enums;
using Utah.Udot.Atspm.Data.Models;

namespace SpeedListener.Tests;

public sealed class SpeedLoadGeneratorTests
{
    [Theory]
    [InlineData(SpeedTestPacketFormat.Prefixed, true)]
    [InlineData(SpeedTestPacketFormat.Prefixed, false)]
    [InlineData(SpeedTestPacketFormat.Compact, true)]
    [InlineData(SpeedTestPacketFormat.Compact, false)]
    public void Packet_RoundTripsSingleAndJoinedFrames(SpeedTestPacketFormat format, bool includeTimestamp)
    {
        var timestamp = new DateTimeOffset(2026, 10, 9, 18, 0, 0, TimeSpan.Zero);
        var packet = SpeedTestPacket.Create("123401", 41, format, includeTimestamp ? timestamp : null);
        var parser = new SpeedPacketParser();
        var datagram = new UdpDatagram(packet, new IPEndPoint(IPAddress.Loopback, 1), timestamp.AddMinutes(1));
        var parsed = parser.Parse(datagram);
        Assert.True(parsed.IsSuccess, parsed.Error);
        Assert.Equal("123401", parsed.Event!.DetectorId);
        Assert.Equal(41, parsed.Event.Mph);
        Assert.Equal(65, parsed.Event.Kph);
        Assert.Equal(includeTimestamp ? timestamp.UtcDateTime : datagram.ReceivedAt.UtcDateTime, parsed.Event.Timestamp);
        var joined = parser.ParseMany(datagram with { Buffer = packet.Concat(packet).ToArray() });
        Assert.Equal(2, joined.Count);
        Assert.All(joined, result => Assert.True(result.IsSuccess, result.Error));
    }

    [Fact]
    public async Task Run_CountLimited_ReusesSocketCyclesEveryTargetAndIncludesUniqueTimes()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var packets = new List<UdpReceiveResult>();
        var receive = Task.Run(async () => {
            for (var index = 0; index < 9; index++) packets.Add(await receiver.ReceiveAsync(timeout.Token));
        });
        var options = new SpeedLoadTestOptions
        {
            Port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port,
            DetectorIds = ["123401", "234501", "345601"], EventsPerSecond = 100, Count = 9,
            Duration = TimeSpan.FromSeconds(2), MinMph = 30, MaxMph = 30
        };
        var result = await new SpeedLoadGenerator().RunAsync(options, cancellationToken: timeout.Token);
        await receive;
        Assert.Equal(9, result.Sent);
        Assert.Equal(0, result.SendFailures);
        Assert.Equal("count", result.StopReason);
        Assert.Equal(3, result.TargetsTouched);
        Assert.Equal(3, result.TargetsTotal);
        Assert.Single(packets.Select(packet => packet.RemoteEndPoint).Distinct());
        var events = packets.Select(packet => new SpeedPacketParser().Parse(new UdpDatagram(packet.Buffer,
            packet.RemoteEndPoint, DateTimeOffset.UtcNow)).Event!).ToArray();
        Assert.All(events, speed => Assert.Equal(30, speed.Mph));
        Assert.Equal(9, events.Select(speed => speed.Timestamp).Distinct().Count());
        Assert.Equal(Enumerable.Range(0, 9).Select(index => options.DetectorIds[index % 3]), events.Select(speed => speed.DetectorId));
        Assert.True(result.Elapsed >= TimeSpan.FromMilliseconds(50));
    }

    [Fact]
    public async Task Run_DurationAndCancellation_StopWithoutHanging()
    {
        using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var options = new SpeedLoadTestOptions
        {
            Port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port,
            DetectorIds = ["123401"], EventsPerSecond = 1, Duration = TimeSpan.FromMilliseconds(100)
        };
        var duration = await new SpeedLoadGenerator().RunAsync(options).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("duration", duration.StopReason);
        Assert.InRange(duration.Sent, 0, 1);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        var cancelled = await new SpeedLoadGenerator().RunAsync(options with { Duration = TimeSpan.FromSeconds(10) },
            cancellationToken: cancellation.Token).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("cancelled", cancelled.StopReason);
    }

    [Fact]
    public void InvalidLimitsAndTags_AreRejectedBeforeSending()
    {
        var valid = new SpeedLoadTestOptions { Port = 12000, DetectorIds = ["123401"] };
        foreach (var invalid in new[] { valid with { Port = 0 }, valid with { EventsPerSecond = 0 },
            valid with { Duration = TimeSpan.Zero }, valid with { Count = 0 }, valid with { MaxMph = 159 },
            valid with { DetectorIds = ["SPD100"] }, valid with { DetectorIds = [] } })
            Assert.Throws<ArgumentException>(() => invalid.Validate());
        Assert.Throws<ArgumentException>(() => SpeedTestPacket.Create("1234", 30, SpeedTestPacketFormat.Prefixed, null));
    }

    [Fact]
    public async Task Discovery_UsesOnlyCurrentActiveLocations_AndReportsUnaddressableExtraDevices()
    {
        var database = Guid.NewGuid().ToString();
        await using var services = new ServiceCollection()
            .AddDbContext<ConfigContext>(options => options.UseInMemoryDatabase(database)).BuildServiceProvider();
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ConfigContext>();
        Device Device(int id, string location, int year, LocationVersionActions action = default) => new()
        {
            Id = id, DeviceIdentifier = $"name-{id}", DeviceType = DeviceTypes.SpeedSensor, Ipaddress = "192.0.2.1",
            Location = new Location { Id = id, LocationIdentifier = location, PrimaryName = "Fixture", Note = "",
                Start = new DateTime(year, 1, 1), VersionAction = action }
        };
        var old = Device(1, "1234", 2020);
        var current = Device(2, "1234", 2025);
        var extra = Device(3, "1234", 2025); extra.Location = current.Location;
        var another = Device(4, "2345", 2025);
        var deletedOld = Device(5, "3456", 2020);
        var deletedCurrent = Device(6, "3456", 2025, LocationVersionActions.Delete);
        var future = Device(7, "4567", 2099);
        context.Devices.AddRange(old, current, extra, another, deletedOld, deletedCurrent, future);
        await context.SaveChangesAsync();
        var metrics = new SpeedListenerMetrics(TimeProvider.System);
        var provider = new DeviceMappingProvider(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SpeedListenerConfiguration()), TimeProvider.System, metrics, NullLogger<DeviceMappingProvider>.Instance);
        var targets = await new DatabaseSpeedTargetProvider(provider, context).LoadAsync("02");
        Assert.Equal(new[] { 2, 4 }, targets.Targets.Select(target => target.DeviceId));
        Assert.Equal(new[] { "123402", "234502" }, targets.Targets.Select(target => target.DetectorId));
        Assert.Equal(1, targets.ExtraDevicesAtSameLocation);
        Assert.Equal(0, targets.InvalidLocationIdentifiers);
    }

    [Fact]
    public async Task Command_ExplicitPreviewAndInvalidInputs_DoNotNeedDatabaseOrNetwork()
    {
        var root = new RootCommand { new GenerateCommand() };
        Assert.Equal(0, await root.InvokeAsync("generate --port 12000 --host unavailable.invalid --detectors 123401,234501 --list-targets"));
        Assert.Equal(1, await root.InvokeAsync("generate --port 12000 --detectors bad-id"));
        Assert.Equal(1, await root.InvokeAsync("generate --port 12000 --rate 10 --rate-per-device 2"));
        Assert.Equal(1, await root.InvokeAsync("generate --port 12000 --duration NaN"));
    }

    [Fact]
    public async Task Command_DetectorFileAndPerDeviceRate_SendEveryTarget()
    {
        var file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "# fixture\n123401,234501\n345601 # third\n");
            using var receiver = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var port = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
            var packets = new List<UdpReceiveResult>();
            var receive = Task.Run(async () => {
                for (var index = 0; index < 3; index++) packets.Add(await receiver.ReceiveAsync(timeout.Token));
            });
            var root = new RootCommand { new GenerateCommand() };
            Assert.Equal(0, await root.InvokeAsync($"generate --port {port} --detectors-file \"{file}\" --rate-per-device 100 --count 3 --duration 2"));
            await receive;
            var events = packets.Select(packet => new SpeedPacketParser().Parse(new UdpDatagram(packet.Buffer,
                packet.RemoteEndPoint, DateTimeOffset.UtcNow)).Event!).ToArray();
            Assert.Equal(new[] { "123401", "234501", "345601" }, events.Select(item => item.DetectorId));
            Assert.InRange((events[1].Timestamp - events[0].Timestamp).TotalSeconds, .0033, .0034);
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public void Command_RequiresPortAndRecognizesDiscoveryOptions()
    {
        var root = new RootCommand { new GenerateCommand() };
        Assert.NotEmpty(root.Parse("generate --list-targets").Errors);
        Assert.Empty(root.Parse("generate --host 127.0.0.1 --port 12000 --rate 2000 --duration 60 --channel 02 --list-targets").Errors);
    }
}
