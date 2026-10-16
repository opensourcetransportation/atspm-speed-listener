namespace SpeedListener.Services;

/// <summary>Maps four-character location identifiers to current-version speed devices.</summary>
public interface IDeviceMappingProvider
{
    /// <summary>Refreshes mappings from ATSPM configuration storage.</summary>
    Task RefreshAsync(CancellationToken cancellationToken);
    /// <summary>Gets the current normalized mappings.</summary>
    Task<IReadOnlyDictionary<string, DeviceMapping>> GetMappingsAsync(CancellationToken cancellationToken);
}
