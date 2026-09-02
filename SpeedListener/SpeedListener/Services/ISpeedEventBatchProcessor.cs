using System.Threading.Channels;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Services;

/// <summary>Consumes and persists queued speed events.</summary>
public interface ISpeedEventBatchProcessor
{
    /// <summary>Processes events until the channel completes or cancellation occurs.</summary>
    Task ProcessAsync(ChannelReader<SpeedEvent> reader, CancellationToken cancellationToken);
}
