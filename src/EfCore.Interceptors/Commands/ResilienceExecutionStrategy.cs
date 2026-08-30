using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// DB-agnostic retry execution strategy without Polly/external deps.
/// Retries transient failures (timeout, deadlock, connection loss) with exponential backoff + jitter.
/// Use via <see cref="ResilienceExecutionStrategyExtensions.UseResilienceExecutionStrategy"/> or per-provider
/// <c>options.ExecutionStrategy(deps => new ResilienceExecutionStrategy(deps, ...))</c>.
/// </summary>
public class ResilienceExecutionStrategy : ExecutionStrategy
{
    private readonly Func<Exception, bool>? _isTransientPredicate;
    private static readonly int[] TransientSqlErrorNumbers =
    [
        4060, 10928, 10929, 40197, 40501, 40613,
        49918, 49919, 49920, // Azure elastic pool
        1205,  // deadlock victim
        10053, 10054, 10060, // TCP
        233, 64, 20, 0
    ];

    public ResilienceExecutionStrategy(
        ExecutionStrategyDependencies dependencies,
        int maxRetryCount,
        TimeSpan maxRetryDelay,
        Func<Exception, bool>? isTransient = null)
        : base(dependencies, maxRetryCount, maxRetryDelay)
    {
        _isTransientPredicate = isTransient;
    }

    public ResilienceExecutionStrategy(
        DbContext context,
        int maxRetryCount,
        TimeSpan maxRetryDelay,
        Func<Exception, bool>? isTransient = null)
        : this(
            context.GetService<ExecutionStrategyDependencies>(),
            maxRetryCount,
            maxRetryDelay,
            isTransient)
    {
    }

    protected override bool ShouldRetryOn(Exception exception)
    {
        if (_isTransientPredicate != null && (_isTransientPredicate(exception) || _isTransientPredicate(Unwrap(exception))))
        {
            return true;
        }

        // Unwrap EF wrapper
        var ex = Unwrap(exception);

        if (ex is TimeoutException)
        {
            return true;
        }

        if (ex is DbException dbEx)
        {
            // Check Sql error numbers via reflection (avoid hard dep on Microsoft.Data.SqlClient)
            if (IsTransientSqlError(dbEx))
            {
                return true;
            }

            // Generic transient by message
            var msg = dbEx.Message;
            if (IsTransientMessage(msg))
            {
                return true;
            }
        }

        return IsTransientMessage(ex.Message);
    }

    private static readonly System.Text.RegularExpressions.Regex TransientRegex = new(@"\b(timeout|deadlock|transient)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsTransientMessage(string message)
    {
        // Avoid false positive on "non-transient" or "not transient" - check negations first
        if (message.Contains("non-transient", StringComparison.OrdinalIgnoreCase)
            || message.Contains("non_transient", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not transient", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (TransientRegex.IsMatch(message)) return true;
        if (message.Contains("transport-level error", StringComparison.OrdinalIgnoreCase)) return true;
        if (message.Contains("connection", StringComparison.OrdinalIgnoreCase) && message.Contains("closed", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static Exception Unwrap(Exception ex)
    {
        var depth = 0;
        while (depth++ < 5 && ex.InnerException is not null && ex is DbUpdateException or AggregateException or InvalidOperationException)
        {
            ex = ex.InnerException;
        }

        return ex;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> NumberPropCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo?> SqlStatePropCache = new();

    private static bool IsTransientSqlError(DbException ex)
    {
        try
        {
            var type = ex.GetType();
            var prop = NumberPropCache.GetOrAdd(type, t => t.GetProperty("Number") ?? t.GetProperty("ErrorCode"));
            if (prop?.GetValue(ex) is int number)
            {
                return Array.IndexOf(TransientSqlErrorNumbers, number) >= 0;
            }

            // Postgres: SqlState 40001 (serialization_failure), 40P01 deadlock
            var sqlStateProp = SqlStatePropCache.GetOrAdd(type, t => t.GetProperty("SqlState"));
            var sqlState = sqlStateProp?.GetValue(ex) as string;
            if (sqlState is "40001" or "40P01" or "55P03")
            {
                return true;
            }
        }
        catch
        {
            // ignore reflection errors
        }

        return false;
    }
}

/// <summary>
/// Factory for <see cref="ResilienceExecutionStrategy"/> to be registered via DI.
/// </summary>
public sealed class ResilienceExecutionStrategyFactory : IExecutionStrategyFactory
{
    private readonly ExecutionStrategyDependencies _dependencies;
    private readonly int _maxRetryCount;
    private readonly TimeSpan _maxRetryDelay;
    private readonly Func<Exception, bool>? _isTransient;

    public ResilienceExecutionStrategyFactory(
        ExecutionStrategyDependencies dependencies,
        int maxRetryCount = 5,
        TimeSpan? maxRetryDelay = null,
        Func<Exception, bool>? isTransient = null)
    {
        _dependencies = dependencies;
        _maxRetryCount = Math.Max(0, maxRetryCount);
        _maxRetryDelay = maxRetryDelay ?? TimeSpan.FromSeconds(30);
        _isTransient = isTransient;
    }

    public IExecutionStrategy Create()
        => new ResilienceExecutionStrategy(_dependencies, _maxRetryCount, _maxRetryDelay, _isTransient);
}
