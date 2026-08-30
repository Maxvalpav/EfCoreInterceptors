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
        if (_isTransientPredicate != null && _isTransientPredicate(exception))
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

    private static bool IsTransientMessage(string message)
    {
        // Avoid false positive on "non-transient" or "not transient"
        if (message.Contains("non-transient", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not transient", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return message.Contains("timeout", StringComparison.OrdinalIgnoreCase)
            || message.Contains("deadlock", StringComparison.OrdinalIgnoreCase)
            || message.Contains("transient", StringComparison.OrdinalIgnoreCase)
            || (message.Contains("connection", StringComparison.OrdinalIgnoreCase) && message.Contains("closed", StringComparison.OrdinalIgnoreCase))
            || message.Contains("transport-level error", StringComparison.OrdinalIgnoreCase);
    }

    private static Exception Unwrap(Exception ex)
    {
        while (ex is DbUpdateException { InnerException: { } inner })
        {
            ex = inner;
        }

        return ex;
    }

    private static bool IsTransientSqlError(DbException ex)
    {
        try
        {
            // Microsoft.Data.SqlClient.SqlException has Number property, Npgsql has SqlState etc.
            // Use reflection to avoid provider-specific reference.
            var prop = ex.GetType().GetProperty("Number") ?? ex.GetType().GetProperty("ErrorCode");
            if (prop?.GetValue(ex) is int number)
            {
                return Array.IndexOf(TransientSqlErrorNumbers, number) >= 0;
            }

            // Postgres: SqlState 40001 (serialization_failure), 40P01 deadlock
            var sqlState = ex.GetType().GetProperty("SqlState")?.GetValue(ex) as string;
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
