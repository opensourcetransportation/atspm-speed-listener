namespace SpeedListener.Receivers;

/// <summary>Receives UDP datagrams until cancellation.</summary>
public interface IUdpDatagramReceiver : IDisposable
{
    /// <summary>Receives datagrams until cancellation.</summary>
    Task ReceiveAsync(Func<UdpDatagram, CancellationToken, ValueTask> onDatagram, CancellationToken cancellationToken);
}
