namespace SpeedListener.Services;

/// <summary>Provides normalized speed-sensor device mappings.</summary>
public interface IDeviceMappingProvider
{
    /// <summary>Refreshes mappings from ATSPM configuration storage.</summary>
    Task RefreshAsync(CancellationToken cancellationToken);
    /// <summary>Gets the current normalized mappings.</summary>
    Task<IReadOnlyDictionary<string, DeviceMapping>> GetMappingsAsync(CancellationToken cancellationToken);
}
