using Microsoft.EntityFrameworkCore;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Buffers.Binary;
using System.Text;
using Utah.Udot.Atspm.Data;
using Utah.Udot.Atspm.Data.Models;
using Utah.Udot.Atspm.Data.Models.EventLogModels;

namespace SpeedListener.Publishing;

/// <summary>Uses packaged ATSPM models and keys with cancellable EF operations.</summary>
public sealed class EfEventLogWriter(EventLogContext context) : IEventLogWriter
{
    // Retained logical rows have small fingerprints, not copies of hourly payloads.
    private static readonly ConditionalWeakTable<CompressedEventLogBase, MergePlan> Plans = new();
    private sealed record Snapshot(int Count, byte[] Hash)
    {
        public bool Matches(Snapshot other) => Count == other.Count && Hash.AsSpan().SequenceEqual(other.Hash);
    }
    private sealed record MergePlan(bool Existed, Snapshot Before, Snapshot After);

    /// <inheritdoc/>
    public async Task UpsertAsync(CompressedEventLogBase input, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var incoming = input.Data.Cast<SpeedEvent>();
        var key = context.Model.FindEntityType(typeof(CompressedEventLogBase))!.FindPrimaryKey()!;
        var values = key.Properties.Select(property => property.PropertyInfo!.GetValue(input)).ToArray();
        var table = context.Set<CompressedEventLogBase>();
        var existing = await table.FindAsync(values, cancellationToken);
        var stored = existing?.Data.Cast<SpeedEvent>() ?? Enumerable.Empty<SpeedEvent>();
        var replay = Plans.TryGetValue(input, out var plan);
        if (replay)
        {
            // LocationIdentifier is row metadata and is not serialized on an event.
            // Compare persisted fields, including multiplicity and order. A later
            // append after our committed data does not invalidate that commitment.
            if (existing is not null && Fingerprint(stored.Take(plan!.After.Count)).Matches(plan.After)) return;
            if ((existing is not null) != plan!.Existed || !Fingerprint(stored).Matches(plan.Before))
                throw new EventLogWriteConflictException("Cannot establish whether this logical write committed after the hourly row changed.");
        }
        var merged = stored.Concat(incoming).ToList();
        if (!replay)
            Plans.Add(input, new MergePlan(existing is not null, Fingerprint(stored), Fingerprint(merged)));
        if (existing is null)
            await table.AddAsync(input, cancellationToken);
        else
        {
            existing.Data = merged;
            context.Entry(existing).State = EntityState.Modified;
        }
        await context.SaveChangesAsync(cancellationToken);
    }

    private static Snapshot Fingerprint(IEnumerable<SpeedEvent> events)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> fields = stackalloc byte[20];
        Span<byte> detectorBuffer = stackalloc byte[64];
        var count = 0;
        foreach (var item in events)
        {
            var detector = item.DetectorId ?? string.Empty;
            var detectorLength = Encoding.UTF8.GetByteCount(detector);
            BinaryPrimitives.WriteInt64LittleEndian(fields, item.Timestamp.Ticks);
            BinaryPrimitives.WriteInt32LittleEndian(fields[8..], item.Mph);
            BinaryPrimitives.WriteInt32LittleEndian(fields[12..], item.Kph);
            BinaryPrimitives.WriteInt32LittleEndian(fields[16..], detectorLength);
            hash.AppendData(fields);
            if (detectorLength <= detectorBuffer.Length)
            {
                var written = Encoding.UTF8.GetBytes(detector, detectorBuffer);
                hash.AppendData(detectorBuffer[..written]);
            }
            else hash.AppendData(Encoding.UTF8.GetBytes(detector));
            count++;
        }
        return new Snapshot(count, hash.GetHashAndReset());
    }
}
