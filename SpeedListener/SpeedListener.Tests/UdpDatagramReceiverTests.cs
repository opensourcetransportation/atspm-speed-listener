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
}
