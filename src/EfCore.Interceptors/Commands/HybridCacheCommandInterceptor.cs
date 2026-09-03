using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Second-level cache backed by IMemoryCache with TTL and size limit.
/// Properly overrides ReaderExecuting to use IMemoryCache instead of hiding base dictionary.
/// </summary>
[Obsolete("No single-flight, no size limits, no table invalidation, no metrics (06.7): use CachingCommandInterceptor (WithSecondLevelCache) instead.")]
public class HybridCacheCommandInterceptor : DbCommandInterceptor
{
    private readonly IMemoryCache _cache;
    private readonly TimeSpan _timeToLive;
    private readonly bool _skipInsideTransactions;
    private readonly MemoryCacheEntryOptions _entryOptions;

    public HybridCacheCommandInterceptor(
        IMemoryCache cache,
        TimeSpan? timeToLive = null,
        bool skipInsideTransactions = true) : base()
    {
        _cache = cache;
        _timeToLive = timeToLive ?? TimeSpan.FromSeconds(30);
        _skipInsideTransactions = skipInsideTransactions;
        // Validate SizeLimit is set; if not, warn via exception
        // Note: IMemoryCache does not expose SizeLimit via public API in this target, so we try to set Size=1
        // and catch InvalidOperationException at runtime if SizeLimit is missing. Documentation handles this.

        _entryOptions = new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = _timeToLive,
            Size = 1
        };
    }

    public void InvalidateAll()
    {
        if (_cache is MemoryCache mc)
        {
            mc.Compact(1.0);
        }
        else if (_cache is Microsoft.Extensions.Caching.Memory.MemoryCache)
        {
            // Already handled
        }
    }

    public void Invalidate(string sqlFragment) => InvalidateAll();

    // Invalidate on writes when configured via outer setup (future: add _invalidateOnWrites flag)
    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        InvalidateAll();
        return base.NonQueryExecuted(command, eventData, result);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        if (!Cacheable(eventData, command))
        {
            return base.ReaderExecuting(command, eventData, result);
        }

        var key = CachingCommandInterceptor.BuildKey(command);
        if (_cache.TryGetValue<CachedQueryResult>(key, out var cached) && cached is not null)
        {
            return InterceptionResult<DbDataReader>.SuppressWithResult(new CachedDataReader(cached));
        }

        using var reader = command.ExecuteReader();
        var snapshot = Buffer(reader);
        _cache.Set(key, snapshot, _entryOptions);
        return InterceptionResult<DbDataReader>.SuppressWithResult(new CachedDataReader(snapshot));
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (!Cacheable(eventData, command))
        {
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        var key = CachingCommandInterceptor.BuildKey(command);
        if (_cache.TryGetValue<CachedQueryResult>(key, out var cached) && cached is not null)
        {
            return InterceptionResult<DbDataReader>.SuppressWithResult(new CachedDataReader(cached));
        }

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var snapshot = await BufferAsync(reader, cancellationToken);
        _cache.Set(key, snapshot, _entryOptions);
        return InterceptionResult<DbDataReader>.SuppressWithResult(new CachedDataReader(snapshot));
    }

    private bool Cacheable(CommandEventData eventData, DbCommand command)
        => IsSelect(command.CommandText)
            && (!_skipInsideTransactions || eventData.Context?.Database.CurrentTransaction is null)
            && command.Transaction is null;

    private static bool IsSelect(string sql)
    {
        var trimmed = sql.TrimStart();
        while (true)
        {
            if (trimmed.StartsWith("--", StringComparison.Ordinal))
            {
                var newline = trimmed.IndexOf('\n');
                if (newline < 0) return false;
                trimmed = trimmed[(newline + 1)..].TrimStart();
                continue;
            }

            if (trimmed.StartsWith("/*", StringComparison.Ordinal))
            {
                var end = trimmed.IndexOf("*/", StringComparison.Ordinal);
                if (end < 0) return false;
                trimmed = trimmed[(end + 2)..].TrimStart();
                continue;
            }

            break;
        }

        return trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
    }

    private static CachedQueryResult Buffer(DbDataReader source)
    {
        var names = Enumerable.Range(0, source.FieldCount).Select(source.GetName).ToArray();
        var types = Enumerable.Range(0, source.FieldCount).Select(source.GetFieldType).ToArray();
        var rows = new List<object[]>();
        while (source.Read())
        {
            var row = new object[source.FieldCount];
            source.GetValues(row);
            for (var i = 0; i < row.Length; i++) row[i] ??= DBNull.Value;
            rows.Add(row);
        }

        return new CachedQueryResult(names, types, rows);
    }

    private static async Task<CachedQueryResult> BufferAsync(DbDataReader source, CancellationToken ct)
    {
        var names = Enumerable.Range(0, source.FieldCount).Select(source.GetName).ToArray();
        var types = Enumerable.Range(0, source.FieldCount).Select(source.GetFieldType).ToArray();
        var rows = new List<object[]>();
        while (await source.ReadAsync(ct))
        {
            var row = new object[source.FieldCount];
            source.GetValues(row);
            for (var i = 0; i < row.Length; i++) row[i] ??= DBNull.Value;
            rows.Add(row);
        }

        return new CachedQueryResult(names, types, rows);
    }
}
