using Utah.Udot.NetStandardToolkit.Extensions;
using Utah.Udot.NetStandardToolkit.Interfaces;

namespace SpeedListener.Services;

/// <summary>Uses the ATSPM toolkit coordinate lookup also used by its downloader.</summary>
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
            var id = new Coordinates { Latitude = latitude, Longitude = longitude }.GetTimeZoneFromCoordinates();
            return ResolveId(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        {
            return null;
        }
    }

    // Windows Server/NLS can lack ICU-backed IANA lookup. Bundled identifier
    // mappings select the Windows registry zone; TimeZoneInfo still converts dates.
    internal static TimeZoneInfo ResolveId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (OperatingSystem.IsWindows() && id.Contains('/'))
            id = TimeZoneConverter.TZConvert.IanaToWindows(id);
        return TimeZoneInfo.FindSystemTimeZoneById(id);
    }

    private sealed class Coordinates : ICoordinates
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }
}
