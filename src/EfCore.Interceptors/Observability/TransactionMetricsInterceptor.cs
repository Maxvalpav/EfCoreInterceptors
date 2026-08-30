using System.Data.Common;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Observability;

/// <summary>
/// Metrics for transactions: counters <c>ef.transaction.started/committed/rolledback/failed</c>
/// and histogram <c>ef.transaction.duration</c> (ms).
/// </summary>
public class TransactionMetricsInterceptor : DbTransactionInterceptor
{
    private static readonly Counter<long> StaticStarted = SharedMeter.Meter.CreateCounter<long>("ef.transaction.started");
    private static readonly Counter<long> StaticCommitted = SharedMeter.Meter.CreateCounter<long>("ef.transaction.committed");
    private static readonly Counter<long> StaticRolledBack = SharedMeter.Meter.CreateCounter<long>("ef.transaction.rolledback");
    private static readonly Counter<long> StaticFailed = SharedMeter.Meter.CreateCounter<long>("ef.transaction.failed");
    private static readonly Histogram<double> StaticDuration = SharedMeter.Meter.CreateHistogram<double>("ef.transaction.duration", unit: "ms");

    private readonly Counter<long> _started = StaticStarted;
    private readonly Counter<long> _committed = StaticCommitted;
    private readonly Counter<long> _rolledBack = StaticRolledBack;
    private readonly Counter<long> _failed = StaticFailed;
    private readonly Histogram<double> _durationMs = StaticDuration;

    public TransactionMetricsInterceptor() { }

    public override DbTransaction TransactionStarted(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        _started.Add(1);
        return base.TransactionStarted(connection, eventData, result);
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        _started.Add(1);
        return base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        Record(_committed, eventData.Duration);
        base.TransactionCommitted(transaction, eventData);
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Record(_committed, eventData.Duration);
        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        Record(_rolledBack, eventData.Duration);
        base.TransactionRolledBack(transaction, eventData);
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Record(_rolledBack, eventData.Duration);
        return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        _failed.Add(1);
        base.TransactionFailed(transaction, eventData);
    }

    public override Task TransactionFailedAsync(
        DbTransaction transaction, TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        TransactionFailed(transaction, eventData);
        return base.TransactionFailedAsync(transaction, eventData, cancellationToken);
    }

    private void Record(Counter<long> counter, TimeSpan duration)
    {
        counter.Add(1);
        _durationMs.Record(Math.Max(0.01, duration.TotalMilliseconds));
    }
}
