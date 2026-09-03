using EfCore.Interceptors.Entities;
using Microsoft.EntityFrameworkCore;

namespace EfCore.Interceptors.Dashboard;

/// <summary>Outbox queue state for the dashboard (03.13).</summary>
public enum OutboxStatus
{
    Pending,
    Dead,
    All
}

/// <summary>
/// Pure query/command logic behind the dashboard endpoints (03.13): unit-testable
/// without hosting ASP.NET. All time comparisons run client-side over keyset pages
/// (DateTimeOffset comparisons do not translate on SQLite).
/// </summary>
public static class DashboardStore
{
    public sealed record OutboxStats(int Pending, int DeadLettered, double? LagSeconds);

    public static async Task<OutboxStats> GetOutboxStatsAsync(
        DbContext db, TimeProvider? clock = null, CancellationToken ct = default)
    {
        var now = (clock ?? TimeProvider.System).GetUtcNow();
        var rows = await db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAtUtc == null)
            .Select(m => new { m.OccurredAtUtc, m.DeadLetteredAtUtc })
            .AsNoTracking()
            .ToListAsync(ct).ConfigureAwait(false);
        var pending = rows.Where(r => r.DeadLetteredAtUtc == null).ToList();
        double? lag = pending.Count == 0
            ? null
            : (now - pending.Min(r => r.OccurredAtUtc)).TotalSeconds;
        return new OutboxStats(pending.Count, rows.Count - pending.Count, lag);
    }

    public static async Task<List<OutboxMessage>> GetOutboxAsync(
        DbContext db, OutboxStatus status = OutboxStatus.Pending,
        int take = 50, CancellationToken ct = default)
    {
        var query = db.Set<OutboxMessage>()
            .Where(m => m.ProcessedAtUtc == null)
            .OrderByDescending(m => m.Id);
        var rows = await query.Take(Math.Clamp(take, 1, 500)).AsNoTracking()
            .ToListAsync(ct).ConfigureAwait(false);
        return status switch
        {
            OutboxStatus.Pending => rows.Where(m => m.DeadLetteredAtUtc == null).ToList(),
            OutboxStatus.Dead => rows.Where(m => m.DeadLetteredAtUtc != null).ToList(),
            _ => rows
        };
    }

    /// <summary>Requeues one message: clears dead-letter state, attempts and locks.</summary>
    /// <returns>False when the message does not exist.</returns>
    public static async Task<bool> RetryOutboxAsync(
        DbContext db, long id, CancellationToken ct = default)
    {
        var affected = await db.Set<OutboxMessage>()
            .Where(m => m.Id == id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(m => m.DeadLetteredAtUtc, (DateTimeOffset?)null)
                .SetProperty(m => m.AttemptCount, 0)
                .SetProperty(m => m.Error, (string?)null)
                .SetProperty(m => m.LockedUntilUtc, (DateTimeOffset?)null)
                .SetProperty(m => m.ClaimToken, (Guid?)null), ct).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>Deletes delivered messages older than <paramref name="days"/> (keyset pages).</summary>
    /// <returns>Deleted rows.</returns>
    public static async Task<int> PurgeDeliveredAsync(
        DbContext db, int days = 30, int batchSize = 500, CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-days);
        var total = 0;
        long lastId = 0;
        while (true)
        {
            var page = await db.Set<OutboxMessage>()
                .Where(m => m.Id > lastId && m.ProcessedAtUtc != null)
                .OrderBy(m => m.Id)
                .Take(Math.Max(1, batchSize))
                .Select(m => new { m.Id, m.ProcessedAtUtc })
                .AsNoTracking()
                .ToListAsync(ct).ConfigureAwait(false);
            if (page.Count == 0) break;
            lastId = page[^1].Id;
            var expired = page.Where(m => m.ProcessedAtUtc < cutoff).Select(m => m.Id).ToList();
            if (expired.Count > 0)
                total += await db.Set<OutboxMessage>()
                    .Where(m => expired.Contains(m.Id))
                    .ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }
        return total;
    }

    /// <summary>Recent changelog rows (empty when the entity is not mapped).</summary>
    public static Task<List<ChangeLogEntry>> GetChangeLogAsync(
        DbContext db, int take = 50, CancellationToken ct = default)
    {
        if (db.Model.FindEntityType(typeof(ChangeLogEntry)) is null)
            return Task.FromResult(new List<ChangeLogEntry>());
        return db.Set<ChangeLogEntry>()
            .OrderByDescending(e => e.Id)
            .Take(Math.Clamp(take, 1, 500))
            .AsNoTracking()
            .ToListAsync(ct);
    }
}
