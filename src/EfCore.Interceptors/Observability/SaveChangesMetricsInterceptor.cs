using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Observability;

/// <summary>
/// Metrics for the save pipeline: histogram <c>ef.save.duration</c> (ms) plus counters
/// <c>ef.save.executed</c>, <c>ef.save.failed</c> and <c>ef.save.entities</c>.
/// </summary>
public class SaveChangesMetricsInterceptor : SaveChangesInterceptor
{
    private static readonly Histogram<double> StaticDuration = SharedMeter.DurationHistogram(SharedMeter.Meter, "ef.save.duration", "ms");
    private static readonly Counter<long> StaticExecuted = SharedMeter.Meter.CreateCounter<long>("ef.save.executed");
    private static readonly Counter<long> StaticFailed = SharedMeter.Meter.CreateCounter<long>("ef.save.failed");
    private static readonly Counter<long> StaticEntities = SharedMeter.Meter.CreateCounter<long>("ef.save.entities");

    private readonly Histogram<double> _durationMs = StaticDuration;
    private readonly Counter<long> _executed = StaticExecuted;
    private readonly Counter<long> _failed = StaticFailed;
    private readonly Counter<long> _entities = StaticEntities;
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
        RecordSuccess(eventData.Context, eventData.EntitiesSavedCount);
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        RecordSuccess(eventData.Context, eventData.EntitiesSavedCount);
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        RecordFailure(eventData.Context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        RecordFailure(eventData.Context);
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

    private void RecordSuccess(DbContext? context, int entitiesSavedCount)
    {
        _executed.Add(1);
        _entities.Add(Math.Max(0, entitiesSavedCount));
        RecordDuration(context);
    }

    private void RecordFailure(DbContext? context)
    {
        _failed.Add(1);
        RecordDuration(context);
    }

    private void RecordDuration(DbContext? context)
    {
        if (context is not null && _startedAt.TryGetValue(context, out var holder))
        {
            _startedAt.Remove(context);
            _durationMs.Record(Math.Max(0.01, Stopwatch.GetElapsedTime(holder.Timestamp).TotalMilliseconds));
        }
    }
}
