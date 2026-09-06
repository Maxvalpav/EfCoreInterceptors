using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Observability;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage;

namespace EfCore.Interceptors.Commands;

/// <summary>
/// Read-through second-level cache for queries: identical SELECTs (same SQL + parameter values)
/// are served from an in-memory buffer without touching the database.
/// The interceptor executes the command itself into a buffer and hands EF a
/// <see cref="CachedDataReader"/> — both on misses and on hits — so the live reader semantics stay intact.
/// Entries expire after the configured TTL. Caching is skipped inside explicit transactions by
/// default to avoid dirty reads.
/// </summary>
/// <remarks>
/// Cache safety: a cached result is correct only if everything that affects the rows is part of
/// the cache key (SQL text + parameter values + connection identity + <see cref="IQueryCacheKeyContributor"/> parts).
/// Non-parameterized influences — SQL Server RLS policies / CONTEXT_INFO, PostgreSQL
/// <c>current_setting('app.tenant')</c>, session state set via <c>WithSessionInit</c> — are invisible
/// to the key. For shared-database multi-tenancy register a tenant contributor (done automatically
/// by <c>WithMultiTenancy</c> in <c>BuildInto</c>); for session-dependent queries disable caching
/// for those queries instead of relying on the global cache.
/// Neighboring <c>DbCommandInterceptor</c>s (slow-query, metrics, logging) still observe the real
/// database round-trip: the fetch happens synchronously inside <c>ReaderExecuting</c>, so their
/// <c>ReaderExecuted</c> events carry the true duration. Register caching last so logging/metrics
/// <c>ReaderExecuting</c> hooks run before the suppress.
/// </remarks>
public class CachingCommandInterceptor : DbCommandInterceptor, Microsoft.EntityFrameworkCore.Diagnostics.IDbTransactionInterceptor
{
    private readonly IQueryCacheStore _store;
    private readonly TimeSpan _timeToLive;
    private readonly bool _skipInsideTransactions;
    private readonly bool _invalidateOnWrites;
    private readonly int _maxRowsPerEntry;
    private readonly long _maxBytesPerEntry;
    private readonly int _maxEntries;
    // Key contributors (tenant id, session state, ...) appended to the cache key.
    private readonly List<IQueryCacheKeyContributor> _contributors = [];
    // Table dependencies for targeted invalidation (06.4): cache key -> tables the
    // query read, plus a per-table generation counter bumped on every write.
    private readonly ConcurrentDictionary<string, string[]> _keyTables = new(StringComparer.Ordinal);
    // Cache key -> raw SQL text (for fragment invalidation over the SQL part only,
    // so a fragment never matches parameter values embedded in the key).
    private readonly ConcurrentDictionary<string, string> _keySql = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, long> _generations = new(StringComparer.OrdinalIgnoreCase);
    // Single-flight gates against thundering herd (06.1): concurrent misses for the
    // same key coalesce onto one DB round-trip; losers re-check the cache after.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new();
    // Async single-flight: concurrent awaiters share one fetch task; removed only when
    // it is our own lazy (pair-remove), so a newer in-flight fetch is never cancelled.
    private readonly ConcurrentDictionary<string, Lazy<Task<CachedQueryResult>>> _inflight = new();
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Data.Common.DbTransaction, HashSet<string>> _pendingTx = new();

    public CachingCommandInterceptor(
        TimeSpan? timeToLive = null,
        bool skipInsideTransactions = true,
        bool invalidateOnWrites = true,
        int maxEntries = 1000,
        IQueryCacheStore? store = null,
        int maxRowsPerEntry = 10_000,
        long maxBytesPerEntry = 8 * 1024 * 1024)
    {
        _timeToLive = timeToLive ?? TimeSpan.FromSeconds(30);
        _skipInsideTransactions = skipInsideTransactions;
        _invalidateOnWrites = invalidateOnWrites;
        _maxRowsPerEntry = Math.Max(1, maxRowsPerEntry);
        _maxBytesPerEntry = Math.Max(1024, maxBytesPerEntry);
        _maxEntries = Math.Max(1, maxEntries);
        _store = store ?? new MemoryQueryCacheStore(_timeToLive, maxEntries);
    }

    /// <summary>Options-object construction (no positional boolean trap).</summary>
    public CachingCommandInterceptor(CachingOptions options)
        : this(options.TimeToLive, options.SkipInsideTransactions, options.InvalidateOnWrites,
            options.MaxEntries, options.Store, options.MaxRowsPerEntry, options.MaxBytesPerEntry)
    {
        foreach (var c in options.KeyContributors) AddContributor(c);
    }

