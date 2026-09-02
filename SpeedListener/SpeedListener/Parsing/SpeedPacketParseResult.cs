using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Parsing;

/// <summary>Result of parsing a speed sensor packet.</summary>
public sealed record SpeedPacketParseResult(SpeedEvent? Event, string? Error)
{
    /// <summary>Gets whether parsing succeeded.</summary>
    public bool IsSuccess => Event is not null;
    /// <summary>Creates a successful result.</summary>
    public static SpeedPacketParseResult Success(SpeedEvent speedEvent) => new(speedEvent, null);
    /// <summary>Creates a failed result.</summary>
    public static SpeedPacketParseResult Failure(string error) => new(null, error);
}
