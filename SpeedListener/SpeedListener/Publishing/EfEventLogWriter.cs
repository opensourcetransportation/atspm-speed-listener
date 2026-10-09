using Microsoft.EntityFrameworkCore;
using Utah.Udot.Atspm.Data;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Publishing;

/// <summary>Uses packaged ATSPM models and keys with cancellable EF operations.</summary>
public sealed class EfEventLogWriter(EventLogContext context) : IEventLogWriter
{
    /// <inheritdoc/>
    public async Task UpsertAsync(CompressedEventLogBase input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var incoming = (CompressedEventLogs<SpeedEvent>)input;
        var key = context.Model.FindEntityType(typeof(CompressedEventLogBase))!.FindPrimaryKey()!;
        var values = key.Properties.Select(property => property.PropertyInfo!.GetValue(input)).ToArray();
        var table = context.Set<CompressedEventLogBase>();
        var existing = await table.FindAsync(values, cancellationToken);
        if (existing is null)
            await table.AddAsync(input, cancellationToken);
        else
        {
            // Match the packaged Upsert's value-equality union, including retries
            // after the server committed but the acknowledgement was lost.
            var stored = (CompressedEventLogs<SpeedEvent>)existing;
            stored.Data = stored.Data.Union(incoming.Data).ToList();
            context.Entry(existing).State = EntityState.Modified;
        }
        await context.SaveChangesAsync(cancellationToken);
    }
}