    /// <summary>Registers a cache-key contributor (tenant id, session version, ...). Same runtime type replaces the previous one.</summary>
    public void AddContributor(IQueryCacheKeyContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        lock (_contributors)
        {
            _contributors.RemoveAll(c => c.GetType() == contributor.GetType());
            _contributors.Add(contributor);
        }
    }

    /// <summary>Number of currently cached query results.</summary>
    public int Count => _store.Count;

    /// <summary>Drops every cached query result.</summary>
    public void InvalidateAll()
    {
        _store.Clear();
        _keyTables.Clear();
        _keySql.Clear();
    }
    public static void Clear(Microsoft.EntityFrameworkCore.DbContext context) { /* global cache — per-context clear no-op; pool does not leak per-context state */ }

    /// <summary>
    /// Drops cached results whose SQL contains the given fragment (e.g. a table name).
    /// Matching runs over parsed read-tables and the SQL text — never over parameter values
    /// embedded in the key, so <c>Invalidate("Order")</c> won't evict a query with
    /// <c>@p0 = 'Order confirmed'</c>. Prefer <see cref="InvalidateTable"/> for table eviction.
    /// Best-effort for very old entries whose SQL was trimmed from diagnostics (they still
    /// expire via TTL and are evicted by table-targeted invalidation).
    /// </summary>
    public void Invalidate(string sqlFragment)
    {
        if (string.IsNullOrWhiteSpace(sqlFragment)) return;
        // Prefer targeted table invalidation: _keyTables holds parsed read-tables per key,
        // so 'Order' won't match a parameter value like 'Order confirmed' stored in the key.
        foreach (var kv in _keyTables)
        {
            if (kv.Value.Any(t => string.Equals(t, sqlFragment, StringComparison.OrdinalIgnoreCase)
                || t.Contains(sqlFragment, StringComparison.OrdinalIgnoreCase)))
            {
                EvictKey(kv.Key);
            }
        }
        // Then SQL-text matching (still not parameter values).
        foreach (var kv in _keySql)
        {
            if (kv.Value.Contains(sqlFragment, StringComparison.OrdinalIgnoreCase))
            {
                EvictKey(kv.Key);
            }
        }
    }

    private void EvictKey(string key)
    {
        _store.Invalidate(key); // exact-key eviction via store scan
        _keyTables.TryRemove(key, out _);
        _keySql.TryRemove(key, out _);
    }

    /// <summary>
    /// Targeted invalidation (06.4): drops only entries that read the given table.
    /// Prefer over <see cref="InvalidateAll"/> on write-heavy workloads.
    /// </summary>
    public void InvalidateTable(string table)
    {
        if (string.IsNullOrWhiteSpace(table)) return;
        InvalidateTables([table]);
    }

    /// <summary>Current write generation of a table (bumps on every invalidating write).</summary>
    public long Generation(string table)
        => _generations.TryGetValue(table, out var g) ? g : 0;

