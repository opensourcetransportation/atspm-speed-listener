using Microsoft.Extensions.DependencyInjection;
using Moq;
using Newtonsoft.Json.Linq;
using SpeedListener.Publishing;
using SpeedListener.Workflows;
using System.Collections.Concurrent;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Data.Models.EventLogModels;
using Utah.Udot.Atspm.Repositories.EventLogRepositories;

namespace SpeedListener.Tests;

public sealed class EventBatchEnvelopeWorkflowTests
{
    [Fact]
    public async Task IndependentDevices_OverlapDatabaseWritesWithinConfiguredLimit()
    {
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active = 0;
        var peak = 0;
        var saved = 0;
        var repository = new Mock<IEventLogRepository>();
        repository.Setup(r => r.LookupAsync(It.IsAny<CompressedEventLogBase>()))
            .Returns(async () => {
                var current = Interlocked.Increment(ref active);
                Interlocked.Exchange(ref peak, Math.Max(peak, current));
                if (current == 2) bothStarted.TrySetResult();
                await release.Task;
                return (CompressedEventLogBase?)null;
            });
        repository.Setup(r => r.AddAsync(It.IsAny<CompressedEventLogBase>()))
            .Returns(() => { Interlocked.Decrement(ref active); Interlocked.Increment(ref saved); return Task.CompletedTask; });
        using var services = new ServiceCollection().AddScoped(_ => repository.Object).BuildServiceProvider();
        var workflow = new EventBatchEnvelopeWorkflow(services.GetRequiredService<IServiceScopeFactory>(),
            databaseWriteParallelism: 2);
        for (var device = 1; device <= 4; device++)
            await workflow.SendAsync(Envelope(device, $"5026{device:00}"));
        workflow.Complete();
        try { await bothStarted.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { release.TrySetResult(); }
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, peak);
        Assert.Equal(4, saved);
    }

    [Fact]
    public async Task SameDeviceSameHour_IsSerializedAndPreservesEveryDetectorEvent()
    {
        var stored = new ConcurrentDictionary<int, CompressedEventLogs<SpeedEvent>>();
        var activeByDevice = new ConcurrentDictionary<int, int>();
        var overlap = 0;
        var repository = new Mock<IEventLogRepository>();
        repository.Setup(r => r.LookupAsync(It.IsAny<CompressedEventLogBase>()))
            .Returns(async (CompressedEventLogBase row) => {
                if (activeByDevice.AddOrUpdate(row.DeviceId, 1, (_, count) => count + 1) > 1)
                    Interlocked.Increment(ref overlap);
                await Task.Delay(10);
                return stored.TryGetValue(row.DeviceId, out var existing) ? existing : null;
            });
        Task Save(CompressedEventLogBase row) {
            stored[row.DeviceId] = (CompressedEventLogs<SpeedEvent>)row;
            activeByDevice.AddOrUpdate(row.DeviceId, 0, (_, count) => count - 1);
            return Task.CompletedTask;
        }
        repository.Setup(r => r.AddAsync(It.IsAny<CompressedEventLogBase>())).Returns((CompressedEventLogBase row) => Save(row));
        repository.Setup(r => r.UpdateAsync(It.IsAny<CompressedEventLogBase>())).Returns((CompressedEventLogBase row) => Save(row));
        using var services = new ServiceCollection().AddScoped(_ => repository.Object).BuildServiceProvider();
        var workflow = new EventBatchEnvelopeWorkflow(services.GetRequiredService<IServiceScopeFactory>(),
            databaseWriteParallelism: 8);
        for (var index = 0; index < 20; index++)
            await workflow.SendAsync(Envelope(1, $"5026{index:00}"));
        workflow.Complete();
        await workflow.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, overlap);
        Assert.Equal(20, stored[1].Data.Count);
    }

    [Fact]
    public async Task ConcurrentWriterFailure_IsReportedByCompletion()
    {
        var expected = new InvalidOperationException("save failed");
        var repository = new Mock<IEventLogRepository>();
        repository.Setup(r => r.LookupAsync(It.IsAny<CompressedEventLogBase>())).ThrowsAsync(expected);
        using var services = new ServiceCollection().AddScoped(_ => repository.Object).BuildServiceProvider();
        var workflow = new EventBatchEnvelopeWorkflow(services.GetRequiredService<IServiceScopeFactory>(),
            databaseWriteParallelism: 8);
        await workflow.SendAsync(Envelope(1, "502620"));
        await workflow.SendAsync(Envelope(2, "527148"));
        workflow.Complete();
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => workflow.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Same(expected, failure);
    }

    private static EventBatchEnvelope Envelope(int deviceId, string detectorId)
    {
        var timestamp = new DateTime(2026, 10, 7, 17, 0, 0, DateTimeKind.Utc);
        return new EventBatchEnvelope {
            DeviceId = deviceId, LocationIdentifier = detectorId[..4], Start = timestamp, End = timestamp,
            DataType = nameof(SpeedEvent), Items = JToken.FromObject(new[] {
                new SpeedEvent { DetectorId = detectorId, Timestamp = timestamp, Mph = 30, Kph = 48 }
            })
        };
    }
}
