using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using Utah.Udot.Atspm.Data;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Publishing;

/// <summary>Uses packaged ATSPM models and keys with cancellable EF operations.</summary>
public sealed class EfEventLogWriter(EventLogContext context) : IEventLogWriter
{
    // Logical rows are retained across publisher retries. Weak keys release this
    // merge plan when a publish completes, without a process-wide receipt cache.
    private static readonly ConditionalWeakTable<CompressedEventLogBase, MergePlan> Plans = new();
    private sealed record MergePlan(IReadOnlyList<SpeedEvent>? Before, IReadOnlyList<SpeedEvent> After);

    /// <inheritdoc/>
    public async Task UpsertAsync(CompressedEventLogBase input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var incoming = input.Data.Cast<SpeedEvent>().ToList();
        var key = context.Model.FindEntityType(typeof(CompressedEventLogBase))!.FindPrimaryKey()!;
        var values = key.Properties.Select(property => property.PropertyInfo!.GetValue(input)).ToArray();
        var table = context.Set<CompressedEventLogBase>();
        var existing = await table.FindAsync(values, cancellationToken);
        var stored = existing?.Data.Cast<SpeedEvent>().ToList();
        var replay = Plans.TryGetValue(input, out var plan);
        plan ??= Plans.GetValue(input, _ => new MergePlan(stored,
            (stored is null ? Enumerable.Empty<SpeedEvent>() : stored).Concat(incoming).ToList()));
        if (replay)
        {
            // An acknowledgement can be lost after committing. Compare the exact
            // intended row, preserving event multiplicity rather than using Union.
            if (stored is not null && stored.SequenceEqual(plan.After)) return;
            if ((stored is null) != (plan.Before is null) ||
                stored is not null && !stored.SequenceEqual(plan.Before!))
                throw new DbUpdateConcurrencyException("Hourly speed row changed outside this logical write; refusing to overwrite or double-append events.");
        }
        if (existing is null)
            await table.AddAsync(input, cancellationToken);
        else
        {
            existing.Data = plan.After.ToList();
            context.Entry(existing).State = EntityState.Modified;
        }
        await context.SaveChangesAsync(cancellationToken);
    }
}