    private void InvalidateTables(IEnumerable<string> tables)
    {
        var set = new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase);
        if (set.Count == 0)
        {
            InvalidateAll(); // unparseable write — fail safe to full clear
            return;
        }
        foreach (var t in set) _generations.AddOrUpdate(t, 1, (_, g) => g + 1);
        foreach (var kv in _keyTables)
        {
            if (kv.Value.Any(t => set.Contains(t)))
            {
                EvictKey(kv.Key);
            }
        }
        TrimKeyTables();
    }

    private void TrimKeyTables()
    {
        // _keyTables/_keySql must not outgrow the store (entries evicted by TTL/SizeLimit
        // inside the store do not notify us): bound them loosely.
        if (_keyTables.Count <= 4 * _maxEntries) return;
        foreach (var k in _keyTables.Keys.Take(_keyTables.Count - 4 * _maxEntries).ToList())
            EvictKey(k);
    }

    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        if (!Cacheable(eventData, command))
        {
            return base.ReaderExecuting(command, eventData, result);
        }

        var key = BuildKey(command, eventData);
        if (TryGet(key, out var cached))
        {
            return InterceptionResult<DbDataReader>.SuppressWithResult(new CachedDataReader(cached));
        }

        // Single-flight (06.1): only one thread fetches; the rest wait and re-check.
        // TryRemove(pair) removes only our own gate, so a newer gate for the same key is never deleted.
        var gate = _gates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            if (TryGet(key, out cached))
            {
                return InterceptionResult<DbDataReader>.SuppressWithResult(new CachedDataReader(cached));
            }

            using (var reader = command.ExecuteReader())
            {
                // Buffer always captures the rows for serving; the entry is stored
                // only when it fits the size limits (06.2) and has a single result set.
                var (snapshot, cacheable, rejectReason) = Buffer(reader);
                if (cacheable) AddOrEvict(key, snapshot, ParseReadTables(command.CommandText), command.CommandText);
                else Bypass(key, rejectReason);
                return InterceptionResult<DbDataReader>.SuppressWithResult(new CachedDataReader(snapshot));
            }
        }
        finally { gate.Release(); _gates.TryRemove(new System.Collections.Generic.KeyValuePair<string, SemaphoreSlim>(key, gate)); }
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (!Cacheable(eventData, command))
        {
            return await base.ReaderExecutingAsync(command, eventData, result, cancellationToken).ConfigureAwait(false);
        }

        var key = BuildKey(command, eventData);
        if (TryGet(key, out var cached))
        {
            return InterceptionResult<DbDataReader>.SuppressWithResult(new CachedDataReader(cached));
        }

        // Async single-flight via Lazy<Task>: concurrent misses share one fetch task.
        // Pair-remove drops only our own lazy, so a newer in-flight fetch survives.
        var lazy = _inflight.GetOrAdd(key, k => new Lazy<Task<CachedQueryResult>>(
            () => FetchAndBufferAsync(command, k, cancellationToken)));
        try
        {
            var snapshot = await lazy.Value.ConfigureAwait(false);
            return InterceptionResult<DbDataReader>.SuppressWithResult(new CachedDataReader(snapshot));
        }
        finally { _inflight.TryRemove(new System.Collections.Generic.KeyValuePair<string, Lazy<Task<CachedQueryResult>>>(key, lazy)); }
    }

    /// <summary>Executes the command, buffers the rows and populates the cache (single-flight worker).</summary>
    private async Task<CachedQueryResult> FetchAndBufferAsync(DbCommand command, string key, CancellationToken ct)
    {
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            var (snapshot, cacheable, rejectReason) = await BufferAsync(reader, ct).ConfigureAwait(false);
            if (cacheable) AddOrEvict(key, snapshot, ParseReadTables(command.CommandText), command.CommandText);
            else Bypass(key, rejectReason);
            return snapshot;
        }
    }

    /// <summary>
    /// Optional write-through invalidation: when enabled, any non-query (INSERT/UPDATE/DELETE,
    /// including raw SQL) evicts only entries that read the written tables (06.4),
    /// so unrelated cached queries keep serving. Covers NonQuery, Scalar and Reader
    /// writes (INSERT RETURNING). Eviction happens on commit inside transactions.
    /// </summary>
    private void MarkPending(System.Data.Common.DbTransaction? tx, string sql)
    {
        var tables = ParseWriteTables(sql);
        if (tx is null) { InvalidateTables(tables); return; }
        if (_pendingTx.TryGetValue(tx, out var existing))
        {
            lock (existing) foreach (var t in tables) existing.Add(t);
        }
        else _pendingTx.Add(tx, new HashSet<string>(tables, StringComparer.OrdinalIgnoreCase));
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        if (_invalidateOnWrites)
        {
            var tx = eventData.Context?.Database.CurrentTransaction?.GetDbTransaction();
            MarkPending(tx, command.CommandText);
        }

        return base.NonQueryExecuted(command, eventData, result);
    }

    public override async ValueTask<int> NonQueryExecutedAsync(
        DbCommand command, CommandExecutedEventData eventData, int result,
        CancellationToken cancellationToken = default)
    {
        if (_invalidateOnWrites)
        {
            var tx = eventData.Context?.Database.CurrentTransaction?.GetDbTransaction();
            MarkPending(tx, command.CommandText);
        }

        return await base.NonQueryExecutedAsync(command, eventData, result, cancellationToken);
    }

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        if (_invalidateOnWrites && SqlWriteDetector.IsWrite(command.CommandText))
        {
            var tx = eventData.Context?.Database.CurrentTransaction?.GetDbTransaction();
            MarkPending(tx, command.CommandText);
        }

        return base.ReaderExecuted(command, eventData, result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        if (_invalidateOnWrites && SqlWriteDetector.IsWrite(command.CommandText))
        {
            var tx = eventData.Context?.Database.CurrentTransaction?.GetDbTransaction();
            MarkPending(tx, command.CommandText);
        }

        return base.ScalarExecuted(command, eventData, result);
    }

    // Invalidate only after transaction commits to avoid dirty reads and premature eviction on rollback
    public void TransactionCommitted(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData)
    {
        if (_pendingTx.TryGetValue(transaction, out var tables))
        {
            _pendingTx.Remove(transaction);
            InvalidateTables(tables);
        }
    }

    // NOTE: Task (not ValueTask) — must match IDbTransactionInterceptor exactly, otherwise
    // commit/rollback invalidation silently goes dead on the async path.
    public Task TransactionCommittedAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        if (_pendingTx.TryGetValue(transaction, out var tables))
        {
            _pendingTx.Remove(transaction);
            InvalidateTables(tables);
        }
        return Task.CompletedTask;
    }

    public void TransactionRolledBack(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData) => _pendingTx.Remove(transaction);
    public Task TransactionRolledBackAsync(System.Data.Common.DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        _pendingTx.Remove(transaction);
        return Task.CompletedTask;
    }

    private void AddOrEvict(string key, CachedQueryResult snapshot, string[] tables, string? sql = null)
    {
        _store.Set(key, snapshot, _timeToLive);
        _keyTables[key] = tables;
        if (sql is not null) _keySql[key] = sql;
        TrimKeyTables();
    }

    private bool Cacheable(CommandEventData eventData, DbCommand command)
        => IsSelect(command.CommandText)
           && (!_skipInsideTransactions || (eventData.Context?.Database.CurrentTransaction is null && command.Transaction is null));

    private static readonly System.Text.RegularExpressions.Regex ReadTablesRegex = new(
        @"\b(?:FROM|JOIN)\s+(?:\""[^\""]+\""|[\w\.\[\]""]+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));
    private static readonly System.Text.RegularExpressions.Regex WriteTablesRegex = new(
        @"\b(?:UPDATE\s+(?:OR\s+(?:IGNORE|REPLACE|ABORT|FAIL|ROLLBACK)\s+)?|INSERT\s+INTO\s+|DELETE\s+FROM\s+)(?:\""[^\""]+\""|[\w\.\[\]""]+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));
    private static readonly System.Text.RegularExpressions.Regex DepTagRegex = new(
        @"dep\s*:\s*([A-Za-z0-9_\., ]+)",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled,
        TimeSpan.FromMilliseconds(100));

    /// <summary>
    /// Tables a SELECT reads (06.4): parsed FROM/JOIN targets plus explicit
    /// <c>TagWith("dep:Orders, Customers")</c> contracts for views/CTEs the regex cannot see.
    /// Public for diagnostics and custom invalidation policies.
    /// </summary>
    public static string[] ParseReadTables(string sql)
    {
        var deps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Text.RegularExpressions.Match m in DepTagRegex.Matches(sql))
            foreach (var part in m.Groups[1].Value.Split(','))
            {
                var t = part.Trim();
                if (t.Length > 0) deps.Add(NormalizeTable(t));
            }
        var clean = StripComments(sql);
        try
        {
            foreach (System.Text.RegularExpressions.Match m in ReadTablesRegex.Matches(clean))
            {
                var t = NormalizeTable(m.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[^1]);
                if (t.Length > 0) deps.Add(t);
            }
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { }
        return [.. deps];
    }

    /// <summary>Tables an INSERT/UPDATE/DELETE writes (06.4). Public for diagnostics.</summary>
    public static string[] ParseWriteTables(string sql)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var clean = StripComments(sql);
        try
        {
            foreach (System.Text.RegularExpressions.Match m in WriteTablesRegex.Matches(clean))
            {
                var t = NormalizeTable(m.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)[^1]);
                if (t.Length > 0) tables.Add(t);
            }
        }
        catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { }
        return [.. tables];
    }

    private static string StripComments(string sql)
    {
        // Literal-aware comment stripper: '--' and '/* */' inside '...' are string content,
        // not comments — and table-name keywords inside literals must not parse as tables either,
        // so single-quoted literal contents are blanked (only '...' — unambiguous literals in every
        // dialect; "...", `...`, [...] may be identifiers and are preserved as-is).
        var sb = new System.Text.StringBuilder(sql.Length);
        var i = 0;
        char? quote = null; // ', ", `
        var inBracketIdent = false;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (quote is not null)
            {
                if (c == quote)
                {
                    // '' inside '...' is an escaped quote, not the end.
                    if (quote == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'')
                    {
                        if (quote == '\'') sb.Append("  ");
                        else sb.Append(c).Append(sql[i + 1]);
                        i += 2;
                        continue;
                    }
                    sb.Append(c);
                    quote = null;
                }
                else if (quote == '\'')
                {
                    sb.Append(' '); // blank literal content so keywords inside don't parse
                }
                else
                {
                    sb.Append(c);
                }
                i++;
                continue;
            }
            if (inBracketIdent)
            {
                sb.Append(c);
                if (c == ']') inBracketIdent = false;
                i++;
                continue;
            }
            if (c == '\'' || c == '"' || c == '`') { quote = c; sb.Append(c); i++; continue; }
            if (c == '[') { inBracketIdent = true; sb.Append(c); i++; continue; }
            if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                var end = sql.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0) break; // unterminated block — drop the rest
                i = end + 2;
                sb.Append(' ');
                continue;
            }
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                var nl = sql.IndexOf('\n', i + 2);
                if (nl < 0) break; // line comment to end of string
                i = nl + 1;
                sb.Append(' ');
                continue;
            }
            sb.Append(c);
            i++;
        }
        return sb.ToString();
    }

    private static string NormalizeTable(string token)
    {
        var t = token.Trim().Trim('"', '[', ']', '`').Trim();
        var dot = t.LastIndexOf('.');
        if (dot >= 0) t = t[(dot + 1)..];
        return t.Trim('"', '[', ']', '`');
    }

    private static bool IsSelect(string sql)
    {
        var trimmed = sql.TrimStart();
        // Interleaved skip of line and block comments
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

        return trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryGet(string key, out CachedQueryResult result)
    {
        result = null!;
        var start = Stopwatch.GetTimestamp();
        if (_store.TryGet(key, out var cached) && cached != null)
        {
            result = cached;
            SharedMeter.CacheHits.Add(1);
            SharedMeter.RecordCacheHit();
            SharedMeter.CacheServeDuration.Record(
                TimeSpan.FromTicks(Stopwatch.GetElapsedTime(start).Ticks).TotalSeconds);
            return true;
        }
        SharedMeter.CacheMisses.Add(1);
        SharedMeter.RecordCacheMiss();
        return false;
    }

    private static void Bypass(string key, string reason)
    {
        // Served from a transient buffer without storing (06.2): result too large
        // or multiple result sets. Observable via reason tag (low-cardinality).
        SharedMeter.CacheEntriesRejected.Add(1,
            new KeyValuePair<string, object?>("reason", reason));
    }

    /// <summary>
    /// Connection string with secret values (Password/Pwd/Token/...) replaced by ***.
    /// Used for the cache-key hash so rotated passwords don't cold-start the cache and secrets
    /// never leak into diagnostics. Public for custom key policies.
    /// </summary>
    public static string StripSecrets(string connectionString)
    {
        try
        {
            var b = new DbConnectionStringBuilder { ConnectionString = connectionString };
            foreach (var k in new[] { "Password", "Pwd", "Token", "AccessToken", "AccountKey", "SharedAccessKey", "ClientSecret" })
                if (b.ContainsKey(k)) b[k] = "***";
            return b.ConnectionString;
        }
        catch
        {
            return connectionString.Length <= 256 ? connectionString : connectionString[..256];
        }
    }

    /// <summary>
    /// Cache key for a command: connection identity (secrets stripped) + SQL + sorted parameter
    /// values. Public for diagnostics (key-shape inspection, custom invalidation policies).
    /// Does not include <see cref="IQueryCacheKeyContributor"/> parts — use the instance
    /// <see cref="BuildKey(DbCommand, CommandEventData?)"/> overload for the effective key.
    /// </summary>
    public static string BuildKey(DbCommand command)
    {
        var sb = new System.Text.StringBuilder();
        // Tenant isolation: include connection identity hash, but never the password/token itself.
        var cs = command.Connection?.ConnectionString;
        if (!string.IsNullOrEmpty(cs))
        {
            var csHash = System.IO.Hashing.XxHash3.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(StripSecrets(cs)));
            sb.Append($"[cs:{csHash:X16}]|");
        }
        sb.Append(command.CommandText);
        // Sort parameters to make key deterministic regardless of provider ordering
        foreach (var parameter in command.Parameters.OfType<DbParameter>().OrderBy(p => p.ParameterName, StringComparer.Ordinal))
        {
            var val = parameter.Value;
            string valStr;
            if (val is null or DBNull)
            {
                valStr = "NULL";
            }
            else if (val is byte[] bytes)
            {
                var h = System.IO.Hashing.XxHash3.HashToUInt64(bytes);
                valStr = $"[bytes:{bytes.Length}:xxh:{h:X16}]";
            }
            else
            {
                var s = Convert.ToString(val, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                if (s.Length > 256)
                {
                    var hash = System.IO.Hashing.XxHash3.HashToUInt64(System.Text.Encoding.UTF8.GetBytes(s));
                    valStr = $"[len:{s.Length}:xxh:{hash:X16}:{s[..64]}...]";
                }
                else
                {
                    // Include DbType to avoid '1' vs '1.0' collision (security-audit #11)
                    valStr = $"{parameter.DbType}:{s}";
                }
            }
            sb.Append('|').Append(parameter.ParameterName).Append('=').Append(valStr);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Effective cache key: <see cref="BuildKey(DbCommand)"/> plus every registered
    /// <see cref="IQueryCacheKeyContributor"/> part (tenant id, session version, ...).
    /// Contributors that return null/empty are skipped.
    /// </summary>
    public string BuildKey(DbCommand command, CommandEventData? eventData)
    {
        var key = BuildKey(command);
        IQueryCacheKeyContributor[] contributors;
        lock (_contributors) contributors = [.. _contributors];
        if (contributors.Length == 0) return key;
        var sb = new System.Text.StringBuilder(key);
        foreach (var c in contributors)
        {
            string? part = null;
            try { part = c.Contribute(command, eventData); }
            catch { part = null; }
            if (!string.IsNullOrEmpty(part)) sb.Append("|k:").Append(part);
        }
        return sb.ToString();
    }

    /// <returns>Buffered rows plus whether the entry may be stored.</returns>
    private (CachedQueryResult Snapshot, bool Cacheable, string RejectReason) Buffer(DbDataReader source)
    {
        var names = Enumerable.Range(0, source.FieldCount).Select(source.GetName).ToArray();
        var types = Enumerable.Range(0, source.FieldCount).Select(source.GetFieldType).ToArray();
        var rows = new List<object[]>();
        long bytes = 0;
        while (source.Read())
        {
            var row = new object[source.FieldCount];
            source.GetValues(row);
            Normalize(row);
            rows.Add(row);
            bytes += EstimateRowBytes(row);
            if (rows.Count > _maxRowsPerEntry || bytes > _maxBytesPerEntry)
            {
                // Keep buffering for serving, but never store: one wide SELECT
                // must not OOM the process (06.2).
                while (source.Read())
                {
                    var rest = new object[source.FieldCount];
                    source.GetValues(rest);
                    Normalize(rest);
                    rows.Add(rest);
                }
                DrainExtraResults(source);
                return (new CachedQueryResult(names, types, rows), false,
                    rows.Count > _maxRowsPerEntry ? "rows" : "bytes");
            }
        }

        if (DrainExtraResults(source))
            return (new CachedQueryResult(names, types, rows), false, "multi-result");

        return (new CachedQueryResult(names, types, rows), true, string.Empty);
    }

    private async Task<(CachedQueryResult Snapshot, bool Cacheable, string RejectReason)> BufferAsync(DbDataReader source, CancellationToken ct)
    {
        var names = Enumerable.Range(0, source.FieldCount).Select(source.GetName).ToArray();
        var types = Enumerable.Range(0, source.FieldCount).Select(source.GetFieldType).ToArray();
        var rows = new List<object[]>();
        long bytes = 0;
        while (await source.ReadAsync(ct).ConfigureAwait(false))
        {
            var row = new object[source.FieldCount];
            source.GetValues(row);
            Normalize(row);
            rows.Add(row);
            bytes += EstimateRowBytes(row);
            if (rows.Count > _maxRowsPerEntry || bytes > _maxBytesPerEntry)
            {
                while (await source.ReadAsync(ct).ConfigureAwait(false))
                {
                    var rest = new object[source.FieldCount];
                    source.GetValues(rest);
                    Normalize(rest);
                    rows.Add(rest);
                }
                await DrainExtraResultsAsync(source, ct).ConfigureAwait(false);
                return (new CachedQueryResult(names, types, rows), false,
                    rows.Count > _maxRowsPerEntry ? "rows" : "bytes");
            }
        }

        if (await DrainExtraResultsAsync(source, ct).ConfigureAwait(false))
            return (new CachedQueryResult(names, types, rows), false, "multi-result");

        return (new CachedQueryResult(names, types, rows), true, string.Empty);
    }

    /// <summary>
    /// CachedDataReader serves a single result set (NextResult() == false), so a command
    /// yielding more sets must never be stored — the extra sets would be silently lost (06.6).
    /// </summary>
    private static bool DrainExtraResults(DbDataReader source)
    {
        try { return source.NextResult(); }
        catch { return true; }
    }

    private static async Task<bool> DrainExtraResultsAsync(DbDataReader source, CancellationToken ct)
    {
        try { return await source.NextResultAsync(ct).ConfigureAwait(false); }
        catch { return true; }
    }

    private static long EstimateRowBytes(object[] row)
    {
        // Best-effort upper bound: fixed-size primitives at 16 (boxed), strings/bytes measured,
        // plus ~32 bytes per row for the object[] itself and the List slot.
        long bytes = 32;
        foreach (var v in row)
        {
            bytes += v switch
            {
                null or DBNull => 8,
                string s => 16 + (long)s.Length * 2,
                byte[] b => 16 + b.Length,
                decimal or Guid or DateTime or DateTimeOffset or TimeSpan or DateOnly or TimeOnly => 16,
                bool or byte or sbyte or char or short or ushort or int or uint or float => 8,
                long or ulong or double => 8,
                _ => 24,
            };
        }
        return bytes;
    }

    private static void Normalize(object[] row)
    {
        for (var i = 0; i < row.Length; i++)
        {
            row[i] ??= DBNull.Value;
        }
    }
}

/// <summary>Buffered snapshot of a query result set (columns + rows + types) stored in the cache.</summary>
public sealed record CachedQueryResult(string[] ColumnNames, Type[] FieldTypes, List<object[]> Rows)
{
    public CachedQueryResult(string[] columnNames, List<object[]> rows)
        : this(columnNames, rows.Select(_ => typeof(object)).ToArray(), rows) { }
}

/// <summary>A repeatable DbDataReader over a buffered query result (used for every cache serve).</summary>
public sealed class CachedDataReader : DbDataReader
{
    private readonly CachedQueryResult _result;
    private int _rowIndex = -1;

    public CachedDataReader(CachedQueryResult result) => _result = result;

    private object[] Current =>
        _rowIndex >= 0 && _rowIndex < _result.Rows.Count
            ? _result.Rows[_rowIndex]
            : throw new InvalidOperationException("No current row.");

    private bool _closed;

    public override int FieldCount => _result.ColumnNames.Length;
    public override bool HasRows => _result.Rows.Count > 0;
    public override bool IsClosed => _closed;
    public override int Depth => 0;
    public override int RecordsAffected => -1;

    public override void Close() => _closed = true;

    protected override void Dispose(bool disposing)
    {
        _closed = true;
        base.Dispose(disposing);
    }

    public override object this[int ordinal] => GetValue(ordinal);
    public override object this[string name] => GetValue(GetOrdinal(name));

    public override bool Read() => ++_rowIndex < _result.Rows.Count;

    public override string GetName(int ordinal) => _result.ColumnNames[ordinal];

    public override string GetDataTypeName(int ordinal) => _result.FieldTypes[ordinal].Name;

    public override int GetValues(object[] values)
    {
        var count = Math.Min(values.Length, FieldCount);
        for (var i = 0; i < count; i++)
        {
            values[i] = GetValue(i);
        }

        return count;
    }

    public override int GetOrdinal(string name)
    {
        for (var i = 0; i < _result.ColumnNames.Length; i++)
        {
            if (string.Equals(_result.ColumnNames[i], name, StringComparison.Ordinal))
            {
                return i;
            }
        }

        for (var i = 0; i < _result.ColumnNames.Length; i++)
        {
            if (string.Equals(_result.ColumnNames[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(name), $"Column '{name}' not found.");
    }

    public override object GetValue(int ordinal) => Current[ordinal];
    public override bool IsDBNull(int ordinal) => Current[ordinal] is DBNull;
    public override Type GetFieldType(int ordinal) => _result.FieldTypes[ordinal];

    public override T GetFieldValue<T>(int ordinal)
    {
        var value = GetValue(ordinal);
        // Defensive copy (06.6): the buffered byte[] is shared across all readers of
        // this entry — a mutating consumer must not poison the cache for everyone else.
        if (value is byte[] bytes && typeof(T).IsAssignableFrom(typeof(byte[])))
            return (T)(object)bytes.ToArray();
        return (T)ConvertValue(value, typeof(T))!;
    }
    public override bool GetBoolean(int ordinal) => (bool)ConvertValue(GetValue(ordinal), typeof(bool))!;
    public override byte GetByte(int ordinal) => (byte)ConvertValue(GetValue(ordinal), typeof(byte))!;
    public override char GetChar(int ordinal) => (char)ConvertValue(GetValue(ordinal), typeof(char))!;
    public override DateTime GetDateTime(int ordinal) => (DateTime)ConvertValue(GetValue(ordinal), typeof(DateTime))!;
    public override decimal GetDecimal(int ordinal) => (decimal)ConvertValue(GetValue(ordinal), typeof(decimal))!;
    public override double GetDouble(int ordinal) => (double)ConvertValue(GetValue(ordinal), typeof(double))!;
    public override float GetFloat(int ordinal) => (float)ConvertValue(GetValue(ordinal), typeof(float))!;
    public override Guid GetGuid(int ordinal) => (Guid)ConvertValue(GetValue(ordinal), typeof(Guid))!;
    public override short GetInt16(int ordinal) => (short)ConvertValue(GetValue(ordinal), typeof(short))!;
    public override int GetInt32(int ordinal) => (int)ConvertValue(GetValue(ordinal), typeof(int))!;
    public override long GetInt64(int ordinal) => (long)ConvertValue(GetValue(ordinal), typeof(long))!;
    public override string GetString(int ordinal) => (string)ConvertValue(GetValue(ordinal), typeof(string))!;

    public override System.Collections.IEnumerator GetEnumerator() => new DbEnumerator(this, closeReader: false);
    public override bool NextResult() => false;

    public override long GetBytes(int ordinal, long dataOffset, byte[]? buffer, int bufferOffset, int length)
    {
        var value = GetValue(ordinal);
        if (value is DBNull) return 0;
        var bytes = value as byte[] ?? (value is string s ? System.Text.Encoding.UTF8.GetBytes(s) : throw new InvalidCastException($"Column {ordinal} is {value.GetType().Name}, not bytes."));
        if (buffer is null) return bytes.Length;
        var available = bytes.Length - (int)dataOffset;
        if (available <= 0) return 0;
        var toCopy = Math.Min(available, length);
        Array.Copy(bytes, (int)dataOffset, buffer, bufferOffset, toCopy);
        return toCopy;
    }

    public override long GetChars(int ordinal, long dataOffset, char[]? buffer, int bufferOffset, int length)
    {
        var value = GetValue(ordinal);
        if (value is DBNull) return 0;
        var str = value as string ?? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        if (buffer is null) return str.Length;
        var available = str.Length - (int)dataOffset;
        if (available <= 0) return 0;
        var toCopy = Math.Min(available, length);
        str.CopyTo((int)dataOffset, buffer, bufferOffset, toCopy);
        return toCopy;
    }

    public override DataTable? GetSchemaTable()
    {
        var table = new DataTable();
        for (var i = 0; i < _result.ColumnNames.Length; i++)
        {
            table.Columns.Add(_result.ColumnNames[i], _result.FieldTypes[i]);
        }

        return table;
    }
    public override Stream GetStream(int ordinal)
    {
        var value = GetValue(ordinal);
        if (value is DBNull) return Stream.Null;
        if (value is byte[] bytes) return new MemoryStream(bytes, writable: false);
        if (value is Stream s) return s;
        throw new InvalidCastException($"Column {ordinal} is {value.GetType().Name}, not Stream/byte[].");
    }
    public override TextReader GetTextReader(int ordinal) => new StringReader(IsDBNull(ordinal) ? string.Empty : GetString(ordinal));

    private static object? ConvertValue(object? value, Type targetType)
    {
        if (value is null or DBNull) return null;
        var nonNullable = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (nonNullable.IsInstanceOfType(value)) return value;
        // Convert.ChangeType cannot parse string -> DateTimeOffset/Guid/TimeSpan/enum
        // (SQLite materializes those as TEXT). Route strings through TypeConverter (06.6).
        if (value is string s && nonNullable != typeof(string))
        {
            var converter = System.ComponentModel.TypeDescriptor.GetConverter(nonNullable);
            if (converter.CanConvertFrom(typeof(string)))
            {
                try { return converter.ConvertFromInvariantString(s); }
                catch (Exception) { /* fall through to ChangeType for a second opinion */ }
            }
        }
        return Convert.ChangeType(value, nonNullable,
            System.Globalization.CultureInfo.InvariantCulture);
    }
}
