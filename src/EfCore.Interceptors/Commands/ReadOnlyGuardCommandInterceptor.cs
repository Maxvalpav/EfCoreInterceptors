using System.Data.Common;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Guards a DbContext against accidental writes: any INSERT/UPDATE/DELETE/DDL statement
/// throws <see cref="ReadOnlyContextException"/> before it reaches the database.
/// Useful for reporting/read-only contexts. Enable selectively via the <paramref name="isEnabled"/> predicate.
/// </summary>
public partial class ReadOnlyGuardCommandInterceptor(
    Func<DbContext?, bool>? isEnabled = null,
    Func<string, bool>? isWriteCommand = null) : DbCommandInterceptor
{
    private static readonly Regex DefaultWritePattern = WriteRegex();

    private readonly Func<DbContext?, bool> _isEnabled = isEnabled ?? (_ => true);
    private readonly Func<string, bool> _isWriteCommand =
        isWriteCommand ?? (sql => DefaultWritePattern.IsMatch(sql));

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

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Guard(eventData.Context, command.CommandText);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData.Context, command.CommandText);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

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

    protected virtual void Guard(DbContext? context, string sql)
    {
        if (!_isEnabled(context) || !_isWriteCommand(sql))
        {
            return;
        }

        throw new ReadOnlyContextException(
            $"Write operation blocked: context '{context?.GetType().Name ?? "<unknown>"}' is read-only. " +
            $"Offending statement: {sql}");
    }

    [GeneratedRegex(
        @"\b(insert\s+into|insert|update\s+\w+\s+set|update|delete\s+from|delete|merge|truncate|create\s+(table|index|view)|alter\s+table|drop\s+(table|index|view))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant)]
    private static partial Regex WriteRegex();
}

/// <summary>Raised when a write command is attempted on a read-only guarded context.</summary>
public sealed class ReadOnlyContextException(string message) : InvalidOperationException(message);