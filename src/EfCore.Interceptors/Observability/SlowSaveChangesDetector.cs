using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Observability;

/// <summary>
/// Warns when a single SaveChanges takes longer than the configured threshold —
/// the save-pipeline counterpart of <c>SlowQueryCommandInterceptor</c>.
/// </summary>
public class SlowSaveChangesDetector(
    TimeSpan threshold,
    ILoggerFactory? loggerFactory = null) : SaveChangesInterceptor
{
    private readonly TimeSpan _threshold = threshold;
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.SlowSave") ?? NullLogger.Instance;
    private readonly ConcurrentDictionary<DbContext, long> _startedAt = new();

    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData, InterceptionResult<int> result)
    {
        StartClock(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        StartClock(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Check(eventData.Context, eventData.EntitiesSavedCount);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        Check(eventData.Context, eventData.EntitiesSavedCount);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private void StartClock(DbContext? context)
    {
        if (context is not null)
        {
            _startedAt[context] = Stopwatch.GetTimestamp();
        }
    }

    protected virtual void Check(DbContext? context, int entitiesCount)
    {
        if (context is null || !_startedAt.TryRemove(context, out var timestamp))
        {
            return;
        }

        var elapsed = Stopwatch.GetElapsedTime(timestamp);
        if (elapsed >= _threshold)
        {
            _logger.LogWarning(
                "Slow SaveChanges detected: {Duration:F1}ms for {Entities} entit(ies) exceeded threshold {Threshold}ms.",
                elapsed.TotalMilliseconds, entitiesCount, _threshold.TotalMilliseconds);
        }
    }
}
