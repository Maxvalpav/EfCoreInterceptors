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
        Guard(eventData, command.CommandText);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData, command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Guard(eventData, command.CommandText);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData, command.CommandText);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Guard(eventData, command.CommandText);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData, command.CommandText);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    protected virtual void Guard(CommandEventData eventData, string sql)
    {
        if (!_isEnabled(eventData.Context))
            return;

        var isWrite = eventData.CommandSource switch
        {
            CommandSource.SaveChanges => true,
            CommandSource.Migrations => true,
            CommandSource.ExecuteDelete => true,
            CommandSource.ExecuteUpdate => true,
            CommandSource.ExecuteSqlRaw => _isWriteCommand(sql),
            _ => false,
        };
#pragma warning disable CS0618
        if (!isWrite && eventData.CommandSource == CommandSource.BulkUpdate) isWrite = true;
#pragma warning restore CS0618
#pragma warning restore CS0618

        if (!isWrite) return;

        throw new ReadOnlyContextException(
            $"Write operation blocked: context '{eventData.Context?.GetType().Name ?? "<unknown>"}' is read-only. " +
            $"Offending statement: {sql}");
    }

    [GeneratedRegex(
        @"^\s*(?:with\b[\s\S]*?\)\s*)?(insert|update|delete|merge|truncate|drop|alter|create)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex WriteRegex();
}

/// <summary>Raised when a write command is attempted on a read-only guarded context.</summary>
public sealed class ReadOnlyContextException(string message) : InvalidOperationException(message);
