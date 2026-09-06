using EfCore.Interceptors.Commands;

namespace EfCore.Interceptors.Abstractions;

/// <summary>
/// Abstraction for second-level query cache store (performance-audit #14, architecture-audit 2.2).
/// Allows swapping MemoryCache with Redis/FusionCache.
/// </summary>
public interface IQueryCacheStore
{
    bool TryGet(string key, out CachedQueryResult? value);
    void Set(string key, CachedQueryResult value, TimeSpan ttl);
    void Invalidate(string tag);
    void Clear();
    int Count { get; }
}

/// <summary>In-memory store with TTL and SizeLimit (default for single instance).</summary>
public sealed class MemoryQueryCacheStore(TimeSpan? defaultTtl = null, int sizeLimit = 1000, TimeProvider? clock = null) : IQueryCacheStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (CachedQueryResult Result, DateTimeOffset Expires)> _map = new();
    private readonly TimeSpan _defaultTtl = defaultTtl ?? TimeSpan.FromSeconds(30);
    private readonly int _sizeLimit = Math.Max(1, sizeLimit);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _evictLock = new();

    public int Count => _map.Count;
    public bool TryGet(string key, out CachedQueryResult? value)
    {
        value = null;
        if (!_map.TryGetValue(key, out var entry)) return false;
        if (entry.Expires > _clock.GetUtcNow()) { value = entry.Result; return true; }
        _map.TryRemove(key, out _);
        return false;
    }
    public void Set(string key, CachedQueryResult value, TimeSpan ttl)
    {
        if (_map.Count >= _sizeLimit)
        {
            lock (_evictLock)
            {
                if (_map.Count >= _sizeLimit)
                {
                    var now = _clock.GetUtcNow();
                    foreach (var kv in _map) if (kv.Value.Expires <= now) _map.TryRemove(kv.Key, out _);
                    if (_map.Count >= _sizeLimit)
                    {
                        // LRU-ish: evict earliest expiry (best-effort without full LRU)
                        var victim = _map.OrderBy(kv => kv.Value.Expires).FirstOrDefault().Key;
                        if (victim != null) _map.TryRemove(victim, out _);
                    }
                }
            }
        }
        _map[key] = (value, _clock.GetUtcNow().Add(ttl == default ? _defaultTtl : ttl));
    }
    public void Invalidate(string tag)
    {
        foreach (var k in _map.Keys.Where(k => k.Contains(tag, StringComparison.OrdinalIgnoreCase)).ToList())
            _map.TryRemove(k, out _);
    }
    public void Clear() => _map.Clear();
}

/// <summary>
/// Distributed store via IDistributedCache (Redis). Serializes CachedQueryResult as JSON.
/// For production use StackExchangeRedis or FusionCache provider. Tag invalidation is best-effort (keys scan).
/// </summary>
public sealed class DistributedQueryCacheStore : IQueryCacheStore
{
    private readonly Microsoft.Extensions.Caching.Distributed.IDistributedCache _cache;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _keys = new();
    private readonly TimeSpan _defaultTtl;
    public DistributedQueryCacheStore(Microsoft.Extensions.Caching.Distributed.IDistributedCache cache, TimeSpan? defaultTtl = null)
    {
        _cache = cache; _defaultTtl = defaultTtl ?? TimeSpan.FromSeconds(30);
    }
    public int Count => _keys.Count;
    public bool TryGet(string key, out CachedQueryResult? value)
    {
        value = null;
        var bytes = _cache.Get(key);
        if (bytes is null) return false;
        try { value = Deserialize(bytes); return value != null; } catch { return false; }
    }
    public void Set(string key, CachedQueryResult value, TimeSpan ttl)
    {
        var opts = new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = ttl == default ? _defaultTtl : ttl };
        _cache.Set(key, Serialize(value), opts);
        _keys.TryAdd(key, 0);
    }
    public void Invalidate(string tag)
    {
        foreach (var k in _keys.Keys.Where(k => k.Contains(tag, StringComparison.OrdinalIgnoreCase)).ToList())
        { _cache.Remove(k); _keys.TryRemove(k, out _); }
    }
    public void Clear() { foreach (var k in _keys.Keys.ToList()) _cache.Remove(k); _keys.Clear(); }

    // Note: System.Text.Json cannot round-trip List<object[]> with mixed types (JsonElement). Serialize preserves field types for Memory, but Distributed requires binary/MessagePack for prod. Keep best-effort JSON with type coercion.
    private static byte[] Serialize(CachedQueryResult r) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(r, new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
    private static CachedQueryResult? Deserialize(byte[] b)
    {
        try { return System.Text.Json.JsonSerializer.Deserialize<CachedQueryResult>(b, new System.Text.Json.JsonSerializerOptions { IncludeFields = true }); }
        catch { return null; }
    }
}

/// <summary>
/// Contributes an extra isolating part to the second-level cache key (tenant id, session
/// version, schema override, ...). The cache is correct only if everything that affects the
/// result rows is present in the SQL/parameters or in a contributor part: filters baked as
/// constants (captured variables in query filters), RLS policies, <c>current_setting</c> and
/// session state set via <c>WithSessionInit</c> are invisible to the default key.
/// Return null/empty when the contributor does not apply.
/// </summary>
public interface IQueryCacheKeyContributor
{
    /// <param name="command">The command about to be executed (SQL + parameters available).</param>
    /// <param name="eventData">EF command event (may carry the DbContext); can be null in diagnostics.</param>
    string? Contribute(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData? eventData);
}

/// <summary>Tenant isolation for the second-level cache (shared-database multi-tenancy).</summary>
/// <remarks>
/// Parameterized tenant filters already isolate keys via parameter values, but constant-captured
/// filters (<c>ApplyTenantFilters(provider)</c>), per-tenant models (<c>TenantModelCacheKeyFactory</c>)
/// and schema/session isolation (<c>WithSessionInit</c>) can produce identical SQL+parameters for
/// different tenants. <c>BuildInto</c> wires this contributor automatically when
/// <c>WithMultiTenancy</c> is used together with a cache.
/// </remarks>
public sealed class TenantKeyContributor(ITenantProvider tenantProvider) : IQueryCacheKeyContributor
{
    public string? Contribute(System.Data.Common.DbCommand command, Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData? eventData)
        => tenantProvider.CurrentTenantId;
}

/// <summary>Options-object for <c>CachingCommandInterceptor</c> (no positional boolean trap).</summary>
public sealed class CachingOptions
{
    /// <summary>Entry time-to-live. Default 30 seconds.</summary>
    public TimeSpan TimeToLive { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Skip caching inside explicit transactions (avoids dirty reads). Default true.</summary>
    public bool SkipInsideTransactions { get; set; } = true;

    /// <summary>Evict affected entries on writes (targeted by table). Default true (read-your-writes).</summary>
    public bool InvalidateOnWrites { get; set; } = true;

    /// <summary>Maximum cached entries. Default 1000.</summary>
    public int MaxEntries { get; set; } = 1000;

    /// <summary>Maximum rows buffered per entry (entries above bypass the store but are still served). Default 10 000.</summary>
    public int MaxRowsPerEntry { get; set; } = 10_000;

    /// <summary>Maximum bytes buffered per entry. Default 8 MiB.</summary>
    public long MaxBytesPerEntry { get; set; } = 8 * 1024 * 1024;

    /// <summary>Custom store (Redis/distributed). Default in-memory.</summary>
    public IQueryCacheStore? Store { get; set; }

    /// <summary>Key contributors (tenant id, session version, ...).</summary>
    public List<IQueryCacheKeyContributor> KeyContributors { get; } = [];
}
