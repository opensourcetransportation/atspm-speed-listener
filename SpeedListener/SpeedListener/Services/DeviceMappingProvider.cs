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
    ILogger<DeviceMappingProvider> logger) : IDeviceMappingProvider, ILocationTimeZoneProvider
{
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private readonly SpeedListenerLogMessages _log = new(logger);
    private IReadOnlyDictionary<string, DeviceMapping>? _mappings;
    private DateTimeOffset _loadedAt;

    /// <inheritdoc/>
    public TimeZoneInfo? GetTimeZone(string locationIdentifier) =>
        Volatile.Read(ref _mappings)?.GetValueOrDefault(locationIdentifier)?.TimeZone;

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
            var fallbackZone = LocationTimeZoneResolver.ResolveId(options.Value.EventTimeZoneId);
            var instant = timeProvider.GetUtcNow();
            var locations = await context.Locations
                .AsNoTracking()
                .Where(location => location.VersionAction != LocationVersionActions.Delete)
                .Select(location => new
                {
                    location.Id, location.LocationIdentifier, location.Start,
                    DeviceId = location.Devices.Where(device => device.DeviceType == DeviceTypes.SpeedSensor && device.DeviceStatus == DeviceStatus.Active)
                        .OrderBy(device => device.Id).Select(device => (int?)device.Id).FirstOrDefault()
                })
                .ToListAsync(cancellationToken);
            var updated = new Dictionary<string, DeviceMapping>(StringComparer.OrdinalIgnoreCase);
            var invalidCount = 0;
            var duplicateCount = 0;
            var wallClock = TimeZoneInfo.ConvertTime(instant, fallbackZone).DateTime;

            foreach (var versions in locations.GroupBy(location => location.LocationIdentifier?.Trim() ?? string.Empty,
                         StringComparer.OrdinalIgnoreCase))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var identifier = versions.Key;
                if (identifier.Length != 4 || identifier.Any(c => c is < '0' or > '9'))
                {
                    invalidCount++;
                    continue;
                }
                var eligible = versions.Where(location => location.Start <= wallClock).ToArray();
                if (eligible.Length == 0) continue;
                var current = eligible.OrderByDescending(location => location.Start)
                    .ThenByDescending(location => location.Id).First();
                duplicateCount += eligible.Count(location => location.Start == current.Start) - 1;
                if (!current.DeviceId.HasValue) continue;
                updated.Add(identifier, new DeviceMapping(current.DeviceId.Value, identifier, fallbackZone));
                if (logger.IsEnabled(LogLevel.Debug)) _log.LocationTimeZone(identifier, fallbackZone.Id, false);
            }

            // Fail an unconfigured startup, but a successful refresh may legitimately
            // remove every mapping when no eligible location/device remains.
            if (updated.Count == 0 && _mappings is null)
                throw new InvalidOperationException(
                    $"No valid speed-sensor mappings were found ({invalidCount} invalid, {duplicateCount} duplicate rows).");

            if (_mappings is null) _log.EventTimeZone(options.Value.EventTimeZoneId, fallbackZone.Id);
            Volatile.Write(ref _mappings, updated);
            _loadedAt = timeProvider.GetUtcNow();
            metrics.RecordMappingRefresh();
            _log.MappingsLoaded(updated.Count, invalidCount, duplicateCount);
            if (invalidCount > 0 || duplicateCount > 0)
                _log.MappingValidationWarning(invalidCount, duplicateCount);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
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
