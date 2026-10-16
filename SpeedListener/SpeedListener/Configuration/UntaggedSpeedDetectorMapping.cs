namespace SpeedListener.Configuration;

/// <summary>Explicit source endpoint identity for a basic untagged XS message.</summary>
public sealed class UntaggedSpeedDetectorMapping
{
    /// <summary>Gets or sets the source IPv4 or IPv6 address, without a port.</summary>
    public string SourceAddress { get; set; } = string.Empty;
    /// <summary>Gets or sets the UDP source port.</summary>
    public int SourcePort { get; set; }
    /// <summary>Gets or sets the six-digit ATSPM detector identifier.</summary>
    public string DetectorId { get; set; } = string.Empty;
}
