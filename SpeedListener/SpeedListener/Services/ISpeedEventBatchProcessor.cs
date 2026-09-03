using System.Threading.Channels;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Services;

/// <summary>Consumes and persists queued speed events.</summary>
public interface ISpeedEventBatchProcessor
{
    /// <summary>Gets the number of events currently held outside the channel.</summary>
    int InFlightEventCount { get; }

    /// <summary>Processes events until the channel completes or cancellation occurs.</summary>
    Task ProcessAsync(ChannelReader<SpeedEvent> reader, CancellationToken cancellationToken);
}
