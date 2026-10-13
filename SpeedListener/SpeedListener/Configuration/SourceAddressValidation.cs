using System.Net;
using System.Net.Sockets;
using System.Globalization;

namespace SpeedListener.Configuration;

internal static class SourceAddressValidation
{
    internal static bool TryParse(string? text, out IPAddress? address)
    {
        address = null;
        if (string.IsNullOrEmpty(text) || !IPAddress.TryParse(text, out var parsed)) return false;
        if (parsed.AddressFamily == AddressFamily.InterNetwork)
        {
            if (!IsDottedQuad(text)) return false;
        }
        else if (parsed.AddressFamily != AddressFamily.InterNetworkV6 || !text.Contains(':') ||
                 text.Contains('.') && !IsDottedQuad(text[(text.LastIndexOf(':') + 1)..])) return false;
        address = parsed;
        return true;
    }

    private static bool IsDottedQuad(string text)
    {
        var parts = text.Split('.');
        return parts.Length == 4 && parts.All(part => part.Length is >= 1 and <= 3 &&
            (part.Length == 1 || part[0] != '0') && part.All(c => c is >= '0' and <= '9') &&
            byte.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _));
    }
}
