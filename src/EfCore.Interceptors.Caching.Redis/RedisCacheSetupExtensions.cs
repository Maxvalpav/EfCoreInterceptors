using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Commands;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.DependencyInjection;

namespace EfCore.Interceptors.Caching.Redis;

/// <summary>
/// Redis second-level cache for multi-instance deployments (02.8): every instance
/// shares invalidations through Redis instead of a process-local dictionary.
/// Backed by the existing <see cref="DistributedQueryCacheStore"/> over
/// <see cref="IDistributedCache"/> — this package only wires the Redis provider.
/// </summary>
public static class RedisCacheSetupExtensions
{
    /// <summary>
    /// Registers StackExchangeRedis as the <see cref="IDistributedCache"/> and returns
    /// a store for <c>WithSecondLevelCache(store)</c>.
    /// </summary>
    public static IServiceCollection AddEfInterceptorsRedisCache(
        this IServiceCollection services,
        string configuration,
        string? instanceName = null,
        TimeSpan? defaultTtl = null)
    {
        services.AddStackExchangeRedisCache(o =>
        {
            o.Configuration = configuration;
            if (instanceName is not null) o.InstanceName = instanceName;
        });
        services.AddSingleton<IQueryCacheStore>(sp => new DistributedQueryCacheStore(
            sp.GetRequiredService<IDistributedCache>(), defaultTtl));
        return services;
    }

    /// <summary>
    /// Fluent second-level cache over an existing <see cref="IDistributedCache"/>
    /// (Redis, SQL Server cache, NCache, …).
    /// </summary>
    public static EfInterceptorsSetup WithRedisCache(
        this EfInterceptorsSetup setup,
        IDistributedCache cache,
        TimeSpan? timeToLive = null,
        bool skipInsideTransactions = true,
        bool invalidateOnWrites = false)
        => setup.WithSecondLevelCache(
            new DistributedQueryCacheStore(cache, timeToLive),
            timeToLive, skipInsideTransactions, invalidateOnWrites);
}
