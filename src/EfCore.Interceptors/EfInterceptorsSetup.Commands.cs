using EfCore.Interceptors.Abstractions;
using EfCore.Interceptors.Commands;
using EfCore.Interceptors.Queries;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;

namespace EfCore.Interceptors;

// Grouped command builders (09.1: EfInterceptorsSetup split into partials).
public sealed partial class EfInterceptorsSetup
{
    /// <summary>Warns when commands exceed the configured duration threshold.</summary>
    public EfInterceptorsSetup WithSlowQueryWarning(
        TimeSpan threshold,
        ILoggerFactory? loggerFactory = null,
        Func<CommandEventData, bool>? filter = null)
        => AddOrReplace(new SlowQueryCommandInterceptor(threshold, loggerFactory, filter));

    /// <summary>
    /// Appends provider hints to queries tagged via TagWith("key"),
    /// e.g. new Dictionary&lt;string,string&gt; { ["recompile"] = "OPTION (RECOMPILE)" } on SQL Server.
    /// Unguarded (backward compatible): the hint is applied on any provider.
    /// Prefer <see cref="WithSqlServerHints"/> / <see cref="WithPostgresHints"/> or
    /// <see cref="WithQueryHintsForProvider"/> so a dialect-specific hint never breaks another provider.
    /// </summary>
    public EfInterceptorsSetup WithQueryHints(IReadOnlyDictionary<string, string> hintsByTag)
        => AddOrReplace(new QueryHintsCommandInterceptor(hintsByTag: hintsByTag));

    /// <summary>Provider-guarded hints: applied only when the EF provider name contains one of <paramref name="allowedProviders"/>.</summary>
    public EfInterceptorsSetup WithQueryHintsForProvider(
        IReadOnlyDictionary<string, string> hintsByTag,
        IReadOnlySet<string> allowedProviders,
        ILoggerFactory? loggerFactory = null)
        => AddOrReplace(new QueryHintsCommandInterceptor(
            hintsByTag: hintsByTag, allowedProviders: allowedProviders, loggerFactory: loggerFactory));

    /// <summary>SQL Server hints guarded to the SqlServer provider (skipped + diagnosed elsewhere).</summary>
    public EfInterceptorsSetup WithSqlServerHints(
        IReadOnlyDictionary<string, string>? hintsByTag = null,
        ILoggerFactory? loggerFactory = null)
        => AddOrReplace(QueryHintsCommandInterceptor.ForSqlServer(hintsByTag, loggerFactory));

    /// <summary>Appends hints selected by a custom predicate over the SQL text.</summary>
    public EfInterceptorsSetup WithQueryHints(Func<string, string?> hintSelector)
        => AddOrReplace(new QueryHintsCommandInterceptor(hintSelector));

    /// <summary>PostgreSQL FOR UPDATE/SHARE locking hints via TagWith (guarded to Npgsql).</summary>
    public EfInterceptorsSetup WithPostgresHints(IReadOnlyDictionary<string, string>? hintsByTag = null, ILoggerFactory? loggerFactory = null)
        => AddOrReplace(QueryHintsCommandInterceptor.ForPostgres(hintsByTag, loggerFactory));

    /// <summary>Throws before any write statement reaches the database.</summary>
    public EfInterceptorsSetup WithReadOnlyGuard(Func<DbContext?, bool>? isEnabled = null)
        => AddOrReplace(new ReadOnlyGuardCommandInterceptor(isEnabled));

    /// <summary>
    /// Logs every SQL command with duration. Options: include parameter values, redact sensitive
    /// fragments (PII/cards — see <c>PiiRedactor.Default</c>) and sample non-error logs
    /// (failures are always logged). Replaces any previously added SQL logger.
    /// </summary>
    public EfInterceptorsSetup WithSqlLogging(
        ILoggerFactory? loggerFactory = null,
        bool includeParameterValues = false,
        Func<string, string>? textRedactor = null,
        double sampleRate = 1.0)
    {
        _interceptors.RemoveAll(i => i is SqlLoggingCommandInterceptor);
        return AddOrReplace(new SqlLoggingCommandInterceptor(loggerFactory, includeParameterValues, textRedactor, sampleRate));
    }

    /// <summary>
    /// Read-through in-memory query cache (same SQL + parameters served without a DB roundtrip).
    /// Writes evict only entries that read the written tables (06.4); entries expire after timeToLive.
    /// Oversize results bypass the store but are still served (06.2).
    /// </summary>
    /// <remarks>
    /// Read-your-writes is on by default (<paramref name="invalidateOnWrites"/>): pass false only for
    /// read-only replicas. For shared-database multi-tenancy combine with <c>WithMultiTenancy</c> —
    /// the tenant id is added to the cache key automatically. Session-dependent state
    /// (<c>WithSessionInit</c>, RLS, <c>current_setting</c>) is invisible to the key: exclude such
    /// queries from caching via a custom <see cref="IQueryCacheKeyContributor"/>.
    /// </remarks>
    public EfInterceptorsSetup WithSecondLevelCache(
        TimeSpan? timeToLive = null,
        bool skipInsideTransactions = true,
        int maxRowsPerEntry = 10_000,
        long maxBytesPerEntry = 8 * 1024 * 1024,
        bool invalidateOnWrites = true)
        => AddOrReplace(new CachingCommandInterceptor(timeToLive, skipInsideTransactions, invalidateOnWrites, 1000, null, maxRowsPerEntry, maxBytesPerEntry));

