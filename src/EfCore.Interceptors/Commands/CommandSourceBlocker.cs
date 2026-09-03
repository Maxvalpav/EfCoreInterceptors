using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace EfCore.Interceptors.Commands;

/// <summary>Raised when a command with a blocked <see cref="CommandSource"/> is executed.</summary>
public sealed class BlockedCommandSourceException(string message) : InvalidOperationException(message);

/// <summary>
/// Governance guard: rejects commands whose <see cref="CommandSource"/> is in the
/// blocked list — e.g. forbid running EF migrations from application runtime
/// (<c>CommandSource.Migrations</c>) or disallow bulk operations during business hours.
/// </summary>
public class CommandSourceBlocker(params CommandSource[] blockedSources) : DbCommandInterceptor
{
    private readonly CommandSource[] _blocked =
        blockedSources.Length > 0 ? blockedSources : [CommandSource.Migrations];

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Guard(eventData);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Guard(eventData);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Guard(eventData);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Guard(eventData);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    protected virtual void Guard(CommandEventData eventData)
    {
        if (Array.IndexOf(_blocked, eventData.CommandSource) >= 0)
        {
            throw new BlockedCommandSourceException(
                $"Commands with source '{eventData.CommandSource}' are blocked by policy.");
        }
    }
}

/// <summary>Unified <c>*Interceptor</c> naming alias (09.6).</summary>
[Obsolete("Use CommandSourceBlocker (canonical name).")]
public sealed class CommandSourceBlockerInterceptor(params CommandSource[] blockedSources)
    : CommandSourceBlocker(blockedSources);
