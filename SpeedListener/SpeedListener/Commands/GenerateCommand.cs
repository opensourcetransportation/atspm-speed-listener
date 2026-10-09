using SpeedListener.LoadTesting;
using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;

namespace SpeedListener.Commands;

/// <summary>Database-discovered synthetic UDP load test command.</summary>
public sealed class GenerateCommand : Command
{
    /// <summary>Creates the generator command and its deployment-independent options.</summary>
    public GenerateCommand() : base("generate", "Discover current speed devices and generate rate-paced synthetic UDP events")
    {
        var host = new Option<string>("--host", () => "127.0.0.1", "Destination hostname or IP address.");
        var port = new Option<int>("--port", "Destination UDP port (required).") { IsRequired = true };
        var detectors = new Option<string>("--detectors", "Comma-separated six-digit detector IDs, cycled in order.");
        var file = new Option<string>("--detectors-file", "Text file of detector IDs, separated by whitespace or commas; # comments supported.");
        var rate = new Option<int?>("--rate", "Aggregate events/second across all targets; alternative to --rate-per-device.");
        var perDevice = new Option<int?>("--rate-per-device", "Events/second for each device (default 1); alternative to --rate.");
        var duration = new Option<double>("--duration", () => 60, "Maximum duration in seconds (up to 86400).");
        var count = new Option<long?>("--count", "Optional maximum event count; stops at count or duration, whichever comes first.");
        var min = new Option<int>("--min-mph", () => 20, "Minimum generated MPH.");
        var max = new Option<int>("--max-mph", () => 80, "Maximum generated MPH, inclusive (at most 158).");
        var seed = new Option<int>("--seed", () => 1, "Random speed seed for repeatable tests.");
        var format = new Option<SpeedTestPacketFormat>("--format", () => SpeedTestPacketFormat.Prefixed, "Packet layout: prefixed or compact.");
        var noTimestamps = new Option<bool>("--no-timestamps", "Omit synthetic timestamps to use the listener's receipt time.");
        var channel = new Option<string>("--channel", () => "01", "Two-digit synthetic channel appended to each discovered location ID.");
        var listTargets = new Option<bool>("--list-targets", "Discover and list targets without sending UDP packets.");
        foreach (var option in new Option[] { host, port, detectors, file, rate, perDevice, duration, count, min, max, seed, format, noTimestamps, channel, listTargets }) AddOption(option);
        this.SetHandler(async context =>
        {
            var parse = context.ParseResult;
            var ids = new List<string>();
            try
            {
                AddIds(parse.GetValueForOption(detectors));
                var path = parse.GetValueForOption(file);
                if (!string.IsNullOrWhiteSpace(path))
                    foreach (var line in File.ReadLines(path)) AddIds(line.Split('#')[0]);
                var seconds = parse.GetValueForOption(duration);
                if (!double.IsFinite(seconds) || seconds <= 0 || seconds > 86400)
                    throw new ArgumentException("Duration must be greater than zero and at most 86400 seconds.");
                var aggregateRate = parse.GetValueForOption(rate);
                var deviceRate = parse.GetValueForOption(perDevice);
                if (aggregateRate.HasValue && deviceRate.HasValue)
                    throw new ArgumentException("Choose --rate or --rate-per-device, not both.");
                if (deviceRate is <= 0) throw new ArgumentException("Rate per device must be positive.");
                var options = new SpeedLoadTestOptions
                {
                    Host = parse.GetValueForOption(host)!, Port = parse.GetValueForOption(port), DetectorIds = ids.ToArray(),
                    EventsPerSecond = aggregateRate ?? 1, Duration = TimeSpan.FromSeconds(seconds),
                    Count = parse.GetValueForOption(count), MinMph = parse.GetValueForOption(min), MaxMph = parse.GetValueForOption(max),
                    Seed = parse.GetValueForOption(seed), Format = parse.GetValueForOption(format), IncludeTimestamps = !parse.GetValueForOption(noTimestamps)
                };
                options.Validate(requireTargets: false);
                var explicitTargets = parse.FindResultFor(detectors) is { IsImplicit: false } || parse.FindResultFor(file) is { IsImplicit: false };
                if (explicitTargets && ids.Count == 0) throw new ArgumentException("The supplied detector list is empty.");
                if (ids.Count == 0)
                {
                    using var discoveryHost = HostBootstrapper.BuildGeneratorHost();
                    await using var scope = discoveryHost.Services.CreateAsyncScope();
                    var discovery = await scope.ServiceProvider.GetRequiredService<DatabaseSpeedTargetProvider>()
                        .LoadAsync(parse.GetValueForOption(channel)!, context.GetCancellationToken());
                    ids.AddRange(discovery.Targets.Select(target => target.DetectorId));
                    Console.WriteLine($"GeneratorTargets RoutableDevices={discovery.Targets.Count} ExtraDevicesAtSameLocation={discovery.ExtraDevicesAtSameLocation} InvalidLocationIdentifiers={discovery.InvalidLocationIdentifiers}");
                    if (discovery.ExtraDevicesAtSameLocation > 0)
                        Console.WriteLine("Additional speed devices at the same location cannot be addressed separately: the listener selects the first current speed device.");
                    if (parse.GetValueForOption(listTargets))
                        foreach (var target in discovery.Targets)
                            Console.WriteLine($"Location={target.LocationIdentifier} DeviceId={target.DeviceId} DetectorId={target.DetectorId}");
                }
                var targets = ids.Distinct().ToArray();
                var effectiveRate = aggregateRate ?? (long)targets.Length * (deviceRate ?? 1);
                if (effectiveRate > 1_000_000) throw new ArgumentException("Aggregate rate must not exceed 1000000 events/second.");
                options = options with
                {
                    DetectorIds = targets, EventsPerSecond = (int)effectiveRate
                };
                options.Validate();
                if (parse.GetValueForOption(listTargets))
                {
                    Console.WriteLine($"Discovery complete: {options.DetectorIds.Count} targets; no packets sent.");
                    return;
                }
                Console.WriteLine($"Synthetic load: UDP {options.Host}:{options.Port}; targets={targets.Length}; requestedRate={options.EventsPerSecond}/s; duration={seconds}s; timestamps={options.IncludeTimestamps}");
                if (Math.Min(options.Count ?? long.MaxValue, Math.Ceiling(seconds * options.EventsPerSecond)) < targets.Length)
                    Console.WriteLine("The selected count/duration cannot reach every target; check TargetsTouched in the final summary.");
                Console.WriteLine("Synthetic events may be archived by the listener. Use detector IDs mapped in your test database.");
                var result = await new SpeedLoadGenerator().RunAsync(options, Report, context.GetCancellationToken());
                Report(result);
                context.ExitCode = result.SendFailures > 0 ? 1 : result.StopReason == "cancelled" ? 130 : 0;
            }
            catch (Exception exception) when (exception is ArgumentException or IOException or System.Net.Sockets.SocketException)
            {
                Console.Error.WriteLine(exception.Message);
                context.ExitCode = 1;
            }

            void AddIds(string? text)
            {
                if (!string.IsNullOrWhiteSpace(text)) ids.AddRange(text.Split([',', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            }
        });
    }

    private static void Report(SpeedLoadTestResult result) => Console.WriteLine(FormattableString.Invariant(
        $"GeneratorSummary Sent={result.Sent} Bytes={result.Bytes} SendFailures={result.SendFailures} TargetsTouched={result.TargetsTouched} TargetsTotal={result.TargetsTotal} ElapsedSeconds={result.Elapsed.TotalSeconds:F3} ActualEventsPerSecond={result.EventsPerSecond:F1} MaxLatenessMs={result.MaxLateness.TotalMilliseconds:F1} StopReason={result.StopReason}"));
}
