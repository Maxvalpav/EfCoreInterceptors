using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Extensions to register <see cref="ResilienceExecutionStrategy"/> without external dependencies (no Polly).
/// </summary>
public static class ResilienceExecutionStrategyExtensions
{
    /// <summary>
    /// Registers a provider-agnostic resilience execution strategy that retries transient failures
    /// with exponential backoff. No Polly dependency.
    /// Call after provider configuration:
    /// <code>options.UseSqlite(cs).UseResilienceExecutionStrategy(maxRetryCount: 5)</code>
    /// Works for any provider (SqlServer, Npgsql, Sqlite for testing) via IDbContextOptionsExtension.
    /// </summary>
    public static DbContextOptionsBuilder UseResilienceExecutionStrategy(
        this DbContextOptionsBuilder optionsBuilder,
        int maxRetryCount = 5,
        TimeSpan? maxRetryDelay = null,
        Func<Exception, bool>? isTransient = null)
    {
        var extension = new ResilienceStrategyOptionsExtension(maxRetryCount, maxRetryDelay, isTransient);
        ((IDbContextOptionsBuilderInfrastructure)optionsBuilder).AddOrUpdateExtension(extension);
        return optionsBuilder;
    }

    /// <summary>Generic overload for typed <see cref="DbContextOptionsBuilder{TContext}"/>.</summary>
    public static TBuilder UseResilienceExecutionStrategy<TBuilder>(
        this TBuilder optionsBuilder,
        int maxRetryCount = 5,
        TimeSpan? maxRetryDelay = null,
        Func<Exception, bool>? isTransient = null)
        where TBuilder : DbContextOptionsBuilder
    {
        ((DbContextOptionsBuilder)optionsBuilder).UseResilienceExecutionStrategy(maxRetryCount, maxRetryDelay, isTransient);
        return optionsBuilder;
    }

    /// <summary>
    /// Factory helper for per-provider configuration:
    /// <code>options.UseSqlServer(cs, o => o.ExecutionStrategy(deps => new ResilienceExecutionStrategy(deps, 5, TimeSpan.FromSeconds(30))))</code>
    /// </summary>
    public static Func<ExecutionStrategyDependencies, Microsoft.EntityFrameworkCore.Storage.IExecutionStrategy> CreateResilienceFactory(
        int maxRetryCount = 5,
        TimeSpan? maxRetryDelay = null,
        Func<Exception, bool>? isTransient = null)
        => deps => new ResilienceExecutionStrategy(deps, maxRetryCount, maxRetryDelay ?? TimeSpan.FromSeconds(30), isTransient);
}
