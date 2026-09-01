using Microsoft.Extensions.Hosting;

namespace SpeedListener.BackgroundServices
{
    /// <summary>
    /// Background service for listening to incoming speed sensor data packets.
    /// </summary>
    public class SpeedListenerBackgroundService : BackgroundService
    {
        /// <inheritdoc/>
        protected override Task ExecuteAsync(CancellationToken stoppingToken)
        {
            throw new NotImplementedException();
        }
    }
}
