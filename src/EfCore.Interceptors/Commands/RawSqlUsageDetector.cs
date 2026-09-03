using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Visibility over raw-SQL bypass paths: logs a warning (and optionally invokes a callback)
/// whenever a command originates from FromSqlRaw / SqlQuery&lt;T&gt; / ExecuteSqlRaw
/// (<see cref="CommandSource.FromSqlQuery"/> / <see cref="CommandSource.ExecuteSqlRaw"/>).
/// </summary>
public class RawSqlUsageDetector(
    ILoggerFactory? loggerFactory = null,
    Action<CommandSource, string>? onRawSqlExecuted = null) : DbCommandInterceptor
{
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.RawSql") ?? NullLogger.Instance;
    private readonly Action<CommandSource, string>? _callback = onRawSqlExecuted;

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Detect(eventData, command.CommandText);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Detect(eventData, command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<object> ScalarExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        Detect(eventData, command.CommandText);
        return base.ScalarExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<object>> ScalarExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<object> result,
        CancellationToken cancellationToken = default)
    {
        Detect(eventData, command.CommandText);
        return base.ScalarExecutingAsync(command, eventData, result, cancellationToken);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        Detect(eventData, command.CommandText);
        return base.NonQueryExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        Detect(eventData, command.CommandText);
        return base.NonQueryExecutingAsync(command, eventData, result, cancellationToken);
    }

    protected virtual void Detect(CommandEventData eventData, string sql)
    {
        if (eventData.CommandSource is not (CommandSource.FromSqlQuery or CommandSource.ExecuteSqlRaw))
        {
            return;
        }

        if (_logger.IsEnabled(LogLevel.Warning))
            _logger.LogWarning(
                "Raw SQL executed ({Source}): {Sql}", eventData.CommandSource, sql.Length > 2048 ? sql[..2048] + "..." : sql);
        try
        {
            _callback?.Invoke(eventData.CommandSource, sql);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RawSqlUsageDetector callback failed for {Source}", eventData.CommandSource);
        }
    }
}

/// <summary>Unified <c>*Interceptor</c> naming alias (09.6).</summary>
[Obsolete("Use RawSqlUsageDetector (canonical name).")]
public sealed class RawSqlUsageDetectorInterceptor(
    ILoggerFactory? loggerFactory = null,
    Action<CommandSource, string>? onRawSqlExecuted = null)
    : RawSqlUsageDetector(loggerFactory, onRawSqlExecuted);
