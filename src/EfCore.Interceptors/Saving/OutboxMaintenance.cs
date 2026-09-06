using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Ready-made mapping for <see cref="OutboxMessage"/>: without these indexes the claim query
/// (pending + unlocked + ordered) degrades to a full scan after the first million rows.
/// Apply via <c>modelBuilder.ApplyConfiguration(new OutboxMessageTypeConfiguration())</c>.
/// </summary>
public sealed class OutboxMessageTypeConfiguration : IEntityTypeConfiguration<OutboxMessage>
{
    public void Configure(EntityTypeBuilder<OutboxMessage> builder)
    {
        // Claim poll: pending, unlocked, oldest first.
        builder.HasIndex(e => new { e.ProcessedAtUtc, e.DeadLetteredAtUtc, e.OccurredAtUtc })
            .HasDatabaseName("IX_Outbox_Claim");
        // Token-filtered selection of our own batch.
        builder.HasIndex(e => e.ClaimToken).HasDatabaseName("IX_Outbox_ClaimToken");
        // Consumer-side idempotency lookups.
        builder.HasIndex(e => e.IdempotencyKey).HasDatabaseName("IX_Outbox_Idempotency");
        // Per-partition ordering / retention scans.
        builder.HasIndex(e => e.PartitionKey).HasDatabaseName("IX_Outbox_Partition");
    }
}

/// <summary>
/// Retention maintenance for the outbox table: delivered and dead-lettered rows accumulate
/// forever unless pruned. Run on a schedule (e.g. daily background job).
/// </summary>
public static class OutboxMaintenance
{
    /// <summary>
    /// Deletes delivered rows older than <paramref name="cutoff"/> in id-batches.
    /// Same portability pattern as <see cref="ChangeLogMaintenance"/>: keyset pages by id
    /// (translatable everywhere), expiry evaluated client-side, deletes by id equality.
    /// </summary>
    /// <returns>Total deleted rows.</returns>
    public static async Task<int> DeleteDeliveredOlderThanAsync(
        DbContext db,
        DateTimeOffset cutoff,
        int batchSize = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        batchSize = Math.Max(1, batchSize);
        var total = 0;
        long lastId = 0;
        while (true)
        {
            var page = await db.Set<OutboxMessage>()
                .Where(e => e.Id > lastId)
                .OrderBy(e => e.Id)
                .Take(batchSize)
                .Select(e => new { e.Id, e.ProcessedAtUtc })
                .AsNoTracking()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) break;
            lastId = page[^1].Id;
            var expired = page.Where(e => e.ProcessedAtUtc is not null && e.ProcessedAtUtc < cutoff).Select(e => e.Id).ToList();
            if (expired.Count > 0)
            {
                total += await db.Set<OutboxMessage>()
                    .Where(e => expired.Contains(e.Id))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return total;
    }

    /// <summary>
    /// Deletes dead-lettered rows older than <paramref name="cutoff"/> in id-batches.
    /// Archive them first if the poison payloads are needed for forensics.
    /// </summary>
    /// <returns>Total deleted rows.</returns>
    public static async Task<int> DeleteDeadLetteredOlderThanAsync(
        DbContext db,
        DateTimeOffset cutoff,
        int batchSize = 1000,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(db);
        batchSize = Math.Max(1, batchSize);
        var total = 0;
        long lastId = 0;
        while (true)
        {
            var page = await db.Set<OutboxMessage>()
                .Where(e => e.Id > lastId)
                .OrderBy(e => e.Id)
                .Take(batchSize)
                .Select(e => new { e.Id, e.DeadLetteredAtUtc })
                .AsNoTracking()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) break;
            lastId = page[^1].Id;
            var expired = page.Where(e => e.DeadLetteredAtUtc is not null && e.DeadLetteredAtUtc < cutoff).Select(e => e.Id).ToList();
            if (expired.Count > 0)
            {
                total += await db.Set<OutboxMessage>()
                    .Where(e => expired.Contains(e.Id))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return total;
    }
}
