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
                    location.Id, location.LocationIdentifier, location.Start, location.Latitude, location.Longitude,
                    DeviceId = location.Devices.Where(device => device.DeviceType == DeviceTypes.SpeedSensor && device.DeviceStatus == DeviceStatus.Active)
                        .OrderBy(device => device.Id).Select(device => (int?)device.Id).FirstOrDefault()
                })
                .ToListAsync(cancellationToken);
            var updated = new Dictionary<string, DeviceMapping>(StringComparer.OrdinalIgnoreCase);
            var invalidCount = 0;
            var duplicateCount = 0;
            var fallbackCount = 0;
            var mismatchCount = 0;
            const int mismatchDetailLimit = 20;
            // Bound lookup work to unique coordinates in this refresh, not packet volume.
            var zones = new Dictionary<(double Latitude, double Longitude), TimeZoneInfo?>();

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
                var eligible = versions.Select(location =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var coordinates = (location.Latitude, location.Longitude);
                    if (!zones.TryGetValue(coordinates, out var resolved))
                        zones[coordinates] = resolved = LocationTimeZoneResolver.Resolve(coordinates.Latitude, coordinates.Longitude);
                    var zone = resolved ?? fallbackZone;
                    // Effective dates are location-local timestamp-without-zone values.
                    var wallClock = TimeZoneInfo.ConvertTime(instant, zone).DateTime;
                    return (Location: location, Zone: zone, UsesFallback: resolved is null, WallClock: wallClock);
                }).Where(candidate => candidate.Location.Start <= candidate.WallClock).ToArray();
                if (eligible.Length == 0) continue;
                var current = eligible.OrderByDescending(candidate => candidate.Location.Start)
                    .ThenByDescending(candidate => candidate.Location.Id).First();
                duplicateCount += eligible.Count(candidate => candidate.Location.Start == current.Location.Start) - 1;
                if (!current.Location.DeviceId.HasValue) continue;
                if (current.UsesFallback) fallbackCount++;
                else if (!current.Zone.HasSameRules(fallbackZone))
                {
                    mismatchCount++;
                    if (mismatchCount <= mismatchDetailLimit)
                        _log.TimeZoneMismatch(identifier, current.Zone.Id, fallbackZone.Id);
                }
                updated.Add(identifier, new DeviceMapping(current.Location.DeviceId.Value, identifier, current.Zone));
                if (logger.IsEnabled(LogLevel.Debug)) _log.LocationTimeZone(identifier, current.Zone.Id, current.UsesFallback);
            }

            // Fail an unconfigured startup, but a successful refresh may legitimately
            // remove every mapping when no eligible location/device remains.
            if (updated.Count == 0 && _mappings is null)
                throw new InvalidOperationException(
                    $"No valid speed-sensor mappings were found ({invalidCount} invalid, {duplicateCount} duplicate rows).");

            Volatile.Write(ref _mappings, updated);
            _loadedAt = timeProvider.GetUtcNow();
            metrics.RecordMappingRefresh();
            _log.MappingsLoaded(updated.Count, invalidCount, duplicateCount);
            if (fallbackCount > 0) _log.TimeZoneFallback(fallbackCount, fallbackZone.Id);
            if (mismatchCount > 0) _log.TimeZoneMismatchSummary(mismatchCount, mismatchDetailLimit,
                Math.Max(0, mismatchCount - mismatchDetailLimit));
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
