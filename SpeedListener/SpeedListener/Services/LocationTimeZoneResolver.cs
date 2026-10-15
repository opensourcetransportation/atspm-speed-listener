namespace SpeedListener.Services;

/// <summary>Resolves ATSPM location coordinates using the same GeoTimeZone lookup as its device importer.</summary>
internal static class LocationTimeZoneResolver
{
    internal static TimeZoneInfo? Resolve(double latitude, double longitude)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude) ||
            latitude is < -90 or > 90 || longitude is < -180 or > 180 ||
            latitude == 0 && longitude == 0)
            return null;
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(
                GeoTimeZone.TimeZoneLookup.GetTimeZone(latitude, longitude).Result);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
    }
}
