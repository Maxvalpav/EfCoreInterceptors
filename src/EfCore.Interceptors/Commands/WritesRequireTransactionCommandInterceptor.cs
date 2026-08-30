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
        Guard(eventData.Context, command.CommandText);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context, command.CommandText);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Guard(eventData.Context, command.CommandText);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context, command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    protected virtual void Guard(DbContext? context, string sql)
    {
        if (!_isEnabled(context) || !SqlWriteDetector.IsWrite(sql))
        {
            return;
        }

        if (context?.Database.CurrentTransaction is null)
        {
            throw new MissingTransactionException(
                $"Write statement rejected: no explicit transaction on '{context?.GetType().Name}'. " +
                "Wrap multi-step writes in BeginTransaction or use an execution strategy.");
        }
    }
}
