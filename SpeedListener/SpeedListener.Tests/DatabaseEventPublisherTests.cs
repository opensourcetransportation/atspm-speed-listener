using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json.Linq;
using SpeedListener.Configuration;
using SpeedListener.Publishing;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Data.Models.EventLogModels;
using Utah.Udot.Atspm.Repositories.EventLogRepositories;

namespace SpeedListener.Tests;

public sealed class DatabaseEventPublisherTests
{
    [Fact]
    public async Task PublishAsync_ArchivesEnvelopeThroughPackagedRepository()
    {
        var repository = new Mock<IEventLogRepository>();
        repository
            .Setup(instance => instance.LookupAsync(It.IsAny<CompressedEventLogBase>()))
            .ReturnsAsync((CompressedEventLogBase?)null);
        repository
            .Setup(instance => instance.AddAsync(It.IsAny<CompressedEventLogBase>()))
            .Returns(Task.CompletedTask);
        var services = new ServiceCollection()
            .AddScoped(_ => repository.Object)
            .BuildServiceProvider();
        var publisher = new DatabaseEventPublisher(
            services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SpeedListenerConfiguration
            {
                ArchiveParallelism = 1,
                WriteTimeout = TimeSpan.FromSeconds(5),
                MaxWriteAttempts = 1
            }),
            NullLogger<DatabaseEventPublisher>.Instance);
        var timestamp = new DateTime(2026, 9, 2, 18, 30, 0, DateTimeKind.Utc);
        var envelope = new EventBatchEnvelope
        {
            LocationIdentifier = "L1",
            DeviceId = 1,
            DataType = nameof(SpeedEvent),
            Start = timestamp,
            End = timestamp,
            Items = JToken.FromObject(new[]
            {
                new SpeedEvent { DetectorId = "D1", Timestamp = timestamp, Mph = 30, Kph = 48 }
            })
        };

        await publisher.PublishAsync(envelope);

        repository.Verify(instance => instance.AddAsync(
            It.Is<CompressedEventLogBase>(value =>
                value.LocationIdentifier == "L1" && value.DeviceId == 1)), Times.Once);
    }
}
