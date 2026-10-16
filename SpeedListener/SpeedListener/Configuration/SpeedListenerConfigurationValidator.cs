using Microsoft.Extensions.Options;
using System.Net;

namespace SpeedListener.Configuration;

/// <summary>Reports actionable per-setting errors for listener and generator options.</summary>
public sealed class SpeedListenerConfigurationValidator : IValidateOptions<SpeedListenerConfiguration>
{
    /// <inheritdoc/>
    public ValidateOptionsResult Validate(string? name, SpeedListenerConfiguration options)
    {
        var failures = new List<string>();
        void Require(bool valid, string setting, string message)
        {
            if (!valid) failures.Add($"{nameof(SpeedListenerConfiguration)}.{setting}: {message}");
        }
        try { SpeedListener.Services.LocationTimeZoneResolver.ResolveId(options.EventTimeZoneId); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException or ArgumentException)
        { Require(false, nameof(options.EventTimeZoneId), "must be Local for server time or an installed time zone matching the agency database."); }
        Require(options.UdpPort is > 0 and <= 65535, nameof(options.UdpPort), "must be between 1 and 65535.");
        Require(options.ChannelCapacity > 0, nameof(options.ChannelCapacity), "must be positive.");
        Require(options.BatchSize > 0 && options.BatchSize <= options.ChannelCapacity, nameof(options.BatchSize), "must be positive and no larger than ChannelCapacity.");
        Require(options.FlushInterval > TimeSpan.Zero, nameof(options.FlushInterval), "must be positive.");
        Require(options.ShutdownFlushTimeout > TimeSpan.Zero, nameof(options.ShutdownFlushTimeout), "must be positive.");
        Require(options.MaxWriteAttempts > 0, nameof(options.MaxWriteAttempts), "must be positive.");
        Require(options.ShutdownMaxWriteAttempts > 0 && options.ShutdownMaxWriteAttempts <= options.MaxWriteAttempts,
            nameof(options.ShutdownMaxWriteAttempts), "must be positive and no larger than MaxWriteAttempts.");
        Require(options.DeviceMappingRefreshInterval > TimeSpan.Zero, nameof(options.DeviceMappingRefreshInterval), "must be positive.");
        Require(options.ArchiveParallelism > 0, nameof(options.ArchiveParallelism), "must be positive.");
        Require(options.DatabaseWriteParallelism > 0, nameof(options.DatabaseWriteParallelism), "must be positive.");
        Require(options.WriteTimeout > TimeSpan.Zero, nameof(options.WriteTimeout), "must be positive.");
        Require(options.PoisonDeviceFailureThreshold > 0, nameof(options.PoisonDeviceFailureThreshold), "must be positive.");
        Require(options.SummaryInterval > TimeSpan.Zero, nameof(options.SummaryInterval), "must be positive.");

        if (options.ShutdownMaxWriteAttempts > 0 && options.WriteTimeout > TimeSpan.Zero)
        {
            var fits = options.WriteTimeout.Ticks <= TimeSpan.MaxValue.Ticks / options.ShutdownMaxWriteAttempts;
            Require(fits, nameof(options.WriteTimeout), "multiplied by ShutdownMaxWriteAttempts exceeds the supported duration.");
            if (fits)
            {
                var budget = TimeSpan.FromTicks(options.WriteTimeout.Ticks * options.ShutdownMaxWriteAttempts);
                var retries = options.ShutdownMaxWriteAttempts - 1;
                var exponentialRetries = Math.Min(retries, 8);
                var retryDelayMs = 200 * (Math.Pow(2, exponentialRetries) - 1) + 100 * exponentialRetries
                    + (retries - exponentialRetries) * 30_100d;
                Require(options.ShutdownFlushTimeout.TotalMilliseconds - budget.TotalMilliseconds > retryDelayMs,
                    nameof(options.ShutdownFlushTimeout), "must exceed WriteTimeout * ShutdownMaxWriteAttempts plus retry delays; this is the minimum budget for one publish, not the entire queue.");
            }
        }

        var mappings = options.UntaggedSpeedDetectorMappings;
        Require(mappings is not null, nameof(options.UntaggedSpeedDetectorMappings), "must be a list, even when empty.");
        var endpoints = new HashSet<string>(StringComparer.Ordinal);
        if (mappings is not null)
        {
            for (var i = 0; i < mappings.Count; i++)
            {
                var mapping = mappings[i];
                var prefix = $"{nameof(options.UntaggedSpeedDetectorMappings)}[{i}]";
                if (mapping is null) { Require(false, prefix, "must be an endpoint mapping object."); continue; }
                var addressValid = SourceAddressValidation.IsSupported(mapping.SourceAddress);
                Require(addressValid, $"{prefix}.SourceAddress", "must be a canonical IPv4 or supported IPv6 address.");
                var portValid = mapping.SourcePort is > 0 and <= 65535;
                Require(portValid, $"{prefix}.SourcePort", "must be between 1 and 65535.");
                Require(mapping.DetectorId is { Length: 6 } && mapping.DetectorId.All(c => c is >= '0' and <= '9'),
                    $"{prefix}.DetectorId", "must contain six ASCII digits: four location digits and two channel digits.");
                if (addressValid && portValid)
                    Require(endpoints.Add(SourceAddressValidation.EndpointKey(IPAddress.Parse(mapping.SourceAddress), mapping.SourcePort)),
                        prefix, "duplicates a source endpoint already configured in this list.");
            }
        }
        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
