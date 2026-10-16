namespace SpeedListener.LoadTesting;

/// <summary>Agency-independent limits and destination for synthetic UDP events.</summary>
public sealed record SpeedLoadTestOptions
{
    /// <summary>Gets the destination hostname or IP address.</summary>
    public string Host { get; init; } = "127.0.0.1";
    /// <summary>Gets the required destination UDP port.</summary>
    public int Port { get; init; }
    /// <summary>Gets the six-digit detector IDs, cycled in order.</summary>
    public IReadOnlyList<string> DetectorIds { get; init; } = [];
    /// <summary>Gets the requested aggregate events per second.</summary>
    public int EventsPerSecond { get; init; } = 1000;
    /// <summary>Gets the maximum run duration.</summary>
    public TimeSpan Duration { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>Gets the optional maximum event count.</summary>
    public long? Count { get; init; }
    /// <summary>Gets the minimum generated speed.</summary>
    public int MinMph { get; init; } = 20;
    /// <summary>Gets the maximum generated speed, inclusive.</summary>
    public int MaxMph { get; init; } = 80;
    /// <summary>Gets the deterministic random speed seed.</summary>
    public int Seed { get; init; } = 1;
    /// <summary>Gets the packet layout.</summary>
    public SpeedTestPacketFormat Format { get; init; } = SpeedTestPacketFormat.Prefixed;
    /// <summary>Gets whether unique synthetic UTC event times are appended.</summary>
    public bool IncludeTimestamps { get; init; } = true;

    /// <summary>Rejects invalid settings before opening a socket.</summary>
    public void Validate(bool requireTargets = true)
    {
        if (string.IsNullOrWhiteSpace(Host)) throw new ArgumentException("Host is required.");
        if (Port is < 1 or > 65535) throw new ArgumentException("Port must be between 1 and 65535.");
        if ((requireTargets && DetectorIds.Count == 0) || DetectorIds.Any(id => id.Length != 6 || id.Any(c => c is < '0' or > '9')))
            throw new ArgumentException("Supply at least one six-digit numeric detector ID.");
        if (EventsPerSecond is < 1 or > 1_000_000) throw new ArgumentException("Rate must be between 1 and 1000000 events/second.");
        if (Duration <= TimeSpan.Zero || Duration > TimeSpan.FromDays(1)) throw new ArgumentException("Duration must be greater than zero and at most 86400 seconds.");
        if (Count is <= 0) throw new ArgumentException("Count must be positive.");
        // Both MPH and converted KPH must fit the one-byte wire fields.
        if (MinMph < 0 || MaxMph < MinMph || MaxMph > 158) throw new ArgumentException("Speeds must satisfy 0 <= min-mph <= max-mph <= 158.");
        if (!Enum.IsDefined(Format)) throw new ArgumentException("Format must be prefixed or compact.");
    }
}

/// <summary>Supported tagged XS wire layouts.</summary>
public enum SpeedTestPacketFormat
{
    /// <summary>Z plus five sensor digits before XS.</summary>
    Prefixed,
    /// <summary>XS without the six-byte sensor prefix.</summary>
    Compact
}
