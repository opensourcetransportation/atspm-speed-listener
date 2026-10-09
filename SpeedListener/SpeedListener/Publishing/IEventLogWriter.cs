using Utah.Udot.Atspm.Data.Models;

namespace SpeedListener.Publishing;

/// <summary>Persists one hourly row with cooperative database cancellation.</summary>
public interface IEventLogWriter
{
    /// <summary>Unions speed events into the matching ATSPM archive row.</summary>
    Task UpsertAsync(CompressedEventLogBase input, CancellationToken cancellationToken);
}
