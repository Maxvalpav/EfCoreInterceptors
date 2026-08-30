using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Observability;

/// <summary>
/// Metrics for the save pipeline: histogram <c>ef.save.duration</c> (ms) plus counters
/// <c>ef.save.executed</c>, <c>ef.save.failed</c> and <c>ef.save.entities</c>.
/// </summary>
public class SaveChangesMetricsInterceptor : SaveChangesInterceptor
{
    private const string MeterName = "EfCore.Interceptors";

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Histogram<double> _durationMs;
    private readonly Counter<long> _executed;
    private readonly Counter<long> _failed;
    private readonly Counter<long> _entities;
    private readonly ConcurrentDictionary<DbContext, long> _startedAt = new();

    public SaveChangesMetricsInterceptor()
    {
        _durationMs = _meter.CreateHistogram<double>("ef.save.duration", unit: "ms");
        _executed = _meter.CreateCounter<long>("ef.save.executed");
        _failed = _meter.CreateCounter<long>("ef.save.failed");
        _entities = _meter.CreateCounter<long>("ef.save.entities");
    }

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
            _startedAt[context] = Stopwatch.GetTimestamp();
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
        if (context is not null && _startedAt.TryRemove(context, out var timestamp))
        {
            _durationMs.Record(Math.Max(0.01, Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds));
        }
    }
}
