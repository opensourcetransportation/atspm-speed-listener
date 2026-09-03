using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Newtonsoft.Json.Linq;
using SpeedListener.Configuration;
using SpeedListener.Publishing;
using SpeedListener.Services;
using System.Data.Common;
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
            new SpeedListenerMetrics(TimeProvider.System),
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

    [Theory]
    [InlineData("23505", DatabaseFailureKind.BatchData)]
    [InlineData("22001", DatabaseFailureKind.BatchData)]
    [InlineData("42P01", DatabaseFailureKind.Fatal)]
    [InlineData("40001", DatabaseFailureKind.Transient)]
    [InlineData("08006", DatabaseFailureKind.Transient)]
    public void Classifier_UsesPostgresSqlState(string sqlState, DatabaseFailureKind expected) =>
        Assert.Equal(expected, DatabaseFailureClassifier.Classify(new FakeDbException(sqlState)));

    [Fact]
    public async Task PublishAsync_ConstraintViolation_DropsUntilDeviceThresholdThenFails()
    {
        var repository = new Mock<IEventLogRepository>();
        repository.Setup(instance => instance.LookupAsync(It.IsAny<CompressedEventLogBase>()))
            .ThrowsAsync(new FakeDbException("23505"));
        var publisher = CreatePublisher(repository, poisonThreshold: 2);
        var envelope = CreateEnvelope();

        await publisher.PublishAsync(envelope);
        var exception = await Assert.ThrowsAsync<PoisonDeviceException>(() => publisher.PublishAsync(envelope));

        Assert.Equal(envelope.DeviceId, exception.DeviceId);
        repository.Verify(instance => instance.LookupAsync(It.IsAny<CompressedEventLogBase>()), Times.Exactly(2));
    }

    [Fact]
    public async Task PublishAsync_SchemaMismatch_FailsImmediatelyWithoutRetry()
    {
        var repository = new Mock<IEventLogRepository>();
        repository.Setup(instance => instance.LookupAsync(It.IsAny<CompressedEventLogBase>()))
            .ThrowsAsync(new FakeDbException("42P01"));
        var publisher = CreatePublisher(repository, maxAttempts: 3);

        await Assert.ThrowsAnyAsync<Exception>(() => publisher.PublishAsync(CreateEnvelope()));

        repository.Verify(instance => instance.LookupAsync(It.IsAny<CompressedEventLogBase>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_TransientFailure_RetriesAndSucceeds()
    {
        var repository = new Mock<IEventLogRepository>();
        repository.SetupSequence(instance => instance.LookupAsync(It.IsAny<CompressedEventLogBase>()))
            .ThrowsAsync(new FakeDbException("40001"))
            .ReturnsAsync((CompressedEventLogBase?)null);
        repository.Setup(instance => instance.AddAsync(It.IsAny<CompressedEventLogBase>())).Returns(Task.CompletedTask);
        var publisher = CreatePublisher(repository, maxAttempts: 2);

        await publisher.PublishAsync(CreateEnvelope());

        repository.Verify(instance => instance.LookupAsync(It.IsAny<CompressedEventLogBase>()), Times.Exactly(2));
        repository.Verify(instance => instance.AddAsync(It.IsAny<CompressedEventLogBase>()), Times.Once);
    }

    [Fact]
    public async Task PublishAsync_MixedBatch_IsolatesPoisonEnvelopeAndArchivesHealthyEnvelope()
    {
        var repository = new Mock<IEventLogRepository>();
        repository.SetupSequence(instance => instance.LookupAsync(It.IsAny<CompressedEventLogBase>()))
            .ThrowsAsync(new FakeDbException("23505"))
            .ThrowsAsync(new FakeDbException("23505"))
            .ReturnsAsync((CompressedEventLogBase?)null);
        repository.Setup(instance => instance.AddAsync(It.IsAny<CompressedEventLogBase>())).Returns(Task.CompletedTask);
        var publisher = CreatePublisher(repository, poisonThreshold: 2);
        var poison = CreateEnvelope();
        var healthy = CreateEnvelope(deviceId: 2, locationIdentifier: "L2", detectorId: "D2");

        await publisher.PublishAsync([poison, healthy], parallelism: 1);

        repository.Verify(instance => instance.AddAsync(
            It.Is<CompressedEventLogBase>(value => value.DeviceId == 2 && value.LocationIdentifier == "L2")),
            Times.Once);
    }

    [Fact]
    public async Task PublishAsync_Success_ResetsConsecutivePoisonDropCount()
    {
        var repository = new Mock<IEventLogRepository>();
        repository.SetupSequence(instance => instance.LookupAsync(It.IsAny<CompressedEventLogBase>()))
            .ThrowsAsync(new FakeDbException("23505"))
            .ReturnsAsync((CompressedEventLogBase?)null)
            .ThrowsAsync(new FakeDbException("23505"));
        repository.Setup(instance => instance.AddAsync(It.IsAny<CompressedEventLogBase>())).Returns(Task.CompletedTask);
        var publisher = CreatePublisher(repository, poisonThreshold: 2);
        var envelope = CreateEnvelope();

        await publisher.PublishAsync(envelope);
        await publisher.PublishAsync(envelope);
        await publisher.PublishAsync(envelope);

        repository.Verify(instance => instance.LookupAsync(It.IsAny<CompressedEventLogBase>()), Times.Exactly(3));
    }

    [Fact]
    public async Task PublishAsync_SameHourBatchesAccumulateAndReplayIsIdempotent()
    {
        var repository = new Mock<IEventLogRepository>();
        CompressedEventLogs<SpeedEvent>? stored = null;
        repository.Setup(instance => instance.LookupAsync(It.IsAny<CompressedEventLogBase>()))
            .ReturnsAsync(() => stored);
        repository.Setup(instance => instance.AddAsync(It.IsAny<CompressedEventLogBase>()))
            .Callback<CompressedEventLogBase>(value => stored = Assert.IsType<CompressedEventLogs<SpeedEvent>>(value))
            .Returns(Task.CompletedTask);
        repository.Setup(instance => instance.UpdateAsync(It.IsAny<CompressedEventLogBase>()))
            .Callback<CompressedEventLogBase>(value => stored = Assert.IsType<CompressedEventLogs<SpeedEvent>>(value))
            .Returns(Task.CompletedTask);
        var publisher = CreatePublisher(repository);
        var first = CreateEnvelope(detectorId: "D1");
        var second = CreateEnvelope(detectorId: "D2");

        await publisher.PublishAsync(first);
        await publisher.PublishAsync(second);
        await publisher.PublishAsync(second);

        Assert.NotNull(stored);
        Assert.Equal(2, stored.Data.Count);
        Assert.Contains(stored.Data, speedEvent => speedEvent.DetectorId == "D1");
        Assert.Contains(stored.Data, speedEvent => speedEvent.DetectorId == "D2");
        repository.Verify(instance => instance.AddAsync(It.IsAny<CompressedEventLogBase>()), Times.Once);
        repository.Verify(instance => instance.UpdateAsync(It.IsAny<CompressedEventLogBase>()), Times.Exactly(2));
    }

    private static DatabaseEventPublisher CreatePublisher(Mock<IEventLogRepository> repository,
        int maxAttempts = 1, int poisonThreshold = 3)
    {
        var services = new ServiceCollection().AddScoped(_ => repository.Object).BuildServiceProvider();
        return new DatabaseEventPublisher(services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new SpeedListenerConfiguration
            {
                ArchiveParallelism = 1,
                WriteTimeout = TimeSpan.FromSeconds(5),
                MaxWriteAttempts = maxAttempts,
                PoisonDeviceFailureThreshold = poisonThreshold
            }), new SpeedListenerMetrics(TimeProvider.System), NullLogger<DatabaseEventPublisher>.Instance);
    }

    private static EventBatchEnvelope CreateEnvelope(
        int deviceId = 1,
        string locationIdentifier = "L1",
        string detectorId = "D1")
    {
        var timestamp = new DateTime(2026, 9, 2, 18, 30, 0, DateTimeKind.Utc);
        return new EventBatchEnvelope
        {
            LocationIdentifier = locationIdentifier, DeviceId = deviceId, DataType = nameof(SpeedEvent),
            Start = timestamp, End = timestamp,
            Items = JToken.FromObject(new[] { new SpeedEvent { DetectorId = detectorId, Timestamp = timestamp, Mph = 30, Kph = 48 } })
        };
    }

    private sealed class FakeDbException(string sqlState) : DbException("database failure")
    {
        public override string? SqlState { get; } = sqlState;
    }
}
