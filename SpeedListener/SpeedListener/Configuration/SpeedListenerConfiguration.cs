#region license
// Copyright 2026 Utah Departement of Transportation
// Licensed under the Apache License, Version 2.0.
#endregion

using Utah.Udot.NetStandardToolkit.Configuration;

namespace SpeedListener.Configuration;

/// <summary>Configuration for receiving and archiving speed sensor events.</summary>
[ConfigurationSection(nameof(SpeedListenerConfiguration), null)]
public sealed class SpeedListenerConfiguration
{
    /// <summary>Gets or sets the UDP bind port.</summary>
    public int UdpPort { get; set; } = 10088;
    /// <summary>Gets or sets the maximum number of queued events.</summary>
    public int ChannelCapacity { get; set; } = 100_000;
    /// <summary>Gets or sets the size-triggered flush threshold.</summary>
    public int BatchSize { get; set; } = 5_000;
    /// <summary>Gets or sets the maximum age of a partial batch.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Gets or sets the shutdown drain deadline.</summary>
    public TimeSpan ShutdownFlushTimeout { get; set; } = TimeSpan.FromSeconds(45);
    /// <summary>Gets or sets the maximum write attempts used while draining during shutdown.</summary>
    public int ShutdownMaxWriteAttempts { get; set; } = 1;
    /// <summary>Gets or sets the device mapping cache lifetime.</summary>
    public TimeSpan DeviceMappingRefreshInterval { get; set; } = TimeSpan.FromMinutes(5);
    /// <summary>Gets or sets archive transformation parallelism.</summary>
    public int ArchiveParallelism { get; set; } = 50;
    /// <summary>Gets or sets the timeout for one database write attempt.</summary>
    public TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(30);
    /// <summary>Gets or sets the maximum database write attempts.</summary>
    public int MaxWriteAttempts { get; set; } = 3;
    /// <summary>Gets or sets the consecutive poison-batch drops allowed for one device before failing.</summary>
    public int PoisonDeviceFailureThreshold { get; set; } = 3;
    /// <summary>Gets or sets the interval between machine-parseable operational summaries.</summary>
    public TimeSpan SummaryInterval { get; set; } = TimeSpan.FromMinutes(1);
}
