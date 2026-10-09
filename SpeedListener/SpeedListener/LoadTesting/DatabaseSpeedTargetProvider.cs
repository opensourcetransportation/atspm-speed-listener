using Microsoft.EntityFrameworkCore;
using SpeedListener.Services;
using Utah.Udot.Atspm.Data;
using Utah.Udot.Atspm.Data.Enums;

namespace SpeedListener.LoadTesting;

/// <summary>Discovers routable current-version devices using the listener's mapping rules.</summary>
public sealed class DatabaseSpeedTargetProvider(IDeviceMappingProvider mappings, ConfigContext context)
{
    /// <summary>Creates one tagged detector per selected speed device, without changing configuration.</summary>
    public async Task<SpeedGeneratorTargets> LoadAsync(string channel = "01", CancellationToken cancellationToken = default)
    {
        if (channel.Length != 2 || channel.Any(c => c is < '0' or > '9'))
            throw new ArgumentException("Channel must contain two digits.", nameof(channel));
        var map = await mappings.GetMappingsAsync(cancellationToken);
        var selected = map.Values.ToDictionary(mapping => mapping.DeviceId);
        var selectedIds = selected.Keys.ToArray();
        var currentLocationIds = await context.Devices.AsNoTracking()
            .Where(device => selectedIds.Contains(device.Id))
            .Select(device => device.Location.Id).Distinct().ToListAsync(cancellationToken);
        var devices = await context.Devices.AsNoTracking()
            .Where(device => device.DeviceType == DeviceTypes.SpeedSensor && currentLocationIds.Contains(device.Location.Id))
            .Select(device => device.Id).ToListAsync(cancellationToken);
        var extraDevices = devices.Count(id => !selected.ContainsKey(id));
        var targets = map.Values.Where(mapping => mapping.LocationIdentifier.Length == 4 &&
                mapping.LocationIdentifier.All(c => c is >= '0' and <= '9'))
            .OrderBy(mapping => mapping.LocationIdentifier, StringComparer.Ordinal)
            .Select(mapping => new SpeedGeneratorTarget(mapping.LocationIdentifier, mapping.DeviceId, mapping.LocationIdentifier + channel)).ToArray();
        return new SpeedGeneratorTargets(targets, extraDevices, map.Count - targets.Length);
    }
}

/// <summary>Identifies a synthetic detector and the device to which the listener routes it.</summary>
public sealed record SpeedGeneratorTarget(string LocationIdentifier, int DeviceId, string DetectorId);
/// <summary>Discovery coverage, including devices that the current listener cannot address separately.</summary>
public sealed record SpeedGeneratorTargets(IReadOnlyList<SpeedGeneratorTarget> Targets, int ExtraDevicesAtSameLocation,
    int InvalidLocationIdentifiers);
