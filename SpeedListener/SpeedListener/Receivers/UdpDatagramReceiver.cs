using Microsoft.Extensions.Logging;
using System.Net.Sockets;

namespace SpeedListener.Receivers;

/// <summary>Cancellation-aware UDP datagram receiver.</summary>
public sealed class UdpDatagramReceiver(int port, TimeProvider timeProvider, ILogger<UdpDatagramReceiver> logger)
    : IUdpDatagramReceiver
{
    private readonly UdpClient _client = new(port);
    private bool _disposed;

    /// <inheritdoc/>
    public async Task ReceiveAsync(
        Func<UdpDatagram, CancellationToken, ValueTask> onDatagram,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("UDP speed listener bound to port {Port}", port);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _client.ReceiveAsync(cancellationToken);
                await onDatagram(
                    new UdpDatagram(result.Buffer, result.RemoteEndPoint, timeProvider.GetUtcNow()),
                    cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || _disposed)
        {
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}
