using Utah.Udot.Atspm.Data.Models;

namespace SpeedListener.Publishing;

/// <summary>Persists one hourly row with cooperative database cancellation.</summary>
public interface IEventLogWriter
{
    /// <summary>Appends speed events to the matching ATSPM archive row while preserving duplicate-event multiplicity.</summary>
    Task UpsertAsync(CompressedEventLogBase input, CancellationToken cancellationToken);
}
