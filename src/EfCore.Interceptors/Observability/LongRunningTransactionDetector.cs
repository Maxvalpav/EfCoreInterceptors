using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Observability;

/// <summary>
/// Long-running transaction watchdog: logs a warning when a transaction stays open longer than
/// the configured threshold (measured by EF between begin and commit/rollback).
/// Long transactions hold locks and block vacuum/log truncation — this makes them visible.
/// </summary>
public class LongRunningTransactionDetector(
    TimeSpan threshold,
    ILoggerFactory? loggerFactory = null) : DbTransactionInterceptor
{
    private readonly TimeSpan _threshold = threshold;
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.LongTransaction") ?? NullLogger.Instance;

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        Check(eventData);
        base.TransactionCommitted(transaction, eventData);
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Check(eventData);
        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        Check(eventData);
        base.TransactionRolledBack(transaction, eventData);
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        Check(eventData);
        return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
    }

    protected virtual void Check(TransactionEndEventData eventData)
    {
        if (eventData.Duration >= _threshold)
        {
            _logger.LogWarning(
                "Long-running transaction {TransactionId}: held for {Duration:F0}ms (threshold {Threshold:F0}ms).",
                eventData.TransactionId,
                eventData.Duration.TotalMilliseconds,
                _threshold.TotalMilliseconds);
        }
    }
}
