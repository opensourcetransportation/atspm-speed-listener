using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
using SpeedListener.Services;
using Utah.Udot.Atspm.Data;
using Utah.Udot.Atspm.Data.Enums;
using Utah.Udot.Atspm.Data.Models;

namespace SpeedListener.Tests;

public sealed class DeviceMappingProviderTests
{
    [Fact]
    public async Task GetMappingsAsync_LoadsOnlyNormalizedSpeedSensorMappings()
    {
        await using var services = CreateServices();
        await SeedAsync(services,
            Device(2, " camera ", DeviceTypes.AICamera, "L2"),
            Device(1, " sensor-1 ", DeviceTypes.SpeedSensor, "L1"));
        var provider = CreateProvider(services);

        var mappings = await provider.GetMappingsAsync(CancellationToken.None);

        var mapping = Assert.Single(mappings);
        Assert.Equal("sensor-1", mapping.Key);
        Assert.True(mappings.ContainsKey("SENSOR-1"));
        Assert.Equal(1, mapping.Value.DeviceId);
        Assert.Equal("L1", mapping.Value.LocationIdentifier);
    }

    [Fact]
    public async Task RefreshAsync_DuplicateNormalizedIdentifiers_KeepsLowestDeviceId()
    {
        await using var services = CreateServices();
        await SeedAsync(services,
            Device(2, "SENSOR-1", DeviceTypes.SpeedSensor, "L2"),
            Device(1, " sensor-1 ", DeviceTypes.SpeedSensor, "L1"));
        var provider = CreateProvider(services);

        var mappings = await provider.GetMappingsAsync(CancellationToken.None);

        var mapping = Assert.Single(mappings).Value;
        Assert.Equal(1, mapping.DeviceId);
        Assert.Equal("L1", mapping.LocationIdentifier);
    }

    [Fact]
    public async Task GetMappingsAsync_NoValidSpeedSensors_FailsInitialLoad()
    {
        await using var services = CreateServices();
        await SeedAsync(services, Device(1, "camera", DeviceTypes.AICamera, "L1"));
        var metrics = new SpeedListenerMetrics(TimeProvider.System);
        var provider = CreateProvider(services, metrics);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => provider.GetMappingsAsync(CancellationToken.None));

        Assert.Contains("No valid speed-sensor mappings", exception.Message);
        Assert.Equal(1, metrics.MappingRefreshFailures);
    }

    [Fact]
    public async Task RefreshAsync_AfterSuccessfulLoad_RetainsLastMappingWhenDatabaseFails()
    {
        var services = CreateServices();
        await SeedAsync(services, Device(1, "sensor-1", DeviceTypes.SpeedSensor, "L1"));
        var timeProvider = new MutableTimeProvider();
        var metrics = new SpeedListenerMetrics(timeProvider);
        var provider = CreateProvider(services, metrics, timeProvider);
        var initial = await provider.GetMappingsAsync(CancellationToken.None);
        await services.DisposeAsync();
        timeProvider.Advance(TimeSpan.FromMinutes(6));

        await provider.RefreshAsync(CancellationToken.None);
        var retained = await provider.GetMappingsAsync(CancellationToken.None);

        Assert.Same(initial, retained);
        Assert.Equal(1, retained["sensor-1"].DeviceId);
        Assert.Equal(2, metrics.MappingRefreshFailures);
    }

    private static ServiceProvider CreateServices()
    {
        var services = new ServiceCollection();
        var databaseName = $"mapping-tests-{Guid.NewGuid():N}";
        services.AddDbContext<ConfigContext>(options =>
            options.UseInMemoryDatabase(databaseName));
        return services.BuildServiceProvider();
    }

    private static DeviceMappingProvider CreateProvider(
        ServiceProvider services,
        SpeedListenerMetrics? metrics = null,
        TimeProvider? timeProvider = null) =>
        new(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SpeedListenerConfiguration
            {
                DeviceMappingRefreshInterval = TimeSpan.FromMinutes(5)
            }),
            timeProvider ?? TimeProvider.System,
            metrics ?? new SpeedListenerMetrics(TimeProvider.System),
            NullLogger<DeviceMappingProvider>.Instance);

    private static async Task SeedAsync(ServiceProvider services, params Device[] devices)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ConfigContext>();
        context.Devices.AddRange(devices);
        await context.SaveChangesAsync();
    }

    private static Device Device(int id, string identifier, DeviceTypes type, string locationIdentifier) => new()
    {
        Id = id,
        DeviceIdentifier = identifier,
        DeviceType = type,
        Ipaddress = "127.0.0.1",
        Location = new Location
        {
            Id = id,
            LocationIdentifier = locationIdentifier,
            PrimaryName = $"Location {locationIdentifier}",
            Note = string.Empty
        }
    };

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = new(2026, 9, 3, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan elapsed) => _utcNow += elapsed;
    }
}
