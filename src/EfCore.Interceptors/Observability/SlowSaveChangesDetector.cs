using System.Diagnostics;
using System.Runtime.CompilerServices;
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
    private readonly ConditionalWeakTable<DbContext, TimestampHolder> _startedAt = new();
    private sealed class TimestampHolder(long timestamp) { public long Timestamp { get; } = timestamp; }

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

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is not null)
        {
            _startedAt.Remove(eventData.Context);
        }

        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            _startedAt.Remove(eventData.Context);
        }

        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private void StartClock(DbContext? context)
    {
        if (context is not null)
        {
            _startedAt.Remove(context);
            _startedAt.Add(context, new TimestampHolder(Stopwatch.GetTimestamp()));
        }
    }

    protected virtual void Check(DbContext? context, int entitiesCount)
    {
        if (context is null || !_startedAt.TryGetValue(context, out var holder))
        {
            return;
        }

        _startedAt.Remove(context);
        var elapsed = Stopwatch.GetElapsedTime(holder.Timestamp);
        if (elapsed >= _threshold)
        {
            _logger.LogWarning(
                "Slow SaveChanges detected: {Duration:F1}ms for {Entities} entit(ies) exceeded threshold {Threshold}ms.",
                elapsed.TotalMilliseconds, entitiesCount, _threshold.TotalMilliseconds);
        }
    }
}
