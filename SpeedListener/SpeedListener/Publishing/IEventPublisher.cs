namespace SpeedListener.Publishing;

/// <summary>Publishes event envelopes to their persistence destination.</summary>
public interface IEventPublisher<T>
{
    /// <summary>Publishes one item.</summary>
    Task PublishAsync(T message, CancellationToken cancellationToken = default);
    /// <summary>Publishes a batch of items.</summary>
    Task PublishAsync(IReadOnlyList<T> batch, int parallelism, CancellationToken cancellationToken = default);
}
