using Microsoft.Extensions.Logging.Abstractions;
using SpeedListener.Receivers;
using System.Net;
using System.Net.Sockets;

namespace SpeedListener.Tests;

public sealed class UdpDatagramReceiverTests
{
    [Fact]
    public async Task ReceiveAsync_ReceivesLoopbackDatagramAndStopsOnCancellation()
    {
        int port;
        using (var reservation = new UdpClient(0))
            port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;

        using var receiver = new UdpDatagramReceiver(
            port, TimeProvider.System, NullLogger<UdpDatagramReceiver>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = new TaskCompletionSource<UdpDatagram>(TaskCreationOptions.RunContinuationsAsynchronously);
        var receiveTask = receiver.ReceiveAsync((datagram, _) =>
        {
            received.TrySetResult(datagram);
            return ValueTask.CompletedTask;
        }, cancellation.Token);

        using var sender = new UdpClient();
        await sender.SendAsync([1, 2, 3], 3, new IPEndPoint(IPAddress.Loopback, port));
        var datagram = await received.Task.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await receiveTask;

        Assert.Equal([1, 2, 3], datagram.Buffer);
        Assert.Equal(IPAddress.Loopback, ((IPEndPoint)datagram.RemoteEndPoint).Address);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DualStackReceivesIpv4AndIpv6AndMatchesSourceMappings(bool mappedIpv4Configuration)
    {
        if (!Socket.OSSupportsIPv6) return;
        int port;
        using (var reservation = new UdpClient(AddressFamily.InterNetworkV6))
        {
            reservation.Client.DualMode = true;
            reservation.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
            port = ((IPEndPoint)reservation.Client.LocalEndPoint!).Port;
        }
        using var sender4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var sender6 = new UdpClient(new IPEndPoint(IPAddress.IPv6Loopback, 0));
        var source4 = (IPEndPoint)sender4.Client.LocalEndPoint!;
        var source6 = (IPEndPoint)sender6.Client.LocalEndPoint!;
        var parser = new SpeedListener.Parsing.SpeedPacketParser(Microsoft.Extensions.Options.Options.Create(
            new SpeedListener.Configuration.SpeedListenerConfiguration
            {
                UntaggedSpeedDetectorMappings = new() {
                    new() { SourceAddress = mappedIpv4Configuration ? "::ffff:127.0.0.1" : "127.0.0.1", SourcePort = source4.Port, DetectorId = "502620" },
                    new() { SourceAddress = "::1", SourcePort = source6.Port, DetectorId = "527148" }
                }
            }));
        using var receiver = new UdpDatagramReceiver(port, TimeProvider.System, NullLogger<UdpDatagramReceiver>.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var bothReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new System.Collections.Concurrent.ConcurrentBag<SpeedListener.Parsing.SpeedPacketParseResult>();
        var receiving = receiver.ReceiveAsync((datagram, _) => {
            results.Add(parser.Parse(datagram));
            if (results.Count == 2) bothReceived.TrySetResult();
            return ValueTask.CompletedTask;
        }, cancellation.Token);
        var payload = Convert.FromHexString("585332507E0D0D");
        await sender4.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.Loopback, port));
        await sender6.SendAsync(payload, payload.Length, new IPEndPoint(IPAddress.IPv6Loopback, port));
        try { await bothReceived.Task.WaitAsync(cancellation.Token); }
        finally { cancellation.Cancel(); await receiving; }
        Assert.All(results, result => Assert.True(result.IsSuccess));
        Assert.Equal(new[] { "502620", "527148" }, results.Select(result => result.Event!.DetectorId).OrderBy(id => id));
    }

}
