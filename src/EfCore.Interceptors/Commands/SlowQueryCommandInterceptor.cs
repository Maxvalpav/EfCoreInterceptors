using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using System.Data.Common;
namespace EfCore.Interceptors.Commands;

/// <summary>
/// Detects commands that run longer than the configured threshold and logs a warning
/// containing the SQL text and measured duration. Uses the <see cref="CommandEndEventData.Duration"/>
/// reported by EF Core, so no stopwatch bookkeeping is required.
/// </summary>
public class SlowQueryCommandInterceptor(
    TimeSpan threshold,
    ILoggerFactory? loggerFactory = null,
    Func<CommandEventData, bool>? filter = null) : DbCommandInterceptor
{
    private readonly TimeSpan _threshold = threshold;
    private readonly Func<CommandEventData, bool>? _filter = filter;
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.SlowQuery") ?? NullLogger.Instance;

    public override DbDataReader ReaderExecuted(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        Check(eventData, command);
        return base.ReaderExecuted(command, eventData, result);
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        Check(eventData, command);
        return base.ReaderExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override object? ScalarExecuted(
        DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        Check(eventData, command);
        return base.ScalarExecuted(command, eventData, result);
    }

    public override ValueTask<object?> ScalarExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, object? result,
        CancellationToken cancellationToken = default)
    {
        Check(eventData, command);
        return base.ScalarExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override int NonQueryExecuted(
        DbCommand command, CommandExecutedEventData eventData, int result)
    {
        Check(eventData, command);
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        Check(eventData, command);
        return base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    protected virtual void Check(CommandEndEventData eventData, DbCommand command)
    {
        if (eventData.Duration < _threshold)
        {
            return;
        }

        if (_filter is not null && !_filter(eventData))
        {
            return;
        }

        _logger.LogWarning(
            "Slow EF command detected: {Duration:F1}ms exceeded threshold {Threshold}ms. Sql: {Sql}",
            eventData.Duration.TotalMilliseconds,
            _threshold.TotalMilliseconds,
            command.CommandText);
    }
}