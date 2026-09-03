using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EfCore.Interceptors.Saving;

/// <summary>
/// Ready-made mapping for <see cref="ChangeLogEntry"/> (02.4): without these indexes
/// the "history of order #42" query degrades to a full scan after the first million rows.
/// Apply via <c>modelBuilder.ApplyConfiguration(new ChangeLogEntryTypeConfiguration())</c>.
/// </summary>
public sealed class ChangeLogEntryTypeConfiguration : IEntityTypeConfiguration<ChangeLogEntry>
{
    public void Configure(EntityTypeBuilder<ChangeLogEntry> builder)
    {
        builder.HasIndex(e => new { e.EntityName, e.EntityKey }).HasDatabaseName("IX_ChangeLog_Entity");
        builder.HasIndex(e => e.TimestampUtc).HasDatabaseName("IX_ChangeLog_Timestamp");
    }
}

/// <summary>
/// Retention maintenance for the changelog table (02.4): the table grows forever
/// unless old rows are pruned. Run on a schedule (e.g. daily background job).
/// </summary>
public static class ChangeLogMaintenance
{
    /// <summary>
    /// Deletes rows older than <paramref name="cutoff"/> in id-batches.
    /// The expiry comparison runs client-side over keyset pages: DateTimeOffset
    /// comparisons do not translate on SQLite, and <c>Take</c>-based bulk deletes
    /// are not portable — so pages are selected by id range (translatable
    /// everywhere) and only expired ids are deleted by equality filter.
    /// </summary>
    /// <returns>Total deleted rows.</returns>
    public static async Task<int> DeleteOlderThanAsync(
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
            var page = await db.Set<ChangeLogEntry>()
                .Where(e => e.Id > lastId)
                .OrderBy(e => e.Id)
                .Take(batchSize)
                .Select(e => new { e.Id, e.TimestampUtc })
                .AsNoTracking()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (page.Count == 0) break;
            lastId = page[^1].Id;
            var expired = page.Where(e => e.TimestampUtc < cutoff).Select(e => e.Id).ToList();
            if (expired.Count > 0)
            {
                total += await db.Set<ChangeLogEntry>()
                    .Where(e => expired.Contains(e.Id))
                    .ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        return total;
    }
}
