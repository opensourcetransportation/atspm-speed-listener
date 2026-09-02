using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
using Utah.Udot.Atspm.Data.Enums;
using Utah.Udot.Atspm.Repositories.ConfigurationRepositories;

namespace SpeedListener.Services;

/// <summary>Provides a periodically refreshed lookup of ATSPM speed-sensor devices.</summary>
public sealed class DeviceMappingProvider(
    IServiceScopeFactory scopeFactory,
    IOptions<SpeedListenerConfiguration> options,
    TimeProvider timeProvider,
    ILogger<DeviceMappingProvider> logger) : IDeviceMappingProvider
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private IReadOnlyDictionary<string, DeviceMapping>? _mappings;
    private DateTimeOffset _loadedAt;

    /// <inheritdoc/>
    public async Task<IReadOnlyDictionary<string, DeviceMapping>> GetMappingsAsync(CancellationToken cancellationToken)
    {
        if (_mappings is null || timeProvider.GetUtcNow() - _loadedAt >= options.Value.DeviceMappingRefreshInterval)
            await RefreshAsync(cancellationToken);
        return _mappings!;
    }

    /// <inheritdoc/>
    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (_mappings is not null && timeProvider.GetUtcNow() - _loadedAt < options.Value.DeviceMappingRefreshInterval)
                return;

            using var scope = scopeFactory.CreateScope();
            var repository = scope.ServiceProvider.GetRequiredService<IDeviceRepository>();
            var devices = repository.GetList()
                .Where(d => d.DeviceType == DeviceTypes.SpeedSensor)
                .OrderBy(d => d.Id)
                .ToList();
            var updated = new Dictionary<string, DeviceMapping>(StringComparer.OrdinalIgnoreCase);

            foreach (var device in devices)
            {
                var identifier = device.DeviceIdentifier?.Trim();
                if (string.IsNullOrWhiteSpace(identifier))
                    throw new InvalidOperationException($"Speed-sensor device {device.Id} has a blank identifier.");
                if (device.Location is null || string.IsNullOrWhiteSpace(device.Location.LocationIdentifier))
                    throw new InvalidOperationException($"Speed-sensor device {device.Id} has no location identifier.");
                if (!updated.TryAdd(identifier, new DeviceMapping(device.Id, device.Location.LocationIdentifier)))
                {
                    logger.LogWarning(
                        "Ignoring duplicate speed-sensor identifier {Identifier} on device {DeviceId}; using the lowest device id",
                        identifier,
                        device.Id);
                }
            }

            _mappings = updated;
            _loadedAt = timeProvider.GetUtcNow();
            logger.LogInformation("Loaded {Count} speed-sensor device mappings", updated.Count);
        }
        catch (Exception ex) when (_mappings is not null)
        {
            logger.LogWarning(ex, "Device mapping refresh failed; continuing with the last successful mapping");
        }
        finally
        {
            _refreshLock.Release();
        }
    }
}
