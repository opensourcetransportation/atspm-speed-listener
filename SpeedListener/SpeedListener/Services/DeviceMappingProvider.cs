using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
using SpeedListener.LogMessages;
using Utah.Udot.Atspm.Data;
using Utah.Udot.Atspm.Data.Enums;

namespace SpeedListener.Services;

/// <summary>Provides a periodically refreshed lookup of ATSPM speed-sensor devices.</summary>
public sealed class DeviceMappingProvider(
    IServiceScopeFactory scopeFactory,
    IOptions<SpeedListenerConfiguration> options,
    TimeProvider timeProvider,
    SpeedListenerMetrics metrics,
    ILogger<DeviceMappingProvider> logger) : IDeviceMappingProvider
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SpeedListenerLogMessages _log = new(logger);
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
            var context = scope.ServiceProvider.GetRequiredService<ConfigContext>();
            var now = timeProvider.GetUtcNow().UtcDateTime;
            var locations = await context.Locations
                .AsNoTracking()
                .Where(location => location.Start <= now && location.VersionAction != LocationVersionActions.Delete)
                .Select(location => new
                {
                    location.Id, location.LocationIdentifier, location.Start, location.VersionAction,
                    DeviceId = location.Devices.Where(device => device.DeviceType == DeviceTypes.SpeedSensor)
                        .OrderBy(device => device.Id).Select(device => (int?)device.Id).FirstOrDefault()
                })
                .ToListAsync(cancellationToken);
            var updated = new Dictionary<string, DeviceMapping>(StringComparer.OrdinalIgnoreCase);
            var invalidCount = 0;
            var duplicateCount = 0;

            foreach (var versions in locations.GroupBy(location => location.LocationIdentifier?.Trim() ?? string.Empty,
                         StringComparer.OrdinalIgnoreCase))
            {
                var identifier = versions.Key;
                if (identifier.Length != 4)
                {
                    invalidCount++;
                    continue;
                }
                var current = versions.OrderByDescending(location => location.Start)
                    .ThenByDescending(location => location.Id).First();
                if (!current.DeviceId.HasValue)
                    continue;
                updated.Add(identifier, new DeviceMapping(current.DeviceId.Value, identifier));
            }

            if (updated.Count == 0)
                throw new InvalidOperationException(
                    $"No valid speed-sensor mappings were found ({invalidCount} invalid, {duplicateCount} duplicate rows).");

            _mappings = updated;
            _loadedAt = timeProvider.GetUtcNow();
            metrics.RecordMappingRefresh();
            _log.MappingsLoaded(updated.Count, invalidCount, duplicateCount);
            if (invalidCount > 0 || duplicateCount > 0)
                _log.MappingValidationWarning(invalidCount, duplicateCount);
        }
        catch (Exception ex) when (_mappings is not null)
        {
            metrics.RecordMappingRefreshFailure();
            _log.MappingRefreshFailed(ex);
        }
        catch
        {
            metrics.RecordMappingRefreshFailure();
            throw;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

}
