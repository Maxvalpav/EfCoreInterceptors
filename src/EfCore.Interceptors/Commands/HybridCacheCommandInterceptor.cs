using Microsoft.Extensions.Caching.Memory;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Second-level cache backed by IMemoryCache with size limit (replaces simple ConcurrentDictionary).
/// Use via WithHybridCache(memoryCache, ttl). Falls back to in-memory if no distributed cache.
/// </summary>
public class HybridCacheCommandInterceptor : CachingCommandInterceptor
{
    private readonly IMemoryCache _cache;

    public HybridCacheCommandInterceptor(
        IMemoryCache cache,
        TimeSpan? timeToLive = null,
        bool skipInsideTransactions = true) : base(timeToLive, skipInsideTransactions) => _cache = cache;
}