    /// <summary>Second-level cache with custom store (e.g. Redis via DistributedQueryCacheStore).</summary>
    public EfInterceptorsSetup WithSecondLevelCache(
        IQueryCacheStore store,
        TimeSpan? timeToLive = null,
        bool skipInsideTransactions = true,
        bool invalidateOnWrites = true,
        int maxRowsPerEntry = 10_000,
        long maxBytesPerEntry = 8 * 1024 * 1024)
        => AddOrReplace(new CachingCommandInterceptor(timeToLive, skipInsideTransactions, invalidateOnWrites, 1000, store, maxRowsPerEntry, maxBytesPerEntry));

    /// <summary>Second-level cache configured via <see cref="CachingOptions"/> (no positional boolean trap, supports DI binding).</summary>
    public EfInterceptorsSetup WithSecondLevelCache(Action<CachingOptions> configure)
    {
        var options = new CachingOptions();
        configure(options);
        return AddOrReplace(new CachingCommandInterceptor(options));
    }

    /// <summary>Distributed cache (IDistributedCache/Redis) for second-level cache.</summary>
    public EfInterceptorsSetup WithDistributedCache(Microsoft.Extensions.Caching.Distributed.IDistributedCache cache, TimeSpan? timeToLive = null)
        => AddOrReplace(new CachingCommandInterceptor(timeToLive, true, true, 1000, new DistributedQueryCacheStore(cache, timeToLive)));

    /// <summary>HybridCache second-level cache (shared IMemoryCache).</summary>
    [Obsolete("No single-flight, no size limits, no table invalidation (06.7): use WithSecondLevelCache(...) instead.")]
    public EfInterceptorsSetup WithHybridCache(Microsoft.Extensions.Caching.Memory.IMemoryCache cache, TimeSpan? ttl = null, bool skipInsideTransactions = true)
        => AddOrReplace(new HybridCacheCommandInterceptor(cache, ttl, skipInsideTransactions));

    /// <summary>Warns when one context repeats the identical SQL template N times (N+1 signature).</summary>
    public EfInterceptorsSetup WithNPlusOneDetection(int threshold = 5, ILoggerFactory? loggerFactory = null, bool captureStackTrace = false)
        => AddOrReplace(new NPlusOneDetectorCommandInterceptor(threshold, loggerFactory, captureStackTrace));

    /// <summary>Publishes ef.command.duration/executed/failed metrics via System.Diagnostics.Metrics.</summary>
    public EfInterceptorsSetup WithCommandMetrics(string? meterName = null)
        => AddOrReplace(new MetricsCommandInterceptor(meterName));

    /// <summary>Rejects write statements executed outside an explicit transaction.</summary>
    public EfInterceptorsSetup WithTransactionalWrites(Func<DbContext?, bool>? isEnabled = null)
        => AddOrReplace(new WritesRequireTransactionCommandInterceptor(isEnabled));

    /// <summary>
    /// Per-command timeout from a selector over the SQL text — e.g. give TagWith("report")
    /// queries 300s while everything else keeps the context default.
    /// </summary>
    public EfInterceptorsSetup WithCommandTimeout(Func<string, int?> timeoutSelector)
        => AddOrReplace(new CommandTimeoutCommandInterceptor(timeoutSelector));

    /// <summary>Per-command timeout keyed by TagWith tags (seconds).</summary>
    public EfInterceptorsSetup WithCommandTimeoutByTags(IReadOnlyDictionary<string, int> timeoutsByTag)
        => AddOrReplace(new CommandTimeoutCommandInterceptor(
            CommandTimeoutCommandInterceptor.FromTags(timeoutsByTag)));

    /// <summary>
    /// Blocks commands by CommandSource. Default: forbid running EF migrations from application
    /// runtime (migrations belong to CI/CD).
    /// </summary>
    public EfInterceptorsSetup WithCommandSourceBlocker(params CommandSource[] blockedSources)
        => AddOrReplace(new CommandSourceBlocker(blockedSources));

    /// <summary>Warns (and optionally calls back) whenever FromSqlRaw/SqlQuery/ExecuteSqlRaw is used.</summary>
    public EfInterceptorsSetup WithRawSqlUsageDetection(
        ILoggerFactory? loggerFactory = null,
        Action<CommandSource, string>? onRawSqlExecuted = null)
        => AddOrReplace(new RawSqlUsageDetector(loggerFactory, onRawSqlExecuted));

