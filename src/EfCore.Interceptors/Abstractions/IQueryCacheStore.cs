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
public sealed class MemoryQueryCacheStore(TimeSpan? defaultTtl = null, int sizeLimit = 1000) : IQueryCacheStore
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (CachedQueryResult Result, DateTimeOffset Expires)> _map = new();
    private readonly TimeSpan _defaultTtl = defaultTtl ?? TimeSpan.FromSeconds(30);
    private readonly int _sizeLimit = Math.Max(1, sizeLimit);

    public int Count => _map.Count;
    public bool TryGet(string key, out CachedQueryResult? value)
    {
        value = null;
        if (!_map.TryGetValue(key, out var entry)) return false;
        if (entry.Expires > DateTimeOffset.UtcNow) { value = entry.Result; return true; }
        _map.TryRemove(key, out _);
        return false;
    }
    public void Set(string key, CachedQueryResult value, TimeSpan ttl)
    {
        if (_map.Count >= _sizeLimit)
        {
            foreach (var kv in _map) if (kv.Value.Expires <= DateTimeOffset.UtcNow) _map.TryRemove(kv.Key, out _);
            if (_map.Count >= _sizeLimit) { var first = _map.Keys.FirstOrDefault(); if (first != null) _map.TryRemove(first, out _); }
        }
        _map[key] = (value, DateTimeOffset.UtcNow.Add(ttl == default ? _defaultTtl : ttl));
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

    private static byte[] Serialize(CachedQueryResult r) => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(r, new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
    private static CachedQueryResult? Deserialize(byte[] b) => System.Text.Json.JsonSerializer.Deserialize<CachedQueryResult>(b, new System.Text.Json.JsonSerializerOptions { IncludeFields = true });
}
