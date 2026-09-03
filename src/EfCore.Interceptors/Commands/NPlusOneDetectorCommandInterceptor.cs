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
/// With <c>captureStackTrace</c> (03.7) the warning also carries the user call site and an
/// eager-loading hint — off by default (stack capture costs microseconds per command).
/// </summary>
public class NPlusOneDetectorCommandInterceptor(
    int threshold = 5,
    ILoggerFactory? loggerFactory = null,
    bool captureStackTrace = false) : DbCommandInterceptor
{
    private readonly int _threshold = threshold;
    private readonly bool _captureStackTrace = captureStackTrace;
    private readonly ILogger _logger =
        loggerFactory?.CreateLogger("EfCore.Interceptors.NPlusOne") ?? NullLogger.Instance;

    // Weak table: entries die with their context, no leak across context lifetimes.
    // Key is XxHash3 of SQL template to reduce allocations (performance-audit #7)
    private static readonly ConditionalWeakTable<DbContext, ConcurrentDictionary<ulong, int>> _executions = new();
    public static void Clear(DbContext context) => _executions.Remove(context);

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
        if (eventData.Context is null) return;
        var key = System.IO.Hashing.XxHash3.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(sql));
        var counters = _executions.GetOrCreateValue(eventData.Context);
        var hits = counters.AddOrUpdate(key, 1, (_, existing) => existing + 1);
        if (hits == _threshold)
        {
            if (!_captureStackTrace)
            {
                _logger.LogWarning(
                    "Possible N+1 detected: the same command has executed {Hits} times in this context. " +
                    "Consider eager loading (Include) or batching. Sql: {Sql}",
                    hits, sql);
                return;
            }
            var site = FindUserCallSite();
            _logger.LogWarning(
                "Possible N+1 detected: {Template} executed {Hits} times in this context{Site}. " +
                "Suggestion: eager-load the collection at the query root with .Include(...) or project with Select. Sql: {Sql}",
                ShortTemplate(sql), hits, site, sql);
        }
    }

    private static string ShortTemplate(string sql)
        => sql.Length > 160 ? sql[..160] + "..." : sql;

    private static string FindUserCallSite()
    {
        try
        {
            var frames = new System.Diagnostics.StackTrace(fNeedFileInfo: true).GetFrames();
            if (frames is null) return string.Empty;
            foreach (var frame in frames)
            {
                var method = frame.GetMethod();
                var ns = method?.DeclaringType?.Namespace ?? string.Empty;
                if (ns.StartsWith("EfCore.Interceptors", StringComparison.Ordinal)
                    || ns.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                    || ns.StartsWith("System.", StringComparison.Ordinal))
                    continue;
                var location = frame.GetFileName() is { } file
                    ? $" at {System.IO.Path.GetFileName(file)}:{frame.GetFileLineNumber()}"
                    : string.Empty;
                return $" — first user frame: {method?.DeclaringType?.FullName}.{method?.Name}(){location}";
            }
        }
        catch { }
        return string.Empty;
    }
}
