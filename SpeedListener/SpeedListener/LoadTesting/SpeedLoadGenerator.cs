using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace SpeedListener.LoadTesting;

/// <summary>Sends rate-paced events through one reusable UDP socket without a database.</summary>
public sealed class SpeedLoadGenerator
{
    /// <summary>Runs until duration, count, cancellation or a send error ends the test.</summary>
    public async Task<SpeedLoadTestResult> RunAsync(SpeedLoadTestOptions options,
        Action<SpeedLoadTestResult>? progress = null, CancellationToken cancellationToken = default)
    {
        options.Validate();
        var addresses = await Dns.GetHostAddressesAsync(options.Host, cancellationToken);
        var address = addresses.FirstOrDefault(ip => ip.AddressFamily == AddressFamily.InterNetwork) ?? addresses.First();
        using var socket = new UdpClient(address.AddressFamily);
        socket.Connect(new IPEndPoint(address, options.Port));
        var random = new Random(options.Seed);
        var startedAt = DateTimeOffset.UtcNow;
        var timer = Stopwatch.StartNew();
        var planned = Math.Min(options.Count ?? long.MaxValue, (long)Math.Ceiling(options.Duration.TotalSeconds * options.EventsPerSecond));
        var nextReport = TimeSpan.FromSeconds(1);
        var sent = 0L;
        var bytes = 0L;
        var failures = 0L;
        var maxLateness = TimeSpan.Zero;
        var stopReason = "running";
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        runCancellation.CancelAfter(options.Duration);
        try
        {
            while (sent < planned)
            {
                runCancellation.Token.ThrowIfCancellationRequested();
                if (timer.Elapsed >= options.Duration) { stopReason = "duration"; break; }
                var due = TimeSpan.FromSeconds(sent / (double)options.EventsPerSecond);
                var wait = due - timer.Elapsed;
                if (wait > TimeSpan.Zero) await Task.Delay(wait, runCancellation.Token);
                if (timer.Elapsed >= options.Duration) { stopReason = "duration"; break; }
                var lateness = timer.Elapsed - due;
                if (lateness > maxLateness) maxLateness = lateness;
                var detector = options.DetectorIds[(int)(sent % options.DetectorIds.Count)];
                var mph = random.Next(options.MinMph, options.MaxMph + 1);
                var timestamp = options.IncludeTimestamps ? startedAt.Add(due) : (DateTimeOffset?)null;
                var packet = SpeedTestPacket.Create(detector, mph, options.Format, timestamp);
                try { bytes += await socket.SendAsync(packet, runCancellation.Token); }
                catch (SocketException) { failures++; stopReason = "send-error"; break; }
                sent++;
                if (timer.Elapsed >= nextReport)
                {
                    progress?.Invoke(Snapshot());
                    nextReport = timer.Elapsed + TimeSpan.FromSeconds(1);
                }
            }
        }
        catch (OperationCanceledException) when (runCancellation.IsCancellationRequested)
        {
            stopReason = cancellationToken.IsCancellationRequested ? "cancelled" : "duration";
        }
        if (stopReason == "running") stopReason = options.Count.HasValue && sent >= options.Count ? "count" : "duration";
        return Snapshot();

        SpeedLoadTestResult Snapshot() => new(sent, bytes, failures, timer.Elapsed, maxLateness, stopReason,
            (int)Math.Min(sent, options.DetectorIds.Count), options.DetectorIds.Count);
    }
}

/// <summary>Sender-side counters; a successful UDP send does not acknowledge receipt or archiving.</summary>
public sealed record SpeedLoadTestResult(long Sent, long Bytes, long SendFailures, TimeSpan Elapsed,
    TimeSpan MaxLateness, string StopReason, int TargetsTouched, int TargetsTotal)
{
    /// <summary>Gets the achieved average event send rate.</summary>
    public double EventsPerSecond => Elapsed.TotalSeconds > 0 ? Sent / Elapsed.TotalSeconds : 0;
}
