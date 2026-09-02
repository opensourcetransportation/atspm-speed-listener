using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SpeedListener.Configuration;
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
            var devices = await context.Devices
                .AsNoTracking()
                .Where(d => d.DeviceType == DeviceTypes.SpeedSensor)
                .OrderBy(d => d.Id)
                .Select(d => new MappingRow(d.Id, d.DeviceIdentifier, d.Location.LocationIdentifier))
                .ToListAsync(cancellationToken);
            var updated = new Dictionary<string, DeviceMapping>(StringComparer.OrdinalIgnoreCase);
            var invalidCount = 0;
            var duplicateCount = 0;

            foreach (var device in devices)
            {
                var identifier = device.DeviceIdentifier?.Trim();
                if (string.IsNullOrWhiteSpace(identifier))
                {
                    invalidCount++;
                    continue;
                }
                if (string.IsNullOrWhiteSpace(device.LocationIdentifier))
                {
                    invalidCount++;
                    continue;
                }
                if (!updated.TryAdd(identifier, new DeviceMapping(device.Id, device.LocationIdentifier))) duplicateCount++;
            }

            _mappings = updated;
            _loadedAt = timeProvider.GetUtcNow();
            metrics.RecordMappingRefresh();
            logger.LogInformation(
                "Loaded {Count} speed-sensor device mappings; skipped {InvalidCount} invalid and {DuplicateCount} duplicate rows",
                updated.Count, invalidCount, duplicateCount);
            if (invalidCount > 0 || duplicateCount > 0)
                logger.LogWarning(
                    "Speed-sensor mapping validation found {InvalidCount} invalid and {DuplicateCount} duplicate rows; valid sensors remain active",
                    invalidCount, duplicateCount);
        }
        catch (Exception ex) when (_mappings is not null)
        {
            metrics.RecordMappingRefreshFailure();
            logger.LogWarning(ex, "Device mapping refresh failed; continuing with the last successful mapping");
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

    private sealed record MappingRow(int Id, string? DeviceIdentifier, string? LocationIdentifier);
}
