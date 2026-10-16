namespace SpeedListener.Services;

/// <summary>Reads the location zones in the current mapping snapshot without database work per packet.</summary>
public interface ILocationTimeZoneProvider
{
    /// <summary>Gets a mapped location's zone, or null when that location is not currently mapped.</summary>
    TimeZoneInfo? GetTimeZone(string locationIdentifier);
}
