using SpeedListener.Publishing;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Extensions;
using Utah.Udot.Atspm.Repositories.EventLogRepositories;

namespace SpeedListener.Tests;

// Retain tests against the real NuGet Upsert extension to verify its equality
// and merge semantics independently from the production cancellable EF writer.
internal sealed class PackagedTestEventLogWriter(IEventLogRepository repository) : IEventLogWriter
{
    public async Task UpsertAsync(CompressedEventLogBase input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await repository.Upsert(input);
    }
}
