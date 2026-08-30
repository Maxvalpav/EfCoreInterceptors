using System.Collections.Concurrent;
using System.Data.Common;
using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// N+1 query detector: counts executions of identical SQL templates per DbContext instance
/// (EF parameterizes queries, so the same template repeated with different values is the classic
/// N+1 signature) and logs a warning once per template when the threshold is crossed.
/// </summary>
public class NPlusOneDetectorCommandInterceptor(
    int threshold = 5,
    ILoggerFactory? loggerFactory = null) : DbCommandInterceptor
{
    private readonly int _threshold = threshold;
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.NPlusOne") ?? NullLogger.Instance;

    // Weak table: entries die with their context, no leak across context lifetimes.
    private readonly ConditionalWeakTable<DbContext, ConcurrentDictionary<string, int>> _executions = new();

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        Track(eventData, command.CommandText);
        return base.ReaderExecuting(command, eventData, result);
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        Track(eventData, command.CommandText);
        return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
    }

    protected virtual void Track(CommandEventData eventData, string sql)
    {
        if (eventData.Context is null)
        {
            return;
        }

        var counters = _executions.GetOrCreateValue(eventData.Context);
        var hits = counters.AddOrUpdate(sql, 1, (_, existing) => existing + 1);

        if (hits == _threshold)
        {
            _logger.LogWarning(
                "Possible N+1 detected: the same command has executed {Hits} times in this context. " +
                "Consider eager loading (Include) or batching. Sql: {Sql}",
                hits, sql);
        }
    }
}