    /// <summary>Guards ExecuteUpdate/ExecuteDelete that bypass SaveChanges interceptors (soft-delete, encryption, audit).</summary>
    public EfInterceptorsSetup WithBulkOperationGuard(BulkOperationPolicy policy = BulkOperationPolicy.Throw, ILoggerFactory? loggerFactory = null)
        => AddOrReplace(new BulkOperationGuardInterceptor(policy, loggerFactory));

    /// <summary>Guards ExecuteUpdate/ExecuteDelete with explicit guarded table set.</summary>
    public EfInterceptorsSetup WithBulkOperationGuard(BulkOperationPolicy policy, IReadOnlySet<string> guardedTables, ILoggerFactory? loggerFactory = null)
        => AddOrReplace(new BulkOperationGuardInterceptor(policy, guardedTables, loggerFactory));

    /// <summary>
    /// Explicit opt-out of bulk safety (audit §2.1): removes the bulk guard and allows
    /// <c>ExecuteUpdate/ExecuteDelete</c> on guarded tables. Name is intentionally scary —
    /// prefer <c>BulkExtensions.ExecuteSoftDeleteAsync / ExecuteAuditedUpdateAsync /
    /// ExecuteEncryptedUpdateAsync</c> helpers. Logs a warning so the bypass is visible in diagnostics.
    /// </summary>
    public EfInterceptorsSetup AllowUnsafeBulkOperations(ILoggerFactory? loggerFactory = null)
    {
        _interceptors.RemoveAll(i => i is BulkOperationGuardInterceptor);
        // Keep IgnoreQueryFilters protection, drop only the ExecuteDelete/ExecuteUpdate ban.
        _interceptors.RemoveAll(i => i is StrictQueryPolicyQueryExpressionInterceptor);
        AddOrReplace(new StrictQueryPolicyQueryExpressionInterceptor(
            forbidIgnoreQueryFilters: true,
            forbidExecuteDelete: false,
            forbidExecuteUpdate: false));
        loggerFactory?.CreateLogger("EfCore.Interceptors.BulkGuard").LogWarning(
            "AllowUnsafeBulkOperations: bulk guard removed — ExecuteUpdate/ExecuteDelete bypass soft-delete/encryption/audit.");
        return this;
    }

    /// <summary>Requires the exact set of TagWith tags on every query.</summary>
    public EfInterceptorsSetup WithRequiredQueryTags(params string[] requiredTags)
        => AddOrReplace(new RequireQueryTagsInterceptor(requiredTags));

    /// <summary>Rejects completely untagged queries.</summary>
    public EfInterceptorsSetup WithRequireAnyQueryTag()
        => AddOrReplace(new RequireQueryTagsInterceptor([], requireAtLeastOneTag: true));

    /// <summary>
    /// Rejects forbidden query shapes at compilation: IgnoreQueryFilters by default,
    /// optionally ExecuteDelete/ExecuteUpdate, and optionally untagged raw SQL
    /// (FromSql/SqlQuery bypass query filters — allow-list via TagWith("tenant-safe")).
    /// </summary>
    public EfInterceptorsSetup WithStrictQueryPolicy(
        bool forbidIgnoreQueryFilters = true,
        bool forbidExecuteDelete = false,
        bool forbidExecuteUpdate = false,
        bool forbidUntaggedRawSql = false,
        string rawSqlBypassTag = "tenant-safe")
        => AddOrReplace(new StrictQueryPolicyQueryExpressionInterceptor(
            forbidIgnoreQueryFilters, forbidExecuteDelete, forbidExecuteUpdate,
            forbidUntaggedRawSql, rawSqlBypassTag));

    /// <summary>Guards unbounded queries (no Take/First/Single).</summary>
    public EfInterceptorsSetup WithUnboundedQueryGuard(int maxRows = 0)
        => AddOrReplace(new UnboundedQueryGuardInterceptor(maxRows));

    /// <summary>Resilience retries for transient command failures.</summary>
    public EfInterceptorsSetup WithResilience(int maxRetries = 2, TimeSpan? baseDelay = null, TimeSpan? maxDelay = null, ILoggerFactory? loggerFactory = null)
        => AddOrReplace(new ResilienceCommandInterceptor(maxRetries, baseDelay, maxDelay, loggerFactory));

    /// <summary>
    /// Query row budget (03.6): throws <c>QueryBudgetExceededException</c> once a single
    /// result set exceeds <paramref name="maxRows"/> rows. Scope with <paramref name="scopeFilter"/>
    /// (e.g. only <c>TagWith("web-request")</c> queries) so reports keep their own budget.
    /// </summary>
    public EfInterceptorsSetup WithQueryBudget(int maxRows, Func<Microsoft.EntityFrameworkCore.Diagnostics.CommandEventData, bool>? scopeFilter = null)
        => AddOrReplace(new QueryBudgetCommandInterceptor(maxRows, scopeFilter));
}
