namespace SpeedListener.Services;

/// <summary>Resolves the server-local zone or an explicitly configured override.</summary>
internal static class LocationTimeZoneResolver
{
    // The library accepts native and alternate-platform identifiers and selects
    // available OS rules without relying on Windows ICU IANA lookup.
    internal static TimeZoneInfo ResolveId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        if (string.Equals(id, "Local", StringComparison.OrdinalIgnoreCase)) return TimeZoneInfo.Local;
        return TimeZoneConverter.TZConvert.GetTimeZoneInfo(id);
    }
}
