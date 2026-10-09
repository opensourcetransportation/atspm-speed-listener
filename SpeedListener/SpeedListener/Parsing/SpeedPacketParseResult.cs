using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Parsing;

/// <summary>Result of parsing a speed sensor packet.</summary>
public sealed record SpeedPacketParseResult(SpeedEvent? Event, string? Error, bool IsActuation = false, bool IsUnmappedSpeed = false)
{
    /// <summary>Gets whether parsing succeeded.</summary>
    public bool IsSuccess => Event is not null;
    /// <summary>Creates a successful result.</summary>
    public static SpeedPacketParseResult Success(SpeedEvent speedEvent) => new(speedEvent, null);
    /// <summary>Creates a recognized actuation result; no speed event is emitted.</summary>
    public static SpeedPacketParseResult Actuation() => new(null, null, IsActuation: true);
    /// <summary>Creates a valid untagged speed result without a configured detector mapping.</summary>
    public static SpeedPacketParseResult UnmappedSpeed() => new(null,
        "Untagged XS speed message requires an exact source IP:port entry in UntaggedSpeedDetectorMappings.", IsUnmappedSpeed: true);
    /// <summary>Creates a failed result.</summary>
    public static SpeedPacketParseResult Failure(string error) => new(null, error);
}
