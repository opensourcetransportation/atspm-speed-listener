using Microsoft.Extensions.Logging;
using SpeedListener.LogMessages;
using System.Net.Sockets;
using System.Net;

namespace SpeedListener.Receivers;

/// <summary>Cancellation-aware UDP datagram receiver.</summary>
public sealed class UdpDatagramReceiver(int port, TimeProvider timeProvider, ILogger<UdpDatagramReceiver> logger)
    : IUdpDatagramReceiver
{
    private readonly UdpClient _client = BindSocket(port);
    private readonly SpeedListenerLogMessages _log = new(logger);
    private bool _disposed;

    /// <inheritdoc/>
    public async Task ReceiveAsync(
        Func<UdpDatagram, CancellationToken, ValueTask> onDatagram,
        CancellationToken cancellationToken)
    {
        _log.UdpBound(port);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var result = await _client.ReceiveAsync(cancellationToken);
                await onDatagram(
                    new UdpDatagram(result.Buffer,
                        result.RemoteEndPoint.Address.IsIPv4MappedToIPv6
                            ? new IPEndPoint(result.RemoteEndPoint.Address.MapToIPv4(), result.RemoteEndPoint.Port)
                            : result.RemoteEndPoint,
                        timeProvider.GetUtcNow()),
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

    private static UdpClient BindSocket(int port)
    {
        if (!Socket.OSSupportsIPv6) return new UdpClient(port);
        var client = new UdpClient(AddressFamily.InterNetworkV6);
        try
        {
            client.Client.DualMode = true;
            client.Client.Bind(new IPEndPoint(IPAddress.IPv6Any, port));
            return client;
        }
        catch { client.Dispose(); throw; }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}
