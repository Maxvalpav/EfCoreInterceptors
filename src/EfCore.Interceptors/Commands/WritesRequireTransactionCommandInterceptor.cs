using System.Data.Common;
using EfCore.Interceptors.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Enforces the unit-of-work rule: any write statement must run inside an explicit transaction,
/// otherwise <see cref="MissingTransactionException"/> is thrown before it reaches the database.
/// Reads are never blocked.
/// </summary>
public class WritesRequireTransactionCommandInterceptor(
    Func<DbContext?, bool>? isEnabled = null) : DbCommandInterceptor
{
    private readonly Func<DbContext?, bool> _isEnabled = isEnabled ?? (_ => true);

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Guard(eventData.Context, command);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context, command);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Guard(eventData.Context, command);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context, command);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Guard(eventData.Context, command);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context, command);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    protected virtual void Guard(DbContext? context, DbCommand command)
    {
        if (!_isEnabled(context) || !SqlWriteDetector.IsWrite(command.CommandText))
        {
            return;
        }

        // command.Transaction covers cases where transaction was started via DbConnection directly
        // CurrentTransaction covers EF-managed transactions. Either satisfies the rule.
        // Also respect implicit SaveChanges transaction: if no explicit Tx but command is part of SaveChanges,
        // EF will create an internal transaction; we allow it when command.Transaction is not null
        // or when caller explicitly opts out via isEnabled predicate.
        if (context?.Database.CurrentTransaction is null && command.Transaction is null)
        {
            throw new MissingTransactionException(
                $"Write statement rejected: no explicit transaction on '{context?.GetType().Name}'. " +
                "Wrap multi-step writes in BeginTransaction or use an execution strategy. " +
                $"Sql: {command.CommandText[..Math.Min(200, command.CommandText.Length)]}");
        }
    }
}
