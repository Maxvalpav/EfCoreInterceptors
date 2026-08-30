using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Transactions;

/// <summary>
/// Logs the full transaction lifecycle: begin, commit, rollback and savepoints.
/// Rollbacks and failures are logged as warnings/errors so they stand out in production logs.
/// </summary>
public class TransactionLifecycleLoggingInterceptor(
    ILoggerFactory? loggerFactory = null) : DbTransactionInterceptor
{
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.Transaction") ?? NullLogger.Instance;

    public override InterceptionResult<DbTransaction> TransactionStarting(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result)
    {
        _logger.LogDebug("Beginning transaction for {Context}...", Describe(eventData.Context));
        return base.TransactionStarting(connection, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbTransaction>> TransactionStartingAsync(
        DbConnection connection, TransactionStartingEventData eventData, InterceptionResult<DbTransaction> result,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Beginning transaction (async) for {Context}...", Describe(eventData.Context));
        return base.TransactionStartingAsync(connection, eventData, result, cancellationToken);
    }

    public override DbTransaction TransactionStarted(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        _logger.LogInformation("Transaction {TransactionId} started (isolation: {Isolation}).",
            eventData.TransactionId, result.IsolationLevel);
        return base.TransactionStarted(connection, eventData, result);
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(
        DbConnection connection, TransactionEndEventData eventData, DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Transaction {TransactionId} started (async, isolation: {Isolation}).",
            eventData.TransactionId, result.IsolationLevel);
        return base.TransactionStartedAsync(connection, eventData, result, cancellationToken);
    }

    public override DbTransaction TransactionUsed(
        DbConnection connection, TransactionEventData eventData, DbTransaction result)
    {
        _logger.LogInformation("Existing transaction {TransactionId} attached to context.", eventData.TransactionId);
        return base.TransactionUsed(connection, eventData, result);
    }

    public override ValueTask<DbTransaction> TransactionUsedAsync(
        DbConnection connection, TransactionEventData eventData, DbTransaction result,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Existing transaction {TransactionId} attached to context (async).", eventData.TransactionId);
        return base.TransactionUsedAsync(connection, eventData, result, cancellationToken);
    }

    public override InterceptionResult TransactionCommitting(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        _logger.LogDebug("Committing transaction {TransactionId}...", eventData.TransactionId);
        return base.TransactionCommitting(transaction, eventData, result);
    }

    public override ValueTask<InterceptionResult> TransactionCommittingAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Committing transaction {TransactionId} (async)...", eventData.TransactionId);
        return base.TransactionCommittingAsync(transaction, eventData, result, cancellationToken);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
    {
        _logger.LogInformation("Transaction {TransactionId} COMMITTED in {Duration:F1}ms.",
            eventData.TransactionId, eventData.Duration.TotalMilliseconds);
        base.TransactionCommitted(transaction, eventData);
    }

    public override Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Transaction {TransactionId} COMMITTED (async) in {Duration:F1}ms.",
            eventData.TransactionId, eventData.Duration.TotalMilliseconds);
        return base.TransactionCommittedAsync(transaction, eventData, cancellationToken);
    }

    public override InterceptionResult TransactionRollingBack(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result)
    {
        _logger.LogWarning("ROLLING BACK transaction {TransactionId}...", eventData.TransactionId);
        return base.TransactionRollingBack(transaction, eventData, result);
    }

    public override ValueTask<InterceptionResult> TransactionRollingBackAsync(
        DbTransaction transaction, TransactionEventData eventData, InterceptionResult result,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("ROLLING BACK transaction {TransactionId} (async)...", eventData.TransactionId);
        return base.TransactionRollingBackAsync(transaction, eventData, result, cancellationToken);
    }

    public override void TransactionRolledBack(DbTransaction transaction, TransactionEndEventData eventData)
    {
        _logger.LogWarning("Transaction {TransactionId} ROLLED BACK in {Duration:F1}ms.",
            eventData.TransactionId, eventData.Duration.TotalMilliseconds);
        base.TransactionRolledBack(transaction, eventData);
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Transaction {TransactionId} ROLLED BACK (async).", eventData.TransactionId);
        return base.TransactionRolledBackAsync(transaction, eventData, cancellationToken);
    }

    public override void TransactionFailed(DbTransaction transaction, TransactionErrorEventData eventData)
    {
        _logger.LogError(eventData.Exception,
            "Transaction {TransactionId} FAILED: {ErrorMessage}",
            eventData.TransactionId, eventData.Exception?.Message);
        base.TransactionFailed(transaction, eventData);
    }

    public override Task TransactionFailedAsync(
        DbTransaction transaction, TransactionErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        TransactionFailed(transaction, eventData);
        return base.TransactionFailedAsync(transaction, eventData, cancellationToken);
    }

    private static string Describe(DbContext? context) => context?.GetType().Name ?? "<no context>";
}
